"""离线语种检测。

先按文字（Unicode 脚本、中英混排占比、少量拉丁正字法与短词表）判断，
判断不了的拉丁文本再交给统计模型。统计模型懒加载，且进程内只有一份。
加载约需 0.4～0.5 秒，服务启动时用 :func:`warmup` 提前加载，避免落在第一次请求上。

纯汉字日语（如「東京大学」）没有假名，会判成 ``zh``。这是规则层的已知局限。
"""

import re
import threading
import time
import unicodedata
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal

# 候选语种的默认集合，与一期常见语种一致。
DEFAULT_CANDIDATES: tuple[str, ...] = ("zh", "en", "ja", "ko", "fr", "de", "es", "ru")

# 汉字占「字母」的比例超过该值即判中文（「超过」是严格大于）。
# 「这个 bug 怎么 fix」里汉字 4、拉丁字母 6，占比 0.4，必须高于阈值。
CJK_ZH_RATIO_THRESHOLD: float = 0.20

# 统计模型只负责这些拉丁语种。中日韩俄由规则层处理。
_LATIN_MODEL_LANGS: tuple[str, ...] = ("de", "en", "es", "fr")

# 四个拉丁语种均匀分布时概率约为 0.25。低于该值视为模型几乎没有把握。
_LOW_MODEL_CONFIDENCE: float = 0.30

_SUPPORTED: frozenset[str] = frozenset(DEFAULT_CANDIDATES)

# 统计模型在 1～3 个词上容易判错或给出均匀分布的高频说法。
# 整段折叠后必须完整命中，避免把长句里的某个词提前截走。
_SHORT_PHRASES: dict[str, str] = {
    "bitte": "de",
    "danke": "de",
    "guten morgen": "de",
    "guten tag": "de",
    "good morning": "en",
    "hello": "en",
    "hello world": "en",
    "hey": "en",
    "hi": "en",
    "how are you": "en",
    "ok": "en",
    "okay": "en",
    "please": "en",
    "sorry": "en",
    "thank you": "en",
    "thanks": "en",
    "yes": "en",
    "bonjour": "fr",
    "merci": "fr",
    "merci beaucoup": "fr",
    "buenos dias": "es",
    "gracias": "es",
    "hola": "es",
}

_LATIN_WORD = re.compile(r"[A-Za-z]+")

_identifier = None
_identifier_lock = threading.Lock()


@dataclass(frozen=True, slots=True)
class Detection:
    """一次检测的结果。

    ``lang`` 是 ISO 639-1；无法判断时为 ``und``。
    ``confidence`` 在 0 到 1 之间。
    ``method`` 为 ``script``（规则）、``model``（统计模型）或 ``fallback``（兜底）。
    """

    lang: str
    confidence: float
    method: Literal["script", "model", "fallback"]


def detect(text: str, candidates: list[str] | None = None) -> Detection:
    """检测 ``text`` 的语种。

    ``candidates`` 为 ``None`` 时使用 :data:`DEFAULT_CANDIDATES`。
    传入空列表表示没有可选语种，结果为 ``und``。
    列表里出现不支持的代码会抛出 ``ValueError``。
    """

    if not isinstance(text, str):
        raise TypeError("text 必须是 str")
    allowed = _resolve_candidates(candidates)
    if not allowed:
        return _fallback()

    normalized = unicodedata.normalize("NFKC", text)
    counts = _count_scripts(normalized)
    total = counts.total
    if total == 0:
        return _fallback()

    kana_hit = _kana_or_hangul(counts, allowed)
    if kana_hit is not None:
        return kana_hit

    if counts.hangul > 0:
        if "ko" in allowed:
            return _script("ko", counts.hangul / total)
        return _fallback()

    han_ratio = counts.han / total
    if han_ratio > CJK_ZH_RATIO_THRESHOLD:
        if "zh" in allowed:
            return _script("zh", han_ratio)
        return _fallback()

    if _cyrillic_dominant(counts):
        if "ru" in allowed:
            return _script("ru", counts.cyrillic / total)
        return _fallback()

    if counts.latin == 0 or not _latin_dominant(counts):
        return _fallback()

    latin_allowed = allowed & frozenset(_LATIN_MODEL_LANGS)
    if not latin_allowed:
        return _fallback()

    ortho = _orthography_lang(normalized)
    if ortho is not None and ortho in latin_allowed:
        return Detection(ortho, 0.97, "script")

    phrase_key = _latin_phrase_key(normalized)
    phrase_lang = _SHORT_PHRASES.get(phrase_key)
    if phrase_lang is not None and phrase_lang in latin_allowed:
        return Detection(phrase_lang, 0.90, "script")

    lang, confidence = _model_detect(normalized, latin_allowed)
    token_count = len(phrase_key.split()) if phrase_key else 0
    if token_count <= 3 and confidence <= _LOW_MODEL_CONFIDENCE and "en" in latin_allowed:
        # 模型近乎均匀分布时，剪贴板里的超短拉丁文本回退为英文。
        return Detection("en", confidence, "fallback")
    return Detection(lang, confidence, "model")


class _Counts:
    __slots__ = ("cyrillic", "hangul", "han", "kana", "latin", "other")

    def __init__(self) -> None:
        self.kana = 0
        self.hangul = 0
        self.han = 0
        self.latin = 0
        self.cyrillic = 0
        self.other = 0

    @property
    def total(self) -> int:
        return self.kana + self.hangul + self.han + self.latin + self.cyrillic + self.other


def _resolve_candidates(candidates: list[str] | None) -> set[str]:
    if candidates is None:
        return set(DEFAULT_CANDIDATES)
    if isinstance(candidates, str) or not isinstance(candidates, Sequence):
        raise TypeError("candidates 必须是语种代码列表")
    allowed: set[str] = set()
    unknown: list[str] = []
    for code in candidates:
        if not isinstance(code, str):
            raise TypeError("candidates 必须是语种代码列表")
        if code not in _SUPPORTED:
            unknown.append(code)
        else:
            allowed.add(code)
    if unknown:
        raise ValueError(f"不支持的语种代码: {', '.join(sorted(set(unknown)))}")
    return allowed


def _fallback() -> Detection:
    return Detection("und", 0.0, "fallback")


def _script(lang: str, ratio: float) -> Detection:
    confidence = min(0.99, max(0.0, 0.5 + 0.5 * ratio))
    return Detection(lang, float(confidence), "script")


def _count_scripts(text: str) -> _Counts:
    counts = _Counts()
    for char in text:
        bucket = _script_bucket(ord(char))
        if bucket is None:
            if unicodedata.category(char).startswith("L"):
                counts.other += 1
            continue
        setattr(counts, bucket, getattr(counts, bucket) + 1)
    return counts


def _script_bucket(cp: int) -> str | None:
    if _is_kana(cp):
        return "kana"
    if _is_hangul(cp):
        return "hangul"
    if _is_han(cp):
        return "han"
    if _is_latin(cp):
        return "latin"
    if _is_cyrillic(cp):
        return "cyrillic"
    return None


def _is_kana(cp: int) -> bool:
    # U+30FB（片假名中点）和 NFKC 之后的 U+FF65 也落在假名块里，
    # 但中文外国人名常用它作间隔号，不能据此判日语。
    if 0x3041 <= cp <= 0x3096 or cp in (0x309D, 0x309E, 0x309F):
        return True
    if 0x30A1 <= cp <= 0x30FA or 0x30FC <= cp <= 0x30FF:
        return True
    if 0x31F0 <= cp <= 0x31FF:
        return True
    return False


def _is_hangul(cp: int) -> bool:
    return (
        0x1100 <= cp <= 0x11FF
        or 0x3130 <= cp <= 0x318F
        or 0xA960 <= cp <= 0xA97F
        or 0xAC00 <= cp <= 0xD7AF
        or 0xD7B0 <= cp <= 0xD7FF
    )


def _is_han(cp: int) -> bool:
    return (
        0x3400 <= cp <= 0x4DBF
        or 0x4E00 <= cp <= 0x9FFF
        or 0xF900 <= cp <= 0xFAFF
        or 0x20000 <= cp <= 0x323AF
    )


def _is_latin(cp: int) -> bool:
    if 0x41 <= cp <= 0x5A or 0x61 <= cp <= 0x7A:
        return True
    if 0x00C0 <= cp <= 0x024F and cp not in (0x00D7, 0x00F7):
        return True
    return 0x1E00 <= cp <= 0x1EFF


def _is_cyrillic(cp: int) -> bool:
    return 0x0400 <= cp <= 0x04FF or 0x0500 <= cp <= 0x052F


def _kana_or_hangul(counts: _Counts, allowed: set[str]) -> Detection | None:
    if counts.kana == 0:
        return None
    # 假名和谚文同时出现时取得多的一方；持平优先日语（假名规则写在前面）。
    if counts.hangul > counts.kana and "ko" in allowed:
        return _script("ko", counts.hangul / counts.total)
    if "ja" in allowed:
        # 日语正文通常是汉字加假名，两者都算日语信号。
        return _script("ja", (counts.kana + counts.han) / counts.total)
    return _fallback()


def _cyrillic_dominant(counts: _Counts) -> bool:
    return (
        counts.cyrillic > 0
        and counts.cyrillic > counts.latin
        and counts.cyrillic > counts.han
        and counts.cyrillic > counts.other
    )


def _latin_dominant(counts: _Counts) -> bool:
    return (
        counts.latin >= counts.han
        and counts.latin >= counts.cyrillic
        and counts.latin >= counts.other
    )


def _orthography_lang(text: str) -> str | None:
    # 只保留候选拉丁语种之间几乎不会撞车的字形。变音字母重叠太多，交给模型。
    if "ß" in text or "ẞ" in text:
        return "de"
    if any(char in text for char in "ñÑ¿¡"):
        return "es"
    if "œ" in text or "Œ" in text:
        return "fr"
    return None


def _latin_phrase_key(text: str) -> str:
    decomposed = unicodedata.normalize("NFD", text)
    stripped = "".join(char for char in decomposed if unicodedata.category(char) != "Mn")
    return " ".join(_LATIN_WORD.findall(stripped)).lower()


def warmup() -> float:
    """加载统计模型并跑一次检测，返回耗时（毫秒）。

    ``detect`` 第一次走到统计模型时才加载 py3langid 的模型（约 0.4～0.5 秒），
    ``serve`` 在开始监听前调用本函数，把这段时间挪到启动阶段。线程安全，重复调用几乎不耗时。
    """

    start = time.perf_counter()
    _model_detect("warm up the language identifier", set(_LATIN_MODEL_LANGS))
    return (time.perf_counter() - start) * 1000.0


def is_warm() -> bool:
    """统计模型是否已经加载。"""

    return _identifier is not None


def _get_identifier():
    global _identifier
    if _identifier is not None:
        return _identifier
    with _identifier_lock:
        if _identifier is None:
            from py3langid.langid import MODEL_FILE, LanguageIdentifier

            identifier = LanguageIdentifier.from_model_file(MODEL_FILE, norm_probs=True)
            identifier.set_languages(list(_LATIN_MODEL_LANGS))
            _identifier = identifier
        return _identifier


def _model_detect(text: str, allowed: set[str]) -> tuple[str, float]:
    ranked = _get_identifier().rank(text)
    best_lang = ""
    best_score = -1.0
    for lang, score in ranked:
        score_f = float(score)
        if lang in allowed and score_f > best_score:
            best_lang = lang
            best_score = score_f
    if not best_lang:
        return "und", 0.0
    return best_lang, max(0.0, min(1.0, best_score))
