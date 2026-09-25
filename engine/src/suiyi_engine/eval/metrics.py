"""chrF、BLEU、专名保留率与按分类汇总。

chrF / BLEU 使用 sacrebleu（可选依赖组 ``eval``），不在运行时依赖里。
本模块在导入时不加载 sacrebleu。
"""

from __future__ import annotations

import math
from collections.abc import Mapping, Sequence
from dataclasses import dataclass

CATEGORY_ORDER = ("short", "colloquial", "proper_noun", "ocr_noise", "paragraph")
DIRECTION_ORDER = (
    ("zh", "en"),
    ("en", "zh"),
    ("zh", "ja"),
    ("ja", "zh"),
    ("en", "ja"),
    ("ja", "en"),
)

OCR_SCORED = "scored"
OCR_MISSING_CLEAN = "missing_clean"
OCR_EMPTY_REFERENCE = "empty_reference"


def direction_label(src_lang: str, tgt_lang: str) -> str:
    """``zh`` + ``en`` → ``zh→en``。"""

    return f"{src_lang}→{tgt_lang}"


def bleu_tokenize(tgt_lang: str, *, ja_mecab: bool) -> str:
    """目标语对应的 sacrebleu BLEU 分词器。

    中文用 ``zh``。日文优先 ``ja-mecab``，不可用时由调用方传入 ``ja_mecab=False``，
    回退 ``char``。其余语种用 sacrebleu 默认的 ``13a``。
    """

    if tgt_lang == "zh":
        return "zh"
    if tgt_lang == "ja":
        return "ja-mecab" if ja_mecab else "char"
    return "13a"


def probe_ja_mecab() -> tuple[bool, str]:
    """试跑一句日文 BLEU，判断 ``ja-mecab`` 能否用。

    失败时返回给报告用的说明，不抛异常。sacrebleu 官方额外依赖是 ``sacrebleu[ja]``
    （通常包含 mecab）。未安装时评测继续，日文 BLEU 回退 ``char``。
    """

    try:
        sacrebleu = _sacrebleu()
        sacrebleu.sentence_bleu(
            "これはテストです。",
            ["これはテストです。"],
            tokenize="ja-mecab",
        )
    except Exception as exc:
        return False, ja_mecab_failure_note(exc)
    return True, "日文 BLEU 使用 tokenize=ja-mecab"


def ja_mecab_failure_note(exc: BaseException) -> str:
    """把 ja-mecab 失败收成报告里的一行说明。"""

    detail = str(exc).strip().splitlines()[0] if str(exc).strip() else type(exc).__name__
    lowered = detail.lower()
    if "extra dependencies" in lowered or "mecab" in lowered:
        detail = "未安装 sacrebleu 的日文分词依赖"
    return f"ja-mecab 不可用（{detail}），日文 BLEU 回退 tokenize=char"


@dataclass(frozen=True, slots=True)
class MustKeepHit:
    """一条 ``must_keep`` 是否在译文中按 ``tgt`` 连续出现。"""

    src: str
    tgt: str
    kept: bool


@dataclass(frozen=True, slots=True)
class ScoredSample:
    """一条已经翻译并打分的样例。"""

    id: str
    src_lang: str
    tgt_lang: str
    category: str
    source: str
    reference: str
    hypothesis: str
    route: tuple[str, ...]
    route_label: str
    elapsed_ms: float
    chrf: float | None
    bleu: float | None
    hits: tuple[MustKeepHit, ...]
    clean_id: str | None
    ocr_chrf_delta: float | None
    ocr_status: str | None
    notes: str

    @property
    def direction(self) -> str:
        return direction_label(self.src_lang, self.tgt_lang)


@dataclass(frozen=True, slots=True)
class LatencyStats:
    """翻译耗时。分位数是线性插值，``max_ms`` 是最大值。"""

    p50_ms: float
    p95_ms: float
    max_ms: float
    n: int


@dataclass(frozen=True, slots=True)
class CorpusScores:
    """一组有参考译文的语料级 chrF / BLEU。分数是 sacrebleu 的 0–100 标度。"""

    chrf: float | None
    bleu: float | None
    n: int
    chrf_signature: str | None
    bleu_signature: str | None


@dataclass(frozen=True, slots=True)
class GroupMetrics:
    """一个方向或一个分类上的汇总。"""

    key: str
    n: int
    n_with_reference: int
    chrf: float | None
    bleu: float | None
    chrf_signature: str | None
    bleu_signature: str | None
    proper_kept: int
    proper_total: int
    proper_noun_retention: float | None
    latency: LatencyStats | None
    ocr_chrf_delta: float | None
    ocr_pairs: int


def must_keep_hits(
    hypothesis: str,
    must_keep: Sequence[Mapping[str, str]],
) -> tuple[MustKeepHit, ...]:
    """``tgt`` 作为连续子串出现在译文中则记为保留。

    大小写敏感，不做 NFKC。空 ``tgt`` 不算保留，避免空串匹配任意译文。
    """

    if not isinstance(hypothesis, str):
        raise TypeError("hypothesis 必须是 str")
    hits: list[MustKeepHit] = []
    for entry in must_keep:
        src = entry["src"]
        tgt = entry["tgt"]
        if not isinstance(src, str) or not isinstance(tgt, str):
            raise TypeError("must_keep 的 src 和 tgt 必须是字符串")
        hits.append(MustKeepHit(src=src, tgt=tgt, kept=tgt != "" and tgt in hypothesis))
    return tuple(hits)


def micro_retention(
    groups: Sequence[Sequence[MustKeepHit]],
) -> tuple[int, int, float | None]:
    """跨条目的微平均：保留条数 / ``must_keep`` 总项数。没有专名时比例为 ``None``。"""

    kept = 0
    total = 0
    for hits in groups:
        total += len(hits)
        kept += sum(1 for hit in hits if hit.kept)
    if total == 0:
        return 0, 0, None
    return kept, total, kept / total


def missed_samples(samples: Sequence[ScoredSample]) -> tuple[ScoredSample, ...]:
    """至少有一项专名未保留的样例，保持原顺序。"""

    return tuple(sample for sample in samples if any(not hit.kept for hit in sample.hits))


def percentile(values: Sequence[float], percent: float) -> float:
    """线性插值分位数。``percent`` 取 0–100，与最近两点之间按排名比例取值。"""

    if not values:
        raise ValueError("没有样本，无法计算分位数")
    if percent < 0 or percent > 100:
        raise ValueError("percent 必须在 0 到 100 之间")
    ordered = sorted(float(value) for value in values)
    if len(ordered) == 1:
        return ordered[0]
    rank = (percent / 100.0) * (len(ordered) - 1)
    low = math.floor(rank)
    high = math.ceil(rank)
    if low == high:
        return ordered[low]
    weight = rank - low
    return ordered[low] * (1.0 - weight) + ordered[high] * weight


def latency_stats(values: Sequence[float]) -> LatencyStats | None:
    """P50、P95 与最大值。空序列返回 ``None``。"""

    if not values:
        return None
    return LatencyStats(
        p50_ms=percentile(values, 50),
        p95_ms=percentile(values, 95),
        max_ms=max(values),
        n=len(values),
    )


def corpus_scores(
    hypotheses: Sequence[str],
    references: Sequence[str],
    tgt_lang: str,
    *,
    ja_mecab: bool,
) -> CorpusScores:
    """只对参考译文非空的条目计算语料级 chrF 与 BLEU。

    chrF 使用 ``sacrebleu`` 默认（字符 n-gram，``word_order=0``）。
    BLEU 使用 :func:`bleu_tokenize`。两者都是 sacrebleu 的 0–100 分。
    """

    if len(hypotheses) != len(references):
        raise ValueError("hypotheses 与 references 长度不一致")
    paired = [(hyp, ref) for hyp, ref in zip(hypotheses, references, strict=True) if ref.strip()]
    if not paired:
        return CorpusScores(None, None, 0, None, None)
    hyps = [hyp for hyp, _ref in paired]
    refs = [ref for _hyp, ref in paired]
    sacrebleu = _sacrebleu()
    tokenize = bleu_tokenize(tgt_lang, ja_mecab=ja_mecab)
    chrf_metric = sacrebleu.metrics.CHRF()
    bleu_metric = sacrebleu.metrics.BLEU(tokenize=tokenize)
    chrf = chrf_metric.corpus_score(hyps, [refs])
    bleu = bleu_metric.corpus_score(hyps, [refs])
    return CorpusScores(
        chrf=float(chrf.score),
        bleu=float(bleu.score),
        n=len(paired),
        chrf_signature=str(chrf_metric.get_signature()),
        bleu_signature=str(bleu_metric.get_signature()),
    )


def sentence_scores(
    hypothesis: str,
    reference: str,
    tgt_lang: str,
    *,
    ja_mecab: bool,
) -> tuple[float | None, float | None]:
    """单句 chrF / BLEU。参考译文为空时返回 ``(None, None)``。"""

    if not reference.strip():
        return None, None
    sacrebleu = _sacrebleu()
    tokenize = bleu_tokenize(tgt_lang, ja_mecab=ja_mecab)
    chrf = sacrebleu.sentence_chrf(hypothesis, [reference]).score
    bleu = sacrebleu.sentence_bleu(hypothesis, [reference], tokenize=tokenize).score
    return float(chrf), float(bleu)


def ocr_chrf_delta(noisy_hypothesis: str, clean_hypothesis: str, reference: str) -> float | None:
    """噪声译文相对干净译文的句级 chrF 差值（都相对同一参考）。

    返回 ``chrF(噪声, 参考) - chrF(干净, 参考)``。负值表示噪声把译文拉离参考。
    参考为空时无法计算，返回 ``None``。
    """

    if not reference.strip():
        return None
    sacrebleu = _sacrebleu()
    noisy = sacrebleu.sentence_chrf(noisy_hypothesis, [reference]).score
    clean = sacrebleu.sentence_chrf(clean_hypothesis, [reference]).score
    return float(noisy) - float(clean)


def summarize_samples(
    samples: Sequence[ScoredSample],
    *,
    ja_mecab: bool,
) -> tuple[GroupMetrics, tuple[GroupMetrics, ...]]:
    """汇总一整组样例，并按固定分类顺序给出非空分类。

    同一组的目标语必须一致。chrF / BLEU 是语料级分数，不是句级分数的平均。
    """

    if not samples:
        raise ValueError("没有样本，无法汇总")
    tgt_lang = samples[0].tgt_lang
    if any(sample.tgt_lang != tgt_lang for sample in samples):
        raise ValueError("同一组的目标语必须一致")
    overall = _group_metrics("all", samples, tgt_lang=tgt_lang, ja_mecab=ja_mecab)
    categories: list[GroupMetrics] = []
    seen = {sample.category for sample in samples}
    ordered = [category for category in CATEGORY_ORDER if category in seen]
    extras = dict.fromkeys(
        sample.category for sample in samples if sample.category not in CATEGORY_ORDER
    )
    ordered.extend(extras)
    for category in ordered:
        subset = tuple(sample for sample in samples if sample.category == category)
        categories.append(_group_metrics(category, subset, tgt_lang=tgt_lang, ja_mecab=ja_mecab))
    return overall, tuple(categories)


def _group_metrics(
    key: str,
    samples: Sequence[ScoredSample],
    *,
    tgt_lang: str,
    ja_mecab: bool,
) -> GroupMetrics:
    scores = corpus_scores(
        [sample.hypothesis for sample in samples],
        [sample.reference for sample in samples],
        tgt_lang,
        ja_mecab=ja_mecab,
    )
    kept, total, rate = micro_retention([sample.hits for sample in samples])
    deltas = [sample.ocr_chrf_delta for sample in samples if sample.ocr_chrf_delta is not None]
    return GroupMetrics(
        key=key,
        n=len(samples),
        n_with_reference=scores.n,
        chrf=scores.chrf,
        bleu=scores.bleu,
        chrf_signature=scores.chrf_signature,
        bleu_signature=scores.bleu_signature,
        proper_kept=kept,
        proper_total=total,
        proper_noun_retention=rate,
        latency=latency_stats([sample.elapsed_ms for sample in samples]),
        ocr_chrf_delta=(sum(deltas) / len(deltas) if deltas else None),
        ocr_pairs=len(deltas),
    )


def _sacrebleu():
    try:
        import sacrebleu
    except ImportError as exc:
        raise ImportError('计算 chrF/BLEU 需要评测依赖：pip install -e "engine[eval]"') from exc
    return sacrebleu
