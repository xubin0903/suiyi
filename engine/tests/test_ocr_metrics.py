"""OCR 评测指标单测（#54）：不需要模型，CI 必跑。"""

from __future__ import annotations

import pytest

from suiyi_engine.eval.ocr_metrics import (
    CerScore,
    cer,
    edit_distance,
    merge_cer,
    merge_segmentation,
    normalize_text,
    segmentation,
)


@pytest.mark.parametrize(
    ("a", "b", "expected"),
    [
        ("", "", 0),
        ("abc", "", 3),
        ("", "abc", 3),
        ("kitten", "sitting", 3),
        ("翻译引擎", "翻泽引擎", 1),
        ("今日は雨", "今日雨", 1),
        ("abc", "abc", 0),
    ],
)
def test_edit_distance(a: str, b: str, expected: int) -> None:
    assert edit_distance(a, b) == expected
    assert edit_distance(b, a) == expected


def test_normalize_folds_width_vertical_forms_and_whitespace() -> None:
    assert normalize_text("ＡＢＣ　１２３") == "ABC123"
    assert normalize_text("雨︒\n晴︑") == "雨。晴、"
    assert normalize_text(" a b\tc\n") == "abc"


def test_cer_basic_and_paragraph_lists() -> None:
    score = cer(["今日は雨なので、", "家で本を読む。"], "今日は雨なので、家で本を読む")
    assert score.ref_chars == 15
    assert score.edits == 1
    assert score.cer == pytest.approx(1 / 15)
    # 空白与换行不计入
    assert cer("hello world", "helloworld").cer == 0.0


def test_cer_counts_reading_order() -> None:
    assert cer(["左栏", "右栏"], ["右栏", "左栏"]).edits > 0


def test_cer_empty_reference() -> None:
    assert cer("", "").cer == 0.0
    assert cer("", "噪点").cer == 1.0
    assert cer("文字", "").cer == 1.0


def test_merge_cer_is_micro_average() -> None:
    merged = merge_cer([CerScore(1, 10, 10), CerScore(9, 90, 90)])
    assert merged.cer == pytest.approx(0.1)
    assert merge_cer([]).cer == 0.0


def test_segmentation_exact() -> None:
    score = segmentation(["第一段。", "第二段。", "第三段。"], ["第一段。", "第二段。", "第三段。"])
    assert (score.matched, score.merges, score.splits) == (2, 0, 0)
    assert score.exact_samples == 1
    assert score.precision == score.recall == score.f1 == 1.0


def test_segmentation_detects_merge() -> None:
    score = segmentation(["确定", "取消", "提示：可以关闭。"], ["确定取消", "提示：可以关闭。"])
    assert score.merges == 1
    assert score.splits == 0
    assert score.matched == 1
    assert score.exact_samples == 0
    assert score.recall == pytest.approx(0.5)
    assert score.precision == 1.0


def test_segmentation_detects_split() -> None:
    score = segmentation(["这是很长的一段正文，跨了两行。"], ["这是很长的一段正文，", "跨了两行。"])
    assert (score.matched, score.merges, score.splits) == (0, 0, 1)
    assert score.precision == 0.0
    assert score.recall == 1.0


def test_segmentation_tolerates_recognition_errors_near_boundary() -> None:
    expected = ["网络连接已断开，正在使用本地模型。", "提示：可以在设置中关闭。"]
    predicted = ["网络连接已断开，正在使用本地模型", "提示:可以在设置中关闭。"]
    score = segmentation(expected, predicted)
    assert score.matched == 1
    assert score.merges == score.splits == 0


def test_segmentation_boundary_outside_tolerance_counts_both_errors() -> None:
    expected = ["一二三四五六七八", "九十甲乙丙丁"]
    predicted = ["一二三四五", "六七八九十甲乙丙丁"]
    score = segmentation(expected, predicted, tolerance=2)
    assert (score.matched, score.merges, score.splits) == (0, 1, 1)
    assert segmentation(expected, predicted, tolerance=3).matched == 1


def test_segmentation_ignores_empty_and_handles_nothing_recognized() -> None:
    score = segmentation(["甲段", "", "乙段"], [])
    assert score.expected_paragraphs == 2
    assert score.predicted_paragraphs == 0
    assert score.merges == 1
    assert score.splits == 0


def test_segmentation_extra_noise_paragraph_is_split() -> None:
    score = segmentation(["正文一段"], ["正文一段", "噪"])
    assert score.splits == 1
    assert score.merges == 0


def test_merge_segmentation_sums_and_exact_rate() -> None:
    a = segmentation(["甲", "乙"], ["甲", "乙"])
    b = segmentation(["丙", "丁"], ["丙丁"])
    total = merge_segmentation([a, b])
    assert total.samples == 2
    assert total.exact_samples == 1
    assert total.exact_rate == 0.5
    assert total.merges == 1
    assert total.matched == 1
    assert merge_segmentation([]).exact_rate == 0.0
