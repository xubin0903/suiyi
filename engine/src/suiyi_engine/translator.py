"""分句、路由、批量翻译、拼回。

不包含 HTTP，也不做 ``src="auto"`` 的语种检测。检测在调用方完成后，
把 ISO 639-1 代码传进来。
"""

from __future__ import annotations

import time
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path

from suiyi_engine.backends.base import TranslationBackend
from suiyi_engine.errors import UnsupportedPairError
from suiyi_engine.glossary import Term
from suiyi_engine.registry import (
    DEFAULT_BEAM_SIZE,
    DEFAULT_INTER_THREADS,
    DEFAULT_MAX_BATCH_SIZE,
    DEFAULT_MAX_DECODING_LENGTH,
    BackendFactory,
    ModelRegistry,
    default_intra_threads,
    normalize_lang,
)
from suiyi_engine.segment import join_segments, split_sentences
from suiyi_engine.terms import GlossaryStore, TermStats, translate_with_terms
from suiyi_engine.zh_punct import normalize_zh_punct

__all__ = ["TranslationResult", "Translator", "UnsupportedPairError"]


@dataclass(frozen=True, slots=True)
class TranslationResult:
    """一次翻译的结果。

    Attributes:
        text: 译文。``src == tgt`` 时是原文；空输入时是空字符串。
        src: 归一化后的源语种。
        tgt: 归一化后的目标语种。
        route: 实际使用的模型 id。直连长度为 1，英文中转长度为 2，
            不需要模型时为空列表。
        elapsed_ms: 本次调用的墙钟毫秒，含首次加载该方向模型的时间。
    """

    text: str
    src: str
    tgt: str
    route: list[str]
    elapsed_ms: float


class Translator:
    """同步翻译入口。模型目录默认来自 ``SUIYI_MODELS_DIR`` 或仓库 ``models/``。

    ``backend_factory`` 只用于测试注入假后端。生产路径使用 CTranslate2，
    ``device`` 默认 ``cpu``，``compute_type`` 默认 ``int8``。一期不支持 GPU，
    参数保留给以后。

    ``glossary`` 为 :class:`GlossaryStore` 时启用术语保护（#83，zh↔en 直连），
    默认开关取 ``glossary.enabled``，每次调用可用 ``glossary=True/False`` 覆盖；
    为 ``None`` 时不做术语保护。
    """

    def __init__(
        self,
        models_dir: Path | str | None = None,
        *,
        manifest_path: Path | str | None = None,
        backend_factory: BackendFactory | None = None,
        device: str = "cpu",
        compute_type: str = "int8",
        inter_threads: int = DEFAULT_INTER_THREADS,
        intra_threads: int | None = None,
        beam_size: int = DEFAULT_BEAM_SIZE,
        max_batch_size: int = DEFAULT_MAX_BATCH_SIZE,
        max_decoding_length: int = DEFAULT_MAX_DECODING_LENGTH,
        glossary: GlossaryStore | None = None,
    ) -> None:
        intra = default_intra_threads() if intra_threads is None else intra_threads
        options: dict[str, object] = {
            "device": device,
            "compute_type": compute_type,
            "inter_threads": inter_threads,
            "intra_threads": intra,
            "beam_size": beam_size,
            "max_batch_size": max_batch_size,
            "max_decoding_length": max_decoding_length,
        }
        _validate_options(options)
        self.registry = ModelRegistry(
            models_dir,
            manifest_path=manifest_path,
            backend_factory=backend_factory,
            backend_options=options,
        )
        self.glossary = glossary
        self.term_stats = TermStats()

    def glossary_status(self) -> dict[str, object]:
        """``/health`` 的 ``glossary_*`` 字段。没有术语表时报告关闭。"""

        if self.glossary is None:
            return {
                "glossary_enabled": False,
                "glossary_builtin_entries": 0,
                "glossary_user_path": None,
                "glossary_user_entries": 0,
                "glossary_error": None,
                "glossary_warnings": [],
            }
        return self.glossary.status()

    def reload_glossary(self) -> dict[str, object]:
        """立即重读用户术语表，返回 :meth:`glossary_status`。"""

        if self.glossary is not None:
            self.glossary.reload()
        return self.glossary_status()

    def available_pairs(self) -> list[tuple[str, str, str]]:
        """当前能翻译的 ``(src, tgt, "direct"|"pivot")``，按语种排序。"""

        return self.registry.available_pairs()

    def preload(self, pairs: Sequence[tuple[str, str]]) -> None:
        """启动预热。见 :meth:`ModelRegistry.preload`。"""

        self.registry.preload(pairs)

    def loaded_model_ids(self) -> list[str]:
        """已经加载进内存的模型 id。"""

        return self.registry.loaded_model_ids()

    def translate(
        self, text: str, src: str, tgt: str, *, glossary: bool | None = None
    ) -> TranslationResult:
        """翻译一段文本。长文先分句，再批量翻译，再按目标语拼回。

        中转时两跳一一对应，中间的英文不再分句，以免和原文的段落分隔错位。
        ``src == tgt`` 原样返回。空白文本返回空字符串，且不检查模型是否已下载。
        ``glossary`` 为 ``None`` 时按术语表的默认开关，``True`` / ``False`` 只影响这一次。
        """

        started = time.perf_counter()
        src_code, tgt_code = self._codes(text, src, tgt)
        if src_code == tgt_code or text.strip() == "":
            body = text if src_code == tgt_code else ""
            return _result(body, src_code, tgt_code, [], started)
        records = self.registry.resolve(src_code, tgt_code)
        backends = [self.registry.get(record.id) for record in records]
        terms = self._terms(src_code, tgt_code, glossary) if len(backends) == 1 else ()
        output = _translate_text(text, src_code, tgt_code, backends, terms, self.term_stats)
        return _result(output, src_code, tgt_code, [record.id for record in records], started)

    def translate_many(
        self, texts: Sequence[str], src: str, tgt: str, *, glossary: bool | None = None
    ) -> list[TranslationResult]:
        """翻译多段文本。语向不支持时在产出结果前失败。

        每一段仍按句批量送给后端。各结果的 ``elapsed_ms`` 只统计该段自身。
        """

        src_code = normalize_lang(src)
        tgt_code = normalize_lang(tgt)
        for text in texts:
            if not isinstance(text, str):
                raise TypeError("texts 中的每一项都必须是 str")
        if src_code != tgt_code and any(text.strip() for text in texts):
            self.registry.resolve(src_code, tgt_code)
        return [self.translate(text, src_code, tgt_code, glossary=glossary) for text in texts]

    def _terms(self, src: str, tgt: str, override: bool | None) -> tuple[Term, ...]:
        store = self.glossary
        if store is None:
            return ()
        enabled = store.enabled if override is None else override
        return store.terms_for(src, tgt) if enabled else ()

    def _codes(self, text: str, src: str, tgt: str) -> tuple[str, str]:
        if not isinstance(text, str):
            raise TypeError("text 必须是 str")
        return normalize_lang(src), normalize_lang(tgt)


def _translate_text(
    text: str,
    src: str,
    tgt: str,
    backends: Sequence[TranslationBackend],
    terms: Sequence[Term] = (),
    stats: TermStats | None = None,
) -> str:
    segments = split_sentences(text, lang=src)
    if not segments:
        return ""
    sources = [segment.text for segment in segments]
    current = sources
    expected = len(current)
    if terms and len(backends) == 1:
        current = translate_with_terms(current, src, tgt, terms, backends[0].translate_batch, stats)
        if len(current) != expected:
            raise RuntimeError(f"后端返回了 {len(current)} 句，期望 {expected} 句")
    else:
        for backend in backends:
            current = backend.translate_batch(current)
            if len(current) != expected:
                raise RuntimeError(f"后端返回了 {len(current)} 句，期望 {expected} 句")
    current = [
        restore_final_punct(source, output, tgt)
        for source, output in zip(sources, current, strict=True)
    ]
    if tgt == "zh":
        current = [normalize_zh_punct(output) for output in current]
    return join_segments(current, segments, tgt)


_FINAL_TO_CJK = {".": "。", "。": "。", "!": "！", "！": "！", "?": "？", "？": "？"}
_FINAL_TO_LATIN = {".": ".", "。": ".", "!": "!", "！": "!", "?": "?", "？": "?"}
_CJK_TARGETS = frozenset({"zh", "ja"})


def _is_cjk(char: str) -> bool:
    return "\u4e00" <= char <= "\u9fff" or "\u3040" <= char <= "\u30ff"


def restore_final_punct(source: str, output: str, tgt: str) -> str:
    """原句以句号 / 叹号 / 问号结尾、译文结尾却是字母数字或汉字时，补上目标语的句末标点（#83）。

    tc-big en→zh 常把「。」丢掉。只在译文最后一个字符是文字时补，已有别的标点（引号、括号、
    省略号、冒号）时不动。
    """

    src_tail = source.rstrip()
    out_tail = output.rstrip()
    if not src_tail or not out_tail:
        return output
    mark = src_tail[-1]
    table = _FINAL_TO_CJK if tgt in _CJK_TARGETS else _FINAL_TO_LATIN
    if mark not in table:
        return output
    if mark == "." and src_tail.endswith(".."):
        return output  # 省略号
    last = out_tail[-1]
    if tgt in _CJK_TARGETS and last in ".!?" and len(out_tail) > 1 and _is_cjk(out_tail[-2]):
        # 中文译文句末却是半角标点（「保持一致.」）：换成全角
        return out_tail[:-1] + _FINAL_TO_CJK[last] + output[len(out_tail) :]
    if not (last.isalnum() or _is_cjk(last)):
        return output
    return out_tail + table[mark] + output[len(out_tail) :]


def _result(
    text: str,
    src: str,
    tgt: str,
    route: list[str],
    started: float,
) -> TranslationResult:
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    return TranslationResult(
        text=text,
        src=src,
        tgt=tgt,
        route=list(route),
        elapsed_ms=elapsed_ms,
    )


def _validate_options(options: Mapping[str, object]) -> None:
    _require_text("device", options["device"])
    _require_text("compute_type", options["compute_type"])
    _require_positive("inter_threads", options["inter_threads"])
    _require_positive("intra_threads", options["intra_threads"])
    _require_positive("beam_size", options["beam_size"])
    _require_non_negative("max_batch_size", options["max_batch_size"])
    _require_positive("max_decoding_length", options["max_decoding_length"])


def _require_text(name: str, value: object) -> None:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{name} 必须是非空字符串")


def _require_positive(name: str, value: object) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        raise ValueError(f"{name} 必须是 >= 1 的整数")


def _require_non_negative(name: str, value: object) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ValueError(f"{name} 必须是 >= 0 的整数")
