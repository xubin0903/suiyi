"""OCR 评测编排的单测（#54）：样例清单、变体解析、打分与报告。不需要模型。"""

from __future__ import annotations

import json
from collections import Counter
from pathlib import Path

import pytest

from suiyi_engine.eval import ocr_bench
from suiyi_engine.eval.ocr_bench import (
    OcrEvalError,
    default_samples_path,
    load_samples,
    parse_floats,
    render_markdown,
    resolve_det,
    score_variant,
    select_samples,
    split_variant,
)
from suiyi_engine.tools.ocr_models import default_manifest_path, load_manifest

REQUIRED_CATEGORIES = {
    "中文网页正文",
    "中文 UI 小字",
    "低对比/深色主题",
    "英文文档",
    "英文代码/等宽",
    "日文网页横排",
    "日文竖排",
    "中英混排",
}


def test_repository_sample_set_meets_issue_requirements() -> None:
    path = default_samples_path()
    samples = load_samples(path)
    assert len(samples) >= 30
    categories = Counter(s["category"] for s in samples)
    assert REQUIRED_CATEGORIES <= set(categories)
    assert categories["日文竖排"] >= 3
    assert {s["size_class"] for s in samples} == {"small", "720p", "1080p"}
    total = 0
    for s in samples:
        assert s["license"] == "CC0-1.0"
        assert s["source"]
        image = path.parent / s["file"]
        total += image.stat().st_size
        assert image.stat().st_size == s["bytes"]
    assert total < 3 * 2**20  # 仓库体积：样例图合计 < 3 MB
    for quick in ocr_bench.QUICK_IDS:
        assert any(s["id"] == quick for s in samples)


def _write_samples(tmp_path: Path, rows: list[dict[str, object]]) -> Path:
    (tmp_path / "a.png").write_bytes(b"png")
    path = tmp_path / "samples.json"
    path.write_text(json.dumps({"schema_version": 1, "samples": rows}), encoding="utf-8")
    return path


def _row(**over: object) -> dict[str, object]:
    row: dict[str, object] = {
        "id": "a",
        "file": "a.png",
        "lang": "zh",
        "category": "中文 UI 小字",
        "size_class": "small",
        "paragraphs": ["确定", "取消"],
    }
    row.update(over)
    return row


def test_load_samples_validation(tmp_path: Path) -> None:
    assert load_samples(_write_samples(tmp_path, [_row()]))[0]["id"] == "a"
    for bad in (
        [_row(), _row()],
        [_row(size_class="4k")],
        [_row(paragraphs=[])],
        [_row(paragraphs=["", "x"])],
        [_row(file="missing.png")],
        [_row(lang="")],
    ):
        with pytest.raises(OcrEvalError):
            load_samples(_write_samples(tmp_path, bad))
    with pytest.raises(OcrEvalError):
        load_samples(tmp_path / "nope.json")


def test_select_samples() -> None:
    rows = [_row(id="a"), _row(id="b")]
    assert [r["id"] for r in select_samples(rows, ["b"])] == ["b"]
    assert len(select_samples(rows, None)) == 2
    with pytest.raises(OcrEvalError):
        select_samples(rows, ["c"])


def test_resolve_and_split_variants() -> None:
    models = load_manifest(default_manifest_path()).models
    assert resolve_det("small", models) == "PP-OCRv6_det_small"
    assert resolve_det("tiny@960+rb1+nomp", models) == "PP-OCRv6_det_tiny@960+rb1+nomp"
    assert split_variant("PP-OCRv6_det_tiny@960+rb1+nomp") == (
        "PP-OCRv6_det_tiny",
        {"det_max_side": 960, "rec_batch": 1, "mem_pattern": False},
    )
    assert split_variant("PP-OCRv6_det_small") == ("PP-OCRv6_det_small", {})
    assert ocr_bench._short("PP-OCRv6_det_small+rb1") == "det small+rb1"
    assert ocr_bench._short("PP-OCRv6_det_tiny@960+nomp") == "det tiny@960+nomp"
    for bad in ("PP-OCRv6_rec_small", "huge", "small@100", "small@x", "small+rb0", "small+fast"):
        with pytest.raises(OcrEvalError):
            resolve_det(bad, models)


def test_parse_floats() -> None:
    assert parse_floats("0.5, 0.9") == [0.5, 0.9]
    for bad in ("", "a", "0", "-1"):
        with pytest.raises(OcrEvalError):
            parse_floats(bad)


def _line(text: str, x: float, y: float, w: float, h: float = 12.0) -> dict[str, object]:
    return {
        "text": text,
        "box": [[x, y], [x + w, y], [x + w, y + h], [x, y + h]],
        "score": 0.99,
        "low_confidence": False,
    }


def _raw() -> dict[str, object]:
    # 三行 12px 小字，行距 = 0.6 × 字号：默认 line_gap=0.9 会合并成一段，0.5 时分开
    lines = [
        _line("自动保存已开启。", 10, 10, 96),
        _line("上次保存于三点。", 10, 29.2, 96),
        _line("提示：可以关闭。", 10, 48.4, 96),
    ]
    return {
        "det": "PP-OCRv6_det_small",
        "ok": True,
        "threads": 4,
        "load_ms": 400.0,
        "first_ms": 5.0,
        "rss": {
            "baseline": 40 * 2**20,
            "after_load": 160 * 2**20,
            "final": 200 * 2**20,
            "peak": 240 * 2**20,
            "load_delta": 120 * 2**20,
            "peak_delta": 200 * 2**20,
        },
        "samples": [
            {
                "id": "a",
                "size_class": "small",
                "width": 400,
                "height": 150,
                "times_ms": [100.0, 110.0, 120.0],
                "stages_mean_ms": {"ocr_ms": 100.0},
                "lines": lines,
            }
        ],
    }


def test_score_variant_and_line_gap_sweep() -> None:
    samples = [_row(paragraphs=["自动保存已开启。", "上次保存于三点。", "提示：可以关闭。"])]
    variant = score_variant(_raw(), samples, [0.5, 0.9])
    assert variant["ok"]
    assert variant["cer"]["overall"]["cer"] == 0.0
    seg = variant["segmentation"]["overall"]
    assert seg["merges"] == 2  # 默认 0.9：三行并成一段
    sweep = {row["line_gap"]: row for row in variant["line_gap_sweep"]}
    assert sweep[0.9]["default"] is True
    assert sweep[0.5]["ui"]["merges"] == 0
    assert sweep[0.5]["overall"]["exact_samples"] == 1
    latency = variant["latency"]["small"]
    assert latency["n"] == 3 and latency["p50_ms"] == 110.0
    assert variant["latency"]["1080p"] is None
    assert variant["samples"][0]["predicted"] == [
        "自动保存已开启。上次保存于三点。提示：可以关闭。"
    ]


def test_score_variant_failure_is_reported() -> None:
    variant = score_variant({"det": "x", "ok": False, "error": "缺模型"}, [], [0.9])
    assert variant == {"det": "x", "ok": False, "error": "缺模型"}


def test_render_markdown_contains_targets_and_failures() -> None:
    samples = [_row(paragraphs=["自动保存已开启。", "上次保存于三点。", "提示：可以关闭。"])]
    ok = score_variant(_raw(), samples, [0.5, 0.9])
    failed = score_variant({"det": "PP-OCRv6_det_tiny", "ok": False, "error": "缺模型"}, [], [])
    report = {
        "generated_at": "2026-09-25T22:00:00+08:00",
        "label": "测试机",
        "quick": True,
        "settings": {
            "sample_count": 1,
            "warmup": 1,
            "repeats": 3,
            "recommended": {"det": "PP-OCRv6_det_small"},
        },
        "environment": ocr_bench.collect_environment(),
        "variants": [ok, failed],
    }
    text = render_markdown(report)
    assert "# OCR 评测报告" in text
    assert "快速子集" in text
    assert "det tiny 未运行" not in text  # 失败行用完整 id
    assert "PP-OCRv6_det_tiny 未运行" in text
    assert "≤ 800 ms" in text
    assert "0.9（默认）" in text
    assert "200 MB ✅" in text


def test_main_reports_bad_arguments(capsys: pytest.CaptureFixture[str]) -> None:
    assert ocr_bench.main(["--det", "huge", "--only", "zh_ui_small_01"]) == 2
    assert "未知检测模型" in capsys.readouterr().err


def test_main_missing_models_writes_report(tmp_path: Path) -> None:
    out = tmp_path / "out"
    code = ocr_bench.main(
        [
            "--models-dir",
            str(tmp_path / "models"),
            "--only",
            "zh_ui_small_01",
            "--det",
            "small",
            "--repeats",
            "1",
            "--out",
            str(out),
        ]
    )
    assert code == 1
    report = json.loads((out / "report.json").read_text(encoding="utf-8"))
    assert report["variants"][0]["ok"] is False
    assert "未运行" in (out / "report.md").read_text(encoding="utf-8")
