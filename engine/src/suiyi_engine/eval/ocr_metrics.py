"""OCR 评测指标：字符错误率（CER）与段落切分准确率（#54）。

纯 Python，不依赖模型，单测见 ``engine/tests/test_ocr_metrics.py``。

- **CER**：参考文本与识别文本先做 NFKC 归一（竖排标点等兼容字符折回常规字符、全角英数折半角），
  再去掉全部空白，然后算字符级编辑距离 / 参考长度。段落按阅读顺序拼接，所以阅读顺序错误也计入 CER。
  汇总时按字符数加权（micro 平均）：长样例权重更大。
- **段落切分**：把期望段落与识别段落各自拼成一串（归一化同上），期望串里每个段落边界是一个位置。
  识别串的段落边界通过字符对齐映射回期望串，在 ±``tolerance`` 个字符内算命中。
  漏掉的期望边界 = **误合并**（两段被并成一段），多出来的识别边界 = **误拆分**。
"""

from __future__ import annotations

import bisect
import difflib
import unicodedata
from collections.abc import Iterable, Sequence
from dataclasses import dataclass

__all__ = [
    "CerScore",
    "SegmentationScore",
    "cer",
    "edit_distance",
    "merge_cer",
    "merge_segmentation",
    "normalize_text",
    "segmentation",
]

DEFAULT_TOLERANCE = 2
"""段落边界的位置容差（字符数）。吸收边界附近一两个字的识别错误。"""


def normalize_text(text: str) -> str:
    """NFKC 归一并去掉全部空白。"""

    return "".join(unicodedata.normalize("NFKC", text).split())


def edit_distance(a: str, b: str) -> int:
    """Levenshtein 距离（插入、删除、替换代价都是 1）。"""

    if a == b:
        return 0
    if len(a) < len(b):
        a, b = b, a
    if not b:
        return len(a)
    previous = list(range(len(b) + 1))
    for i, ca in enumerate(a, start=1):
        current = [i]
        append = current.append
        for j, cb in enumerate(b, start=1):
            cost = previous[j - 1] + (ca != cb)
            insert = current[j - 1] + 1
            delete = previous[j] + 1
            append(min(cost, insert, delete))
        previous = current
    return previous[-1]


@dataclass(frozen=True)
class CerScore:
    edits: int
    ref_chars: int
    hyp_chars: int

    @property
    def cer(self) -> float:
        """编辑距离 / 参考字符数。参考为空时：识别也为空记 0，否则记 1。"""

        if self.ref_chars == 0:
            return 0.0 if self.hyp_chars == 0 else 1.0
        return self.edits / self.ref_chars


def cer(reference: str | Sequence[str], hypothesis: str | Sequence[str]) -> CerScore:
    """单个样例的 CER。字符串或段落列表都可以（列表按顺序拼接）。"""

    ref = normalize_text(_joined(reference))
    hyp = normalize_text(_joined(hypothesis))
    return CerScore(edits=edit_distance(ref, hyp), ref_chars=len(ref), hyp_chars=len(hyp))


def merge_cer(scores: Iterable[CerScore]) -> CerScore:
    """按字符数加权汇总（编辑数与参考字符数分别求和）。"""

    edits = ref = hyp = 0
    for score in scores:
        edits += score.edits
        ref += score.ref_chars
        hyp += score.hyp_chars
    return CerScore(edits=edits, ref_chars=ref, hyp_chars=hyp)


@dataclass(frozen=True)
class SegmentationScore:
    expected_paragraphs: int
    predicted_paragraphs: int
    matched: int
    """命中的边界数。"""
    merges: int
    """漏掉的期望边界：本该分开的两段被合并。"""
    splits: int
    """多出来的识别边界：本该连在一起的一段被拆开。"""
    samples: int = 1
    exact_samples: int = 0
    """边界全部命中且没有多余边界的样例数。"""

    @property
    def expected_boundaries(self) -> int:
        return self.matched + self.merges

    @property
    def predicted_boundaries(self) -> int:
        return self.matched + self.splits

    @property
    def precision(self) -> float:
        total = self.predicted_boundaries
        return 1.0 if total == 0 else self.matched / total

    @property
    def recall(self) -> float:
        total = self.expected_boundaries
        return 1.0 if total == 0 else self.matched / total

    @property
    def f1(self) -> float:
        p, r = self.precision, self.recall
        return 0.0 if p + r == 0 else 2 * p * r / (p + r)

    @property
    def exact_rate(self) -> float:
        return 0.0 if self.samples == 0 else self.exact_samples / self.samples


def segmentation(
    expected: Sequence[str],
    predicted: Sequence[str],
    *,
    tolerance: int = DEFAULT_TOLERANCE,
) -> SegmentationScore:
    """单个样例的段落边界得分。空段落（归一化后为空）会被忽略。"""

    exp = [p for p in (normalize_text(t) for t in expected) if p]
    pred = [p for p in (normalize_text(t) for t in predicted) if p]
    exp_bounds = _boundaries(exp)
    pred_bounds = _boundaries(pred)
    ref, hyp = "".join(exp), "".join(pred)
    mapped = sorted({_map_position(pos, hyp, ref, _blocks(hyp, ref)) for pos in pred_bounds})
    # 映射后重合的识别边界只算一次；落在串首/串尾的边界没有意义，按误拆分计。
    mapped_inner = [pos for pos in mapped if 0 < pos < len(ref)]
    dropped = len(pred_bounds) - len(mapped_inner)

    matched = 0
    used: set[int] = set()
    for boundary in exp_bounds:
        best: int | None = None
        for index, pos in enumerate(mapped_inner):
            if index in used or abs(pos - boundary) > tolerance:
                continue
            if best is None or abs(pos - boundary) < abs(mapped_inner[best] - boundary):
                best = index
        if best is not None:
            used.add(best)
            matched += 1
    merges = len(exp_bounds) - matched
    splits = len(mapped_inner) - matched + dropped
    return SegmentationScore(
        expected_paragraphs=len(exp),
        predicted_paragraphs=len(pred),
        matched=matched,
        merges=merges,
        splits=splits,
        samples=1,
        exact_samples=int(merges == 0 and splits == 0),
    )


def merge_segmentation(scores: Iterable[SegmentationScore]) -> SegmentationScore:
    exp = pred = matched = merges = splits = samples = exact = 0
    for score in scores:
        exp += score.expected_paragraphs
        pred += score.predicted_paragraphs
        matched += score.matched
        merges += score.merges
        splits += score.splits
        samples += score.samples
        exact += score.exact_samples
    return SegmentationScore(
        expected_paragraphs=exp,
        predicted_paragraphs=pred,
        matched=matched,
        merges=merges,
        splits=splits,
        samples=samples,
        exact_samples=exact,
    )


def _joined(value: str | Sequence[str]) -> str:
    return value if isinstance(value, str) else "\n".join(value)


def _boundaries(paragraphs: Sequence[str]) -> list[int]:
    """段落之间的位置（拼接串里的下标），不含 0 和串尾。"""

    bounds: list[int] = []
    total = 0
    for text in paragraphs[:-1]:
        total += len(text)
        bounds.append(total)
    return bounds


def _blocks(hyp: str, ref: str) -> list[tuple[int, int, int]]:
    matcher = difflib.SequenceMatcher(None, hyp, ref, autojunk=False)
    return [(b.a, b.b, b.size) for b in matcher.get_matching_blocks() if b.size]


def _map_position(pos: int, hyp: str, ref: str, blocks: list[tuple[int, int, int]]) -> int:
    """识别串里的位置 → 期望串里的位置。

    落在匹配块内（含块首/块尾）直接平移；落在两个匹配块之间的未对齐区域时按比例插值。
    """

    if not blocks:
        return round(pos * len(ref) / len(hyp)) if hyp else 0
    starts = [a for a, _b, _size in blocks]
    index = bisect.bisect_right(starts, pos) - 1
    if index >= 0:
        a, b, size = blocks[index]
        if pos <= a + size:
            return b + (pos - a)
        prev_hyp, prev_ref = a + size, b + size
    else:
        prev_hyp, prev_ref = 0, 0
    if index + 1 < len(blocks):
        next_hyp, next_ref = blocks[index + 1][0], blocks[index + 1][1]
    else:
        next_hyp, next_ref = len(hyp), len(ref)
    span = next_hyp - prev_hyp
    if span <= 0:
        return prev_ref
    return prev_ref + round((pos - prev_hyp) * (next_ref - prev_ref) / span)
