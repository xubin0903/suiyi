"""翻译核心：假后端覆盖路由与缓存，真实模型用 model 标记。"""

from __future__ import annotations

import ast
import json
import os
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

import pytest

from suiyi_engine.backends.ct2_opus import (
    Ct2OpusBackend,
    cleanup_decoded,
    decode_hypothesis,
    has_spurious_cjk_spacing,
    prepare_source_tokens,
    sort_indices_by_length,
)
from suiyi_engine.registry import (
    ModelRecord,
    default_intra_threads,
    default_models_dir,
    repo_root,
)
from suiyi_engine.translator import TranslationResult, Translator, UnsupportedPairError

_ENGINE_SRC = Path(__file__).resolve().parents[1] / "src" / "suiyi_engine"


class TagBackend:
    """把模型 id 打进译文，便于断言路由和句对齐。"""

    def __init__(self, record: ModelRecord) -> None:
        self.record = record
        self.batches: list[list[str]] = []

    def translate_batch(self, sentences: list[str]) -> list[str]:
        self.batches.append(list(sentences))
        return [f"<{self.record.id}>{sentence}" for sentence in sentences]


class ScriptBackend:
    def __init__(self, outputs: list[str]) -> None:
        self.outputs = outputs
        self.seen: list[list[str]] = []

    def translate_batch(self, sentences: list[str]) -> list[str]:
        self.seen.append(list(sentences))
        if len(sentences) != len(self.outputs):
            raise AssertionError(f"{len(sentences)} != {len(self.outputs)}")
        return list(self.outputs)


class BadLengthBackend:
    def translate_batch(self, sentences: list[str]) -> list[str]:
        return ["x"]


def _install(
    root: Path,
    model_id: str,
    src: str,
    tgt: str,
    prefix: str | None = None,
) -> None:
    directory = root / model_id
    directory.mkdir(parents=True, exist_ok=True)
    payload = {
        "id": model_id,
        "src": src,
        "tgt": tgt,
        "src_prefix_token": prefix,
        "quantization": "int8",
    }
    (directory / "suiyi-model.json").write_text(
        json.dumps(payload, ensure_ascii=False),
        encoding="utf-8",
    )


def _translator(root: Path, log: list[str], boxes: dict[str, TagBackend]) -> Translator:
    def factory(record: ModelRecord) -> TagBackend:
        log.append(record.id)
        backend = TagBackend(record)
        boxes[record.id] = backend
        return backend

    return Translator(root, backend_factory=factory)


def test_direct_route_and_lazy_load_once(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh", ">>cmn_Hans<<")
    log: list[str] = []
    boxes: dict[str, TagBackend] = {}
    translator = _translator(tmp_path, log, boxes)

    first = translator.translate("你好。", "zh", "en")
    second = translator.translate("再见。", "zh", "en")

    assert isinstance(first, TranslationResult)
    assert first.route == ["opus-mt-zh-en"]
    assert first.src == "zh" and first.tgt == "en"
    assert first.text == "<opus-mt-zh-en>你好。"
    assert second.text == "<opus-mt-zh-en>再见。"
    assert log == ["opus-mt-zh-en"]
    assert translator.loaded_model_ids() == ["opus-mt-zh-en"]
    assert first.elapsed_ms >= 0


def test_pivot_route_length_is_two_and_keeps_sentence_alignment(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-ja-en", "ja", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh", ">>cmn_Hans<<")
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    log: list[str] = []
    boxes: dict[str, TagBackend] = {}
    translator = _translator(tmp_path, log, boxes)

    result = translator.translate("こんにちは。", "ja", "zh")

    assert result.route == ["opus-mt-ja-en", "opus-mt-en-zh"]
    assert len(result.route) == 2
    assert result.text == "<opus-mt-en-zh><opus-mt-ja-en>こんにちは。"
    assert boxes["opus-mt-ja-en"].batches == [["こんにちは。"]]
    assert boxes["opus-mt-en-zh"].batches == [["<opus-mt-ja-en>こんにちは。"]]
    assert boxes["opus-mt-en-zh"].record.src_prefix_token == ">>cmn_Hans<<"
    assert "opus-mt-zh-en" not in log

    translator.translate("さようなら。", "ja", "zh")
    assert log == ["opus-mt-ja-en", "opus-mt-en-zh"]


def test_manifest_direct_is_not_replaced_by_english_pivot(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    _install(tmp_path, "opus-tatoeba-en-ja", "en", "ja")
    log: list[str] = []
    translator = _translator(tmp_path, log, {})

    with pytest.raises(UnsupportedPairError) as exc_info:
        translator.translate("你好。", "zh", "ja")

    assert exc_info.value.missing_ids == ("opus-mt-tc-big-zh-ja",)
    assert "opus-mt-tc-big-zh-ja" in str(exc_info.value)
    assert log == []


def test_missing_direct_model_names_manifest_id(tmp_path: Path) -> None:
    translator = Translator(tmp_path)

    with pytest.raises(UnsupportedPairError) as exc_info:
        translator.translate("Hello.", "en", "zh")

    assert exc_info.value.src == "en"
    assert exc_info.value.tgt == "zh"
    assert exc_info.value.missing_ids == ("opus-mt-en-zh",)
    assert "opus-mt-en-zh" in str(exc_info.value)


def test_missing_pivot_lists_leg_ids(tmp_path: Path) -> None:
    translator = Translator(tmp_path)

    with pytest.raises(UnsupportedPairError) as exc_info:
        translator.translate("こんにちは。", "ja", "zh")

    assert exc_info.value.missing_ids == ("opus-mt-ja-en", "opus-mt-en-zh")


def test_pivot_rejects_a_different_model_for_the_same_direction(tmp_path: Path) -> None:
    _install(tmp_path, "custom-ja-en", "ja", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh", ">>cmn_Hans<<")
    translator = _translator(tmp_path, [], {})

    with pytest.raises(UnsupportedPairError) as exc_info:
        translator.translate("こんにちは。", "ja", "zh")

    assert exc_info.value.missing_ids == ("opus-mt-ja-en",)


def test_implicit_english_pivot_when_manifest_has_no_direct(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-fr-en", "fr", "en")
    _install(tmp_path, "opus-mt-en-de", "en", "de")
    log: list[str] = []
    translator = _translator(tmp_path, log, {})

    result = translator.translate("Bonjour.", "fr", "de")

    assert result.route == ["opus-mt-fr-en", "opus-mt-en-de"]
    assert ("fr", "de", "pivot") in translator.available_pairs()
    assert log == ["opus-mt-fr-en", "opus-mt-en-de"]


def test_unlisted_pair_reports_manifest_hop_ids(tmp_path: Path) -> None:
    translator = Translator(tmp_path)

    with pytest.raises(UnsupportedPairError) as exc_info:
        translator.translate("안녕하세요.", "ko", "ja")

    assert exc_info.value.missing_ids == ("opus-mt-ko-en", "opus-tatoeba-en-ja")


def test_src_equals_tgt_returns_input_without_loading(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    log: list[str] = []
    translator = _translator(tmp_path, log, {})
    text = "你好。\n\n保持原样。"

    result = translator.translate(text, "ZH", "zh")

    assert result.text == text
    assert result.route == []
    assert result.src == result.tgt == "zh"
    assert log == []


def test_empty_and_whitespace_return_empty_without_loading(tmp_path: Path) -> None:
    log: list[str] = []
    translator = _translator(tmp_path, log, {})

    empty = translator.translate("", "zh", "en")
    blank = translator.translate(" \n\t", "xx", "yy")

    assert empty.text == "" and empty.route == []
    assert blank.text == "" and blank.route == []
    assert log == []
    with pytest.raises(UnsupportedPairError):
        translator.translate("你好。", "zh", "en")


def test_auto_is_rejected(tmp_path: Path) -> None:
    translator = Translator(tmp_path)

    with pytest.raises(ValueError, match="auto"):
        translator.translate("你好。", "auto", "en")


def test_region_code_uses_primary_subtag(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    translator = _translator(tmp_path, [], {})

    result = translator.translate("你好。", "zh-CN", "en_US")

    assert result.src == "zh" and result.tgt == "en"
    assert result.route == ["opus-mt-zh-en"]


def test_paragraphs_are_joined_and_cjk_target_has_no_gap(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh", ">>cmn_Hans<<")

    def zh_en_factory(_record: ModelRecord) -> ScriptBackend:
        return ScriptBackend(["First.", "Second."])

    zh_en = Translator(tmp_path, backend_factory=zh_en_factory)
    paragraph = zh_en.translate("甲。\n\n乙。", "zh", "en")
    assert paragraph.text == "First.\n\nSecond."
    assert paragraph.route == ["opus-mt-zh-en"]

    def en_zh_factory(_record: ModelRecord) -> ScriptBackend:
        return ScriptBackend(["你好。", "世界。"])

    en_zh = Translator(tmp_path, backend_factory=en_zh_factory)
    compact = en_zh.translate("Hello. World.", "en", "zh")
    assert compact.text == "你好。世界。"
    assert " " not in compact.text


def test_english_sentences_are_joined_with_one_space(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    boxes: dict[str, TagBackend] = {}
    translator = _translator(tmp_path, [], boxes)

    result = translator.translate("Hello. World.", "zh", "en")

    assert boxes["opus-mt-zh-en"].batches == [["Hello.", "World."]]
    assert result.text == "<opus-mt-zh-en>Hello. <opus-mt-zh-en>World."


def test_installed_direct_beats_any_other_id(tmp_path: Path) -> None:
    _install(tmp_path, "custom-zh-en", "zh", "en")
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    log: list[str] = []
    translator = _translator(tmp_path, log, {})

    preferred = translator.translate("你好。", "zh", "en")
    assert preferred.route == ["opus-mt-zh-en"]

    only_custom = tmp_path / "only-custom"
    only_custom.mkdir()
    _install(only_custom, "custom-zh-en", "zh", "en")
    custom = _translator(only_custom, [], {})
    assert custom.translate("你好。", "zh", "en").route == ["custom-zh-en"]


def test_available_pairs_lists_direct_and_pivot(tmp_path: Path) -> None:
    assert Translator(tmp_path).available_pairs() == []
    _install(tmp_path, "opus-mt-ja-en", "ja", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh", ">>cmn_Hans<<")
    translator = Translator(tmp_path, backend_factory=lambda record: TagBackend(record))

    assert translator.available_pairs() == [
        ("en", "zh", "direct"),
        ("ja", "en", "direct"),
        ("ja", "zh", "pivot"),
    ]


def test_translate_many_matches_single_calls_and_fails_fast(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    log: list[str] = []
    translator = _translator(tmp_path, log, {})
    texts = ["你好。", "世界。"]

    many = translator.translate_many(texts, "zh", "en")
    assert [item.text for item in many] == [
        translator.translate(text, "zh", "en").text for text in texts
    ]
    assert [item.route for item in many] == [["opus-mt-zh-en"], ["opus-mt-zh-en"]]
    assert log == ["opus-mt-zh-en"]

    same = translator.translate_many(["甲。", "乙。"], "zh", "zh")
    assert [item.text for item in same] == ["甲。", "乙。"]
    assert log == ["opus-mt-zh-en"]

    blank = translator.translate_many(["", " \n"], "xx", "yy")
    assert [item.text for item in blank] == ["", ""]

    with pytest.raises(UnsupportedPairError) as exc_info:
        translator.translate_many(["", "你好。"], "zh", "ja")
    assert "opus-mt-tc-big-zh-ja" in exc_info.value.missing_ids


def test_backend_length_mismatch_raises(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    translator = Translator(tmp_path, backend_factory=lambda _record: BadLengthBackend())

    with pytest.raises(RuntimeError, match="期望"):
        translator.translate("你好。世界。", "zh", "en")


def test_preload_loads_pivot_once(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-ja-en", "ja", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh", ">>cmn_Hans<<")
    log: list[str] = []
    translator = _translator(tmp_path, log, {})

    translator.preload([("ja", "zh"), ("ja", "ja")])

    assert log == ["opus-mt-ja-en", "opus-mt-en-zh"]
    translator.translate("こんにちは。", "ja", "zh")
    assert log == ["opus-mt-ja-en", "opus-mt-en-zh"]
    assert translator.loaded_model_ids() == ["opus-mt-en-zh", "opus-mt-ja-en"]


def test_ten_threads_load_a_model_once(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    log: list[str] = []
    lock = threading.Lock()

    def factory(record: ModelRecord) -> TagBackend:
        with lock:
            log.append(record.id)
        time.sleep(0.2)
        return TagBackend(record)

    translator = Translator(tmp_path, backend_factory=factory)
    errors: list[BaseException] = []
    texts: list[str] = []

    def worker() -> None:
        try:
            texts.append(translator.translate("你好。", "zh", "en").text)
        except BaseException as exc:  # noqa: BLE001 — 收集线程里的失败
            errors.append(exc)

    threads = [threading.Thread(target=worker) for _ in range(10)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    assert errors == []
    assert texts == ["<opus-mt-zh-en>你好。"] * 10
    assert log == ["opus-mt-zh-en"]


def test_scanner_skips_partial_dirs_and_rejects_bad_metadata(tmp_path: Path) -> None:
    partial = tmp_path / ".opus-mt-zh-en.partial"
    partial.mkdir()
    (partial / "suiyi-model.json").write_text("{", encoding="utf-8")
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    translator = Translator(tmp_path, backend_factory=lambda record: TagBackend(record))
    assert translator.available_pairs() == [("zh", "en", "direct")]

    broken = tmp_path / "broken"
    broken.mkdir()
    (broken / "suiyi-model.json").write_text("{", encoding="utf-8")
    with pytest.raises(ValueError, match="无法解析"):
        Translator(tmp_path, backend_factory=lambda record: TagBackend(record))

    duplicated = tmp_path / "copy"
    duplicated.mkdir()
    _install(duplicated, "left", "zh", "en")
    _install(duplicated, "right", "zh", "en")
    meta = json.loads((duplicated / "right" / "suiyi-model.json").read_text(encoding="utf-8"))
    meta["id"] = "left"
    (duplicated / "right" / "suiyi-model.json").write_text(json.dumps(meta), encoding="utf-8")
    with pytest.raises(ValueError, match="重复"):
        Translator(duplicated, backend_factory=lambda record: TagBackend(record))


def test_models_dir_and_thread_defaults(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.delenv("SUIYI_MODELS_DIR", raising=False)
    assert default_models_dir() == repo_root() / "models"
    monkeypatch.setenv("SUIYI_MODELS_DIR", "  ")
    assert default_models_dir() == repo_root() / "models"
    monkeypatch.setenv("SUIYI_MODELS_DIR", str(tmp_path))
    assert default_models_dir() == tmp_path
    assert Translator().registry.models_dir == tmp_path
    assert 1 <= default_intra_threads() <= 4

    with pytest.raises(ValueError, match="beam_size"):
        Translator(tmp_path, beam_size=0)
    with pytest.raises(ValueError, match="intra_threads"):
        Translator(tmp_path, intra_threads=True)  # type: ignore[arg-type]


def test_ct2_backend_rejects_bad_files_before_loading(tmp_path: Path) -> None:
    missing = ModelRecord("opus-mt-zh-en", "zh", "en", tmp_path, None, "int8")
    with pytest.raises(FileNotFoundError, match="model.bin"):
        Ct2OpusBackend(missing)

    wrong = ModelRecord("opus-mt-zh-en", "zh", "en", tmp_path, None, "float16")
    with pytest.raises(ValueError, match="float16"):
        Ct2OpusBackend(wrong, compute_type="int8")


def test_runtime_modules_do_not_import_torch() -> None:
    for name in (
        "translator.py",
        "registry.py",
        "backends/ct2_opus.py",
        "backends/base.py",
        "api.py",
        "serve.py",
    ):
        source = (_ENGINE_SRC / name).read_text(encoding="utf-8")
        assert "import torch" not in source
        assert "import transformers" not in source
        tree = ast.parse(source)
        for node in tree.body:
            if isinstance(node, ast.Import):
                imported = [alias.name.split(".")[0] for alias in node.names]
                assert "ctranslate2" not in imported
                assert "sentencepiece" not in imported
            elif isinstance(node, ast.ImportFrom) and node.module is not None:
                root = node.module.split(".", 1)[0]
                assert root not in {"ctranslate2", "sentencepiece", "torch", "transformers"}


@pytest.mark.parametrize(
    ("text", "lang", "expected"),
    [
        ("今 天 天 气 很 好 。", "zh", "今天天气很好。"),
        ("你 好 啊", "zh", "你好啊"),
        ("使用 Python 语言", "zh", "使用 Python 语言"),
        (">>cmn_Hans<< 你好", "zh", "你好"),
        ("こん にちは", "ja", "こんにちは"),
        ("▁Hello ▁world", "en", "Hello world"),
        ("Hello   world", "en", "Hello world"),
    ],
)
def test_cleanup_removes_sentencepiece_artifacts(text: str, lang: str, expected: str) -> None:
    assert cleanup_decoded(text, lang) == expected
    if lang in {"zh", "ja"}:
        assert not has_spurious_cjk_spacing(cleanup_decoded(text, lang))


def test_decode_drops_special_tokens_and_cjk_spaces() -> None:
    class FakeSP:
        def decode(self, pieces: list[str]) -> str:
            return " ".join(pieces)

    text = decode_hypothesis(FakeSP(), ["今", "天", "</s>"], "zh")
    assert text == "今天"
    assert "▁" not in text
    assert "</s>" not in text


def test_source_tokens_put_prefix_first() -> None:
    assert prepare_source_tokens(["▁Hello", "▁world"], ">>cmn_Hans<<") == [
        ">>cmn_Hans<<",
        "▁Hello",
        "▁world",
        "</s>",
    ]
    assert prepare_source_tokens(["▁Hi", "</s>"], None) == ["▁Hi", "</s>"]
    assert prepare_source_tokens([], None) == ["</s>"]
    assert sort_indices_by_length(["bb", "a", "ccc", "a"]) == [1, 3, 0, 2]
    assert has_spurious_cjk_spacing("你 好")
    assert has_spurious_cjk_spacing("你好▁")
    assert not has_spurious_cjk_spacing("Hello world")
    assert not has_spurious_cjk_spacing("使用 Python")


def test_cli_reports_missing_model_id(tmp_path: Path) -> None:
    env = os.environ.copy()
    env["SUIYI_MODELS_DIR"] = str(tmp_path)
    env["PYTHONIOENCODING"] = "utf-8"
    completed = subprocess.run(
        [
            sys.executable,
            "-m",
            "suiyi_engine",
            "translate",
            "--src",
            "zh",
            "--tgt",
            "en",
            "你好。",
        ],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=env,
    )
    assert completed.returncode == 1
    assert "opus-mt-zh-en" in completed.stderr


def _require_models(*model_ids: str) -> None:
    root = Path(os.environ["SUIYI_MODELS_DIR"])
    missing = [
        model_id for model_id in model_ids if not (root / model_id / "suiyi-model.json").is_file()
    ]
    if missing:
        pytest.skip("尚未转换 " + "、".join(missing))


_REAL: Translator | None = None


def _real_translator() -> Translator:
    global _REAL
    if _REAL is None:
        _REAL = Translator(os.environ["SUIYI_MODELS_DIR"])
    return _REAL


@pytest.mark.model
def test_model_zh_en_direct() -> None:
    _require_models("opus-mt-zh-en")
    result = _real_translator().translate("今天天气很好。我们去公园吧。", "zh", "en")
    lowered = result.text.lower()
    assert result.route == ["opus-mt-zh-en"]
    assert result.elapsed_ms > 0
    assert "▁" not in result.text
    assert re.search(r"[A-Za-z]{3,}", result.text)
    assert any(word in lowered for word in ("weather", "today", "nice", "good", "fine", "day"))
    assert any(word in lowered for word in ("park", "garden", "go", "let", "we"))


@pytest.mark.model
def test_model_en_zh_direct_has_no_cjk_gaps() -> None:
    _require_models("opus-mt-en-zh")
    result = _real_translator().translate("The weather is nice today.", "en", "zh")
    assert result.route == ["opus-mt-en-zh"]
    assert re.search(r"[\u4e00-\u9fff]", result.text)
    assert not has_spurious_cjk_spacing(result.text)


@pytest.mark.model
def test_model_ja_zh_pivots_through_english() -> None:
    _require_models("opus-mt-ja-en", "opus-mt-en-zh")
    result = _real_translator().translate("今日はいい天気です。", "ja", "zh")
    assert result.route == ["opus-mt-ja-en", "opus-mt-en-zh"]
    assert len(result.route) == 2
    assert re.search(r"[\u4e00-\u9fff]", result.text)
    assert not has_spurious_cjk_spacing(result.text)


@pytest.mark.model
def test_model_cli_zh_en() -> None:
    _require_models("opus-mt-zh-en")
    env = os.environ.copy()
    completed = subprocess.run(
        [
            sys.executable,
            "-m",
            "suiyi_engine",
            "translate",
            "--src",
            "zh",
            "--tgt",
            "en",
            "今天天气很好。我们去公园吧。",
        ],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=env,
    )
    assert completed.returncode == 0
    assert "route: opus-mt-zh-en" in completed.stdout
    assert "elapsed_ms:" in completed.stdout
    assert "▁" not in completed.stdout
