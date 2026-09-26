"""进程内存整理（#92）：分配器设置、把空闲堆还给系统。

CTranslate2 加载和解码时的临时缓冲释放后，glibc 通常不马上还给系统，多线程时还会按线程开很多个
arena，常驻内存（RSS）因此比实际占用高几十 MiB。本模块只做两件事：

- :func:`configure_allocator`：在加载 CTranslate2 之前调用。glibc 上把 arena 数限制为 1
  （翻译本来就串行）；没设置 ``MKL_DISABLE_FAST_MM`` 时设成 ``1``，关掉 MKL 的内存缓存。
  用户自己设了环境变量时不覆盖。
- :func:`trim`：glibc 上调用 ``malloc_trim(0)``，把空闲的堆内存还给系统。

两者在非 glibc 平台（Windows、macOS、musl）上都是空操作，只有 ``MKL_DISABLE_FAST_MM`` 各平台都设。
"""

from __future__ import annotations

import ctypes
import ctypes.util
import logging
import os
import sys
import threading
import time
from collections.abc import Callable
from typing import Protocol

logger = logging.getLogger(__name__)

DEFAULT_MALLOC_ARENAS = 1
_M_ARENA_MAX = -8  # glibc <malloc.h>

_libc: ctypes.CDLL | None = None
_libc_checked = False
_lock = threading.Lock()


def _glibc() -> ctypes.CDLL | None:
    global _libc, _libc_checked
    with _lock:
        if _libc_checked:
            return _libc
        _libc_checked = True
        if not sys.platform.startswith("linux"):
            return None
        try:
            libc = ctypes.CDLL(ctypes.util.find_library("c") or "libc.so.6")
            libc.gnu_get_libc_version  # noqa: B018 —— musl 没有这个符号
            libc.malloc_trim.argtypes = [ctypes.c_size_t]
            libc.malloc_trim.restype = ctypes.c_int
            libc.mallopt.argtypes = [ctypes.c_int, ctypes.c_int]
            libc.mallopt.restype = ctypes.c_int
        except (OSError, AttributeError):
            return None
        _libc = libc
        return libc


def configure_allocator(environ: dict[str, str] | None = None) -> dict[str, object]:
    """加载模型前调用一次。返回实际生效的设置，便于日志与测试。"""

    env = os.environ if environ is None else environ
    applied: dict[str, object] = {}
    if "MKL_DISABLE_FAST_MM" not in env:
        env["MKL_DISABLE_FAST_MM"] = "1"
    applied["mkl_disable_fast_mm"] = env["MKL_DISABLE_FAST_MM"]
    libc = _glibc()
    if libc is not None and "MALLOC_ARENA_MAX" not in env:
        applied["malloc_arena_max"] = (
            DEFAULT_MALLOC_ARENAS if libc.mallopt(_M_ARENA_MAX, DEFAULT_MALLOC_ARENAS) else None
        )
    return applied


def trim() -> bool:
    """把空闲堆内存还给系统。非 glibc 平台返回 ``False``。"""

    libc = _glibc()
    if libc is None:
        return False
    try:
        libc.malloc_trim(0)
    except OSError:  # pragma: no cover - 防御
        logger.debug("malloc_trim 调用失败", exc_info=True)
        return False
    return True


class _Registry(Protocol):
    def unload_idle(self, idle_s: float, *, now: float | None = None) -> list[str]: ...

    def last_activity(self) -> float | None: ...


class _Lock(Protocol):
    def acquire(self, blocking: bool = ..., timeout: float = ...) -> bool: ...

    def release(self) -> None: ...


DEFAULT_TRIM_AFTER_S = 2.0


class ModelJanitor:
    """``serve`` 的后台整理线程（#92）。

    每秒检查一次，只在拿得到翻译锁（此刻没有翻译在跑）时动手：

    - ``idle_unload_s > 0`` 时卸载超过这么久没用过的翻译模型；
    - 有过翻译、且已经安静 ``trim_after_s`` 秒时调用一次 :func:`trim`，把解码时的临时内存还给系统。

    翻译锁被占用时这一轮直接跳过，从不让请求排队等整理。
    """

    def __init__(
        self,
        registry: _Registry,
        lock: _Lock,
        idle_unload_s: float,
        *,
        trim_after_s: float = DEFAULT_TRIM_AFTER_S,
        interval_s: float = 1.0,
        clock: Callable[[], float] = time.monotonic,
        trimmer: Callable[[], bool] = trim,
    ) -> None:
        self.registry = registry
        self.lock = lock
        self.idle_unload_s = idle_unload_s
        self.trim_after_s = trim_after_s
        self.interval_s = interval_s
        self._clock = clock
        self._trimmer = trimmer
        self._trimmed_for: float | None = None
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def tick(self) -> list[str]:
        """做一轮整理，返回这一轮卸载的模型 id。"""

        if not self.lock.acquire(blocking=False):
            return []
        try:
            now = self._clock()
            unloaded: list[str] = []
            if self.idle_unload_s > 0:
                unloaded = self.registry.unload_idle(self.idle_unload_s, now=now)
                for model_id in unloaded:
                    logger.info("模型 %s 空闲超过 %g 秒，已卸载", model_id, self.idle_unload_s)
            last = self.registry.last_activity()
            quiet = last is not None and now - last >= self.trim_after_s
            if unloaded or (quiet and last != self._trimmed_for):
                self._trimmer()
                self._trimmed_for = last
            return unloaded
        finally:
            self.lock.release()

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="suiyi-janitor", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5)
            self._thread = None

    def _run(self) -> None:
        while not self._stop.wait(self.interval_s):
            try:
                self.tick()
            except Exception:  # 整理失败不能拖垮服务
                logger.exception("后台内存整理失败")
