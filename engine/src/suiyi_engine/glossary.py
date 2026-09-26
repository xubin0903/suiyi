"""术语表：在原文里找术语，在译文里查术语是否按约定写法出现。

#78 专业领域评测用它算术语准确率。只依赖标准库，不接入默认翻译流程。

术语条目有两类：

- ``keep``：专有名词，各语种写法相同或几乎相同（Kubernetes、CNCF、gRPC）；
- ``fixed``：有固定译法的术语（container orchestration ↔ 容器编排）。

每个语种可以有多种写法，第一种是规范写法（术语保护时强制用它），其余是评测时也算对的
变体（例如「竞态条件」「竞争条件」）。

匹配规则：

- 英文：词边界匹配；空格与连字符互通（fine-tuned = fine tuned）；允许复数 s / es；
  ``keep`` 条目在原文里区分大小写，``fixed`` 不区分；在译文里一律不区分大小写。
- 中文、日文：去掉全部空白后做子串匹配（「OAuth 令牌」=「OAuth令牌」），区分大小写。
- 一段原文里多个条目重叠时，长的优先（cache hit rate 优先于 cache）。

术语保护原型（#78，只在评测里用）：:func:`protect` 把原文里的术语换成占位符（``ZXQ``、``ZXW``……），
翻译后 :func:`restore` 把占位符换回目标语的规范写法（``keep`` 条目保留原文写法）。
占位符格式的取舍见 docs/engine/专业领域评测.md 的「术语保护原型」。
"""

from __future__ import annotations

import json
import re
import unicodedata
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

KEEP = "keep"
FIXED = "fixed"
KINDS = (KEEP, FIXED)
LANGS = ("en", "zh", "ja")
_SPACE_LANGS = frozenset({"en"})
_CJK_LANGS = frozenset({"zh", "ja"})
_CJK = "\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uff01-\uff60\u3000-\u303f"
_CJK_GAP = re.compile(rf"(?<=[{_CJK}])[ \t]+(?=[{_CJK}])")


class GlossaryError(ValueError):
    """术语表格式错误。"""


@dataclass(frozen=True, slots=True)
class Term:
    """一个术语条目。``forms[lang]`` 的第一项是规范写法。"""

    id: str
    kind: str
    domain: str
    forms: Mapping[str, tuple[str, ...]]

    def canonical(self, lang: str) -> str | None:
        values = self.forms.get(lang, ())
        return values[0] if values else None


@dataclass(frozen=True, slots=True)
class TermMatch:
    """原文中的一次命中。``start`` / ``end`` 是原文里的字符下标。"""

    term: Term
    start: int
    end: int
    surface: str


def load_glossary(path: Path | str) -> tuple[Term, ...]:
    """读取 JSON 术语表（``{"terms": [...]}``）。"""

    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(data, Mapping) or not isinstance(data.get("terms"), list):
        raise GlossaryError(f"{path}：顶层必须是含 terms 数组的对象")
    return parse_terms(data["terms"])


def parse_terms(items: Iterable[object]) -> tuple[Term, ...]:
    terms: list[Term] = []
    seen: set[str] = set()
    for index, item in enumerate(items, 1):
        if not isinstance(item, Mapping):
            raise GlossaryError(f"terms[{index}] 必须是对象")
        term_id = item.get("id")
        kind = item.get("kind")
        domain = item.get("domain", "")
        if not isinstance(term_id, str) or not term_id:
            raise GlossaryError(f"terms[{index}] 缺少 id")
        if term_id in seen:
            raise GlossaryError(f"术语 id 重复：{term_id}")
        seen.add(term_id)
        if kind not in KINDS:
            raise GlossaryError(f"{term_id}：kind 必须是 {' / '.join(KINDS)}")
        if not isinstance(domain, str):
            raise GlossaryError(f"{term_id}：domain 必须是字符串")
        forms: dict[str, tuple[str, ...]] = {}
        for lang in LANGS:
            values = item.get(lang, [])
            if not isinstance(values, list) or not all(
                isinstance(value, str) and value.strip() for value in values
            ):
                raise GlossaryError(f"{term_id}：{lang} 必须是非空字符串数组")
            if values:
                forms[lang] = tuple(values)
        if "en" not in forms:
            raise GlossaryError(f"{term_id}：至少要有 en 写法")
        terms.append(Term(id=term_id, kind=kind, domain=domain, forms=forms))
    return tuple(terms)


def find_terms(
    text: str, lang: str, glossary: Sequence[Term], *, every: bool = False
) -> list[TermMatch]:
    """找出 ``text`` 里出现的术语，按位置排序；重叠时保留更长的那个。

    默认每个术语只取第一次出现（评测按「术语 × 条目」计数）；
    ``every=True`` 时取全部出现（术语保护用）。
    """

    candidates: list[TermMatch] = []
    for term in glossary:
        for form in term.forms.get(lang, ()):
            for start, end in _find_all(text, form, lang, case_sensitive=term.kind == KEEP):
                candidates.append(TermMatch(term, start, end, text[start:end]))
    candidates.sort(key=lambda match: (-(match.end - match.start), match.start))
    chosen: list[TermMatch] = []
    used: set[str] = set()
    for match in candidates:
        if any(match.start < other.end and other.start < match.end for other in chosen):
            continue
        if not every and match.term.id in used:
            continue
        chosen.append(match)
        used.add(match.term.id)
    chosen.sort(key=lambda match: match.start)
    return chosen


def term_present(text: str, term: Term, lang: str) -> bool:
    """译文 ``text`` 里是否出现该术语在 ``lang`` 下的任一写法（不区分大小写）。"""

    return any(
        _find_all(text, form, lang, case_sensitive=False) for form in term.forms.get(lang, ())
    )


def present_form(text: str, term: Term, lang: str) -> str | None:
    """译文里出现的第一种写法，没有则 ``None``。"""

    for form in term.forms.get(lang, ()):
        if _find_all(text, form, lang, case_sensitive=False):
            return form
    return None


def _find_all(text: str, form: str, lang: str, *, case_sensitive: bool) -> list[tuple[int, int]]:
    if lang in _SPACE_LANGS:
        pattern = _en_pattern(form, case_sensitive)
        return [(match.start(), match.end()) for match in pattern.finditer(text)]
    return _cjk_find_all(text, form, case_sensitive)


@lru_cache(maxsize=4096)
def _en_pattern(form: str, case_sensitive: bool) -> re.Pattern[str]:
    parts = re.split(r"[\s\-]+", form.strip())
    body = r"[\s\-]*".join(re.escape(part) for part in parts)
    plural = r"(?:s|es)?" if form[-1:].isalpha() else ""
    flags = 0 if case_sensitive else re.IGNORECASE
    return re.compile(rf"(?<![A-Za-z0-9]){body}{plural}(?![A-Za-z0-9])", flags)


def _cjk_find_all(text: str, form: str, case_sensitive: bool) -> list[tuple[int, int]]:
    stripped, positions = _strip_spaces(text)
    needle, _ = _strip_spaces(form)
    if not needle:
        return []
    haystack = stripped if case_sensitive else stripped.casefold()
    target = needle if case_sensitive else needle.casefold()
    if len(haystack) != len(stripped) or len(target) != len(needle):
        haystack, target = stripped.lower(), needle.lower()
    found: list[tuple[int, int]] = []
    start = haystack.find(target)
    while start >= 0:
        end = start + len(target)
        found.append((positions[start], positions[end - 1] + 1))
        start = haystack.find(target, start + 1)
    return found


def _strip_spaces(text: str) -> tuple[str, list[int]]:
    kept: list[str] = []
    positions: list[int] = []
    for index, char in enumerate(text):
        if char.isspace() or unicodedata.category(char) == "Zs":
            continue
        kept.append(char)
        positions.append(index)
    return "".join(kept), positions


# ---------------------------------------------------------------- 术语保护原型

# Marian（opus-mt）在 en→zh / zh→en 上都能原样抄过去的占位符：
# 大写 ZX + 一个字母，超过 5 个再加数字。
# TERM0、__0__、{0} 这类写法会被翻译、拆开或丢掉，实测见 docs/engine/专业领域评测.md。
_PLACEHOLDER_LETTERS = "QWJKV"
_PLACEHOLDER_ANY = re.compile(r"zx[qwjkv]\d*", re.IGNORECASE)


def placeholder(index: int) -> str:
    letter = _PLACEHOLDER_LETTERS[index % len(_PLACEHOLDER_LETTERS)]
    suffix = "" if index < len(_PLACEHOLDER_LETTERS) else str(index // len(_PLACEHOLDER_LETTERS))
    return f"ZX{letter}{suffix}"


@dataclass(frozen=True, slots=True)
class Slot:
    """一个被保护的术语：占位符、原文写法、要写回的目标语写法。"""

    placeholder: str
    term: Term
    source: str
    target: str


@dataclass(frozen=True, slots=True)
class Protected:
    text: str
    slots: tuple[Slot, ...]


def target_form(term: Term, surface: str, tgt_lang: str) -> str | None:
    """术语在目标语里要强制使用的写法；``fixed`` 条目没有目标语写法时返回 ``None``（不保护）。"""

    canonical = term.canonical(tgt_lang)
    if term.kind == KEEP:
        return canonical or surface
    return canonical


def protect(text: str, src_lang: str, tgt_lang: str, glossary: Sequence[Term]) -> Protected:
    """把原文里的术语换成占位符。原文里本来就有类似占位符的字样时不做保护。"""

    if _PLACEHOLDER_ANY.search(text):
        return Protected(text, ())
    matches = [
        (match, target)
        for match in find_terms(text, src_lang, glossary, every=True)
        if (target := target_form(match.term, match.surface, tgt_lang))
    ]
    slots = [
        Slot(placeholder(index), match.term, match.surface, target)
        for index, (match, target) in enumerate(matches)
    ]
    pieces: list[str] = []
    cursor = 0
    for (match, _target), slot in zip(matches, slots, strict=True):
        pieces.append(text[cursor : match.start])
        pieces.append(slot.placeholder)
        cursor = match.end
    pieces.append(text[cursor:])
    return Protected("".join(pieces), tuple(slots))


def restore(translation: str, protected: Protected, tgt_lang: str) -> tuple[str, list[Slot]]:
    """把译文里的占位符换回目标语写法。返回（译文，丢失或重复的占位符）。

    占位符匹配不区分大小写；丢失（0 次）或重复（多于 1 次）的都算失败，交给调用方决定是否回退。
    英文译文里占位符在句首时，写回的术语首字母大写。
    """

    failed: list[Slot] = []
    text = translation
    # 长的占位符先换，避免 ZXQ 吃掉 ZXQ1 的前缀
    for slot in sorted(protected.slots, key=lambda item: -len(item.placeholder)):
        pattern = re.compile(rf"(?<![A-Za-z0-9]){re.escape(slot.placeholder)}(?![0-9])", re.I)
        found = list(pattern.finditer(text))
        if len(found) != 1:
            failed.append(slot)
            continue
        match = found[0]
        value = slot.target
        if tgt_lang == "en" and slot.term.kind == FIXED and _sentence_start(text, match.start()):
            value = value[:1].upper() + value[1:]
        text = text[: match.start()] + value + text[match.end() :]
    if tgt_lang in _CJK_LANGS and len(failed) < len(protected.slots):
        # 占位符两侧常被模型加上空格（「开源 ZXQ 引擎」），写回中日文术语后去掉汉字 / 假名之间的空白
        text = _CJK_GAP.sub("", text)
    return text, failed


def _sentence_start(text: str, index: int) -> bool:
    before = text[:index].rstrip()
    return not before or before[-1] in ".!?:;\"'“”"
