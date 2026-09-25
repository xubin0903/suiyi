"""评测编排：假后端覆盖路由与报告，真实模型用 model 标记。"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

from suiyi_engine.eval.metrics import OCR_MISSING_CLEAN, OCR_SCORED
from suiyi_engine.eval.report import (
    EnvironmentInfo,
    EvalReport,
    read_model_meta,
    render_markdown,
    write_outputs,
)
from suiyi_engine.eval.runner import (
    EvalError,
    evaluate,
    load_samples,
    main,
    parse_directions,
    select_samples,
)
from suiyi_engine.registry import ModelRecord, repo_root
from suiyi_engine.translator import Translator

_ROOT = repo_root()
_SCRIPT = _ROOT / "scripts" / "eval_samples.py"


class MapBackend:
    """按模型 id 把整句换成预定译文。"""

    def __init__(self, record: ModelRecord, tables: dict[str, dict[str, str]]) -> None:
        self.record = record
        self.tables = tables

    def translate_batch(self, sentences: list[str]) -> list[str]:
        table = self.tables[self.record.id]
        output: list[str] = []
        for sentence in sentences:
            if sentence not in table:
                raise AssertionError(f"{self.record.id} 没有 {sentence!r} 的译文")
            output.append(table[sentence])
        return output


def _install(root: Path, model_id: str, src: str, tgt: str) -> None:
    directory = root / model_id
    directory.mkdir(parents=True)
    payload = {
        "id": model_id,
        "src": src,
        "tgt": tgt,
        "hf_revision": "a" * 40,
        "quantization": "int8",
        "ctranslate2_version": "4.8.2",
        "converted_at": "2026-09-25T00:00:00+00:00",
    }
    (directory / "suiyi-model.json").write_text(
        json.dumps(payload, ensure_ascii=False),
        encoding="utf-8",
    )


def _translator(root: Path, tables: dict[str, dict[str, str]]) -> Translator:
    def factory(record: ModelRecord) -> MapBackend:
        return MapBackend(record, tables)

    return Translator(root, backend_factory=factory)


def _sample(
    sample_id: str,
    src: str,
    tgt: str,
    category: str,
    source: str,
    reference: str,
    must_keep: list[dict[str, str]] | None = None,
    **extra: object,
) -> dict[str, object]:
    row: dict[str, object] = {
        "id": sample_id,
        "src_lang": src,
        "tgt_lang": tgt,
        "category": category,
        "source": source,
        "reference": reference,
        "must_keep": must_keep or [],
    }
    row.update(extra)
    return row


def test_pivot_route_is_labeled_with_language_path(tmp_path: Path) -> None:
    pytest.importorskip("sacrebleu")
    _install(tmp_path, "opus-mt-ja-en", "ja", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh")
    tables = {
        "opus-mt-ja-en": {"こんにちは。": "Hello."},
        "opus-mt-en-zh": {"Hello.": "你好。"},
    }
    samples = [
        _sample(
            "ja-zh-short-001",
            "ja",
            "zh",
            "short",
            "こんにちは。",
            "你好。",
            [{"src": "こんにちは", "tgt": "你好"}],
        )
    ]
    evaluation = evaluate(samples, _translator(tmp_path, tables), ja_mecab=False)
    direction = evaluation.directions[0]
    assert direction.route_label == "ja→en→zh"
    assert direction.pivot is True
    assert direction.model_ids == ("opus-mt-ja-en", "opus-mt-en-zh")
    assert evaluation.samples[0].hypothesis == "你好。"
    assert evaluation.samples[0].route == ("opus-mt-ja-en", "opus-mt-en-zh")
    assert evaluation.samples[0].hits[0].kept is True
    report = _report(evaluation, tmp_path)
    markdown = render_markdown(report)
    assert "ja→en→zh" in markdown
    assert "opus-mt-ja-en" in markdown
    assert "峰值 RSS" in markdown
    assert "P50（ms）" in markdown
    assert "P95（ms）" in markdown
    assert "专名" in markdown
    assert "## 环境" in markdown


def test_proper_noun_misses_ocr_delta_and_directions_limit(tmp_path: Path) -> None:
    pytest.importorskip("sacrebleu")
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh")
    tables = {
        "opus-mt-zh-en": {
            "张伟在北京大学。": "Zhang Wei is at Peking University today.",
            "他在学校。": "He is at school.",
            "明天开会。": "Meet tomorrow.",
            "明天开汇。": "Meet tomorrow.",
            "明天开汇吧。": "xxxx",
        },
        "opus-mt-en-zh": {},
    }
    samples = [
        _sample(
            "zh-en-proper_noun-001",
            "zh",
            "en",
            "proper_noun",
            "张伟在北京大学。",
            "Zhang Wei is at Peking University today.",
            [
                {"src": "张伟", "tgt": "Zhang Wei"},
                {"src": "北京大学", "tgt": "Peking University"},
            ],
        ),
        _sample(
            "zh-en-proper_noun-002",
            "zh",
            "en",
            "proper_noun",
            "他在学校。",
            "He is at the school.",
            [{"src": "学校", "tgt": "campus"}],
        ),
        _sample(
            "zh-en-short-001",
            "zh",
            "en",
            "short",
            "明天开会。",
            "Meet tomorrow.",
        ),
        _sample(
            "zh-en-ocr_noise-001",
            "zh",
            "en",
            "ocr_noise",
            "明天开汇。",
            "Meet tomorrow.",
            clean_id="zh-en-short-001",
        ),
        _sample(
            "zh-en-ocr_noise-002",
            "zh",
            "en",
            "ocr_noise",
            "明天开汇吧。",
            "Meet tomorrow.",
            clean_id="zh-en-short-001",
        ),
        _sample(
            "zh-en-ocr_noise-003",
            "zh",
            "en",
            "ocr_noise",
            "明天开会。",
            "Meet tomorrow.",
            clean_id="zh-en-missing",
        ),
        _sample("en-zh-short-001", "en", "zh", "short", "Hello.", "你好。"),
    ]
    limited = evaluate(
        samples,
        _translator(tmp_path, tables),
        directions=[("zh", "en")],
        limit=2,
        ja_mecab=False,
    )
    assert [sample.id for sample in limited.samples] == [
        "zh-en-proper_noun-001",
        "zh-en-proper_noun-002",
    ]
    assert limited.limit == 2

    evaluation = evaluate(
        samples, _translator(tmp_path, tables), directions=[("zh", "en")], ja_mecab=False
    )
    by_id = {sample.id: sample for sample in evaluation.samples}
    assert by_id["zh-en-proper_noun-002"].hits[0].kept is False
    assert by_id["zh-en-ocr_noise-001"].ocr_status == OCR_SCORED
    assert by_id["zh-en-ocr_noise-001"].ocr_chrf_delta == pytest.approx(0)
    worse = by_id["zh-en-ocr_noise-002"].ocr_chrf_delta
    assert worse is not None and worse < 0
    assert by_id["zh-en-ocr_noise-003"].ocr_status == OCR_MISSING_CLEAN
    assert evaluation.directions[0].ocr_pairs_missing_clean == 1
    assert evaluation.directions[0].route_label == "zh→en"
    report = _report(evaluation, tmp_path)
    out = tmp_path / "out"
    write_outputs(out, report)
    assert (out / "report.md").is_file()
    assert (out / "summary.json").is_file()
    assert (out / "results.jsonl").is_file()
    markdown = (out / "report.md").read_text(encoding="utf-8")
    assert "school" in markdown
    assert "OCR chrF 差值" in markdown
    summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
    assert summary["schema_version"] == 1
    assert summary["directions"][0]["route_label"] == "zh→en"
    assert summary["directions"][0]["chrf"] is not None
    assert summary["proper_noun_misses"][0]["missing"] == ["campus"]
    rows = [
        json.loads(line)
        for line in (out / "results.jsonl").read_text(encoding="utf-8").splitlines()
    ]
    assert rows[0]["hypothesis"]
    assert rows[0]["route"] == ["opus-mt-zh-en"]
    assert "elapsed_ms" in rows[0]
    assert "chrf" in rows[0] and "bleu" in rows[0]
    blank = evaluate(
        [_sample("zh-en-short-009", "zh", "en", "short", "他在学校。", "")],
        _translator(tmp_path, tables),
        ja_mecab=False,
    )
    assert blank.samples[0].chrf is None
    assert blank.samples[0].bleu is None


def test_parse_directions_and_reject_unknown() -> None:
    assert parse_directions("zh-en, ja→zh") == [("zh", "en"), ("ja", "zh")]
    assert parse_directions(None) is None
    with pytest.raises(EvalError):
        parse_directions("zh")
    samples = [_sample("zh-en-short-001", "zh", "en", "short", "你好。", "Hi.")]
    with pytest.raises(EvalError) as caught:
        select_samples(samples, [("en", "ja")], None)
    assert caught.value.code == 2


def test_load_samples_rejects_bad_must_keep(tmp_path: Path) -> None:
    path = tmp_path / "bad.jsonl"
    path.write_text(
        '{"id":"a","src_lang":"zh","tgt_lang":"en","category":"short",'
        '"source":"你好。","reference":"Hi.","must_keep":["OCR"]}\n',
        encoding="utf-8",
    )
    with pytest.raises(EvalError):
        load_samples(path)


def test_reports_directory_is_gitignored() -> None:
    text = (_ROOT / ".gitignore").read_text(encoding="utf-8")
    assert "/reports/" in text.splitlines()


def test_cli_help_and_usage_errors(tmp_path: Path) -> None:
    help_run = subprocess.run(
        [sys.executable, str(_SCRIPT), "--help"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert help_run.returncode == 0
    assert "--samples" in help_run.stdout
    assert "--models-dir" in help_run.stdout
    missing = main(
        [
            "--samples",
            str(tmp_path / "missing.jsonl"),
            "--models-dir",
            str(tmp_path),
            "--out",
            str(tmp_path / "out"),
        ]
    )
    assert missing == 2
    assert main(["--samples", "x", "--models-dir", "y", "--out", "z", "--limit", "0"]) == 2


def test_missing_model_is_an_error(tmp_path: Path) -> None:
    pytest.importorskip("sacrebleu")
    samples = [_sample("zh-en-short-001", "zh", "en", "short", "你好。", "Hello.")]
    with pytest.raises(EvalError) as caught:
        evaluate(samples, Translator(tmp_path, backend_factory=lambda record: None), ja_mecab=False)
    assert caught.value.code == 1
    assert "opus-mt-zh-en" in str(caught.value)


@pytest.mark.model
def test_real_model_eval_writes_scores(tmp_path: Path) -> None:
    pytest.importorskip("sacrebleu")
    models_dir = Path(os.environ["SUIYI_MODELS_DIR"])
    meta = models_dir / "opus-mt-zh-en" / "suiyi-model.json"
    if not meta.is_file():
        pytest.skip("未找到 opus-mt-zh-en")
    samples_path = tmp_path / "one.jsonl"
    samples_path.write_text(
        json.dumps(
            {
                "id": "zh-en-short-001",
                "src_lang": "zh",
                "tgt_lang": "en",
                "category": "short",
                "source": "你好。",
                "reference": "Hello.",
                "must_keep": [{"src": "你好", "tgt": "Hello"}],
            },
            ensure_ascii=False,
        )
        + "\n",
        encoding="utf-8",
    )
    out = tmp_path / "report"
    code = main(
        [
            "--samples",
            str(samples_path),
            "--models-dir",
            str(models_dir),
            "--out",
            str(out),
            "--directions",
            "zh-en",
        ]
    )
    assert code == 0
    summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
    direction = summary["directions"][0]
    assert direction["direction"] == "zh→en"
    assert direction["route_label"] == "zh→en"
    assert direction["model_ids"] == ["opus-mt-zh-en"]
    assert direction["n"] == 1
    assert direction["chrf"] is not None
    row = json.loads((out / "results.jsonl").read_text(encoding="utf-8"))
    assert row["hypothesis"]
    assert row["elapsed_ms"] >= 0
    models = read_model_meta(models_dir, ["opus-mt-zh-en"])
    assert models[0].hf_revision


def _report(evaluation, models_dir: Path) -> EvalReport:
    return EvalReport(
        run=evaluation,
        environment=EnvironmentInfo(
            cpu_model="Test CPU",
            cpu_count=4,
            memory_bytes=8 * 1024**3,
            os="Test OS",
            python="3.12.3",
            ctranslate2="4.8.2",
            sacrebleu="2.6.0",
            psutil="7.2.2",
            engine="0.0.1",
        ),
        models=read_model_meta(models_dir, evaluation.directions[0].model_ids),
        peak_rss_bytes=128 * 1024**2,
        ja_mecab=False,
        ja_mecab_note="ja-mecab 不可用，日文 BLEU 回退 tokenize=char",
        samples_path="tests/samples/zh_core_v1.jsonl",
        samples_sha256="abc",
        models_dir=str(models_dir),
        evaluated_at="2026-09-25T00:00:00+00:00",
    )
