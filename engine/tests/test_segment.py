"""中英日规则分句器测试。

用例分类（parametrize 的每条都算一个用例）：

| 分类 | 覆盖 |
| --- | --- |
| 中文 | 句号、问叹号、顿号不切开、全角句号 |
| 日文 | 句号、省略号连用、平假名 OCR 断行 |
| 英文 | 句号、问叹号、后接小写不断开 |
| 中英混排 | 中文句号后接英文、数字版本号 |
| 引号括号 | 中文引号、英文引号、括号内叹号 |
| 缩写 | Mr/Mrs/Dr、e.g./i.e./etc.、U.S.、白名单外仍切开 |
| 小数 | 3.14、圆周率、版本号 v1.2.3 / Python 3.11 |
| URL | https、带查询串、www |
| 邮箱 | 英文句中邮箱、中文句中邮箱 |
| OCR断行 | 中文拼接、英文空格、行尾连字符、已有句末则不并 |
| 空行段落 | 空行、仅空白的行、CRLF |
| 超长句 | 次级标点再切、硬切、默认 400 |
| 空输入 | 空串、纯空白 |
| 连续标点 | ？！、！！！、...、。。不产生空句 |
"""

from __future__ import annotations

import re
from dataclasses import dataclass

import pytest

from suiyi_engine.segment import Segment, join_segments, split_sentences

_CJK_CHAR = r"[\u4e00-\u9fff\u3400-\u4dbf\u3040-\u30ff\u31f0-\u31ff\uff66-\uff9d]"


def comparable(text: str, *, cjk: bool) -> str:
    """去掉多余空白后的可比形式，与 docs/engine/分句规则.md 的往返定义一致。"""

    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = re.sub(r"-\n(?=[A-Za-z])", "", text)
    text = re.sub(rf"(?<={_CJK_CHAR})[^\S\n]*\n[^\S\n]*(?={_CJK_CHAR})", "", text)
    text = re.sub(r"\n{2,}", "\n\n", text)
    parts: list[str] = []
    for part in text.split("\n\n"):
        part = re.sub(r"[^\S\n]+", " ", part)
        part = re.sub(r" *\n *", " ", part)
        part = part.strip()
        # 句末标点后的空格视为多余空白：英文拼接会补一个，中文拼接会去掉。
        part = re.sub(r"([。！？!?.]) +", r"\1", part)
        if cjk:
            part = re.sub(r"([；;，,、]) +", r"\1", part)
        parts.append(part)
    return "\n\n".join(part for part in parts if part)


@dataclass(frozen=True, slots=True)
class Case:
    id: str
    category: str
    text: str
    expected: tuple[str, ...]
    lang: str | None = None
    tgt: str = "zh"


CASES: tuple[Case, ...] = (
    Case("zh-period", "中文", "我喜欢猫。他也喜欢。", ("我喜欢猫。", "他也喜欢。")),
    Case("zh-bang-question", "中文", "真的吗？太好了！", ("真的吗？", "太好了！")),
    Case("zh-enum-no-split", "中文", "苹果、香蕉和橘子", ("苹果、香蕉和橘子",)),
    Case("zh-fullwidth-bang", "中文", "你好！世界？", ("你好！", "世界？")),
    Case(
        "zh-fullwidth-dot",
        "中文",
        "结束了．下一句。",
        ("结束了．", "下一句。"),
    ),
    Case(
        "ja-period",
        "日文",
        "私は学生です。東京に住んでいます。",
        ("私は学生です。", "東京に住んでいます。"),
        tgt="ja",
    ),
    Case("ja-ellipsis", "日文", "そうだった……次の話だ。", ("そうだった……", "次の話だ。"), tgt="ja"),
    Case("ja-two-dot", "日文", "えっ‥‥本当？", ("えっ‥‥", "本当？"), tgt="ja"),
    Case("ja-ocr-merge", "OCR断行", "これは\nテストです。", ("これはテストです。",), tgt="ja"),
    Case(
        "ja-line-keeps-period",
        "OCR断行",
        "これはテストです。\n次の文です。",
        ("これはテストです。", "次の文です。"),
        tgt="ja",
    ),
    Case(
        "en-period",
        "英文",
        "Hello world. This is a test.",
        ("Hello world.", "This is a test."),
        tgt="en",
    ),
    Case("en-question-bang", "英文", "Really? Yes! OK.", ("Really?", "Yes!", "OK."), tgt="en"),
    Case(
        "en-lowercase-no-split",
        "英文",
        "hello. world stays one.",
        ("hello. world stays one.",),
        tgt="en",
    ),
    Case(
        "en-single-initial",
        "英文",
        "Read item A. Then go.",
        ("Read item A.", "Then go."),
        tgt="en",
    ),
    Case(
        "mixed-python",
        "中英混排",
        "我在用 Python 3.11 写代码。It works!",
        ("我在用 Python 3.11 写代码。", "It works!"),
    ),
    Case(
        "mixed-version",
        "中英混排",
        "版本 v1.2.3 已发布。请更新。",
        ("版本 v1.2.3 已发布。", "请更新。"),
    ),
    Case(
        "zh-quotes",
        "引号括号",
        "他说：「你好。」然后离开了。",
        ("他说：「你好。」", "然后离开了。"),
    ),
    Case(
        "en-quotes",
        "引号括号",
        '"Hello." She smiled.',
        ('"Hello."', "She smiled."),
        tgt="en",
    ),
    Case(
        "en-quotes-inner",
        "引号括号",
        'She said "Hi." Then left.',
        ('She said "Hi."', "Then left."),
        tgt="en",
    ),
    Case(
        "zh-parens",
        "引号括号",
        "结束了（真的！）。下一句。",
        ("结束了（真的！）。", "下一句。"),
    ),
    Case(
        "en-parens",
        "引号括号",
        "Done (really!). Next.",
        ("Done (really!).", "Next."),
        tgt="en",
    ),
    Case(
        "abbr-mr",
        "缩写",
        "Mr. Smith left. He was tired.",
        ("Mr. Smith left.", "He was tired."),
        tgt="en",
    ),
    Case(
        "abbr-titles-us",
        "缩写",
        "Mr. Smith and Mrs. Jones met Dr. Lee in the U.S. yesterday.",
        ("Mr. Smith and Mrs. Jones met Dr. Lee in the U.S. yesterday.",),
        tgt="en",
    ),
    Case(
        "abbr-eg",
        "缩写",
        "See e.g. this note. Done.",
        ("See e.g. this note.", "Done."),
        tgt="en",
    ),
    Case(
        "abbr-etc-ie",
        "缩写",
        "Use apples, etc. and pears, i.e. fruit. Then sit.",
        ("Use apples, etc. and pears, i.e. fruit.", "Then sit."),
        tgt="en",
    ),
    Case(
        "abbr-not-on-list",
        "缩写",
        "Assoc. Then go.",
        ("Assoc.", "Then go."),
        tgt="en",
    ),
    Case(
        "decimal-pi",
        "小数",
        "Pi is 3.14 today. Yes.",
        ("Pi is 3.14 today.", "Yes."),
        tgt="en",
    ),
    Case(
        "decimal-zh",
        "小数",
        "圆周率约 3.1415。记住。",
        ("圆周率约 3.1415。", "记住。"),
    ),
    Case(
        "version-en",
        "小数",
        "Install v1.2.3 now. Please.",
        ("Install v1.2.3 now.", "Please."),
        tgt="en",
    ),
    Case(
        "url-https",
        "URL",
        "Go to https://example.com/a.b/c now. Please.",
        ("Go to https://example.com/a.b/c now.", "Please."),
        tgt="en",
    ),
    Case(
        "url-query",
        "URL",
        "Go to https://example.com/a.b/c?q=1.2 now. Please.",
        ("Go to https://example.com/a.b/c?q=1.2 now.", "Please."),
        tgt="en",
    ),
    Case(
        "url-www",
        "URL",
        "See www.example.com/a.b today. Done.",
        ("See www.example.com/a.b today.", "Done."),
        tgt="en",
    ),
    Case(
        "email-en",
        "邮箱",
        "Mail foo@bar.com today. Thanks.",
        ("Mail foo@bar.com today.", "Thanks."),
        tgt="en",
    ),
    Case(
        "email-zh",
        "邮箱",
        "联系 a.b@x.io 即可。谢谢。",
        ("联系 a.b@x.io 即可。", "谢谢。"),
    ),
    Case("ocr-zh-merge", "OCR断行", "这是一句\n被断开的话。", ("这是一句被断开的话。",)),
    Case(
        "ocr-zh-padded",
        "OCR断行",
        "这是一句 \n 被断开的话。",
        ("这是一句被断开的话。",),
    ),
    Case(
        "ocr-zh-keep-break",
        "OCR断行",
        "这是一句。\n这是下一句。",
        ("这是一句。", "这是下一句。"),
    ),
    Case("ocr-en-space", "OCR断行", "Hello\nworld.", ("Hello world.",), tgt="en"),
    Case(
        "ocr-hyphen",
        "OCR断行",
        "This is a long-\nword in a sentence.",
        ("This is a longword in a sentence.",),
        tgt="en",
    ),
    Case(
        "ocr-mixed-space",
        "OCR断行",
        "使用\nPython。",
        ("使用 Python。",),
    ),
    Case("para-blank", "空行段落", "第一段。\n\n第二段。", ("第一段。", "第二段。")),
    Case("para-space-line", "空行段落", "甲。\n \n乙。", ("甲。", "乙。")),
    Case("para-crlf", "空行段落", "你好。\r\n\r\n世界。", ("你好。", "世界。")),
    Case("empty", "空输入", "", ()),
    Case("blank", "空输入", "  \n\t\n  ", ()),
    Case("ideo-space", "空输入", "　你好。　", ("你好。",)),
    Case("ellipsis-en", "连续标点", "Wait... Really?", ("Wait...", "Really?"), tgt="en"),
    Case("ellipsis-only", "连续标点", "End...", ("End...",), tgt="en"),
    Case("zh-ellipsis", "连续标点", "什么……然后呢。", ("什么……", "然后呢。")),
    Case("bang-run", "连续标点", "哇！！！真的。", ("哇！！！", "真的。")),
    Case("question-bang", "连续标点", "真的吗？！太好了。", ("真的吗？！", "太好了。")),
    Case("double-period", "连续标点", "。。", ("。。",)),
    Case("punct-only", "连续标点", "？！", ("？！",)),
    Case("no-space-bang", "连续标点", "Hello!World", ("Hello!", "World"), tgt="en"),
    Case("single-ellipsis-no-split", "连续标点", "未完…继续说完。", ("未完…继续说完。",)),
)


@pytest.mark.parametrize("case", CASES, ids=[case.id for case in CASES])
def test_split_expected(case: Case) -> None:
    segments = split_sentences(case.text, lang=case.lang)
    assert tuple(segment.text for segment in segments) == case.expected


@pytest.mark.parametrize("case", CASES, ids=[case.id for case in CASES])
def test_roundtrip_same_language(case: Case) -> None:
    segments = split_sentences(case.text, lang=case.lang)
    joined = join_segments([segment.text for segment in segments], segments, case.tgt)
    cjk = case.tgt in {"zh", "ja"}
    assert comparable(joined, cjk=cjk) == comparable(case.text, cjk=cjk)


@pytest.mark.parametrize("case", CASES, ids=[case.id for case in CASES])
def test_offsets_cover_sentence_without_overlap(case: Case) -> None:
    segments = split_sentences(case.text, lang=case.lang)
    previous = 0
    for segment in segments:
        assert segment.text
        assert segment.text == segment.text.strip()
        assert segment.start < segment.end <= len(case.text)
        assert segment.start >= previous
        span = case.text[segment.start : segment.end]
        if span != segment.text:
            assert "\n" in span or "\r" in span
        previous = segment.end
    for left, right in zip(segments, segments[1:], strict=False):
        assert left.end <= right.start


def test_required_categories_are_covered() -> None:
    found = {case.category for case in CASES}
    required = {
        "中文",
        "日文",
        "英文",
        "中英混排",
        "引号括号",
        "缩写",
        "小数",
        "URL",
        "邮箱",
        "OCR断行",
        "空行段落",
        "空输入",
        "连续标点",
    }
    assert required <= found
    assert len(CASES) >= 30


def test_english_trailing_space_and_exact_span() -> None:
    source = "Hello. World."
    segments = split_sentences(source)
    assert segments[0] == Segment("Hello.", 0, 6, " ")
    assert source[segments[1].start : segments[1].end] == "World."
    assert join_segments(["A.", "B."], segments, "en") == "A. B."
    assert join_segments(["甲。", "乙。"], segments, "zh") == "甲。乙。"


def test_multiple_spaces_collapse_on_english_join() -> None:
    source = "Hello.   World."
    segments = split_sentences(source)
    assert segments[0].trailing == "   "
    assert join_segments([segment.text for segment in segments], segments, "en") == "Hello. World."


def test_cjk_join_drops_spaces_between_sentences() -> None:
    source = "你好。   世界。"
    segments = split_sentences(source)
    assert segments[0].trailing == "   "
    assert join_segments([segment.text for segment in segments], segments, "zh") == "你好。世界。"
    assert join_segments(["你好。", "世界。"], segments, "ja") == "你好。世界。"


def test_paragraph_trailing_is_preserved() -> None:
    source = "第一段。\n\n第二段。"
    segments = split_sentences(source)
    assert "\n\n" in segments[0].trailing
    assert join_segments(["A", "B"], segments, "en") == "A\n\nB"
    assert join_segments(["甲", "乙"], segments, "zh") == "甲\n\n乙"


def test_crlf_paragraph_trailing_keeps_original_newlines() -> None:
    source = "你好。\r\n\r\n世界。"
    segments = split_sentences(source)
    assert segments[0].trailing == "\r\n\r\n"
    assert source[segments[0].start : segments[0].end] == "你好。"
    assert join_segments(["甲。", "乙。"], segments, "zh") == "甲。\r\n\r\n乙。"


def test_ocr_span_includes_newline_but_text_is_merged() -> None:
    source = "这是一句\n被断开的话。"
    segments = split_sentences(source)
    assert len(segments) == 1
    assert segments[0].text == "这是一句被断开的话。"
    span = source[segments[0].start : segments[0].end]
    assert "\n" in span
    assert span != segments[0].text


def test_hyphen_break_is_rejoined_and_span_keeps_source() -> None:
    source = "This is a long-\nword in a sentence."
    segments = split_sentences(source)
    assert segments[0].text == "This is a longword in a sentence."
    assert "-\n" in source[segments[0].start : segments[0].end]


def test_leading_and_trailing_whitespace_not_in_text() -> None:
    source = "  Hello.  "
    segments = split_sentences(source)
    assert segments[0].text == "Hello."
    assert source[segments[0].start : segments[0].end] == "Hello."
    assert segments[0].trailing == "  "


def test_lang_changes_only_newline_merge() -> None:
    source = "你好\n世界"
    assert split_sentences(source, lang=None)[0].text == "你好世界"
    assert split_sentences(source, lang="zh")[0].text == "你好世界"
    assert split_sentences(source, lang="zh-CN")[0].text == "你好世界"
    assert split_sentences(source, lang="ja_JP")[0].text == "你好世界"
    assert split_sentences(source, lang="en")[0].text == "你好 世界"
    assert split_sentences(source, lang="EN")[0].text == "你好 世界"
    assert split_sentences("Hello\nworld", lang="zh")[0].text == "Hello world"


def test_no_nfkc_on_fullwidth_letters() -> None:
    source = "ＡＢＣ．ＤＥＦ。"
    segments = split_sentences(source)
    assert [segment.text for segment in segments] == [source]
    assert "ABC" not in segments[0].text
    spaced = "Ａ． Ｂ。"
    spaced_segments = split_sentences(spaced)
    assert [segment.text for segment in spaced_segments] == ["Ａ．", "Ｂ。"]
    assert spaced[spaced_segments[0].start : spaced_segments[0].end] == "Ａ．"


def test_secondary_split_then_keep_short_tail() -> None:
    source = "甲乙丙丁，戊己庚辛，壬癸"
    segments = split_sentences(source, max_chars=6)
    assert [segment.text for segment in segments] == ["甲乙丙丁，", "戊己庚辛，", "壬癸"]
    assert all(len(segment.text) <= 6 for segment in segments)


def test_hard_cut_when_no_secondary_punctuation() -> None:
    source = "a" * 10
    segments = split_sentences(source, max_chars=4)
    assert [segment.text for segment in segments] == ["aaaa", "aaaa", "aa"]
    assert "".join(segment.text for segment in segments) == source
    assert [(segment.start, segment.end) for segment in segments] == [(0, 4), (4, 8), (8, 10)]


def test_default_max_chars_is_400() -> None:
    assert len(split_sentences("啊" * 400)) == 1
    segments = split_sentences("啊" * 401)
    assert [len(segment.text) for segment in segments] == [400, 1]
    assert "".join(segment.text for segment in segments) == "啊" * 401


def test_long_input_never_emits_empty_or_overlong_sentences() -> None:
    samples = [case.text for case in CASES if case.text.strip()]
    samples.append("啊" * 50 + "，" + "bcd " * 30 + "结束。Next sentence is here.")
    samples.append("https://example.com/a.b?q=1\n\n" + "字" * 25)
    for sample in samples:
        segments = split_sentences(sample, max_chars=20)
        assert segments, sample
        for segment in segments:
            assert segment.text.strip() == segment.text != ""
            assert len(segment.text) <= 20
            assert 0 <= segment.start < segment.end <= len(sample)


def test_join_length_mismatch() -> None:
    segments = split_sentences("你好。世界。")
    with pytest.raises(ValueError, match="长度不一致"):
        join_segments(["只有一句"], segments, "zh")


def test_join_empty() -> None:
    assert join_segments([], [], "en") == ""


@pytest.mark.parametrize("bad", [0, -1, True])
def test_max_chars_must_be_positive_int(bad: object) -> None:
    with pytest.raises(ValueError, match="max_chars"):
        split_sentences("你好。", max_chars=bad)  # type: ignore[arg-type]


def test_fig_and_number_abbreviation_not_cut() -> None:
    assert split_sentences("See Fig. 1 now.")[0].text == "See Fig. 1 now."
    segments = split_sentences("No. 1 is ready. Next.")
    assert [segment.text for segment in segments] == ["No. 1 is ready.", "Next."]


def test_us_army_sentence_continues_past_abbreviation() -> None:
    segments = split_sentences("The U.S. army moved. Then it stopped.")
    assert [segment.text for segment in segments] == [
        "The U.S. army moved.",
        "Then it stopped.",
    ]
