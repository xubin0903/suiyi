"""OCR 提供者（进程内）与不可用错误（#53；#104 从 ``api_ocr`` 拆出）。

本模块不导入 FastAPI，OCR 子进程（:mod:`suiyi_engine.ocr_worker`）也用它：子进程里的
:class:`OcrProvider` 把依赖/清单/模型问题映射成 :class:`OcrUnavailable`，与进程内时的错误原因、
提示文字完全一致。导入本模块不加载 rapidocr / onnxruntime / numpy。
"""

from __future__ import annotations

import threading
import time
from collections.abc import Callable
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from suiyi_engine.ocr import OcrEngine, OcrResult

INSTALL_HINT = '请安装 OCR 依赖（pip install -e "engine[ocr]"）'
DOWNLOAD_HINT = "请执行 python scripts/download_ocr_models.py download 下载 OCR 模型"


class OcrUnavailable(Exception):
    """OCR 依赖未安装、清单缺失或模型缺失/损坏。对应 503 ``ocr_unavailable``。"""

    def __init__(self, message: str, *, reason: str, missing_models: tuple[str, ...] = ()) -> None:
        super().__init__(message)
        self.reason = reason
        self.missing_models = missing_models

    def details(self) -> dict[str, object]:
        return {"reason": self.reason, "missing_models": list(self.missing_models)}


class OcrProvider:
    """懒加载 :class:`OcrEngine`。失败不缓存：补齐模型后下一次请求就能用，不用重启服务。"""

    def __init__(
        self,
        models_dir: Path | str,
        *,
        engine_factory: Callable[[], OcrEngine] | None = None,
    ) -> None:
        self.models_dir = Path(models_dir)
        self._factory = engine_factory
        self._engine: OcrEngine | None = None
        self._lock = threading.Lock()
        self._last_used: float | None = None
        self.last_error: OcrUnavailable | None = None
        """最近一次加载失败的原因（``/health.ocr_error``）；加载成功后清空。"""

    @property
    def loaded(self) -> bool:
        engine = self._engine
        return engine is not None and engine.loaded

    def engine(self) -> OcrEngine:
        """返回已加载模型的引擎；不可用时抛 :class:`OcrUnavailable` 并记到 :attr:`last_error`。"""

        try:
            engine = self._engine_or_raise()
        except OcrUnavailable as exc:
            self.last_error = exc
            raise
        self.last_error = None
        self.touch()
        return engine

    def touch(self) -> None:
        """记一次使用（空闲卸载按最后一次使用计时，#96）。"""

        self._last_used = time.monotonic()

    def last_activity(self) -> float | None:
        """最近一次使用 OCR 的 ``time.monotonic()`` 时刻；从没用过时为 ``None``。"""

        return self._last_used

    def unload_idle(self, idle_s: float, *, now: float | None = None) -> bool:
        """OCR 超过 ``idle_s`` 秒没用过就卸载模型（#96）。正在识别时不卸载。返回是否卸载了。"""

        engine = self._engine
        last = self._last_used
        if engine is None or not engine.loaded or last is None:
            return False
        current = time.monotonic() if now is None else now
        if current - last < idle_s:
            return False
        return bool(engine.unload())

    def health(self) -> dict[str, object] | None:
        """``/health.ocr_error``：最近一次加载失败的原因，没有失败（或尚未尝试）时为 ``None``。"""

        error = self.last_error
        if error is None:
            return None
        return {"message": str(error), **error.details()}

    def _engine_or_raise(self) -> OcrEngine:
        from suiyi_engine.ocr import OcrEngine, OcrError, OcrModelError, OcrModelsMissingError

        with self._lock:
            if self._engine is None:
                try:
                    if self._factory is not None:
                        self._engine = self._factory()
                    else:
                        self._engine = OcrEngine(self.models_dir)
                except OcrModelError as exc:
                    raise OcrUnavailable(
                        f"OCR 模型清单不可用：{exc}", reason="manifest_unavailable"
                    ) from exc
            engine = self._engine
        try:
            engine.load()
        except OcrModelsMissingError as exc:
            raise OcrUnavailable(
                f"缺少 OCR 模型：{'、'.join(exc.missing)}（目录 {exc.ocr_dir}）。{DOWNLOAD_HINT}",
                reason="models_missing",
                missing_models=exc.missing,
            ) from exc
        except OcrModelError as exc:
            raise OcrUnavailable(f"OCR 模型不可用：{exc}", reason="models_invalid") from exc
        except OcrError as exc:
            raise OcrUnavailable(
                f"OCR 依赖未安装：{exc}。{INSTALL_HINT}", reason="dependency_missing"
            ) from exc
        return engine

    def warmup(self) -> float:
        """加载模型并预热，返回耗时毫秒；不可用时抛 :class:`OcrUnavailable`。"""

        started = time.perf_counter()
        self.engine().warmup()
        return 1000 * (time.perf_counter() - started)

    def recognize(self, data: bytes, lang: str = "auto") -> OcrResult:
        """识别一张 PNG。不可用时抛 :class:`OcrUnavailable`；解码失败抛 ``InvalidImageError``。"""

        engine = self.engine()
        try:
            return engine.recognize(data, lang=lang)
        finally:
            self.touch()

    def worker_health(self) -> dict[str, object]:
        """``/health`` 的 OCR 子进程字段（#104）。进程内 OCR 没有子进程。"""

        return {"ocr_worker_pid": None, "ocr_worker_state": "in_process"}

    def close(self) -> None:
        """服务退出时调用。进程内 OCR 随进程释放，这里什么都不做。"""
