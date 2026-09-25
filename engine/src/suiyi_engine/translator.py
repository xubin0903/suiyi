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
from suiyi_engine.registry import (
    BackendFactory,
    ModelRegistry,
    default_intra_threads,
    normalize_lang,
)
from suiyi_engine.segment import join_segments, split_sentences

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
    """

    def __init__(
        self,
        models_dir: Path | str | None = None,
        *,
        manifest_path: Path | str | None = None,
        backend_factory: BackendFactory | None = None,
        device: str = "cpu",
        compute_type: str = "int8",
        inter_threads: int = 1,
        intra_threads: int | None = None,
        beam_size: int = 2,
        max_batch_size: int = 32,
        max_decoding_length: int = 512,
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

    def available_pairs(self) -> list[tuple[str, str, str]]:
        """当前能翻译的 ``(src, tgt, "direct"|"pivot")``，按语种排序。"""

        return self.registry.available_pairs()

    def preload(self, pairs: Sequence[tuple[str, str]]) -> None:
        """启动预热。见 :meth:`ModelRegistry.preload`。"""

        self.registry.preload(pairs)

    def loaded_model_ids(self) -> list[str]:
        """已经加载进内存的模型 id。"""

        return self.registry.loaded_model_ids()

    def translate(self, text: str, src: str, tgt: str) -> TranslationResult:
        """翻译一段文本。长文先分句，再批量翻译，再按目标语拼回。

        中转时两跳一一对应，中间的英文不再分句，以免和原文的段落分隔错位。
        ``src == tgt`` 原样返回。空白文本返回空字符串，且不检查模型是否已下载。
        """

        started = time.perf_counter()
        src_code, tgt_code = self._codes(text, src, tgt)
        if src_code == tgt_code or text.strip() == "":
            body = text if src_code == tgt_code else ""
            return _result(body, src_code, tgt_code, [], started)
        records = self.registry.resolve(src_code, tgt_code)
        backends = [self.registry.get(record.id) for record in records]
        output = _translate_text(text, src_code, tgt_code, backends)
        return _result(output, src_code, tgt_code, [record.id for record in records], started)

    def translate_many(self, texts: Sequence[str], src: str, tgt: str) -> list[TranslationResult]:
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
        return [self.translate(text, src_code, tgt_code) for text in texts]

    def _codes(self, text: str, src: str, tgt: str) -> tuple[str, str]:
        if not isinstance(text, str):
            raise TypeError("text 必须是 str")
        return normalize_lang(src), normalize_lang(tgt)


def _translate_text(
    text: str,
    src: str,
    tgt: str,
    backends: Sequence[TranslationBackend],
) -> str:
    segments = split_sentences(text, lang=src)
    if not segments:
        return ""
    current = [segment.text for segment in segments]
    expected = len(current)
    for backend in backends:
        current = backend.translate_batch(current)
        if len(current) != expected:
            raise RuntimeError(f"后端返回了 {len(current)} 句，期望 {expected} 句")
    return join_segments(current, segments, tgt)


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
