"""中英日规则分句。

把剪贴板或 OCR 文本切成句子，供句级翻译使用，并保留足够的位置与分隔信息，
使译文能按原段落结构拼回。只使用标准库，不依赖语种检测模型。

偏移约定（相对传入的原文，不做 NFKC）：

- ``source[segment.start:segment.end]`` 是该句在原文中的半开区间。
- 没有断行合并时，该区间与 ``segment.text`` 相同。
- 有 OCR 断行合并时，区间仍覆盖合并前的原文（含被吃掉的换行、行尾连字符），
  ``segment.text`` 是合并后的句子。
- ``trailing`` 是本句结束到下一句开始之间的原文，最后一句则直到原文末尾。
"""

from __future__ import annotations

import re
from dataclasses import dataclass

__all__ = ["Segment", "join_segments", "split_sentences"]

# 无条件句末（中日，以及半角 ! ?）。英文句号另走更严的规则。
_ALWAYS_END = frozenset("。！？!?")
_DOTS = frozenset(".．")
_ELLIPSIS = frozenset("…‥")
_CLOSERS = frozenset("」』”’）】〉》\"')]}>］｝＞＂＇")
_OPENERS = frozenset("\"“‘「『'（(《〈【[<＜＂＇")
_SECONDARY = frozenset("；;，,、")
_URL_TRAIL = frozenset(".,;:!?")

# 句号前的缩写。词表用大小写不敏感；字母缩写（U.S.）保持大写，避免把 a.b. 误保护。
_ABBREV_END = re.compile(
    r"""(?x)
    (?:
        (?i:\b(?:
            mr|mrs|ms|dr|prof|sr|jr|st|etc|vs|cf|inc|ltd|co|corp|fig|vol|no|pp|ed|
            gen|sgt|capt|col|lt|mt|ave|blvd|dept|univ|gov|sen|rep|rev|hon|
            jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec|al
        )\.)
        |
        (?i:\b(?:e\.g|i\.e|a\.m|p\.m|p\.s|ph\.d)\.)
        |
        \b(?:[A-Z]\.){1,}[A-Z]\.
    )
    $
    """
)

# URL / 邮箱内部的 . ? ! 不作为句界。中文标点不吞进 URL，避免把后面的句子吃掉。
_URL_RE = re.compile(r"""(?ix)(?:https?://|www\.)[A-Za-z0-9\-._~:/?#\[\]@!$&'*+,;=%]+""")
_EMAIL_RE = re.compile(r"(?i)[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}")

_CJK_TARGET = frozenset(
    {
        "zh",
        "ja",
        "jp",
        "cn",
        "cmn",
        "jpn",
        "yue",
        "chinese",
        "japanese",
    }
)


@dataclass(frozen=True, slots=True)
class Segment:
    """一个句子及其在原文中的位置。

    Attributes:
        text: 去掉首尾空白并完成断行合并后的句子。
        start: 原文起始偏移（含）。
        end: 原文结束偏移（不含）。
        trailing: 句后原样保留的分隔（空格、换行等），直到下一句或原文末尾。
    """

    text: str
    start: int
    end: int
    trailing: str


def split_sentences(
    text: str,
    lang: str | None = None,
    max_chars: int = 400,
) -> list[Segment]:
    """把 ``text`` 切成句子。

    Args:
        text: 原文。偏移相对这个字符串，不会先做 NFKC 或换行归一化。
        lang: ``None`` 时按字符决定换行如何合并。``en`` / ``en-US`` 等把单换行
            合并成空格；``zh`` / ``ja`` 及其地区变体在两侧都是中日文字时直接拼接，
            否则也合并成空格。句末标点规则与 ``lang`` 无关。
        max_chars: 单句最大字符数（Unicode 码位）。超长时先在 ``；;，,、`` 处再切，
            仍超长则硬切。必须是 ``>= 1`` 的整数。

    Returns:
        按出现顺序排列的句子。空字符串和纯空白返回空列表，不会产生空句。

    Raises:
        ValueError: ``max_chars`` 不是大于等于 1 的整数。
    """

    if isinstance(max_chars, bool) or not isinstance(max_chars, int) or max_chars < 1:
        raise ValueError("max_chars 必须是 >= 1 的整数")
    if text.strip() == "":
        return []

    parts: list[tuple[str, int, int]] = []
    for piece in _build_pieces(text, _newline_policy(lang)):
        spans = _protected_spans(piece.cleaned)
        for start, end in _sentence_ranges(piece.cleaned, spans):
            parts.extend(_split_long(piece.cleaned, piece.orig, start, end, max_chars))
    return _with_trailing(text, parts)


def join_segments(
    translated: list[str],
    segments: list[Segment],
    tgt_lang: str,
) -> str:
    """按目标语把译文拼回，并保留原段落换行。

    含换行的 ``trailing`` 原样接上，用来还原段落和断行。只有空格（或为空）时：
    目标语为中文或日文则句间不加空格，为英文则句间加一个空格。最后一句末尾的
    纯空格不保留。

    Args:
        translated: 与 ``segments`` 一一对应的译文。
        segments: :func:`split_sentences` 的输出。
        tgt_lang: 目标语。``zh`` / ``ja`` 及其地区变体不加句间空格，其余加一个空格。

    Returns:
        拼接后的文本。``segments`` 为空时返回空字符串。

    Raises:
        ValueError: ``translated`` 与 ``segments`` 长度不一致。
    """

    if len(translated) != len(segments):
        raise ValueError(f"translated 与 segments 长度不一致：{len(translated)} != {len(segments)}")
    if not segments:
        return ""
    cjk = _target_is_cjk(tgt_lang)
    out: list[str] = []
    last = len(segments) - 1
    for index, (piece, segment) in enumerate(zip(translated, segments, strict=True)):
        out.append(piece)
        out.append(_separator(segment.trailing, cjk=cjk, is_last=index == last))
    return "".join(out)


@dataclass
class _Piece:
    """合并断行之后的一块文本，以及每个字符对应的原文下标（插入的空格为 -1）。"""

    cleaned: str
    orig: list[int]


def _newline_policy(lang: str | None) -> str:
    if lang is None:
        return "auto"
    key = lang.strip().lower().replace("_", "-")
    if key in {"en", "eng", "english"} or key.startswith("en-"):
        return "en"
    return "auto"


def _target_is_cjk(tgt_lang: str) -> bool:
    key = tgt_lang.strip().lower().replace("_", "-")
    if key in _CJK_TARGET or key.startswith(("zh-", "ja-", "jp-")):
        return True
    return False


def _is_cjk_letter(ch: str) -> bool:
    code = ord(ch)
    return (
        0x4E00 <= code <= 0x9FFF
        or 0x3400 <= code <= 0x4DBF
        or 0x3040 <= code <= 0x30FF
        or 0x31F0 <= code <= 0x31FF
        or 0xFF66 <= code <= 0xFF9D
        or 0x20000 <= code <= 0x2A6DF
    )


def _break_count(sep: str) -> int:
    count = 0
    index = 0
    length = len(sep)
    while index < length:
        if sep.startswith("\r\n", index):
            count += 1
            index += 2
        elif sep[index] in "\r\n":
            count += 1
            index += 1
        else:
            index += 1
    return count


def _iter_lines(text: str):
    """产出 ``(line_start, line_end, sep_start, sep_end)``。

    连续换行并进同一分隔，这样空行不会变成单独的空行。只含空格的行仍会单独出现。
    """

    length = len(text)
    index = 0
    while index < length:
        line_start = index
        while index < length and text[index] not in "\r\n":
            index += 1
        line_end = index
        sep_start = index
        while index < length and text[index] in "\r\n":
            if text.startswith("\r\n", index):
                index += 2
            else:
                index += 1
        yield line_start, line_end, sep_start, index


def _strip_bounds(text: str, start: int, end: int) -> tuple[int, int]:
    while start < end and text[start].isspace():
        start += 1
    while end > start and text[end - 1].isspace():
        end -= 1
    return start, end


def _is_protected_dot(text: str, dot_index: int) -> bool:
    window = text[max(0, dot_index - 24) : dot_index + 1]
    return _ABBREV_END.search(window) is not None


def _ends_sentence(line: str) -> bool:
    """这一行（已去掉首尾空白）是否以句末标点结束。缩写后的句号不算。"""

    if not line:
        return False
    index = len(line) - 1
    while index >= 0 and line[index] in _CLOSERS:
        index -= 1
    if index < 0:
        return False
    ch = line[index]
    if ch in _ELLIPSIS:
        return index > 0 and line[index - 1] == ch
    if ch in _ALWAYS_END:
        return True
    if ch in _DOTS:
        return not _is_protected_dot(line, index)
    return False


def _merge_mode(prev: str, nxt: str, policy: str) -> str:
    if prev.endswith("-") and nxt[:1].isascii() and nxt[:1].isalpha():
        return "dehyphen"
    if policy != "en" and prev and nxt and _is_cjk_letter(prev[-1]) and _is_cjk_letter(nxt[0]):
        return "concat"
    return "space"


def _build_pieces(text: str, policy: str) -> list[_Piece]:
    buf_chars: list[str] = []
    buf_orig: list[int] = []
    pieces: list[_Piece] = []
    prev_line: str | None = None

    def flush() -> None:
        nonlocal buf_chars, buf_orig, prev_line
        if buf_chars:
            start = 0
            end = len(buf_chars)
            while start < end and buf_chars[start].isspace():
                start += 1
            while end > start and buf_chars[end - 1].isspace():
                end -= 1
            if start < end and any(index >= 0 for index in buf_orig[start:end]):
                pieces.append(_Piece("".join(buf_chars[start:end]), buf_orig[start:end]))
        buf_chars = []
        buf_orig = []
        prev_line = None

    def append_range(start: int, end: int) -> None:
        for index in range(start, end):
            buf_chars.append(text[index])
            buf_orig.append(index)

    for line_start, line_end, sep_start, sep_end in _iter_lines(text):
        content_start, content_end = _strip_bounds(text, line_start, line_end)
        if content_start >= content_end:
            flush()
            continue
        line = text[content_start:content_end]
        if prev_line is None:
            append_range(content_start, content_end)
        elif _ends_sentence(prev_line):
            flush()
            append_range(content_start, content_end)
        else:
            mode = _merge_mode(prev_line, line, policy)
            if mode == "dehyphen":
                if buf_chars and buf_chars[-1] == "-":
                    buf_chars.pop()
                    buf_orig.pop()
            elif mode == "space" and (not buf_chars or not buf_chars[-1].isspace()):
                buf_chars.append(" ")
                buf_orig.append(-1)
            append_range(content_start, content_end)
        prev_line = line
        if _break_count(text[sep_start:sep_end]) >= 2:
            flush()
    flush()
    return pieces


def _protected_spans(text: str) -> list[tuple[int, int]]:
    spans: list[tuple[int, int]] = []
    for match in _URL_RE.finditer(text):
        start, end = match.start(), match.end()
        while end > start and text[end - 1] in _URL_TRAIL:
            end -= 1
        if end > start:
            spans.append((start, end))
    covered = spans[:]
    for match in _EMAIL_RE.finditer(text):
        start, end = match.span()
        if any(left <= start < right for left, right in covered):
            continue
        spans.append((start, end))
    spans.sort()
    return spans


def _in_span(index: int, spans: list[tuple[int, int]]) -> bool:
    return any(start <= index < end for start, end in spans)


def _follows_dot_boundary(text: str, index: int) -> bool:
    """句号（或省略号点串）之后是否构成句界。"""

    cursor = index
    length = len(text)
    while cursor < length and text[cursor] in _CLOSERS:
        cursor += 1
    if cursor >= length:
        return True
    if text[cursor].isspace():
        while cursor < length and text[cursor].isspace():
            cursor += 1
        if cursor >= length:
            return True
        nxt = text[cursor]
        return nxt.isupper() or nxt.isdigit() or nxt in _OPENERS or _is_cjk_letter(nxt)
    return _is_cjk_letter(text[cursor])


def _consume_tail(text: str, index: int) -> int:
    """把紧跟的右引号、右括号和连续句末标点并进当前句，不含后面的空白。"""

    length = len(text)
    while index < length and not text[index].isspace():
        ch = text[index]
        if ch in _CLOSERS or ch in _ALWAYS_END or ch in _ELLIPSIS or ch in _DOTS:
            index += 1
            continue
        break
    return index


def _ender_span_end(text: str, index: int, spans: list[tuple[int, int]]) -> int | None:
    """若 ``text[index]`` 是句末序列的起点，返回该序列结束后的下标。"""

    if _in_span(index, spans):
        return None
    ch = text[index]
    length = len(text)
    if ch in _ALWAYS_END:
        cursor = index + 1
        while cursor < length and text[cursor] in _ALWAYS_END:
            cursor += 1
        return _consume_tail(text, cursor)
    if ch in _ELLIPSIS:
        if index > 0 and text[index - 1] == ch:
            return None
        cursor = index + 1
        while cursor < length and text[cursor] == ch:
            cursor += 1
        if cursor - index < 2:
            return None
        return _consume_tail(text, cursor)
    if ch in _DOTS:
        if index > 0 and text[index - 1] in _DOTS:
            return None
        cursor = index + 1
        while cursor < length and text[cursor] in _DOTS:
            cursor += 1
        if _is_protected_dot(text, cursor - 1):
            return None
        if not _follows_dot_boundary(text, cursor):
            return None
        return _consume_tail(text, cursor)
    return None


def _sentence_ranges(text: str, spans: list[tuple[int, int]]) -> list[tuple[int, int]]:
    length = len(text)
    index = 0
    while index < length and text[index].isspace():
        index += 1
    ranges: list[tuple[int, int]] = []
    start = index
    while index < length:
        end_at = _ender_span_end(text, index, spans)
        if end_at is None or end_at <= start:
            index += 1
            continue
        ranges.append((start, end_at))
        index = end_at
        while index < length and text[index].isspace():
            index += 1
        start = index
    if start < length:
        end = length
        while end > start and text[end - 1].isspace():
            end -= 1
        if end > start:
            ranges.append((start, end))
    return ranges


def _bounds(orig: list[int], start: int, end: int) -> tuple[int, int] | None:
    indexes = [orig[index] for index in range(start, end) if orig[index] >= 0]
    if not indexes:
        return None
    return indexes[0], indexes[-1] + 1


def _last_secondary(text: str, start: int, window_end: int) -> int | None:
    cut: int | None = None
    for index in range(start, window_end):
        if text[index] in _SECONDARY:
            cut = index + 1
    if cut is None or cut <= start:
        return None
    return cut


def _split_long(
    cleaned: str,
    orig: list[int],
    start: int,
    end: int,
    max_chars: int,
) -> list[tuple[str, int, int]]:
    parts: list[tuple[str, int, int]] = []
    cursor = start
    while cursor < end:
        remaining = end - cursor
        if remaining <= max_chars:
            chunk_end = end
        else:
            window_end = cursor + max_chars
            cut = _last_secondary(cleaned, cursor, window_end)
            chunk_end = cut if cut is not None else window_end
        if chunk_end <= cursor:
            chunk_end = min(end, cursor + max_chars)
        raw_start = cursor
        raw_end = chunk_end
        while raw_start < raw_end and cleaned[raw_start].isspace():
            raw_start += 1
        while raw_end > raw_start and cleaned[raw_end - 1].isspace():
            raw_end -= 1
        if raw_start < raw_end:
            bounds = _bounds(orig, raw_start, raw_end)
            if bounds is not None:
                parts.append((cleaned[raw_start:raw_end], bounds[0], bounds[1]))
        cursor = chunk_end
    return parts


def _with_trailing(text: str, parts: list[tuple[str, int, int]]) -> list[Segment]:
    segments: list[Segment] = []
    for index, (piece, start, end) in enumerate(parts):
        if index + 1 < len(parts):
            next_start = parts[index + 1][1]
            trailing = text[end:next_start] if next_start >= end else ""
        else:
            trailing = text[end:]
        segments.append(Segment(piece, start, end, trailing))
    return segments


def _separator(trailing: str, *, cjk: bool, is_last: bool) -> str:
    if any(ch in "\r\n" for ch in trailing):
        return trailing
    if is_last or cjk:
        return ""
    return " "
