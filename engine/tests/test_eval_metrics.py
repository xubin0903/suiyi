"""评测指标：专名保留率、分位数，以及用固定字符串核对的分类汇总。"""

from __future__ import annotations

import pytest

from suiyi_engine.eval.metrics import (
    MustKeepHit,
    ScoredSample,
    bleu_tokenize,
    corpus_scores,
    latency_stats,
    micro_retention,
    missed_samples,
    must_keep_hits,
    ocr_chrf_delta,
    percentile,
    summarize_samples,
)

_SENTENCE = "This is a complete sentence."


def test_proper_noun_retention_counts_exact_substrings() -> None:
    hypothesis = "Zhang Wei works at Peking University."
    hits = must_keep_hits(
        hypothesis,
        (
            {"src": "张伟", "tgt": "Zhang Wei"},
            {"src": "北京大学", "tgt": "Peking University"},
            {"src": "清华大学", "tgt": "Tsinghua University"},
        ),
    )
    assert [hit.kept for hit in hits] == [True, True, False]
    assert micro_retention([hits]) == (2, 3, pytest.approx(2 / 3))


def test_empty_tgt_does_not_match_every_hypothesis() -> None:
    hits = must_keep_hits("anything", ({"src": "甲", "tgt": ""},))
    assert hits == (MustKeepHit("甲", "", False),)
    assert micro_retention([hits]) == (0, 1, 0.0)


def test_empty_must_keep_has_no_retention_rate() -> None:
    assert must_keep_hits("hello", ()) == ()
    assert micro_retention([()]) == (0, 0, None)
    assert micro_retention([]) == (0, 0, None)


def test_proper_noun_match_is_case_sensitive() -> None:
    hits = must_keep_hits("ocr result", ({"src": "OCR", "tgt": "OCR"},))
    assert hits[0].kept is False


def test_percentile_interpolates_between_neighbors() -> None:
    values = [10.0, 20.0, 30.0, 40.0]
    assert percentile(values, 0) == 10
    assert percentile(values, 50) == 25
    assert percentile(values, 95) == pytest.approx(38.5)
    assert percentile(values, 100) == 40
    assert percentile([7.0], 50) == 7
    stats = latency_stats(values)
    assert stats is not None
    assert stats.p50_ms == 25
    assert stats.max_ms == 40
    assert stats.n == 4
    assert latency_stats(()) is None


def test_bleu_tokenize_falls_back_for_japanese() -> None:
    assert bleu_tokenize("zh", ja_mecab=False) == "zh"
    assert bleu_tokenize("zh", ja_mecab=True) == "zh"
    assert bleu_tokenize("ja", ja_mecab=True) == "ja-mecab"
    assert bleu_tokenize("ja", ja_mecab=False) == "char"
    assert bleu_tokenize("en", ja_mecab=False) == "13a"
    assert bleu_tokenize("fr", ja_mecab=True) == "13a"


def test_summarize_groups_categories_and_skips_blank_references() -> None:
    pytest.importorskip("sacrebleu")
    kept = (MustKeepHit("张伟", "Zhang Wei", True),)
    missed = (MustKeepHit("北京大学", "Peking University", False),)
    samples = (
        _sample("a", "short", _SENTENCE, _SENTENCE, hits=kept, elapsed_ms=10),
        _sample("b", "short", _SENTENCE, "   ", hits=missed, elapsed_ms=30),
        _sample(
            "c", "colloquial", _SENTENCE, "totally different words here now", hits=(), elapsed_ms=20
        ),
    )
    overall, categories = summarize_samples(samples, ja_mecab=False)
    assert overall.n == 3
    assert overall.n_with_reference == 2
    assert overall.proper_kept == 1
    assert overall.proper_total == 2
    assert overall.proper_noun_retention == pytest.approx(0.5)
    assert overall.chrf is not None and overall.chrf < 100
    assert [group.key for group in categories] == ["short", "colloquial"]
    short = categories[0]
    assert short.n == 2
    assert short.n_with_reference == 1
    assert short.chrf == pytest.approx(100)
    assert short.bleu == pytest.approx(100)
    assert short.proper_kept == 1 and short.proper_total == 2
    assert short.latency is not None
    assert short.latency.p50_ms == 20
    colloquial = categories[1]
    assert colloquial.chrf is not None and colloquial.chrf < 100
    assert colloquial.proper_noun_retention is None
    assert missed_samples(samples)[0].id == "b"


def test_corpus_scores_match_sacrebleu_on_fixed_strings() -> None:
    sacrebleu = pytest.importorskip("sacrebleu")
    hyp = "你好世界，今天天气很好。"
    scores = corpus_scores([hyp], [hyp], "zh", ja_mecab=False)
    direct_chrf = sacrebleu.corpus_chrf([hyp], [[hyp]]).score
    direct_bleu = sacrebleu.corpus_bleu([hyp], [[hyp]], tokenize="zh").score
    assert scores.n == 1
    assert scores.chrf == pytest.approx(direct_chrf)
    assert scores.bleu == pytest.approx(direct_bleu)
    assert scores.chrf == pytest.approx(100)
    assert scores.bleu == pytest.approx(100)
    assert scores.chrf_signature
    assert "tok:zh" in (scores.bleu_signature or "")
    blank = corpus_scores(["x"], ["  "], "en", ja_mecab=False)
    assert blank.chrf is None and blank.bleu is None and blank.n == 0


def test_ocr_chrf_delta_on_fixed_strings() -> None:
    pytest.importorskip("sacrebleu")
    assert ocr_chrf_delta(_SENTENCE, _SENTENCE, _SENTENCE) == pytest.approx(0)
    worse = ocr_chrf_delta("xxxx", _SENTENCE, _SENTENCE)
    assert worse is not None and worse < 0
    assert ocr_chrf_delta("a", "b", "  ") is None


def test_japanese_bleu_uses_char_when_mecab_disabled() -> None:
    pytest.importorskip("sacrebleu")
    text = "これはテストです。"
    scores = corpus_scores([text], [text], "ja", ja_mecab=False)
    assert scores.bleu == pytest.approx(100)
    assert scores.bleu_signature is not None
    assert "tok:char" in scores.bleu_signature


def _sample(
    sample_id: str,
    category: str,
    hypothesis: str,
    reference: str,
    *,
    hits: tuple[MustKeepHit, ...] = (),
    elapsed_ms: float = 1.0,
    ocr_chrf_delta: float | None = None,
) -> ScoredSample:
    return ScoredSample(
        id=sample_id,
        src_lang="en",
        tgt_lang="en",
        category=category,
        source="source",
        reference=reference,
        hypothesis=hypothesis,
        route=("fake",),
        route_label="en→en",
        elapsed_ms=elapsed_ms,
        chrf=None,
        bleu=None,
        hits=hits,
        clean_id=None,
        ocr_chrf_delta=ocr_chrf_delta,
        ocr_status=None,
        notes="",
    )
