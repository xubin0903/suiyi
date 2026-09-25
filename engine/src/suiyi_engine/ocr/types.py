"""OCR 结果的数据结构。坐标单位是原图像素，原点在左上角。"""

from __future__ import annotations

from dataclasses import dataclass, field

Point = tuple[float, float]
Box = tuple[float, float, float, float]
"""外接矩形 ``(x0, y0, x1, y1)``，``x0 <= x1``、``y0 <= y1``。"""


@dataclass(frozen=True)
class OcrLine:
    """一个文本行（竖排时是一列）。

    ``box`` 是四点框，顺序与 RapidOCR 一致：左上、右上、右下、左下（按文字方向）。
    ``low_confidence`` 为真表示置信度低于阈值，没有进入段落。
    """

    text: str
    box: tuple[Point, Point, Point, Point]
    score: float
    low_confidence: bool = False

    @property
    def rect(self) -> Box:
        xs = [p[0] for p in self.box]
        ys = [p[1] for p in self.box]
        return (min(xs), min(ys), max(xs), max(ys))

    def to_dict(self) -> dict[str, object]:
        return {
            "text": self.text,
            "box": [[round(x, 1), round(y, 1)] for x, y in self.box],
            "score": round(self.score, 4),
            "low_confidence": self.low_confidence,
        }


@dataclass(frozen=True)
class OcrParagraph:
    """合并后的段落。``line_indices`` 指向 :attr:`OcrResult.lines`，按段内阅读顺序排列。"""

    text: str
    box: Box
    line_indices: tuple[int, ...]
    vertical: bool = False

    def to_dict(self) -> dict[str, object]:
        return {
            "text": self.text,
            "box": [round(v, 1) for v in self.box],
            "line_indices": list(self.line_indices),
            "vertical": self.vertical,
        }


@dataclass(frozen=True)
class OcrResult:
    """一次识别的结果。``paragraphs`` 的顺序就是阅读顺序。"""

    lines: tuple[OcrLine, ...]
    paragraphs: tuple[OcrParagraph, ...]
    width: int
    height: int
    elapsed_ms: float
    stats: dict[str, float] = field(default_factory=dict)

    @property
    def text(self) -> str:
        """段落以 ``\\n`` 连接；识别为空时是空串。"""

        return "\n".join(p.text for p in self.paragraphs)

    @property
    def low_confidence_count(self) -> int:
        return sum(1 for line in self.lines if line.low_confidence)

    def to_dict(self) -> dict[str, object]:
        return {
            "lines": [line.to_dict() for line in self.lines],
            "paragraphs": [p.to_dict() for p in self.paragraphs],
            "text": self.text,
            "image": {"width": self.width, "height": self.height},
            "elapsed_ms": round(self.elapsed_ms, 1),
        }
