"""CTranslate2 上的 OPUS-MT（Marian）后端。

加载约定见 ``docs/engine/模型目录约定.md`` 与 ``docs/engine/翻译核心.md``。
``torch`` / ``transformers`` 不在导入路径上；``ctranslate2`` 与 ``sentencepiece``
只在真正构造后端时导入。

参考：https://opennmt.net/CTranslate2/guides/transformers.html#marianmt
"""

from __future__ import annotations

import re
from collections.abc import Sequence
from typing import Protocol

from suiyi_engine.registry import ModelRecord

__all__ = [
    "Ct2OpusBackend",
    "cleanup_decoded",
    "has_spurious_cjk_spacing",
    "prepare_source_tokens",
    "sort_indices_by_length",
]

# SentencePiece 词首标记（U+2581）。解码后不应留在译文里。
_SP_SPACE = "▁"
_SPECIAL_TOKENS = frozenset({"</s>", "<s>", "<pad>", "<unk>"})
_CONTROL_TOKEN = re.compile(r">>[A-Za-z0-9_]+<<")
_CJK_NEIGHBOR = (
    r"[\u3001-\u303f\u3040-\u30ff\u31f0-\u31ff\u3400-\u4dbf\u4e00-\u9fff"
    r"\uff01-\uff60\uff61-\uff9f]"
)
_CJK_GAP = re.compile(rf"(?<={_CJK_NEIGHBOR})[ \t\u00a0\u3000]+(?={_CJK_NEIGHBOR})")
_REQUIRED_FILES = ("model.bin", "config.json", "source.spm", "target.spm")
_CJK_LANGS = frozenset({"zh", "ja", "jp", "cn"})


class _SentencePiece(Protocol):
    def decode(self, pieces: list[str]) -> str: ...


def prepare_source_tokens(pieces: Sequence[str], prefix: str | None) -> list[str]:
    """拼出送给 CTranslate2 的源 token。

    ``prefix``（例如 ``>>cmn_Hans<<``）作为第一个 token 插入，不交给
    SentencePiece 再切。末尾补一个 ``</s>``，与 Marian 转换后的输入一致。
    """

    tokens = [str(piece) for piece in pieces if str(piece) != "</s>"]
    if prefix:
        tokens.insert(0, prefix)
    tokens.append("</s>")
    return tokens


def sort_indices_by_length(sentences: Sequence[str]) -> list[int]:
    """按源句长度升序排列下标，等长时保持原顺序，用来减少批内 padding。"""

    return sorted(range(len(sentences)), key=lambda index: (len(sentences[index]), index))


def is_cjk_target(lang: str) -> bool:
    """目标语是中文或日文时，译文不应保留字间空格。"""

    primary = lang.strip().lower().replace("_", "-").split("-", 1)[0]
    return primary in _CJK_LANGS


def cleanup_decoded(text: str, tgt_lang: str) -> str:
    """去掉 SentencePiece 残留，并按目标语整理空格。

    中文、日文去掉汉字 / 假名 / 中日标点之间的空格，拉丁字母两侧的空格保留。
    其他语种只压缩连续空白。``>>id<<`` 控制 token 一律删除。
    """

    text = text.replace(_SP_SPACE, " ")
    text = _CONTROL_TOKEN.sub("", text)
    if is_cjk_target(tgt_lang):
        text = _CJK_GAP.sub("", text)
    text = re.sub(r"[ \t\u00a0\u3000]+", " ", text)
    return text.strip()


def has_spurious_cjk_spacing(text: str) -> bool:
    """译文里是否还有 ``▁``，或中日文字之间的空格。"""

    return _SP_SPACE in text or _CJK_GAP.search(text) is not None


def decode_hypothesis(sp: _SentencePiece, tokens: Sequence[str], tgt_lang: str) -> str:
    """丢掉特殊 token 后反分词，再做 :func:`cleanup_decoded`。"""

    kept = [token for token in tokens if token not in _SPECIAL_TOKENS]
    text = sp.decode(kept)
    if not isinstance(text, str):
        text = str(text)
    return cleanup_decoded(text, tgt_lang)


class Ct2OpusBackend:
    """用 CTranslate2 int8 模型翻译一个方向。"""

    def __init__(
        self,
        record: ModelRecord,
        *,
        device: str = "cpu",
        compute_type: str = "int8",
        inter_threads: int = 1,
        intra_threads: int = 1,
        beam_size: int = 2,
        max_batch_size: int = 32,
        max_decoding_length: int = 512,
    ) -> None:
        self._record = record
        self._device = _require_text("device", device)
        self._compute_type = _require_text("compute_type", compute_type)
        self._inter_threads = _require_positive("inter_threads", inter_threads)
        self._intra_threads = _require_positive("intra_threads", intra_threads)
        self._beam_size = _require_positive("beam_size", beam_size)
        self._max_batch_size = _require_non_negative("max_batch_size", max_batch_size)
        self._max_decoding_length = _require_positive("max_decoding_length", max_decoding_length)
        self._prefix = record.src_prefix_token
        self._tgt_lang = record.tgt

        if record.quantization != self._compute_type:
            raise ValueError(
                f"{record.id} 的 quantization 是 {record.quantization}，"
                f"与 compute_type={self._compute_type} 不一致"
            )
        missing = [name for name in _REQUIRED_FILES if not (record.model_dir / name).is_file()]
        if missing:
            listed = "、".join(missing)
            raise FileNotFoundError(f"{record.id} 缺少文件：{listed}（{record.model_dir}）")

        import ctranslate2
        import sentencepiece as spm

        self._ct2 = ctranslate2.Translator(
            str(record.model_dir),
            device=self._device,
            compute_type=self._compute_type,
            inter_threads=self._inter_threads,
            intra_threads=self._intra_threads,
        )
        source_model = str(record.model_dir / "source.spm")
        target_model = str(record.model_dir / "target.spm")
        self._source_sp = spm.SentencePieceProcessor(model_file=source_model)
        self._target_sp = spm.SentencePieceProcessor(model_file=target_model)

    def translate_batch(self, sentences: list[str]) -> list[str]:
        """按长度排序后批量翻译，再还原成输入顺序。"""

        translated = [""] * len(sentences)
        pending = [index for index, sentence in enumerate(sentences) if sentence.strip()]
        if not pending:
            return translated
        order = sort_indices_by_length([sentences[index] for index in pending])
        ordered_indices = [pending[position] for position in order]
        ordered = [sentences[index] for index in ordered_indices]
        outputs = self._translate_ordered(ordered)
        if len(outputs) != len(ordered_indices):
            raise RuntimeError(
                f"{self._record.id} 返回了 {len(outputs)} 句，期望 {len(ordered_indices)} 句"
            )
        for index, text in zip(ordered_indices, outputs, strict=True):
            translated[index] = text
        return translated

    def _translate_ordered(self, sentences: list[str]) -> list[str]:
        tokenized = [self._encode(sentence) for sentence in sentences]
        results = self._ct2.translate_batch(
            tokenized,
            beam_size=self._beam_size,
            max_batch_size=self._max_batch_size,
            max_decoding_length=self._max_decoding_length,
        )
        if len(results) != len(sentences):
            raise RuntimeError(
                f"{self._record.id} 返回了 {len(results)} 条假设，期望 {len(sentences)} 条"
            )
        decoded: list[str] = []
        for result in results:
            hypotheses = getattr(result, "hypotheses", None)
            tokens = [str(token) for token in hypotheses[0]] if hypotheses else []
            decoded.append(decode_hypothesis(self._target_sp, tokens, self._tgt_lang))
        return decoded

    def _encode(self, text: str) -> list[str]:
        pieces = self._source_sp.encode(text, out_type=str)
        return prepare_source_tokens(pieces, self._prefix)


def _require_text(name: str, value: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{name} 必须是非空字符串")
    return value


def _require_positive(name: str, value: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        raise ValueError(f"{name} 必须是 >= 1 的整数")
    return value


def _require_non_negative(name: str, value: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ValueError(f"{name} 必须是 >= 0 的整数")
    return value
