"""OCR 评测编排的单测（#54）：样例清单、变体解析、打分与报告。不需要模型。"""

from __future__ import annotations

import json
import re
import time
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
    assert resolve_det("small@0+legacy+nodil", models) == "PP-OCRv6_det_small@0+legacy+nodil"
    assert split_variant("PP-OCRv6_det_small@0+legacy+mp+dil") == (
        "PP-OCRv6_det_small",
        {"det_max_side": None, "legacy": True, "mem_pattern": True, "det_dilation": True},
    )
    assert split_variant("PP-OCRv6_det_small+sh0+sh1.5+pad2") == (
        "PP-OCRv6_det_small",
        {"box_shrink": 1.5, "crop_pad": 2.0},
    )
    assert resolve_det("small+sh3+pad0", models) == "PP-OCRv6_det_small+sh3+pad0"
    bads = ("PP-OCRv6_rec_small", "huge", "small@100", "small@x", "small+rb0", "small+fast")
    for bad in (*bads, "small+sh-1", "small+shx", "small+padx"):
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
    # 三行 12px 字、行宽 20 × 字号（不算短行），行距 = 0.6 × 字号：
    # 默认 line_gap=0.9 会合并成一段，0.5 时分开
    lines = [
        _line("自动保存已开启。", 10, 10, 240),
        _line("上次保存于三点。", 10, 29.2, 240),
        _line("提示：可以关闭。", 10, 48.4, 240),
    ]
    return {
        "det": "PP-OCRv6_det_small",
        "ok": True,
        "threads": 4,
        "load_ms": 400.0,
        "first_ms": 5.0,
        "runtime": {"det_max_side": 960, "rec_batch": 1, "mem_pattern": True},
        "peak_method": "linux-vmhwm",
        "rss": {
            "process_base": 40 * 2**20,
            "after_load": 160 * 2**20,
            "idle": 165 * 2**20,
            "final": 200 * 2**20,
            "lifetime_peak": 500 * 2**20,
            "model_delta": 125 * 2**20,
            "resident_delta": 35 * 2**20,
        },
        "samples": [
            {
                "id": "a",
                "size_class": "small",
                "width": 400,
                "height": 150,
                "times_ms": [100.0, 110.0, 120.0],
                "request_peak_delta": [150 * 2**20, 180 * 2**20, 200 * 2**20],
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


def test_runtime_for_defaults_and_legacy() -> None:
    from suiyi_engine.ocr.engine import DEFAULT_RUNTIME, LEGACY_RUNTIME

    assert ocr_bench.runtime_for({}) == DEFAULT_RUNTIME
    legacy = ocr_bench.runtime_for({"legacy": True})
    assert legacy == LEGACY_RUNTIME
    assert legacy.det_max_side is None and legacy.rec_batch == 6
    tuned = ocr_bench.runtime_for({"legacy": True, "det_max_side": 960, "rec_batch": 1})
    assert (tuned.det_max_side, tuned.rec_batch, tuned.mem_pattern) == (960, 1, True)


def test_runtime_options_validation() -> None:
    from suiyi_engine.ocr.engine import OcrRuntimeOptions

    with pytest.raises(ValueError):
        OcrRuntimeOptions(det_max_side=100)
    with pytest.raises(ValueError):
        OcrRuntimeOptions(rec_batch=0)
    with pytest.raises(ValueError):
        OcrRuntimeOptions(box_shrink=-1)
    with pytest.raises(ValueError):
        OcrRuntimeOptions(crop_pad=-1)
    assert OcrRuntimeOptions(det_max_side=None).det_max_side is None


def test_shrink_box_insets_short_side() -> None:
    np = pytest.importorskip("numpy")
    from suiyi_engine.ocr.engine import shrink_box

    horizontal = np.array([[0, 0], [100, 0], [100, 20], [0, 20]], dtype=np.float32)
    out = shrink_box(horizontal, 2.0, "一行文字")
    assert out[:, 1].min() == pytest.approx(2) and out[:, 1].max() == pytest.approx(18)
    assert out[:, 0].min() == 0 and out[:, 0].max() == 100
    assert shrink_box(horizontal, 50.0, "一行")[:, 1].min() == pytest.approx(6)  # 最多收 30%
    assert shrink_box(horizontal, 0.0, "一行") is horizontal
    vertical = np.array([[0, 0], [20, 0], [20, 100], [0, 100]], dtype=np.float32)
    out = shrink_box(vertical, 2.0, "竖排文字")
    assert out[:, 0].min() == pytest.approx(2) and out[:, 0].max() == pytest.approx(18)
    # 单字竖框按横排处理（与分段的竖排判断一致）
    assert shrink_box(vertical, 2.0, "た")[:, 1].min() == pytest.approx(2)


def test_crop_box_pads_short_side_and_clips() -> None:
    np = pytest.importorskip("numpy")
    from suiyi_engine.ocr.engine import _crop_box

    box = np.array([[10, 10], [110, 10], [110, 30], [10, 30]], dtype=np.float32)
    out = _crop_box(box, 3.0, 200, 32)
    assert out[:, 1].min() == pytest.approx(7) and out[:, 1].max() == pytest.approx(31)
    assert out[:, 0].min() == 10 and out[:, 0].max() == 110
    same = _crop_box(box, 0.0, 200, 200)
    assert same is not box and np.array_equal(same, box)


def test_memory_aggregation_and_targets() -> None:
    samples = [_row(paragraphs=["自动保存已开启。", "上次保存于三点。", "提示：可以关闭。"])]
    variant = score_variant(_raw(), samples, [0.9])
    overall = variant["memory"]["overall"]
    assert overall["n"] == 3 and overall["max"] == 200 * 2**20
    assert variant["memory"]["small"]["p50"] == 180 * 2**20
    assert variant["memory"]["1080p"] is None
    assert variant["samples"][0]["request_peak_max"] == 200 * 2**20
    assert ocr_bench._peak_cell(overall) == "200 MB ✅"
    assert ocr_bench._peak_cell({"max": 401 * 2**20}).endswith("❌")
    assert ocr_bench._exact_cell({"exact_samples": 23, "samples": 32}, True) == "23/32 ✅"
    assert ocr_bench._exact_cell({"exact_samples": 22, "samples": 32}, True) == "22/32 ❌"
    assert ocr_bench._exact_cell({"exact_samples": 1, "samples": 1}, False) == "1/1"


def test_peak_meter_sees_allocation_inside_request() -> None:
    meter = ocr_bench.PeakMeter()
    if meter.method == "none":
        pytest.skip("本平台既没有 /proc/self/clear_refs 也没有 psutil")
    before = ocr_bench.current_rss_bytes()
    meter.start()
    block = bytearray(64 * 2**20)
    block[::4096] = b"x" * len(block[::4096])  # 触碰每一页，让它计入 RSS
    time.sleep(0.05)  # 采样法（非 Linux）需要尖峰持续几个采样周期；Windows 计时精度约 15 ms
    del block
    peak = meter.stop()
    assert peak is not None and before is not None
    assert peak - before >= 48 * 2**20


def test_repository_holdout_split_for_issue_75() -> None:
    samples = load_samples(default_samples_path())
    dev = [s for s in samples if s["split"] == "dev"]
    holdout = [s for s in samples if s["split"] == "holdout"]
    assert len(dev) == ocr_bench.FULL_SAMPLE_COUNT
    assert len(holdout) >= 8
    assert all(ocr_bench.UI_MARK in s["category"] for s in holdout)
    assert not {s["id"] for s in holdout} & set(ocr_bench.QUICK_IDS)


def test_score_variant_keeps_holdout_out_of_main_totals(tmp_path: Path) -> None:
    raw = _raw()
    first = raw["samples"][0]  # type: ignore[index]
    raw["samples"] = [first, {**first, "id": "b"}]  # type: ignore[dict-item]
    paragraphs = ["自动保存已开启。", "上次保存于三点。", "提示：可以关闭。"]
    samples = [_row(paragraphs=paragraphs), _row(id="b", paragraphs=paragraphs, split="holdout")]
    variant = score_variant(raw, samples, [0.9])
    assert variant["segmentation"]["overall"]["samples"] == 1
    assert variant["latency"]["small"]["samples"] == 1
    assert variant["holdout"]["samples"] == 1
    assert variant["holdout"]["segmentation"]["ui"]["samples"] == 1
    assert [s["split"] for s in variant["samples"]] == ["dev", "holdout"]
    assert re.fullmatch(r"[01]/1 / \d+ / \d+", ocr_bench._holdout_cell(variant["holdout"]))
    # 只跑留出集时，主汇总退回到全部样例，不再单列留出集
    only = score_variant({**raw, "samples": [raw["samples"][1]]}, samples[1:], [0.9])  # type: ignore[index]
    assert only["segmentation"]["overall"]["samples"] == 1 and only["holdout"] is None
    bad = _write_samples(tmp_path, [_row(split="test")])
    with pytest.raises(OcrEvalError):
        load_samples(bad)
    assert load_samples(_write_samples(tmp_path, [_row()]))[0]["split"] == "dev"


def test_merge_touching_boxes_joins_overlapping_word_fragments() -> None:
    np = pytest.importorskip("numpy")
    from suiyi_engine.ocr.engine import merge_touching_boxes

    def quad(x0: float, y0: float, x1: float, y1: float) -> list[list[float]]:
        return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]

    # return 与 "" 重叠 10 px：合成一个框
    boxes = np.array([quad(124, 92, 199, 115), quad(189, 91, 225, 111), quad(82, 149, 549, 168)])
    out = merge_touching_boxes(boxes)
    assert len(out) == 2
    assert out[0].tolist() == quad(124, 91, 225, 115)
    assert out[1].tolist() == boxes[2].tolist()
    # 菜单项之间有一个字宽的空隙：不合并
    menu = np.array([quad(10, 10, 34, 22), quad(46, 10, 70, 22)])
    assert len(merge_touching_boxes(menu)) == 2
    # 竖排列不合并
    columns = np.array([quad(100, 10, 130, 300), quad(128, 10, 158, 300)])
    assert len(merge_touching_boxes(columns)) == 2
