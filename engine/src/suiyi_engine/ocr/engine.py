"""RapidOCR 适配层：本地模型、懒加载、线程安全、低置信度标记（#52）。

- 只用 ``<models_dir>/ocr/`` 下按清单校验过的本地文件，每个阶段都显式给 ``model_path``，
  RapidOCR 不会走下载分支；缺模型时在导入 rapidocr 之前就抛 :class:`OcrModelsMissingError`。
- 图片由本模块自己解码成 BGR 数组再交给 RapidOCR，从不把字符串交给它（RapidOCR 会把
  ``http`` 开头的字符串当 URL 去下载）。
- 模型只加载一次；并发调用串行执行（onnxruntime 内部已经多线程，串行不损失吞吐，
  也避免 RapidOCR 前后处理里的共享状态）。
"""

from __future__ import annotations

import io
import os
import threading
import time
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import TYPE_CHECKING, Any

from suiyi_engine.ocr.layout import DEFAULT_OPTIONS, LayoutOptions, merge_paragraphs
from suiyi_engine.ocr.types import OcrLine, OcrResult, Point
from suiyi_engine.tools.ocr_models import (
    OcrManifest,
    OcrModelError,
    OcrModelsMissingError,
    default_manifest_path,
    load_manifest,
    local_model_paths,
    rapidocr_params,
)

if TYPE_CHECKING:
    import numpy as np

__all__ = [
    "DEFAULT_MIN_SCORE",
    "OCR_LANGS",
    "Backend",
    "BackendFactory",
    "ImageTooLargeError",
    "InvalidImageError",
    "OcrEngine",
    "OcrError",
    "OcrModelError",
    "OcrModelsMissingError",
    "decode_image",
]

DEFAULT_MIN_SCORE = 0.5
"""低于该置信度的行标记为 ``low_confidence``，保留在 ``lines`` 里但不进入段落。"""

OCR_LANGS = ("auto", "zh", "en", "ja")
"""接受的语言提示。当前是一个中英日共用的识别模型，提示只做校验，不改变识别行为。"""

RawLine = tuple[Sequence[Sequence[float]], str, float]
Backend = Callable[["np.ndarray"], list[RawLine]]
"""输入 BGR ``uint8`` 数组 (H, W, 3)，返回 ``[(四点框, 文本, 置信度), ...]``。"""
BackendFactory = Callable[[dict[str, Path], OcrManifest, int], Backend]
"""``(本地模型路径, 清单, 线程数) -> Backend``。测试用它注入假后端。"""

ImageInput = bytes | bytearray | memoryview | Path | str | Any


class OcrError(Exception):
    """OCR 调用错误（图片、参数、依赖）。模型缺失/损坏见 :class:`OcrModelError`。"""


class InvalidImageError(OcrError):
    """图片无法解码或尺寸为 0。"""


class ImageTooLargeError(InvalidImageError):
    """图片像素数超过 ``max_pixels``（只读图片头判断，不解码像素）。"""


def default_threads() -> int:
    """onnxruntime 线程数：最多 4 个，给界面和翻译留出核心。"""

    return max(1, min(4, os.cpu_count() or 1))


class OcrEngine:
    """一个进程共用一个实例：``recognize`` 可在多个线程里并发调用。"""

    def __init__(
        self,
        models_dir: Path,
        *,
        manifest: OcrManifest | None = None,
        min_score: float = DEFAULT_MIN_SCORE,
        threads: int | None = None,
        max_pixels: int | None = None,
        layout: LayoutOptions = DEFAULT_OPTIONS,
        backend_factory: BackendFactory | None = None,
    ) -> None:
        if not 0.0 <= min_score <= 1.0:
            raise ValueError(f"min_score 必须在 0~1 之间：{min_score}")
        self.models_dir = Path(models_dir)
        self.manifest = manifest if manifest is not None else load_manifest(default_manifest_path())
        self.min_score = min_score
        self.threads = threads if threads is not None else default_threads()
        self.max_pixels = max_pixels
        self.layout = layout
        self._factory = backend_factory or rapidocr_backend
        self._backend: Backend | None = None
        self._load_lock = threading.Lock()
        self._run_lock = threading.Lock()

    @property
    def loaded(self) -> bool:
        return self._backend is not None

    def check(self) -> dict[str, Path]:
        """只校验本地模型（存在、大小、SHA256），不导入 rapidocr。

        缺失或损坏时抛 :class:`OcrModelsMissingError`。
        """

        return local_model_paths(self.models_dir, self.manifest)

    def load(self) -> Backend:
        """加载模型（只做一次，并发调用者等待同一次加载）。"""

        backend = self._backend
        if backend is not None:
            return backend
        with self._load_lock:
            if self._backend is None:
                paths = self.check()
                self._backend = self._factory(paths, self.manifest, self.threads)
            return self._backend

    def warmup(self) -> None:
        """加载模型并跑一次小图，让 onnxruntime 完成首次分配。"""

        import numpy as np

        backend = self.load()
        blank = np.full((48, 160, 3), 255, dtype=np.uint8)
        with self._run_lock:
            backend(blank)

    def recognize(self, image: ImageInput, *, lang: str = "auto") -> OcrResult:
        """识别一张图片。``image`` 可以是编码后的字节、文件路径、PIL 图片或 BGR 数组。"""

        if lang not in OCR_LANGS:
            raise OcrError(f"不支持的语言提示 {lang!r}，可选：{', '.join(OCR_LANGS)}")
        started = time.perf_counter()
        backend = self.load()  # 先查模型：缺模型时不必解码图片
        array = decode_image(image, max_pixels=self.max_pixels)
        height, width = int(array.shape[0]), int(array.shape[1])
        loaded = time.perf_counter()
        with self._run_lock:
            raw = backend(array)
        recognized = time.perf_counter()
        lines = tuple(self._to_line(item) for item in raw)
        paragraphs = tuple(merge_paragraphs(lines, self.layout))
        done = time.perf_counter()
        return OcrResult(
            lines=lines,
            paragraphs=paragraphs,
            width=width,
            height=height,
            elapsed_ms=1000 * (done - started),
            stats={
                "load_decode_ms": 1000 * (loaded - started),
                "ocr_ms": 1000 * (recognized - loaded),
                "layout_ms": 1000 * (done - recognized),
            },
        )

    def _to_line(self, item: RawLine) -> OcrLine:
        box, text, score = item
        points = tuple((float(p[0]), float(p[1])) for p in box)
        if len(points) != 4:
            raise OcrError(f"OCR 后端返回的框不是四个点：{box!r}")
        quad: tuple[Point, Point, Point, Point] = (points[0], points[1], points[2], points[3])
        score = float(score)
        return OcrLine(text=str(text), box=quad, score=score, low_confidence=score < self.min_score)


def rapidocr_backend(paths: dict[str, Path], manifest: OcrManifest, threads: int) -> Backend:
    """默认后端：RapidOCR + onnxruntime，参数来自 :func:`rapidocr_params`。"""

    try:
        from rapidocr import RapidOCR
    except ImportError as exc:
        raise OcrError('未安装 OCR 依赖：请执行 pip install -e "engine[ocr]"') from exc

    params = rapidocr_params(paths, manifest)
    params["Global.text_score"] = 0.0  # 低置信度由 OcrEngine 标记，不让 RapidOCR 静默丢掉
    params["EngineConfig.onnxruntime.intra_op_num_threads"] = threads
    params["EngineConfig.onnxruntime.inter_op_num_threads"] = 1
    engine = RapidOCR(params=params)

    def run(image: np.ndarray) -> list[RawLine]:
        result = engine(image)
        if result.boxes is None or result.txts is None or result.scores is None:
            return []
        return [
            (box.tolist(), text, float(score))
            for box, text, score in zip(result.boxes, result.txts, result.scores, strict=True)
        ]

    return run


def decode_image(image: ImageInput, *, max_pixels: int | None = None) -> np.ndarray:
    """解码成 BGR ``uint8`` 数组 (H, W, 3)。带透明通道的图片合成到白底上。

    ``str`` 一律当本地路径，不会当 URL。
    """

    import numpy as np

    if isinstance(image, np.ndarray):
        return _check_array(image, max_pixels)

    if isinstance(image, str | Path | bytes | bytearray | memoryview):
        if isinstance(image, str | Path):
            try:
                data = Path(image).read_bytes()
            except OSError as exc:
                raise InvalidImageError(f"无法读取图片文件 {image}：{exc}") from exc
        else:
            data = bytes(image)
        if not data:
            raise InvalidImageError("图片为空")
        from PIL import Image, UnidentifiedImageError

        try:
            pil = Image.open(io.BytesIO(data))
        except (UnidentifiedImageError, Image.DecompressionBombError, OSError, ValueError) as exc:
            raise InvalidImageError(f"无法识别的图片格式：{exc}") from exc
    elif type(image).__module__.startswith("PIL."):
        from PIL import Image

        pil = image
    else:
        raise InvalidImageError(f"不支持的图片类型：{type(image).__name__}")

    width, height = pil.size
    _check_size(width, height, max_pixels)
    try:
        if pil.mode in ("RGBA", "LA", "PA") or (pil.mode == "P" and "transparency" in pil.info):
            rgba = pil.convert("RGBA")
            canvas = Image.new("RGB", rgba.size, (255, 255, 255))
            canvas.paste(rgba, mask=rgba.getchannel("A"))
            rgb = canvas
        else:
            rgb = pil.convert("RGB")
    except (OSError, ValueError, Image.DecompressionBombError) as exc:
        raise InvalidImageError(f"图片解码失败：{exc}") from exc
    return np.ascontiguousarray(np.asarray(rgb, dtype=np.uint8)[:, :, ::-1])


def _check_size(width: int, height: int, max_pixels: int | None) -> None:
    if width <= 0 or height <= 0:
        raise InvalidImageError(f"图片尺寸无效：{width}x{height}")
    if max_pixels is not None and width * height > max_pixels:
        raise ImageTooLargeError(f"图片 {width}x{height} 超过 {max_pixels} 像素上限")


def _check_array(array: np.ndarray, max_pixels: int | None) -> np.ndarray:
    import numpy as np

    if array.dtype != np.uint8:
        raise InvalidImageError(f"数组必须是 uint8，实际是 {array.dtype}")
    if array.ndim == 2:
        array = np.repeat(array[:, :, None], 3, axis=2)
    if array.ndim != 3 or array.shape[2] not in (3, 4):
        raise InvalidImageError(f"数组形状必须是 (H, W)、(H, W, 3) 或 (H, W, 4)：{array.shape}")
    _check_size(int(array.shape[1]), int(array.shape[0]), max_pixels)
    if array.shape[2] == 4:
        bgr = array[:, :, :3].astype(np.float32)
        alpha = array[:, :, 3:4].astype(np.float32) / 255.0
        array = (bgr * alpha + 255.0 * (1.0 - alpha)).round().astype(np.uint8)
    return np.ascontiguousarray(array)
