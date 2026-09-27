"""常用极短句表（#89）与中文目标的 ``<unk>`` 标点（#89）。"""

from __future__ import annotations

import json
import re
from pathlib import Path

import pytest

from suiyi_engine.backends.ct2_opus import decode_hypothesis
from suiyi_engine.registry import ModelRecord
from suiyi_engine.short_phrases import _table, lookup_short_phrase, phrase_count
from suiyi_engine.translator import Translator
from suiyi_engine.zh_punct import unk_marks
from suiyi_engine.zh_script import is_mojibake_char, is_traditional_char

ROOT = Path(__file__).resolve().parents[2]
SHORT_SET = ROOT / "engine" / "eval" / "short" / "short_v1.jsonl"


# ---------------------------------------------------------------- 查表


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("I'm sorry.", "对不起。"),
        ("I\u2019m sorry.", "对不起。"),
        ("  THANK YOU!  ", "谢谢你！"),
        ("How are you?", "你好吗？"),
        ("How are you", "你好吗"),
        ("Good evening...", "晚上好"),
        ("Hello ,  everyone.", "大家好。"),
        ("I'm good,thanks.", "我很好，谢谢。"),
    ],
)
def test_lookup_normalizes_case_quotes_spacing_and_final_mark(text: str, expected: str) -> None:
    assert lookup_short_phrase(text, "en", "zh") == expected


@pytest.mark.parametrize(
    ("text", "src", "tgt"),
    [
        ("I'm sorry.", "zh", "en"),
        ("I'm sorry.", "en", "ja"),
        ("I'm sorry about the delay.", "en", "zh"),
        ("Deploy.", "en", "zh"),
        ("", "en", "zh"),
        ("Thank you " * 10, "en", "zh"),
    ],
)
def test_lookup_misses(text: str, src: str, tgt: str) -> None:
    assert lookup_short_phrase(text, src, tgt) is None


def test_table_values_are_clean_simplified_chinese() -> None:
    assert phrase_count() > 400
    for key, value in _table().items():
        words = [word for word in re.split(r"[\s,]+", key) if word]
        assert 1 <= len(words) <= 5, key
        assert not re.search(r"[.!?。！？]$", value), key
        assert not any(is_traditional_char(char) or is_mojibake_char(char) for char in value), key
        assert not re.search(r"[A-Za-z]", value), key


def test_short_eval_set_shape() -> None:
    rows = [json.loads(line) for line in SHORT_SET.read_text(encoding="utf-8").splitlines()]
    assert len(rows) >= 200
    assert len({row["id"] for row in rows}) == len(rows)
    for row in rows:
        assert row["src_lang"] == "en" and row["tgt_lang"] == "zh"
        assert 1 <= len(re.findall(r"[A-Za-z']+", row["source"])) <= 4, row["id"]
        assert row["reference"]


# ---------------------------------------------------------------- 接进 Translator


class Recorder:
    def __init__(self, record: ModelRecord) -> None:
        self.record = record
        self.batches: list[list[str]] = []

    def translate_batch(self, sentences: list[str]) -> list[str]:
        self.batches.append(list(sentences))
        return [f"<{sentence}>" for sentence in sentences]


EN_ZH = "opus-mt-eng-zho-tc-big-2022-05-14"


def _install(root: Path) -> None:
    directory = root / EN_ZH
    directory.mkdir(parents=True, exist_ok=True)
    payload = {"id": EN_ZH, "src": "en", "tgt": "zh", "quantization": "int8"}
    (directory / "suiyi-model.json").write_text(json.dumps(payload), encoding="utf-8")


def _translator(root: Path, boxes: list[Recorder], **kwargs: object) -> Translator:
    def factory(record: ModelRecord) -> Recorder:
        backend = Recorder(record)
        boxes.append(backend)
        return backend

    return Translator(root, backend_factory=factory, **kwargs)  # type: ignore[arg-type]


def test_hit_skips_the_model_and_miss_is_translated(tmp_path: Path) -> None:
    _install(tmp_path)
    boxes: list[Recorder] = []
    translator = _translator(tmp_path, boxes)

    result = translator.translate("Thank you. The build failed.", "en", "zh")

    assert boxes[0].batches == [["The build failed."]]
    assert result.text.startswith("谢谢你。")
    assert result.route == [EN_ZH]
    assert translator.short_phrase_hits == 1


def test_whole_input_hit_sends_nothing_to_the_model(tmp_path: Path) -> None:
    _install(tmp_path)
    boxes: list[Recorder] = []
    translator = _translator(tmp_path, boxes)
    assert translator.translate("I'm sorry.", "en", "zh").text == "对不起。"
    assert boxes[0].batches == []


def test_short_phrases_can_be_turned_off(tmp_path: Path) -> None:
    _install(tmp_path)
    boxes: list[Recorder] = []
    translator = _translator(tmp_path, boxes, short_phrases=False)
    translator.translate("I'm sorry.", "en", "zh")
    assert boxes[0].batches == [["I'm sorry."]]
    assert translator.short_phrase_hits == 0


# ---------------------------------------------------------------- <unk> → 标点


def test_unk_between_words_is_enumeration_comma() -> None:
    tokens = ["▁加", "盐", "<unk>", "胡椒", "和", "油", "<unk>"]
    assert unk_marks(tokens, "Add salt, pepper, and oil.") == ["、", None]


def test_unk_mid_sentence_is_full_stop_when_source_has_that_many_sentences() -> None:
    tokens = ["▁出错", "了", "<unk>", "请", "稍后", "再试", "<unk>"]
    assert unk_marks(tokens, "Something went wrong. Please try again later.") == ["。", None]


def test_unk_count_mismatch_with_sentences_is_dropped() -> None:
    tokens = ["▁甲", "<unk>", "乙", "<unk>", "丙", "<unk>"]
    assert unk_marks(tokens, "One. Two three four.") == [None, None, None]


def test_unk_at_start_or_after_bare_word_marker_is_dropped() -> None:
    assert unk_marks(["<unk>", "▁", "告诉", "医生"], "Tell your doctor.") == [None]
    assert unk_marks(["▁", "<unk>", "簆"], "I'm sorry.") == [None]
    assert unk_marks(["▁红色", ",", "<unk>", "绿色"], "Red, green.") == [None]


class JoinSP:
    def decode(self, pieces: list[str]) -> str:
        return "".join(pieces).replace("▁", " ").strip()


def test_decode_hypothesis_maps_unk_for_chinese_only() -> None:
    tokens = ["▁支持", "的", "格式", "包括", "▁PNG", "<unk>", "JPEG", "▁", "和", "▁WebP", "<unk>"]
    source = "Supported formats include PNG, JPEG and WebP."
    assert decode_hypothesis(JoinSP(), tokens, "zh", source) == "支持的格式包括 PNG、JPEG 和 WebP"
    assert decode_hypothesis(JoinSP(), tokens, "zh") == "支持的格式包括 PNGJPEG 和 WebP"
    assert decode_hypothesis(JoinSP(), ["▁a", "<unk>", "b"], "ja", "a, b") == "ab"
