"""RapidOCR 适配层：本地模型、懒加载、线程安全、低置信度标记（#52）。

- 只用 ``<models_dir>/ocr/`` 下按清单校验过的本地文件，每个阶段都显式给 ``model_path``，
  RapidOCR 不会走下载分支；缺模型时在导入 rapidocr 之前就抛 :class:`OcrModelsMissingError`。
- 图片由本模块自己解码成 BGR 数组再交给 RapidOCR，从不把字符串交给它（RapidOCR 会把
  ``http`` 开头的字符串当 URL 去下载）。
- 模型只加载一次；并发调用串行执行（onnxruntime 内部已经多线程，串行不损失吞吐，
  也避免 RapidOCR 前后处理里的共享状态）。
"""

from __future__ import annotations

import contextlib
import functools
import io
import os
import threading
import time
from collections.abc import Callable, Iterator, Sequence
from dataclasses import dataclass
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
    "OcrRuntimeOptions",
    "decode_image",
]

DEFAULT_MIN_SCORE = 0.5
"""低于该置信度的行标记为 ``low_confidence``，保留在 ``lines`` 里但不进入段落。"""

OCR_LANGS = ("auto", "zh", "en", "ja")
"""接受的语言提示。当前是一个中英日共用的识别模型，提示只做校验，不改变识别行为。"""


@dataclass(frozen=True)
class OcrRuntimeOptions:
    """RapidOCR / onnxruntime 运行参数（#74 调优后的默认值，依据见 ``docs/engine/OCR评测.md``）。

    - ``det_max_side``：检测前把长边缩到该像素数以内（只缩不放），识别仍从**原图**裁切，
      小字不受影响。``None`` 表示不缩放（RapidOCR 3.9 的原行为：≤2000 px 的截图按原尺寸检测）。
    - ``rec_batch``：识别批大小。批内按最宽的行补齐，批越大中间数据越大；1 最省内存，也最快。
    - ``mem_pattern``：onnxruntime ``enable_mem_pattern``。RapidOCR 不暴露该选项，
      关闭时在创建会话期间临时替换其会话选项构造函数（只影响本实例的会话）。
    - ``det_dilation``：检测后处理是否做 2×2 膨胀（RapidOCR 默认开）。
      缩小检测图时膨胀相对变大、行框变胖；``None`` 表示「缩放时关、不缩放时保持默认」。
    - ``box_shrink``：缩放补偿（检测图像素）。DB 后处理的外扩在检测图上是常数像素，
      换回原图后放大 ``1/scale`` 倍，段间空隙被吃掉、分段变差。返回给分段的行框沿短边
      每侧收回 ``box_shrink × (1/scale − 1)`` 原图像素（不缩放时为 0，行为不变）；
      识别裁切仍用未收缩的框，不影响 CER。
    - ``crop_pad``：缩放且关闭膨胀时，识别裁切框沿短边每侧外扩 ``crop_pad / scale`` 原图像素
      （检测图像素计），补回膨胀原本给的余量，避免下划线、下伸部被切掉。
    """

    det_max_side: int | None = 1024
    rec_batch: int = 1
    mem_pattern: bool = False
    det_dilation: bool | None = None
    box_shrink: float = 2.0
    crop_pad: float = 1.0

    def __post_init__(self) -> None:
        if self.det_max_side is not None and self.det_max_side < 320:
            raise ValueError(f"det_max_side 至少为 320：{self.det_max_side}")
        if self.rec_batch < 1:
            raise ValueError(f"rec_batch 至少为 1：{self.rec_batch}")
        if self.box_shrink < 0 or self.crop_pad < 0:
            raise ValueError(f"box_shrink / crop_pad 不能为负：{self.box_shrink} / {self.crop_pad}")


DEFAULT_RUNTIME = OcrRuntimeOptions()
LEGACY_RUNTIME = OcrRuntimeOptions(
    det_max_side=None,
    rec_batch=6,
    mem_pattern=True,
    det_dilation=True,
    box_shrink=0.0,
    crop_pad=0.0,
)
"""#74 之前的行为（RapidOCR 默认），供评测对比。"""

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
        runtime: OcrRuntimeOptions = DEFAULT_RUNTIME,
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
        self.runtime = runtime
        self._factory = backend_factory or functools.partial(rapidocr_backend, runtime=runtime)
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

    def unload(self) -> bool:
        """卸载模型（#96）。正在识别时不卸载，返回 ``False``；下一次 :meth:`load` 会重新加载。"""

        if not self._run_lock.acquire(blocking=False):
            return False
        try:
            with self._load_lock:
                if self._backend is None:
                    return False
                self._backend = None
                return True
        finally:
            self._run_lock.release()

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


def rapidocr_backend(
    paths: dict[str, Path],
    manifest: OcrManifest,
    threads: int,
    *,
    runtime: OcrRuntimeOptions = DEFAULT_RUNTIME,
) -> Backend:
    """默认后端：RapidOCR + onnxruntime，参数来自 :func:`rapidocr_params` 与 ``runtime``。

    RapidOCR 3.9 没有「只缩小检测图」的参数：``limit_type=max`` 时忽略 ``limit_side_len``、
    按原图长边选 960/1500/2000；``limit_type=min`` 只放大不缩小；
    ``Global.max_side_len`` 会连识别裁切一起缩小。
    所以这里把流程拆成公开的两步：先把缩小后的图交给 RapidOCR 只做检测（``use_rec=False``），
    框按比例换回原图坐标；再从原图裁切，交给 RapidOCR 的识别器（``text_rec``）。
    """

    try:
        import cv2
        import numpy as np
        from rapidocr import RapidOCR
        from rapidocr.ch_ppocr_rec import TextRecInput
        from rapidocr.utils.process_img import get_rotate_crop_image
    except ImportError as exc:
        raise OcrError('未安装 OCR 依赖：请执行 pip install -e "engine[ocr]"') from exc

    params = rapidocr_params(paths, manifest)
    params["Global.text_score"] = 0.0  # 低置信度由 OcrEngine 标记，不让 RapidOCR 静默丢掉
    params["Global.use_rec"] = False  # 整体调用只做检测，识别在下面单独调用
    params["Global.use_cls"] = False
    params["EngineConfig.onnxruntime.intra_op_num_threads"] = threads
    params["EngineConfig.onnxruntime.inter_op_num_threads"] = 1
    params["EngineConfig.onnxruntime.enable_cpu_mem_arena"] = False
    params["Rec.rec_batch_num"] = runtime.rec_batch
    use_cls = manifest.use_cls
    with _session_options(mem_pattern=runtime.mem_pattern):
        engine = RapidOCR(params=params)
    post = engine.text_det.postprocess_op
    default_kernel = post.dilation_kernel

    def run(image: np.ndarray) -> list[RawLine]:
        height, width = image.shape[:2]
        limit = runtime.det_max_side
        scale = 1.0
        det_image = image
        if limit is not None and max(height, width) > limit:
            scale = limit / max(height, width)
            size = (max(1, round(width * scale)), max(1, round(height * scale)))
            det_image = cv2.resize(image, size, interpolation=cv2.INTER_AREA)
        dilation = runtime.det_dilation if runtime.det_dilation is not None else scale == 1.0
        post.dilation_kernel = default_kernel if dilation else None
        det = engine(det_image)
        boxes = getattr(det, "boxes", None)
        if boxes is None or len(boxes) == 0:
            return []
        boxes = np.asarray(boxes, dtype=np.float32)
        if scale != 1.0:
            boxes = boxes / np.float32(scale)
        boxes[..., 0] = np.clip(boxes[..., 0], 0, width - 1)
        boxes[..., 1] = np.clip(boxes[..., 1], 0, height - 1)
        boxes = merge_touching_boxes(boxes)
        pad = runtime.crop_pad / scale if scale != 1.0 and not dilation else 0.0
        crops = [get_rotate_crop_image(image, _crop_box(box, pad, width, height)) for box in boxes]
        if use_cls:
            crops = list(engine.text_cls(crops).img_list)
        rec = engine.text_rec(TextRecInput(img=crops))
        if rec.txts is None or rec.scores is None:
            return []
        inset = runtime.box_shrink * (1.0 / scale - 1.0)
        return [
            (shrink_box(box, inset, text).tolist(), text, float(score))
            for box, text, score in zip(boxes, rec.txts, rec.scores, strict=True)
            if text.strip()
        ]

    return run


def merge_touching_boxes(boxes: np.ndarray) -> np.ndarray:
    """把同一行上相接或重叠的横排框合成一个再识别（#75）。

    关闭膨胀后，检测偶尔把一个词切成相互重叠的两个框（代码里的 ``return ""``），
    引号单独识别会出错（``" I``）。只合并近似水平、非竖排、左右间距 ≤ 0.25 × 行高、
    上下重叠 ≥ 0.7 × 行高的框；有明显空隙的碎片（菜单项之间）不动，交给分段合成一行。
    """

    import numpy as np

    boxes = np.asarray(boxes, dtype=np.float32)
    if len(boxes) < 2:
        return boxes
    rects = []
    for quad in boxes:
        x0, y0 = quad[:, 0].min(), quad[:, 1].min()
        x1, y1 = quad[:, 0].max(), quad[:, 1].max()
        tilt = abs(float(quad[1, 1] - quad[0, 1]))
        h = y1 - y0
        flat = h > 0 and tilt <= 0.2 * h and h < 1.5 * (x1 - x0)
        rects.append([float(x0), float(y0), float(x1), float(y1), flat])
    merged = True
    while merged:
        merged = False
        for i in range(len(rects)):
            a = rects[i]
            if a is None or not a[4]:
                continue
            for j in range(i + 1, len(rects)):
                b = rects[j]
                if b is None or not b[4]:
                    continue
                h = min(a[3] - a[1], b[3] - b[1])
                gap = max(b[0] - a[2], a[0] - b[2])
                overlap = min(a[3], b[3]) - max(a[1], b[1])
                if gap <= 0.25 * h and overlap >= 0.7 * h:
                    rects[i] = a = [
                        min(a[0], b[0]),
                        min(a[1], b[1]),
                        max(a[2], b[2]),
                        max(a[3], b[3]),
                        True,
                    ]
                    rects[j] = None
                    merged = True
    if all(r is not None for r in rects):
        return boxes
    out = [_rect_quad(r, q) for q, r in zip(boxes, rects, strict=True) if r is not None]
    return np.stack(out).astype(np.float32)


def _rect_quad(rect: list[Any], quad: np.ndarray) -> np.ndarray:
    import numpy as np

    x0, y0, x1, y1 = rect[:4]
    original = (
        float(quad[:, 0].min()),
        float(quad[:, 1].min()),
        float(quad[:, 0].max()),
        float(quad[:, 1].max()),
    )
    if (x0, y0, x1, y1) == original:
        return quad  # 没参与合并：保留原来的四点框（可能略有倾斜）
    return np.array([[x0, y0], [x1, y0], [x1, y1], [x0, y1]], dtype=np.float32)


def _crop_box(box: np.ndarray, pad: float, width: int, height: int) -> np.ndarray:
    """识别裁切用的框：沿短边外扩 ``pad`` 像素并限制在图内（``pad=0`` 时原样复制）。"""

    import numpy as np

    out = np.asarray(box, dtype=np.float32).copy()
    if pad > 0:
        across = out[1] - out[0]
        down = out[3] - out[0]
        across_len = float(np.hypot(*across))
        down_len = float(np.hypot(*down))
        if across_len > 0 and down_len > 0:
            if down_len >= 1.5 * across_len:  # 竖排列：外扩左右
                step = across / across_len * pad
                out[[0, 3]] -= step
                out[[1, 2]] += step
            else:
                step = down / down_len * pad
                out[[0, 1]] -= step
                out[[2, 3]] += step
        out[:, 0] = np.clip(out[:, 0], 0, width - 1)
        out[:, 1] = np.clip(out[:, 1], 0, height - 1)
    return out


def shrink_box(box: np.ndarray, inset: float, text: str) -> np.ndarray:
    """把四点框（左上、右上、右下、左下）沿短边方向每侧收回 ``inset`` 像素。

    竖排（高 ≥ 1.5 倍宽且至少两个字，与分段的竖排判断一致）收左右，其余收上下；
    每侧最多收掉该方向长度的 30%，避免把细框收没。
    """

    if inset <= 0:
        return box
    import numpy as np

    pts = np.asarray(box, dtype=np.float32).copy()
    across = pts[1] - pts[0]  # 沿文字行方向
    down = pts[3] - pts[0]  # 垂直于文字行
    across_len = float(np.hypot(*across))
    down_len = float(np.hypot(*down))
    if across_len <= 0 or down_len <= 0:
        return pts
    vertical = down_len >= 1.5 * across_len and len(text.strip()) >= 2
    if vertical:
        step = across / across_len * min(inset, 0.3 * across_len)
        pts[[0, 3]] += step
        pts[[1, 2]] -= step
    else:
        step = down / down_len * min(inset, 0.3 * down_len)
        pts[[0, 1]] += step
        pts[[2, 3]] -= step
    return pts


@contextlib.contextmanager
def _session_options(*, mem_pattern: bool) -> Iterator[None]:
    """在创建 RapidOCR 会话期间调整 onnxruntime 会话选项（RapidOCR 只暴露了 arena 与线程数）。"""

    if mem_pattern:
        yield
        return
    from rapidocr.inference_engine.onnxruntime import main as ort_main

    original = ort_main.OrtInferSession.__dict__["_init_sess_opts"]
    build = original.__func__

    def init_opts(cfg: Any) -> Any:
        options = build(cfg)
        options.enable_mem_pattern = False
        return options

    with _SESSION_PATCH_LOCK:
        ort_main.OrtInferSession._init_sess_opts = staticmethod(init_opts)
        try:
            yield
        finally:
            ort_main.OrtInferSession._init_sess_opts = original


_SESSION_PATCH_LOCK = threading.Lock()


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
