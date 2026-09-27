"""常用英文极短句的固定译法（#89）。

tc-big en→zh 在一到五个词的口语上常出古文或错字（「I'm sorry.」→「叹曰。」、
「Thank you.」→「谅谅。」、「Good evening.」→「边。」），``opus-mt-en-zh`` 又常补出
原文没有的内容（「I don't understand.」→「我不懂，我不明白，我不明白……」）。
整句命中 ``data/short_phrases_en_zh.tsv`` 时直接用表里的译法，不经过模型；没命中的照常翻译。

匹配规则：去掉两端空白和句末的 ``. ! ? …``，忽略大小写，弯引号当直引号，连续空白压成一个，
逗号后统一一个空格；之后与表里左列完全相同才算命中。句末标点按原文补（``.`` → 「。」，
``!`` → 「！」，``?`` → 「？」，原文没有就不补）。
"""

from __future__ import annotations

import re
from functools import lru_cache
from importlib import resources

__all__ = ["lookup_short_phrase", "phrase_count"]

_RESOURCE = "short_phrases_en_zh.tsv"
_MAX_CHARS = 40
_FINAL = {".": "。", "!": "！", "?": "？", "。": "。", "！": "！", "？": "？"}
_TRAILING = re.compile(r"[\s.!?。！？…]+$")


def _normalize(text: str) -> str:
    text = text.replace("\u2019", "'").replace("\u2018", "'").replace("\u00a0", " ")
    text = _TRAILING.sub("", text.strip())
    text = re.sub(r"\s*,\s*", ", ", text)
    return re.sub(r"\s+", " ", text).lower()


@lru_cache(maxsize=1)
def _table() -> dict[str, str]:
    raw = resources.files("suiyi_engine").joinpath("data", _RESOURCE).read_text("utf-8")
    table: dict[str, str] = {}
    for line in raw.splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        key, _, value = line.partition("\t")
        if value.strip():
            table[_normalize(key)] = value.strip()
    return table


def phrase_count() -> int:
    """表里的条目数。"""

    return len(_table())


def lookup_short_phrase(text: str, src: str, tgt: str) -> str | None:
    """en→zh 且整句命中表时返回带句末标点的译文，否则 ``None``。"""

    if src != "en" or tgt != "zh":
        return None
    stripped = text.strip()
    if not stripped or len(stripped) > _MAX_CHARS:
        return None
    value = _table().get(_normalize(stripped))
    if value is None:
        return None
    tail = stripped.rstrip(".…") if stripped.endswith("...") else stripped
    return value + _FINAL.get(tail[-1:], "")
