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


def find_terms(text: str, lang: str, glossary: Sequence[Term]) -> list[TermMatch]:
    """找出 ``text`` 里出现的术语，按位置排序；重叠时保留更长的那个。"""

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
        if match.term.id in used:
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
