"""OCR 独立子进程（#104）：按需启动、空闲退出、崩溃/超时恢复、并发、不留孤儿、协议与 HTTP 兼容。

大部分用例用假 OCR 子进程（不需要 rapidocr / 模型，Windows 与 Linux 都跑真实的子进程）；
最后一组用真实模型，比较子进程与进程内结果逐字节相同，
并确认主进程没有导入 onnxruntime / cv2 / numpy。
"""

from __future__ import annotations

import io
import json
import os
import signal
import struct
import subprocess
import sys
import threading
import time
import zlib
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from suiyi_engine.api import ApiSettings, create_app
from suiyi_engine.ocr import InvalidImageError, OcrResult
from suiyi_engine.ocr_provider import OcrUnavailable
from suiyi_engine.ocr_worker import (
    OcrProcessProvider,
    make_ocr_provider,
    read_frame,
    worker_command,
    write_frame,
)

WINDOWS = sys.platform == "win32"

FAKE_WORKER = r"""
import os, sys, time
from suiyi_engine.ocr import InvalidImageError, OcrLine, OcrParagraph, OcrResult
from suiyi_engine.ocr_provider import OcrProvider
from suiyi_engine.ocr_worker import run_worker


class Engine:
    loaded = False

    def load(self):
        self.loaded = True

    def unload(self):
        self.loaded = False
        return True

    def warmup(self):
        pass

    def recognize(self, data, lang="auto"):
        print("第三方库往 stdout 打印的噪音")  # 不能弄坏协议
        tail = data[-12:].decode("ascii", "replace")
        if tail.endswith("CRASH"):
            os._exit(3)
        if "SLEEP" in tail:
            time.sleep(float(tail.split("SLEEP")[1]))
        if tail.endswith("BAD"):
            raise InvalidImageError("坏图")
        text = f"{tail}|{lang}|{os.getpid()}"
        line = OcrLine(text=text, box=((0.0, 0.0), (10.0, 0.0), (10.0, 5.0), (0.0, 5.0)), score=0.9)
        para = OcrParagraph(text=text, box=(0.0, 0.0, 10.0, 5.0), line_indices=(0,))
        return OcrResult(lines=(line,), paragraphs=(para,), width=10, height=5, elapsed_ms=1.0)


if sys.argv[2:] == ["die-at-start"]:
    sys.exit(4)
run_worker(OcrProvider(".", engine_factory=Engine), parent_pid=int(sys.argv[1]))
"""


@pytest.fixture
def fake_command(tmp_path: Path):
    script = tmp_path / "fake_worker.py"
    script.write_text(FAKE_WORKER, encoding="utf-8")

    def command(*extra: str) -> list[str]:
        return [sys.executable, str(script), str(os.getpid()), *extra]

    return command


@pytest.fixture
def provider(fake_command):
    made: list[OcrProcessProvider] = []

    def make(**kwargs: object) -> OcrProcessProvider:
        kwargs.setdefault("start_timeout_s", 60.0)
        p = OcrProcessProvider(".", command=fake_command(), **kwargs)  # type: ignore[arg-type]
        made.append(p)
        return p

    yield make
    for p in made:
        p.close()


def _text(result: OcrResult) -> str:
    return result.paragraphs[0].text


def _alive(pid: int) -> bool:
    if WINDOWS:
        import ctypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.restype = ctypes.c_void_p
        handle = kernel32.OpenProcess(0x1000, False, pid)  # QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        code = ctypes.c_ulong()
        kernel32.GetExitCodeProcess(ctypes.c_void_p(handle), ctypes.byref(code))
        kernel32.CloseHandle(ctypes.c_void_p(handle))
        return code.value == 259  # STILL_ACTIVE
    try:
        os.kill(pid, 0)
    except OSError:
        return False
    try:  # 已退出但还没被回收的僵尸不算活着
        status = Path(f"/proc/{pid}/status").read_text()
    except OSError:
        return True
    return "\nState:\tZ" not in status


def _wait_dead(pid: int, timeout: float = 15.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not _alive(pid):
            return True
        time.sleep(0.1)
    return False


# ---- 协议 ------------------------------------------------------------------------


def test_frame_roundtrip_and_eof() -> None:
    buf = io.BytesIO()
    write_frame(buf, {"op": "recognize", "lang": "中文"}, b"\x89PNG\x00\xff")
    write_frame(buf, {"ok": True})
    buf.seek(0)
    assert read_frame(buf) == ({"op": "recognize", "lang": "中文"}, b"\x89PNG\x00\xff")
    assert read_frame(buf) == ({"ok": True}, b"")
    assert read_frame(buf) is None  # 对端正常关闭
    with pytest.raises(EOFError):  # 一帧读到一半
        read_frame(io.BytesIO(b"\x00\x00\x00\x10\x00\x00\x00\x00{"))


def test_worker_command_plain_and_frozen(monkeypatch: pytest.MonkeyPatch) -> None:
    plain = worker_command("m")
    assert plain[:4] == [sys.executable, "-m", "suiyi_engine", "ocr-worker"]
    assert plain[-4:] == ["--models-dir", "m", "--parent-pid", str(os.getpid())]
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    assert worker_command("m")[:2] == [sys.executable, "ocr-worker"]  # exe 重新启动自己


def test_make_ocr_provider_defaults_to_subprocess(monkeypatch: pytest.MonkeyPatch) -> None:
    from suiyi_engine.ocr_provider import OcrProvider

    monkeypatch.delenv("SUIYI_OCR_WORKER", raising=False)
    assert isinstance(make_ocr_provider("m"), OcrProcessProvider)
    monkeypatch.setenv("SUIYI_OCR_WORKER", "0")
    assert type(make_ocr_provider("m")) is OcrProvider


# ---- 生命周期 ----------------------------------------------------------------------


def test_starts_on_demand_and_reuses_worker(provider) -> None:
    p = provider()
    assert p.pid is None and not p.loaded
    assert p.worker_health() == {"ocr_worker_pid": None, "ocr_worker_state": "stopped"}
    first = p.recognize(b"img-one", "zh")
    pid = p.pid
    assert pid is not None and pid != os.getpid() and p.loaded
    assert _text(first) == f"img-one|zh|{pid}"
    assert p.worker_health() == {"ocr_worker_pid": pid, "ocr_worker_state": "ready"}
    assert _text(p.recognize(b"img-two")) == f"img-two|auto|{pid}"
    assert p.starts == 1 and p.last_activity() is not None


def test_idle_exit_then_cold_restart(provider) -> None:
    p = provider()
    p.recognize(b"a")
    pid = p.pid
    assert pid is not None
    assert not p.unload_idle(60)  # 刚用过
    assert p.unload_idle(0.5, now=time.monotonic() + 1)
    assert p.pid is None and not p.loaded
    assert _wait_dead(pid)
    assert not p.unload_idle(0)  # 没有子进程时什么都不做
    again = p.recognize(b"b")
    assert p.pid not in (None, pid) and p.starts == 2
    assert _text(again).startswith("b|")


def test_busy_worker_is_not_reclaimed(provider) -> None:
    p = provider()
    p.recognize(b"warm")
    done: list[str] = []
    worker = threading.Thread(target=lambda: done.append(_text(p.recognize(b"x SLEEP0.8"))))
    worker.start()
    time.sleep(0.3)
    assert p.worker_health()["ocr_worker_state"] == "busy"
    assert not p.unload_idle(0, now=time.monotonic() + 3600)  # 处理中，不回收
    worker.join(10)
    assert done and p.loaded


def test_crash_returns_unavailable_then_recovers(provider) -> None:
    p = provider()
    p.recognize(b"ok")
    old = p.pid
    with pytest.raises(OcrUnavailable) as info:
        p.recognize(b"CRASH")
    assert info.value.reason == "worker_crashed"
    assert p.health()["reason"] == "worker_crashed"  # type: ignore[index]
    assert p.pid is None
    result = p.recognize(b"after")
    assert p.pid not in (None, old) and _text(result).startswith("after|")
    assert p.health() is None  # 恢复后清空


def test_killed_worker_is_restarted_transparently(provider) -> None:
    p = provider()
    p.recognize(b"ok")
    old = p.pid
    assert old is not None
    os.kill(old, signal.SIGTERM)
    assert _wait_dead(old)
    deadline = time.monotonic() + 10
    while p.pid is not None and time.monotonic() < deadline:  # 等父进程回收（线程可能晚一点退出）
        time.sleep(0.05)
    assert _text(p.recognize(b"next")).startswith("next|")
    assert p.pid not in (None, old)


def test_timeout_kills_worker_then_recovers(provider) -> None:
    p = provider(request_timeout_s=0.5)
    p.recognize(b"ok")
    old = p.pid
    assert old is not None
    started = time.monotonic()
    with pytest.raises(OcrUnavailable) as info:
        p.recognize(b"x SLEEP30")
    assert info.value.reason == "worker_timeout"
    assert time.monotonic() - started < 15
    assert _wait_dead(old)
    assert _text(p.recognize(b"fine")).startswith("fine|")


def test_start_failure_is_unavailable(fake_command) -> None:
    p = OcrProcessProvider(".", command=fake_command("die-at-start"), start_timeout_s=30)
    with pytest.raises(OcrUnavailable) as info:
        p.recognize(b"x")
    assert info.value.reason == "worker_crashed"
    assert p.pid is None
    p.close()


def test_invalid_image_keeps_worker(provider) -> None:
    p = provider()
    p.recognize(b"ok")
    pid = p.pid
    with pytest.raises(InvalidImageError):
        p.recognize(b"BAD")
    assert p.pid == pid and p.health() is None


def test_concurrent_requests_are_serialized_without_crosstalk(provider) -> None:
    p = provider()
    results: dict[int, str] = {}

    def run(i: int) -> None:
        results[i] = _text(p.recognize(f"req-{i:02d}".encode()))

    threads = [threading.Thread(target=run, args=(i,)) for i in range(12)]
    for t in threads:
        t.start()
    for t in threads:
        t.join(30)
    assert len(results) == 12
    assert all(text.startswith(f"req-{i:02d}|") for i, text in results.items())
    assert len({text.rsplit("|", 1)[1] for text in results.values()}) == 1  # 同一个子进程
    assert p.starts == 1


def test_real_worker_reports_missing_models_and_exits(tmp_path: Path) -> None:
    """真实的 ocr-worker 子命令：缺模型/缺依赖时原因与进程内一致，子进程退出。"""

    p = OcrProcessProvider(tmp_path, start_timeout_s=120)
    try:
        with pytest.raises(OcrUnavailable) as info:
            p.warmup()
        assert info.value.reason in ("models_missing", "dependency_missing")
        assert p.health()["reason"] == info.value.reason  # type: ignore[index]
        assert p.pid is None
    finally:
        p.close()


def test_worker_exits_when_parent_is_killed(fake_command, tmp_path: Path) -> None:
    """主进程被强杀（不走任何清理）后子进程也退出，不留孤儿。"""

    script = tmp_path / "parent.py"
    script.write_text(
        "import os, sys, time\n"
        "from suiyi_engine.ocr_worker import OcrProcessProvider\n"
        f"worker = {str(tmp_path / 'fake_worker.py')!r}\n"
        "p = OcrProcessProvider('.', command=[sys.executable, worker, str(os.getpid())])\n"
        "p.recognize(b'hi')\n"
        "print(p.pid, flush=True)\n"
        "time.sleep(120)\n",
        encoding="utf-8",
    )
    env = dict(os.environ)
    src = str(Path(__file__).resolve().parents[1] / "src")
    env["PYTHONPATH"] = os.pathsep.join([src, env.get("PYTHONPATH", "")])
    parent = subprocess.Popen(
        [sys.executable, str(script)], stdout=subprocess.PIPE, env=env, text=True
    )
    try:
        line = parent.stdout.readline() if parent.stdout else ""
        child = int(line.strip())
        assert _alive(child)
    finally:
        parent.kill()
        parent.wait(10)
    assert _wait_dead(child, 20), f"OCR 子进程 {child} 在主进程被杀后仍在运行"


# ---- HTTP ------------------------------------------------------------------------


def _png(tail: bytes = b"") -> bytes:
    """合法的 64x32 灰度 PNG，末尾追加的字节告诉假子进程怎么做（接口只检查文件头与 IHDR）。"""

    def chunk(kind: bytes, data: bytes) -> bytes:
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    ihdr = struct.pack(">IIBBBBB", 64, 32, 8, 0, 0, 0, 0)
    pixels = zlib.compress(b"".join(b"\x00" + b"\xff" * 64 for _ in range(32)))
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", pixels)
    return png + chunk(b"IEND", b"") + tail


def test_http_ocr_health_and_crash_503(provider) -> None:
    p = provider()
    app = create_app(_translator(), None, ApiSettings(), p)
    with TestClient(app) as client:
        health = client.get("/health").json()
        assert health["ocr_loaded"] is False and health["ocr_worker_state"] == "stopped"
        assert health["ocr_worker_pid"] is None
        ok = client.post("/ocr", content=_png(b"hello"), headers={"Content-Type": "image/png"})
        assert ok.status_code == 200, ok.text
        body = ok.json()
        assert set(body) == {"lines", "paragraphs", "text", "image", "elapsed_ms"}
        health = client.get("/health").json()
        assert health["ocr_loaded"] is True and health["ocr_worker_state"] == "ready"
        assert health["ocr_worker_pid"] == p.pid
        crash = client.post("/ocr", content=_png(b"CRASH"), headers={"Content-Type": "image/png"})
        assert crash.status_code == 503
        assert crash.json()["error"]["code"] == "ocr_unavailable"
        assert crash.json()["error"]["details"]["reason"] == "worker_crashed"
        health = client.get("/health").json()
        assert health["ocr_error"]["reason"] == "worker_crashed"
        assert health["ocr_loaded"] is False and health["ocr_worker_state"] == "stopped"
        again = client.post("/ocr", content=_png(b"again"), headers={"Content-Type": "image/png"})
        assert again.status_code == 200
        assert client.get("/health").json()["ocr_error"] is None


def _translator():
    from suiyi_engine.translator import Translator

    return Translator(Path("does-not-exist"))


# ---- 真实模型 ----------------------------------------------------------------------


def _real_models() -> Path:
    raw = os.environ.get("SUIYI_MODELS_DIR", "").strip()
    required = os.environ.get("SUIYI_OCR_TESTS_REQUIRED") == "1"
    try:
        import rapidocr  # noqa: F401

        from suiyi_engine.ocr import OcrModelsMissingError
        from suiyi_engine.tools.ocr_models import (
            default_manifest_path,
            load_manifest,
            local_model_paths,
        )

        if not raw:
            raise OcrModelsMissingError(("SUIYI_MODELS_DIR",), Path("."))
        local_model_paths(Path(raw), load_manifest(default_manifest_path()))
    except Exception as exc:
        if required:
            raise
        pytest.skip(f"没有 OCR 依赖或模型：{exc}")
    return Path(raw)


def test_real_worker_matches_in_process_and_keeps_main_process_light(tmp_path: Path) -> None:
    models = _real_models()
    image = Path(__file__).parent / "fixtures" / "ocr" / "zh_web_01.png"
    code = (
        "import json, sys\n"
        "from pathlib import Path\n"
        "from fastapi.testclient import TestClient\n"
        "from suiyi_engine.api import ApiSettings, create_app\n"
        "from suiyi_engine.ocr_worker import OcrProcessProvider\n"
        "from suiyi_engine.translator import Translator\n"
        "models, image = Path(sys.argv[1]), Path(sys.argv[2]).read_bytes()\n"
        "p = OcrProcessProvider(models)\n"
        "app = create_app(Translator(models), None, ApiSettings(), p)\n"
        "with TestClient(app) as c:\n"
        "    r = c.post('/ocr', content=image, headers={'Content-Type': 'image/png'})\n"
        "    health = c.get('/health').json()\n"
        "heavy = [m for m in ('onnxruntime', 'cv2', 'numpy', 'rapidocr') if m in sys.modules]\n"
        "body = r.json(); body.pop('elapsed_ms', None)\n"
        "pid, state = health['ocr_worker_pid'], health['ocr_worker_state']\n"
        "print(json.dumps({'status': r.status_code, 'body': body, 'heavy': heavy,\n"
        "                  'pid': pid, 'state': state}))\n"
        "p.close()\n"
    )
    out = subprocess.run(
        [sys.executable, "-c", code, str(models), str(image)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=600,
        check=True,
    )
    report = json.loads(out.stdout.strip().splitlines()[-1])
    assert report["status"] == 200, report
    assert report["heavy"] == []  # 主进程没有导入 onnxruntime / OpenCV / numpy
    assert report["state"] == "ready" and isinstance(report["pid"], int)

    from suiyi_engine.ocr_provider import OcrProvider

    expected = OcrProvider(models).recognize(image.read_bytes()).to_dict()
    expected.pop("elapsed_ms")
    assert json.dumps(report["body"], ensure_ascii=False, sort_keys=True) == json.dumps(
        expected, ensure_ascii=False, sort_keys=True
    )  # 与进程内识别逐字节相同


# ---------------------------------------------------------------- 子进程空闲整理堆


def test_idle_trimmer_trims_once_after_quiet_period() -> None:
    from suiyi_engine.ocr_worker import _IdleTrimmer

    calls: list[float] = []
    trimmer = _IdleTrimmer(lambda: calls.append(time.monotonic()), after_s=0.05)
    time.sleep(0.2)
    assert calls == []  # 还没处理过请求，不整理
    trimmer.done()
    time.sleep(0.4)
    assert len(calls) == 1  # 安静后整理一次，之后不重复
    trimmer.done()
    time.sleep(0.4)
    assert len(calls) == 2


def test_idle_trimmer_never_trims_while_request_in_progress() -> None:
    from suiyi_engine.ocr_worker import _IdleTrimmer

    calls: list[int] = []
    trimmer = _IdleTrimmer(lambda: calls.append(1), after_s=0.05)
    trimmer.done()
    with trimmer.busy():  # 模拟下一次识别正在进行
        time.sleep(0.4)
        assert calls == []
        trimmer.done()
    time.sleep(0.4)
    assert len(calls) == 1


def test_run_worker_trims_after_request() -> None:
    import io

    from suiyi_engine.ocr_worker import read_frame, run_worker, write_frame

    class Provider:
        def warmup(self) -> float:
            return 1.0

    request = io.BytesIO()
    write_frame(request, {"op": "warmup"})
    request.seek(0)
    reply = io.BytesIO()
    calls: list[int] = []
    assert run_worker(Provider(), stdin=request, stdout=reply, trimmer=lambda: calls.append(1),
                      trim_after_s=0.05) == 0  # fmt: skip
    reply.seek(0)
    assert read_frame(reply)[0]["op"] == "hello"  # type: ignore[index]
    assert read_frame(reply)[0] == {"ok": True, "ms": 1.0}  # type: ignore[index]
    time.sleep(0.4)
    assert calls == [1]
