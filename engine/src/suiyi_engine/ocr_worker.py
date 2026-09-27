"""OCR 独立子进程（#104）：主进程不导入 onnxruntime / OpenCV / numpy，空闲时子进程整个退出。

为什么用子进程：
    RapidOCR 的 onnxruntime 会话、OpenCV 与 numpy 在进程里卸载不干净（#96 实测卸载后仍多几十 MiB），
    放进子进程后，空闲退出就把这些内存全部还给系统，主进程只剩翻译模型。

为什么用 ``subprocess`` + stdin/stdout 而不是 ``multiprocessing``：
    - 协议是自定义的定长帧（见 :func:`write_frame`），只传 JSON 与 PNG 原始字节，不用 pickle，
      主进程与子进程之间没有共享状态，也不需要 ``multiprocessing`` 的资源跟踪进程。
    - Windows 上 ``subprocess`` 就是 ``CreateProcess``（相当于 spawn），不依赖 ``__main__`` 可被
      重新导入；打包成 exe（``sys.frozen``）时直接用 ``[exe, "ocr-worker"]`` 重新启动自己，
      不需要 ``multiprocessing.freeze_support()``。
    - 子进程的 stdout 只用于协议：启动时先复制一份 fd 1 给协议用，再把 fd 1 指向 stderr，
      第三方库的 ``print`` 不会弄坏协议；stderr 继承主进程，日志照常出现在服务日志里。

生命周期：
    - 第一次 OCR（或 ``--preload-ocr``）时按需启动；一次只处理一个请求（与原来的 ``OcrEngine``
      运行锁一样串行），处理中不会被空闲回收。
    - 空闲超过 ``ocr_idle_unload_s``（``--ocr-idle-unload`` / ``SUIYI_OCR_IDLE_UNLOAD``）由
      :class:`~suiyi_engine.memory.ModelJanitor` 调 :meth:`OcrProcessProvider.unload_idle`
      让子进程退出，
      下一次 OCR 重新启动（冷启动）。
    - 子进程崩溃、被杀或超时：本次请求 503 ``ocr_unavailable``（``reason`` 为 ``worker_crashed`` /
      ``worker_timeout``），下一次请求重新启动子进程。
    - 主进程退出时子进程一定退出：stdin 读到 EOF 就退出；另有看门狗线程在父进程消失时立即退出
      （POSIX 看 ``getppid``，Windows 等父进程句柄）；Windows 上主进程还把子进程放进
      ``KILL_ON_JOB_CLOSE`` 的 Job Object，主进程被强杀时由系统结束子进程。
"""

from __future__ import annotations

import atexit
import json
import logging
import os
import queue
import struct
import subprocess
import sys
import threading
import time
import weakref
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import IO, TYPE_CHECKING

from suiyi_engine.ocr_provider import OcrProvider, OcrUnavailable

if TYPE_CHECKING:
    from suiyi_engine.ocr import OcrResult

logger = logging.getLogger(__name__)

WINDOWS = sys.platform == "win32"
WORKER_ENV = "SUIYI_OCR_WORKER"
"""设为 ``0`` 时 serve 退回进程内 OCR（排查问题用）；默认用子进程。"""

DEFAULT_START_TIMEOUT_S = 300.0
"""启动子进程并加载模型（含第一次识别）的超时。慢机器冷启动也远小于这个值。"""
DEFAULT_REQUEST_TIMEOUT_S = 120.0
"""模型已加载时单次识别的超时。像素上限内的截图在慢机器上也只要几秒。"""

_HEADER = struct.Struct(">II")
_MAX_FRAME = 256 * 1024 * 1024


# ---------------------------------------------------------------- 协议


def write_frame(stream: IO[bytes], meta: dict[str, object], blob: bytes = b"") -> None:
    """一帧 = 8 字节头（JSON 长度、二进制长度，大端）+ UTF-8 JSON + 二进制（PNG 原始字节）。"""

    data = json.dumps(meta, ensure_ascii=False).encode("utf-8")
    stream.write(_HEADER.pack(len(data), len(blob)))
    stream.write(data)
    if blob:
        stream.write(blob)
    stream.flush()


def read_frame(stream: IO[bytes]) -> tuple[dict[str, object], bytes] | None:
    """读一帧；对端关闭（帧开头就是 EOF）时返回 ``None``，帧不完整时抛 ``EOFError``。"""

    head = _read_exact(stream, _HEADER.size, allow_eof=True)
    if head is None:
        return None
    meta_len, blob_len = _HEADER.unpack(head)
    if meta_len > _MAX_FRAME or blob_len > _MAX_FRAME:
        raise EOFError(f"帧长度异常：{meta_len} / {blob_len}")
    meta = json.loads(_read_exact(stream, meta_len) or b"{}")
    blob = _read_exact(stream, blob_len) if blob_len else b""
    return meta, blob or b""


def _read_exact(stream: IO[bytes], size: int, *, allow_eof: bool = False) -> bytes | None:
    chunks: list[bytes] = []
    remaining = size
    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            if allow_eof and remaining == size:
                return None
            raise EOFError("对端在一帧中途关闭")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


# ---------------------------------------------------------------- 子进程


TRIM_AFTER_S = 2.0
"""子进程处理完请求、安静这么多秒后整理一次堆（与主进程 ``ModelJanitor`` 的默认值相同）。"""


class _IdleTrimmer:
    """请求处理完、安静 ``after_s`` 秒后调用一次 ``gc.collect()`` + ``trimmer()``（#104）。

    onnxruntime / OpenCV 推理时的临时缓冲在 glibc / Windows 堆里不会自己还给系统，
    不整理的话子进程用过一次 OCR 后常驻会多出 150 MiB 左右。整理与请求处理互斥，识别中不会整理。
    """

    def __init__(self, trimmer: Callable[[], object], after_s: float) -> None:
        self._trimmer = trimmer
        self.after_s = after_s
        self._lock = threading.Lock()
        self._last_done: float | None = None
        self._trimmed_for: float | None = None
        self.trims = 0
        threading.Thread(target=self._loop, name="suiyi-ocr-trim", daemon=True).start()

    def busy(self) -> threading.Lock:
        return self._lock

    def done(self) -> None:
        self._last_done = time.monotonic()

    def _loop(self) -> None:
        import gc

        while True:
            time.sleep(min(0.5, self.after_s / 2 or 0.05))
            last = self._last_done
            if last is None or last == self._trimmed_for:
                continue
            if time.monotonic() - last < self.after_s:
                continue
            with self._lock:
                if self._last_done != last:  # 期间又处理了请求，重新计时
                    continue
                gc.collect()
                try:
                    self._trimmer()
                except Exception:  # 整理失败不影响服务
                    logger.debug("OCR 子进程整理堆失败", exc_info=True)
                self._trimmed_for = last
                self.trims += 1


def run_worker(
    provider: OcrProvider,
    *,
    parent_pid: int | None = None,
    stdin: IO[bytes] | None = None,
    stdout: IO[bytes] | None = None,
    trimmer: Callable[[], object] | None = None,
    trim_after_s: float = TRIM_AFTER_S,
) -> int:
    """子进程主循环：按帧读请求、用进程内 :class:`OcrProvider` 识别、按帧回结果。

    stdin EOF 即退出。每次请求处理完、安静 ``trim_after_s`` 秒后整理一次堆
    （``trimmer`` 默认 :func:`suiyi_engine.memory.trim`）。
    """

    if stdout is None:
        stdout = _claim_stdout()
    if stdin is None:
        stdin = sys.stdin.buffer
    if trimmer is None:
        from suiyi_engine import memory

        trimmer = memory.trim
    idle = _IdleTrimmer(trimmer, trim_after_s)
    if parent_pid:
        _watch_parent(parent_pid)
    write_frame(stdout, {"ok": True, "op": "hello", "pid": os.getpid()})
    while True:
        frame = read_frame(stdin)
        if frame is None:
            return 0
        meta, blob = frame
        op = meta.get("op")
        if op == "exit":
            write_frame(stdout, {"ok": True})
            return 0
        with idle.busy():
            reply = _handle(provider, str(op), meta, blob)
            idle.done()
        write_frame(stdout, reply)


def _handle(
    provider: OcrProvider, op: str, meta: dict[str, object], blob: bytes
) -> dict[str, object]:
    from suiyi_engine.ocr import InvalidImageError
    from suiyi_engine.ocr.engine import ImageTooLargeError

    try:
        if op == "warmup":
            return {"ok": True, "ms": provider.warmup()}
        if op == "recognize":
            result = provider.recognize(blob, str(meta.get("lang", "auto")))
            return {"ok": True, "result": result.to_dict(), "stats": dict(result.stats)}
        return {"ok": False, "kind": "internal", "message": f"未知操作：{op}"}
    except OcrUnavailable as exc:
        return {"ok": False, "kind": "unavailable", "message": str(exc), **exc.details()}
    except ImageTooLargeError as exc:
        return {"ok": False, "kind": "too_large", "message": str(exc)}
    except InvalidImageError as exc:
        return {"ok": False, "kind": "invalid_image", "message": str(exc)}
    except Exception as exc:  # 意外错误回给主进程，子进程继续服务
        logger.exception("OCR 子进程处理 %s 失败", op)
        return {"ok": False, "kind": "internal", "message": f"{type(exc).__name__}: {exc}"}


def _claim_stdout() -> IO[bytes]:
    """把 fd 1 留给协议，再让 fd 1 / ``sys.stdout`` 指向 stderr，防止第三方库的输出混进协议。"""

    sys.stdout.flush()
    proto = os.fdopen(os.dup(1), "wb")
    os.dup2(2, 1)
    sys.stdout = sys.stderr
    return proto


def _watch_parent(parent_pid: int) -> None:
    """父进程消失时立即退出（识别进行中也退出），不留孤儿进程。"""

    def watch_posix() -> None:
        while True:
            if os.getppid() != parent_pid:
                os._exit(0)
            time.sleep(1.0)

    def watch_windows() -> None:
        import ctypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        handle = kernel32.OpenProcess(0x00100000, False, parent_pid)  # SYNCHRONIZE
        if not handle:
            if ctypes.get_last_error() == 87:  # ERROR_INVALID_PARAMETER：父进程已不存在
                os._exit(0)
            return  # 打不开父进程句柄：只靠 stdin EOF 与 Job Object
        kernel32.WaitForSingleObject(handle, 0xFFFFFFFF)
        os._exit(0)

    target = watch_windows if WINDOWS else watch_posix
    threading.Thread(target=target, name="suiyi-ocr-parent-watch", daemon=True).start()


def worker_main(models_dir: str | None, parent_pid: int | None) -> int:
    """``python -m suiyi_engine ocr-worker`` 的入口。"""

    from suiyi_engine import memory

    memory.configure_allocator()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s ocr-worker %(message)s")
    if models_dir is None:
        from suiyi_engine.registry import default_models_dir

        models_dir = str(default_models_dir())
    return run_worker(OcrProvider(models_dir), parent_pid=parent_pid)


def worker_command(models_dir: Path | str) -> list[str]:
    """启动子进程的命令。打包成 exe 时 ``sys.executable`` 就是 exe 本身，直接带子命令。"""

    base = (
        [sys.executable]
        if getattr(sys, "frozen", False)
        else [sys.executable, "-m", "suiyi_engine"]
    )
    return [*base, "ocr-worker", "--models-dir", str(models_dir), "--parent-pid", str(os.getpid())]


# ---------------------------------------------------------------- 主进程一侧


class _WorkerGone(Exception):
    pass


class OcrProcessProvider:
    """与 :class:`OcrProvider` 接口相同，但识别在子进程里做（#104）。线程安全。

    状态（``/health.ocr_worker_state``）：``stopped`` 没有子进程；``starting`` 已启动、模型未就绪；
    ``ready`` 模型已加载、空闲；``busy`` 正在处理请求。
    """

    def __init__(
        self,
        models_dir: Path | str,
        *,
        command: Sequence[str] | None = None,
        start_timeout_s: float = DEFAULT_START_TIMEOUT_S,
        request_timeout_s: float = DEFAULT_REQUEST_TIMEOUT_S,
    ) -> None:
        self.models_dir = Path(models_dir)
        self._command = list(command) if command is not None else None
        self.start_timeout_s = start_timeout_s
        self.request_timeout_s = request_timeout_s
        self._lock = threading.Lock()
        """一次只有一个请求；持锁期间不会被空闲回收。"""
        self._proc: subprocess.Popen[bytes] | None = None
        self._replies: queue.Queue[tuple[dict[str, object], bytes] | None] | None = None
        self._model_ready = False
        self._state = "stopped"
        self._last_used: float | None = None
        self._job: object | None = None
        self._worker_pid: int | None = None
        self._closed = False
        self.starts = 0
        """启动过几次子进程（冷启动次数）。"""
        self.last_error: OcrUnavailable | None = None
        atexit.register(_close_at_exit, weakref.ref(self))

    # ---- 与 OcrProvider 相同的接口

    @property
    def loaded(self) -> bool:
        proc = self._proc
        return self._model_ready and proc is not None and proc.poll() is None

    @property
    def pid(self) -> int | None:
        """真正做 OCR 的子进程 pid（子进程自己报告；Windows venv 的 python.exe 只是启动器）。"""

        proc = self._proc
        if proc is None or proc.poll() is not None:
            return None
        return self._worker_pid or proc.pid

    def touch(self) -> None:
        self._last_used = time.monotonic()

    def last_activity(self) -> float | None:
        return self._last_used

    def health(self) -> dict[str, object] | None:
        error = self.last_error
        if error is None:
            return None
        return {"message": str(error), **error.details()}

    def worker_health(self) -> dict[str, object]:
        pid = self.pid
        state = self._state if pid is not None else "stopped"
        return {"ocr_worker_pid": pid, "ocr_worker_state": state}

    def warmup(self) -> float:
        """启动子进程、加载并预热模型，返回主进程看到的总耗时毫秒（含启动子进程）。"""

        started = time.perf_counter()
        self._call({"op": "warmup"}, b"")
        return 1000 * (time.perf_counter() - started)

    def recognize(self, data: bytes, lang: str = "auto") -> OcrResult:
        from suiyi_engine.ocr import OcrResult

        reply = self._call({"op": "recognize", "lang": lang}, data)
        return OcrResult.from_dict(reply["result"], reply.get("stats"))  # type: ignore[arg-type]

    def unload_idle(self, idle_s: float, *, now: float | None = None) -> bool:
        """空闲超过 ``idle_s`` 秒就让子进程退出。正在处理请求（或有请求在排队）时不回收。"""

        if not self._lock.acquire(blocking=False):
            return False
        try:
            last = self._last_used
            if self.pid is None or last is None:
                return False
            current = time.monotonic() if now is None else now
            if current - last < idle_s:
                return False
            self._stop_locked(graceful=True)
            return True
        finally:
            self._lock.release()

    def close(self) -> None:
        """服务退出：让子进程退出（最多等几秒，之后强制结束）。"""

        self._closed = True
        with self._lock:
            self._stop_locked(graceful=True)

    # ---- 内部

    def _call(self, meta: dict[str, object], blob: bytes) -> dict[str, object]:
        with self._lock:
            try:
                reply = self._call_locked(meta, blob)
            except OcrUnavailable as exc:
                self.last_error = exc
                raise
            finally:
                self.touch()
        kind = reply.get("kind")
        if kind == "too_large":
            from suiyi_engine.ocr.engine import ImageTooLargeError

            raise ImageTooLargeError(str(reply.get("message")))
        if kind == "invalid_image":
            from suiyi_engine.ocr import InvalidImageError

            raise InvalidImageError(str(reply.get("message")))
        if kind == "internal":
            raise RuntimeError(f"OCR 子进程内部错误：{reply.get('message')}")
        return reply

    def _call_locked(self, meta: dict[str, object], blob: bytes) -> dict[str, object]:
        cold = not self.loaded
        if self.pid is None:
            self._start_locked()
        timeout = self.request_timeout_s + (self.start_timeout_s if cold else 0.0)
        self._state = "busy" if self._model_ready else "starting"
        try:
            reply = self._exchange(meta, blob, timeout)
        except TimeoutError as exc:
            self._stop_locked(graceful=False)
            raise OcrUnavailable(
                f"OCR 子进程 {timeout:.0f} 秒内没有响应，已结束，下一次请求会重新启动",
                reason="worker_timeout",
            ) from exc
        except (_WorkerGone, OSError, ValueError) as exc:
            code = self._proc.poll() if self._proc is not None else None
            self._stop_locked(graceful=False)
            raise OcrUnavailable(
                f"OCR 子进程意外退出（返回码 {code}），下一次请求会重新启动",
                reason="worker_crashed",
            ) from exc
        if not reply.get("ok") and reply.get("kind") == "unavailable":
            # 依赖或模型问题：子进程留着没用，退出；补齐后下一次请求重新启动就能用
            self._stop_locked(graceful=True)
            missing = tuple(str(m) for m in reply.get("missing_models") or ())  # type: ignore[union-attr]
            raise OcrUnavailable(
                str(reply.get("message")), reason=str(reply.get("reason")), missing_models=missing
            )
        self._model_ready = True
        self._state = "ready"
        self.last_error = None
        return reply

    def _exchange(self, meta: dict[str, object], blob: bytes, timeout: float) -> dict[str, object]:
        proc, replies = self._proc, self._replies
        if proc is None or proc.stdin is None or replies is None:
            raise _WorkerGone("子进程未启动")
        write_frame(proc.stdin, meta, blob)
        try:
            frame = replies.get(timeout=timeout)
        except queue.Empty as exc:
            raise TimeoutError from exc
        if frame is None:
            raise _WorkerGone("子进程已退出")
        return frame[0]

    def _start_locked(self) -> None:
        if self._closed:
            raise OcrUnavailable("服务正在退出", reason="worker_crashed")
        command = self._command if self._command is not None else worker_command(self.models_dir)
        env = dict(os.environ)
        if not getattr(sys, "frozen", False):  # 子进程导入与主进程同一份 suiyi_engine
            src = str(Path(__file__).resolve().parents[1])
            env["PYTHONPATH"] = os.pathsep.join(
                [src, *(p for p in env.get("PYTHONPATH", "").split(os.pathsep) if p)]
            )
        kwargs: dict[str, object] = {}
        if WINDOWS:
            kwargs["creationflags"] = 0x08000000  # CREATE_NO_WINDOW：不弹控制台窗口
        started = time.perf_counter()
        try:
            proc = subprocess.Popen(  # noqa: S603 —— 命令是本程序自己
                command,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                env=env,
                **kwargs,  # type: ignore[arg-type]
            )
        except OSError as exc:
            raise OcrUnavailable(f"无法启动 OCR 子进程：{exc}", reason="worker_crashed") from exc
        if WINDOWS:
            self._job = _assign_kill_on_close_job(proc.pid, self._job)
        replies: queue.Queue[tuple[dict[str, object], bytes] | None] = queue.Queue()
        threading.Thread(
            target=_pump, args=(proc, replies), name="suiyi-ocr-reader", daemon=True
        ).start()
        self._proc, self._replies = proc, replies
        self._model_ready = False
        self._state = "starting"
        self.starts += 1
        try:
            hello = replies.get(timeout=self.start_timeout_s)
        except queue.Empty:
            hello = None
        if hello is None or hello[0].get("op") != "hello":
            code = proc.poll()
            self._stop_locked(graceful=False)
            raise OcrUnavailable(f"OCR 子进程启动失败（返回码 {code}）", reason="worker_crashed")
        worker_pid = hello[0].get("pid")
        self._worker_pid = worker_pid if isinstance(worker_pid, int) else None
        logger.info(
            "OCR 子进程已启动 pid=%s（%.0f ms）",
            self._worker_pid or proc.pid,
            1000 * (time.perf_counter() - started),
        )

    def _stop_locked(self, *, graceful: bool) -> None:
        proc = self._proc
        self._proc, self._replies = None, None
        self._worker_pid = None
        self._model_ready = False
        self._state = "stopped"
        if proc is None:
            return
        try:
            if graceful and proc.poll() is None and proc.stdin is not None:
                try:
                    write_frame(proc.stdin, {"op": "exit"})
                    proc.stdin.close()
                    proc.wait(timeout=5)
                except (OSError, ValueError, subprocess.TimeoutExpired):
                    pass
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)
        except (OSError, subprocess.TimeoutExpired):
            logger.warning("OCR 子进程 pid=%d 未能结束", proc.pid)
        finally:
            for stream in (proc.stdin, proc.stdout):
                try:
                    if stream is not None:
                        stream.close()
                except OSError:
                    pass
        logger.info("OCR 子进程已退出 pid=%d 返回码 %s", proc.pid, proc.returncode)


def _close_at_exit(ref: weakref.ref[OcrProcessProvider]) -> None:
    provider = ref()
    if provider is not None:
        provider.close()


def _pump(
    proc: subprocess.Popen[bytes], replies: queue.Queue[tuple[dict[str, object], bytes] | None]
) -> None:
    """读子进程 stdout 的帧放进队列；EOF 或出错时放 ``None``。每个子进程一个线程。"""

    try:
        assert proc.stdout is not None
        while True:
            frame = read_frame(proc.stdout)
            if frame is None:
                break
            replies.put(frame)
    except (OSError, ValueError, EOFError):
        pass
    finally:
        replies.put(None)


def _assign_kill_on_close_job(pid: int, job: object | None) -> object | None:
    """Windows：把子进程放进 ``KILL_ON_JOB_CLOSE`` 的 Job Object。

    主进程退出时系统关闭 Job 句柄并结束子进程。

    Job 句柄整个进程只建一次、从不关闭。失败只记日志（看门狗与 stdin EOF 仍能保证子进程退出）。
    """

    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateJobObjectW.restype = wintypes.HANDLE
    kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel32.SetInformationJobObject.argtypes = [
        wintypes.HANDLE,
        ctypes.c_int,
        ctypes.c_void_p,
        wintypes.DWORD,
    ]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

    class BasicLimits(ctypes.Structure):
        _fields_ = [
            ("PerProcessUserTimeLimit", ctypes.c_int64),
            ("PerJobUserTimeLimit", ctypes.c_int64),
            ("LimitFlags", wintypes.DWORD),
            ("MinimumWorkingSetSize", ctypes.c_size_t),
            ("MaximumWorkingSetSize", ctypes.c_size_t),
            ("ActiveProcessLimit", wintypes.DWORD),
            ("Affinity", ctypes.c_size_t),
            ("PriorityClass", wintypes.DWORD),
            ("SchedulingClass", wintypes.DWORD),
        ]

    class IoCounters(ctypes.Structure):
        _fields_ = [
            (name, ctypes.c_uint64)
            for name in ("Read", "Write", "Other", "ReadBytes", "WriteBytes", "OtherBytes")
        ]

    class ExtendedLimits(ctypes.Structure):
        _fields_ = [
            ("BasicLimitInformation", BasicLimits),
            ("IoInfo", IoCounters),
            ("ProcessMemoryLimit", ctypes.c_size_t),
            ("JobMemoryLimit", ctypes.c_size_t),
            ("PeakProcessMemoryUsed", ctypes.c_size_t),
            ("PeakJobMemoryUsed", ctypes.c_size_t),
        ]

    try:
        if job is None:
            handle = kernel32.CreateJobObjectW(None, None)
            if not handle:
                raise OSError(ctypes.get_last_error(), "CreateJobObjectW")
            info = ExtendedLimits()
            info.BasicLimitInformation.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if not kernel32.SetInformationJobObject(
                handle, 9, ctypes.byref(info), ctypes.sizeof(info)
            ):
                raise OSError(ctypes.get_last_error(), "SetInformationJobObject")
            job = handle
        process = kernel32.OpenProcess(0x0100 | 0x0001, False, pid)  # SET_QUOTA | TERMINATE
        if not process:
            raise OSError(ctypes.get_last_error(), "OpenProcess")
        try:
            if not kernel32.AssignProcessToJobObject(job, process):
                raise OSError(ctypes.get_last_error(), "AssignProcessToJobObject")
        finally:
            kernel32.CloseHandle(process)
    except OSError as exc:
        logger.warning("OCR 子进程未能放进 Job Object（仍由看门狗保证随主进程退出）：%s", exc)
    return job


def make_ocr_provider(models_dir: Path | str) -> OcrProvider | OcrProcessProvider:
    """serve 用的 OCR 提供者：默认子进程；``SUIYI_OCR_WORKER=0`` 时退回进程内（排查用）。"""

    if os.environ.get(WORKER_ENV, "").strip().lower() in ("0", "false", "off", "no"):
        return OcrProvider(models_dir)
    return OcrProcessProvider(models_dir)
