"""专业领域评测（#78）：测试集、术语表、候选清单与评测编排（不需要真实模型）。"""

from __future__ import annotations

import json
import re
from collections import Counter
from pathlib import Path

import pytest

from suiyi_engine.eval import domain_bench as bench
from suiyi_engine.glossary import find_terms, load_glossary, present_form

SAMPLES = bench.load_samples(bench.DEFAULT_SAMPLES)
GLOSSARY = load_glossary(bench.DEFAULT_GLOSSARY)
TERMS = {term.id: term for term in GLOSSARY}
RAW = [
    json.loads(line)
    for line in bench.DEFAULT_SAMPLES.read_text(encoding="utf-8").splitlines()
    if line.strip()
]


def test_test_set_meets_issue_78_coverage() -> None:
    per_dir = Counter(sample.direction for sample in SAMPLES)
    assert per_dir[("en", "zh")] >= 150
    assert per_dir[("zh", "en")] >= 150
    for direction in (("ja", "zh"), ("ja", "en"), ("zh", "ja"), ("en", "ja")):
        assert per_dir[direction] >= 20
    for direction in (("en", "zh"), ("zh", "en")):
        domains = {s.domain for s in SAMPLES if s.direction == direction}
        assert domains == set(bench.DOMAINS)
        paragraphs = [s for s in SAMPLES if s.direction == direction and s.category == "paragraph"]
        assert len(paragraphs) >= 10
        for sample in paragraphs:
            ends = re.findall(r"[。！？]|[.!?](?=\s|$)", sample.source)
            assert 3 <= len(ends) <= 5, sample.id


def test_ids_licenses_and_sources_are_clean() -> None:
    for row in RAW:
        assert re.fullmatch(
            rf"{row['src_lang']}-{row['tgt_lang']}-{row['domain']}-\d{{3}}", row["id"]
        )
        assert row["license"] in {"MIT", "CC-BY-4.0"}
        assert row["reference"].strip()
        if row["license"] == "CC-BY-4.0":
            assert row["source_url"].startswith("https://")
            assert row["reference_url"].startswith("https://")
            assert row["reference_status"] == "published"
        else:
            assert row["reference_status"] == "draft"


def test_terms_match_glossary_and_references() -> None:
    """``terms`` / ``must_keep`` 必须等于按术语表在原文里重新匹配的结果，且参考译文里有该写法。"""

    for row in RAW:
        expected = []
        for match in find_terms(row["source"], row["src_lang"], GLOSSARY):
            if row["tgt_lang"] in match.term.forms:
                expected.append(match.term.id)
        assert row["terms"] == expected, row["id"]
        for term_id, keep in zip(row["terms"], row["must_keep"], strict=True):
            assert keep["tgt"] in row["reference"], row["id"]
            assert present_form(row["reference"], TERMS[term_id], row["tgt_lang"]) is not None
    total = sum(len(row["terms"]) for row in RAW)
    assert total >= 300
    assert len(GLOSSARY) >= 200
    assert {term.kind for term in GLOSSARY} == {"keep", "fixed"}


def test_kubernetes_example_from_issue_is_in_the_set() -> None:
    sample = next(s for s in SAMPLES if s.id == "en-zh-code-009")
    assert "container orchestration engine" in sample.source
    assert "CNCF" in sample.source
    assert "容器编排" in sample.reference
    assert set(sample.terms) >= {"container-orchestration", "cncf", "kubernetes"}


def test_existing_eval_runner_can_load_the_domain_set() -> None:
    from suiyi_engine.eval.runner import load_samples

    rows = load_samples(bench.DEFAULT_SAMPLES)
    assert len(rows) == len(SAMPLES)


def test_candidates_list_licenses_and_flags_nc_models() -> None:
    candidates = bench.load_candidates(bench.DEFAULT_CANDIDATES)
    assert {"opus-mt", "opus-tc-big", "nllb-600m", "nllb-1.3b", "m2m100-418m"} <= set(candidates)
    for candidate in candidates.values():
        assert candidate.license and candidate.redistributable and candidate.commercial
    for name in ("nllb-600m", "nllb-1.3b"):
        assert candidates[name].license == "CC-BY-NC-4.0"
        assert candidates[name].redistributable.startswith("否")
    assert candidates["opus-mt"].kind == "suiyi"
    assert any(candidate.kind == "llama" for candidate in candidates.values())


def test_quick_subset_is_small_and_keeps_typical_examples() -> None:
    quick = bench.select_samples(SAMPLES, quick=True)
    assert 60 <= len(quick) <= 110
    ids = {sample.id for sample in quick}
    assert set(bench.TYPICAL_IDS) <= ids
    per_dir = Counter(sample.direction for sample in quick)
    assert set(per_dir) == set(bench.DIRECTIONS)
    only = bench.select_samples(SAMPLES, directions=[("en", "zh")], limit=5)
    assert len(only) == 5 and {s.direction for s in only} == {("en", "zh")}


class _FakeBackend:
    """返回参考译文，但 en→zh 句子里故意丢掉「容器编排」。"""

    def __init__(self, samples: list[bench.DomainSample]) -> None:
        self._refs = {(s.source, s.src_lang, s.tgt_lang): s.reference for s in samples}
        self.closed = False

    def supports(self, src: str, tgt: str) -> bool:
        return (src, tgt) != ("en", "ja")

    def load(self, directions):
        return None

    def translate(self, text: str, src: str, tgt: str) -> str:
        return self._refs[(text, src, tgt)].replace("容器编排", "集装箱管弦")

    def close(self) -> None:
        self.closed = True

    def process_pid(self) -> int:
        import os

        return os.getpid()


def test_run_candidate_scores_and_renders_report(tmp_path: Path) -> None:
    pytest.importorskip("sacrebleu")
    samples = bench.select_samples(SAMPLES, quick=True)
    candidate = bench.load_candidates(bench.DEFAULT_CANDIDATES)["opus-mt"]
    backend = _FakeBackend(samples)
    config = bench.RunConfig(candidate, tmp_path, samples, TERMS)
    result = bench.run_candidate(config, backend=backend)
    assert backend.closed
    summary = result["summary"]
    assert "en-ja" not in summary["directions"]
    assert result["settings"]["skipped_directions"] == ["en-ja"]
    en_zh = summary["directions"]["en-zh"]
    assert 90 < en_zh["chrf"] < 100.1
    assert en_zh["term_acc"] < 100
    assert summary["directions"]["zh-en"]["chrf"] == pytest.approx(100.0)
    assert set(en_zh["domains"]) <= set(bench.DOMAINS)
    assert en_zh["latency_sentence"]["n"] > 0
    row = next(r for r in result["samples"] if r["id"] == "en-zh-code-009")
    assert "container-orchestration" in row["term_misses"]
    text = bench.render_report([result, result], title="测试")
    assert "# 测试" in text and "## 典型译例" in text
    assert "en-zh-code-009" in text and "集装箱管弦" in text
    assert "| OPUS-MT（现用基线） |" in text


def test_cli_report_round_trip(tmp_path: Path) -> None:
    pytest.importorskip("sacrebleu")
    samples = bench.select_samples(SAMPLES, directions=[("zh", "en")], limit=3)
    candidate = bench.load_candidates(bench.DEFAULT_CANDIDATES)["opus-mt"]
    result = bench.run_candidate(
        bench.RunConfig(candidate, tmp_path, samples, TERMS), backend=_FakeBackend(samples)
    )
    path = tmp_path / "a.json"
    path.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    out = tmp_path / "report.md"
    assert bench.main(["report", str(path), "--out", str(out)]) == 0
    assert "zh→en" in out.read_text(encoding="utf-8")


def test_prompts_and_output_cleanup() -> None:
    hy = bench.build_prompt("hy-mt", "Hello", "en", "zh")
    assert hy.startswith("将以下文本翻译为中文") and hy.endswith("Hello")
    assert bench.build_prompt("hy-mt", "Hello", "en", "ja").startswith(
        "Translate the following segment into Japanese"
    )
    gemma = bench.build_prompt("gemma-translate", "你好", "zh", "en")
    assert "Chinese (zh-Hans) to English (en)" in gemma
    assert gemma.endswith("你好<end_of_turn>\n<start_of_turn>model\n")
    assert "Japanese" in bench.build_prompt("generic", "x", "en", "ja")
    with pytest.raises(bench.BenchError):
        bench.build_prompt("nope", "x", "en", "zh")
    assert bench.clean_llm_output("<think>\n\n</think>\n\n容器编排") == "容器编排"
    assert bench.clean_llm_output("“设置”") == "设置"
    assert bench.clean_llm_output("```\nSettings\n```") == "Settings"


def test_ct2_routes_and_model_paths(tmp_path: Path) -> None:
    candidates = bench.load_candidates(bench.DEFAULT_CANDIDATES)
    big = candidates["opus-tc-big"]
    paths = big.model_paths(tmp_path)
    assert tmp_path / "cand/opus-tc-big-en-zh" in paths
    assert len(paths) == len(set(paths))
    nllb = candidates["nllb-600m"]
    assert nllb.model_paths(tmp_path) == [tmp_path / "cand/nllb-600m"]
    assert set(bench._routes(nllb.spec)) == {f"{s}-{t}" for s, t in bench.DIRECTIONS}
    assert bench.disk_mb([tmp_path / "missing"]) == 0


def test_parse_directions() -> None:
    assert bench.parse_directions("en-zh, zh→en") == [("en", "zh"), ("zh", "en")]
    assert bench.parse_directions(None) is None
    with pytest.raises(bench.BenchError):
        bench.parse_directions("en-fr")
