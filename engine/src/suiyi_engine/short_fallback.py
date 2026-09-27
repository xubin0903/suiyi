"""表外极短句改用 ``opus-mt-en-zh`` 翻译，带质量门（#122）。

tc-big（en→zh 默认模型）在一到五个词的口语上常出文言 / 佛经腔（「Take a seat.」→「便下座问。」），
#89 的极短句表只覆盖表里的说法。表外的极短句先交给同方向的另一个已安装模型（``opus-mt-en-zh``，
按需加载，见 :meth:`suiyi_engine.registry.ModelRegistry.get_auxiliary`），译文过了质量门才用；
没过就用 tc-big 的译文。

范围（:func:`eligible`）：只管**单独成块**、以 ``.`` / ``?`` / ``!`` 结尾的一到五个词的句子，
即用户单独输入的一句口语。段落里的短句（「The outage lasted forty minutes. …」）和
不带句末标点的界面文案（「Remember me on this device」）仍用 tc-big：
领域集上这两类换成 ``opus-mt-en-zh`` 后 chrF 下降
（「四十分钟」→「40分钟」、「在此设备上记住我」→「记得我用这个装置」）。

质量门（:func:`check`）：

1. 必须有汉字，且没有乱码字 / 繁体（:func:`suiyi_engine.zh_script.find_issues`）；
2. 原文只有一个分句、译文却多出分句（「今天很冷，我还想说...」）时截到第一个分句；
3. 截完后不能有重复：同一个两字片段出现两次以上（「省省吧省省时间吧」）；
4. 长度比例：汉字数 / 原文词数在 [0.5, 5] 之间；
5. 译文里不能出现原文没有的英文单词。

tc-big 的译文作兜底时也过第 2、3 条：能截掉多余分句就截，截完仍重复就原样用（没有更好的候选）。
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from suiyi_engine.zh_script import find_issues

__all__ = [
    "DEFAULT_EVICT_IDLE_S",
    "MAX_WORDS",
    "ShortFallbackStats",
    "check",
    "eligible",
    "is_short",
    "tidy_fallback",
]

MAX_WORDS = 5
"""一到五个词算极短句，与极短句表一致（#89）。"""
MAX_CHARS = 40
DEFAULT_EVICT_IDLE_S = 60.0
"""常驻名额满时，某个常驻模型超过这么多秒没用过才为兜底模型挤掉它（桌面端默认预热 zh↔en）。"""
MIN_RATIO = 0.5
MAX_RATIO = 5.0

_WORD = re.compile(r"[A-Za-z0-9]+(?:'[A-Za-z]+)?")
_LATIN_WORD = re.compile(r"[A-Za-z]{2,}")
_HAN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")
_HAN_RUN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]+")
# 分句符：逗号、分号、冒号、省略号，以及句中的句末标点
_BREAK = re.compile(r"[,，;；:：、]|\.\.\.|…|[.!?。！？](?=.)")
_SOURCE_BREAK = re.compile(r"[,;:]|[.!?](?=\s*\S)")
_TRAIL = re.compile(r"[\s.!?。！？…]+$")
_SENTENCE_END = re.compile(r"(?<!\.)[.!?][\"'\u201d\u2019)\]]*$")


@dataclass
class ShortFallbackStats:
    """累计计数（进程内，不含原文）。"""

    candidates: int = 0
    """送去兜底模型的表外极短句数。"""
    used: int = 0
    """兜底模型译文过了质量门、被采用的句数。"""
    trimmed: int = 0
    """截掉多余分句后采用的句数（兜底模型与 tc-big 合计）。"""
    rejected: dict[str, int] = field(default_factory=dict)
    """没过质量门、改用 tc-big 的句数，按原因。"""
    skipped: int = 0
    """兜底模型没装、或没有可腾出的常驻名额，直接用 tc-big 的句数。"""

    def as_dict(self) -> dict[str, object]:
        return {
            "candidates": self.candidates,
            "used": self.used,
            "trimmed": self.trimmed,
            "rejected": dict(self.rejected),
            "skipped": self.skipped,
        }


def is_short(source: str) -> bool:
    """一到五个英文词、不超过 40 个字符的一句话。"""

    stripped = source.strip()
    if not stripped or len(stripped) > MAX_CHARS or "\n" in stripped:
        return False
    return 1 <= len(_WORD.findall(stripped)) <= MAX_WORDS


def eligible(source: str, *, alone: bool) -> bool:
    """兜底的范围：``alone``（这一块只有这一句）、以 ``.`` / ``?`` / ``!`` 结尾的极短句。"""

    return alone and is_short(source) and _SENTENCE_END.search(source.strip()) is not None


def _single_clause(source: str) -> bool:
    return _SOURCE_BREAK.search(_TRAIL.sub("", source.strip())) is None


def _trim(source: str, output: str) -> str:
    """原文只有一个分句时，把译文截到第一个分句符之前。"""

    if not _single_clause(source):
        return output
    body = _TRAIL.sub("", output.strip())
    match = _BREAK.search(body)
    if match is None or match.start() == 0:
        return output
    return body[: match.start()].rstrip()


def _repeated(text: str) -> bool:
    seen: set[str] = set()
    for run in _HAN_RUN.findall(text):
        for start in range(len(run) - 1):
            gram = run[start : start + 2]
            if gram in seen:
                return True
            seen.add(gram)
    return False


def check(source: str, output: str) -> tuple[str | None, str]:
    """质量门。过了返回（译文，``"ok"`` 或 ``"trimmed"``），没过返回（``None``，原因）。"""

    if not _HAN.search(output):
        return None, "empty"
    if not find_issues(output, source).ok:
        return None, "mojibake"
    text = _trim(source, output)
    trimmed = text != output
    if _repeated(text):
        return None, "repeat"
    words = max(1, len(_WORD.findall(source)))
    ratio = len(_HAN.findall(text)) / words
    if not MIN_RATIO <= ratio <= MAX_RATIO:
        return None, "length"
    known = {word.lower() for word in _LATIN_WORD.findall(source)}
    if any(word.lower() not in known for word in _LATIN_WORD.findall(text)):
        return None, "latin"
    return text, "trimmed" if trimmed else "ok"


def tidy_fallback(source: str, output: str) -> str:
    """tc-big 兜底译文：原文只有一个分句而译文多出分句（「我在家工作，我在家工作……」）时截掉。"""

    text = _trim(source, output)
    return text if text and _HAN.search(text) else output
