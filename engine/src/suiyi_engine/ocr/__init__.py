"""OCR 核心：图片 → 文本行（框 + 置信度）→ 段落（#52）。

与 HTTP 解耦。导入本包不会加载 rapidocr / onnxruntime / numpy / PIL，第一次识别时才加载。
选型见 ``docs/engine/OCR选型与许可证.md``，数据结构与合并规则见 ``docs/engine/OCR核心.md``。
"""

from suiyi_engine.ocr.engine import (
    DEFAULT_MIN_SCORE,
    OCR_LANGS,
    InvalidImageError,
    OcrEngine,
    OcrError,
    OcrModelsMissingError,
)
from suiyi_engine.ocr.layout import LayoutOptions, is_vertical, join_lines, merge_paragraphs
from suiyi_engine.ocr.types import Box, OcrLine, OcrParagraph, OcrResult, Point

__all__ = [
    "DEFAULT_MIN_SCORE",
    "OCR_LANGS",
    "Box",
    "InvalidImageError",
    "LayoutOptions",
    "OcrEngine",
    "OcrError",
    "OcrLine",
    "OcrModelsMissingError",
    "OcrParagraph",
    "OcrResult",
    "Point",
    "is_vertical",
    "join_lines",
    "merge_paragraphs",
]
