"""表外极短句改用 opus-mt-en-zh（#122）：质量门、按需加载与常驻名额。"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from suiyi_engine.registry import ModelRecord, ModelRegistry
from suiyi_engine.short_fallback import check, eligible, is_short, tidy_fallback
from suiyi_engine.terms import GlossaryStore
from suiyi_engine.translator import Translator

TC_BIG = "opus-mt-eng-zho-tc-big-2022-05-14"
AUX = "opus-mt-en-zh"


def _install(root: Path, model_id: str, src: str, tgt: str) -> None:
    directory = root / model_id
    directory.mkdir(parents=True, exist_ok=True)
    payload = {"id": model_id, "src": src, "tgt": tgt, "src_prefix_token": None}
    (directory / "suiyi-model.json").write_text(json.dumps(payload), encoding="utf-8")
    (directory / "model.bin").write_bytes(b"fake")


class Table:
    def __init__(self, name: str, mapping: dict[str, str]) -> None:
        self.name = name
        self.mapping = mapping
        self.calls: list[list[str]] = []

    def translate_batch(self, sentences: list[str]) -> list[str]:
        self.calls.append(list(sentences))
        return [self.mapping.get(s, f"{self.name}:{s}") for s in sentences]


# ---- 质量门


@pytest.mark.parametrize(
    ("source", "output", "expected"),
    [
        ("Take a seat.", "请坐。", ("请坐。", "ok")),
        ("It's cold today.", "今天很冷，我还想说...", ("今天很冷", "trimmed")),
        ("Where to?", "去哪？来？", ("去哪", "trimmed")),
        ("Save your work.", "省省吧省省时间吧。", (None, "repeat")),
        ("I'm working from home.", "我在家里工作工作，我在家工作。", (None, "repeat")),
        ("Hello.", "Hello.", (None, "empty")),
        ("This is a test.", "硂琌代刚", (None, "mojibake")),
        ("Good night, everyone.", "边。", (None, "length")),
        ("Good.", "非常非常好的一个很长的回答内容", (None, "repeat")),
        ("Nice.", "这是一个相当不错的结果", (None, "length")),
        ("Open the file.", "打开 file 文件。", ("打开 file 文件。", "ok")),
        ("Open it.", "打开 README。", (None, "latin")),
        ("Hey, what's up?", "嘿，你好吗？", ("嘿，你好吗？", "ok")),  # 原文本来就两个分句
    ],
)
def test_check(source: str, output: str, expected: tuple[str | None, str]) -> None:
    assert check(source, output) == expected


@pytest.mark.parametrize(
    ("text", "short"),
    [
        ("Take a seat.", True),
        ("I'm so happy.", True),
        ("Pass the salt to me, please.", False),  # 6 个词
        ("A" * 41, False),
        ("", False),
        ("two\nlines", False),
    ],
)
def test_is_short(text: str, short: bool) -> None:
    assert is_short(text) is short


def test_tidy_fallback_trims_extra_clause_only_for_single_clause_source() -> None:
    assert tidy_fallback("I'm working from home.", "我在家工作，我在家工作。") == "我在家工作"
    assert tidy_fallback("Well, okay.", "好吧，没问题。") == "好吧，没问题。"
    assert tidy_fallback("Fine.", "很好。") == "很好。"


# ---- 翻译器


@pytest.fixture
def models(tmp_path: Path) -> Path:
    _install(tmp_path, TC_BIG, "en", "zh")
    _install(tmp_path, AUX, "en", "zh")
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    return tmp_path


def _translator(models: Path, made: dict[str, Table], **kwargs: object) -> Translator:
    tables = {
        TC_BIG: {"Take a seat.": "便下座问", "Save your work.": "保存你的工作"},
        AUX: {"Take a seat.": "请坐", "Save your work.": "省省吧省省时间吧"},
        "opus-mt-zh-en": {},
    }

    def factory(record: ModelRecord) -> Table:
        made[record.id] = Table(record.id, tables[record.id])
        return made[record.id]

    options: dict[str, object] = {"glossary": None, "short_fallback_evict_idle_s": 0.0}
    options.update(kwargs)
    return Translator(models, backend_factory=factory, **options)  # type: ignore[arg-type]


def test_short_miss_uses_fallback_model_and_long_sentence_does_not(models: Path) -> None:
    made: dict[str, Table] = {}
    translator = _translator(models, made)
    assert translator.translate("Take a seat.", "en", "zh").text == "请坐。"
    long = "Please take a seat in the waiting room."
    assert translator.translate(long, "en", "zh").text == f"{TC_BIG}:{long}"
    assert made[AUX].calls == [["Take a seat."]]
    stats = translator.short_fallback_stats
    assert (stats.candidates, stats.used) == (1, 1)
    assert translator.short_fallback_status()["short_fallback_loaded"] is True


@pytest.mark.parametrize(
    ("text", "alone", "expected"),
    [
        ("Take a seat.", True, True),
        ("Where to?", True, True),
        ('"Nice try!"', True, True),
        ("Take a seat.", False, False),  # 段落里的短句
        ("Remember me on this device", True, False),  # 不带句末标点的界面文案
        ("Reconnecting...", True, False),
        ("Please take a seat in the waiting room.", True, False),
    ],
)
def test_eligible(text: str, alone: bool, expected: bool) -> None:
    assert eligible(text, alone=alone) is expected


def test_paragraph_and_label_stay_on_default_model(models: Path) -> None:
    made: dict[str, Table] = {}
    translator = _translator(models, made)
    translator.translate("Take a seat. The doctor will see you soon.", "en", "zh")
    translator.translate("Take a seat", "en", "zh")
    assert AUX not in made
    assert translator.short_fallback_stats.candidates == 0


def test_failed_gate_falls_back_to_default_model(models: Path) -> None:
    made: dict[str, Table] = {}
    translator = _translator(models, made)
    assert translator.translate("Save your work.", "en", "zh").text == "保存你的工作。"
    assert translator.short_fallback_stats.rejected == {"repeat": 1}


def test_table_hit_term_and_other_directions_skip_fallback(models: Path) -> None:
    made: dict[str, Table] = {}
    store = GlossaryStore(models / "none.tsv")
    translator = _translator(models, made, glossary=store)
    assert translator.translate("Thank you.", "en", "zh").text == "谢谢你。"  # 表内命中
    translator.translate("Deploy to Kubernetes.", "en", "zh")  # 有术语：走默认模型的术语保护
    translator.translate("请坐。", "zh", "en")
    assert AUX not in made
    assert translator.short_fallback_stats.candidates == 0


def test_disabled_or_not_installed(models: Path, tmp_path: Path) -> None:
    made: dict[str, Table] = {}
    translator = _translator(models, made, short_fallback=False)
    assert translator.translate("Take a seat.", "en", "zh").text == "便下座问。"
    assert translator.short_fallback_status()["short_fallback_model"] is None
    (models / AUX / "suiyi-model.json").unlink()
    made.clear()
    translator = _translator(models, made)
    assert translator.translate("Take a seat.", "en", "zh").text == "便下座问。"
    assert translator.short_fallback_stats.candidates == 0


def test_busy_slots_skip_fallback_then_idle_model_is_evicted(models: Path) -> None:
    made: dict[str, Table] = {}
    translator = _translator(models, made, max_loaded_models=2, short_fallback_evict_idle_s=60.0)
    translator.preload([("zh", "en"), ("en", "zh")])
    # 两个常驻模型刚用过：不挤，直接用 tc-big
    assert translator.translate("Take a seat.", "en", "zh").text == "便下座问。"
    assert translator.short_fallback_stats.skipped == 1
    assert sorted(translator.loaded_model_ids()) == sorted([TC_BIG, "opus-mt-zh-en"])
    # zh→en 闲了 60 秒以上：挤掉它，兜底模型加载
    registry = translator.registry
    registry._last_used["opus-mt-zh-en"] -= 120  # noqa: SLF001
    assert translator.translate("Take a seat.", "en", "zh").text == "请坐。"
    assert sorted(translator.loaded_model_ids()) == sorted([TC_BIG, AUX])
    # 常驻模型回来要名额：兜底模型最先被挤掉，不挤 tc-big
    translator.translate("请坐。", "zh", "en")
    assert sorted(translator.loaded_model_ids()) == sorted([TC_BIG, "opus-mt-zh-en"])


def test_registry_auxiliary_is_evicted_first_and_idle_unloaded(models: Path) -> None:
    registry = ModelRegistry(models, backend_factory=lambda r: Table(r.id, {}), max_loaded=2)
    registry.get(TC_BIG)
    assert registry.get_auxiliary(AUX, min_idle_s=0) is not None
    assert registry.is_auxiliary(AUX)
    registry.get("opus-mt-zh-en")  # 名额满：挤掉辅助模型而不是更久没用的 tc-big
    assert sorted(registry.loaded_model_ids()) == sorted([TC_BIG, "opus-mt-zh-en"])
    assert not registry.is_auxiliary(AUX)
    registry.get_auxiliary(AUX, min_idle_s=0)
    assert AUX in registry.unload_idle(0)
    # 作为常驻路由用到时不再算辅助模型
    registry.get_auxiliary(AUX, min_idle_s=0)
    registry.get(AUX)
    assert not registry.is_auxiliary(AUX)


def test_unlimited_slots_always_load(models: Path) -> None:
    registry = ModelRegistry(models, backend_factory=lambda r: Table(r.id, {}))
    registry.get(TC_BIG)
    registry.get("opus-mt-zh-en")
    assert registry.get_auxiliary(AUX, min_idle_s=1e9) is not None
    assert registry.get_auxiliary("missing", min_idle_s=0) is None


def test_serve_env(monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    from suiyi_engine.serve import resolve_short_fallback

    monkeypatch.delenv("SUIYI_SHORT_FALLBACK", raising=False)
    monkeypatch.delenv("SUIYI_SHORT_FALLBACK_EVICT_IDLE_S", raising=False)
    assert resolve_short_fallback() == (True, 60.0)
    monkeypatch.setenv("SUIYI_SHORT_FALLBACK", "0")
    monkeypatch.setenv("SUIYI_SHORT_FALLBACK_EVICT_IDLE_S", "5")
    assert resolve_short_fallback() == (False, 5.0)
    monkeypatch.setenv("SUIYI_SHORT_FALLBACK", "maybe")
    monkeypatch.setenv("SUIYI_SHORT_FALLBACK_EVICT_IDLE_S", "-1")
    assert resolve_short_fallback() == (True, 60.0)
    assert "无法识别" in capsys.readouterr().err


def test_tune_set_is_disjoint_from_table_and_short_set() -> None:
    """调参集（#122）与短句集、常用短句表都不重叠，留出集保持干净。"""

    from suiyi_engine.short_phrases import lookup_short_phrase

    root = Path(__file__).resolve().parents[2] / "engine" / "eval" / "short"

    def sources(name: str) -> list[str]:
        lines = (root / name).read_text(encoding="utf-8").splitlines()
        return [json.loads(line)["source"] for line in lines if line.strip()]

    tune = sources("short_tune.jsonl")
    assert len(tune) >= 50
    short = {text.casefold().strip() for text in sources("short_v1.jsonl")}
    for text in tune:
        assert text.casefold().strip() not in short, text
        assert lookup_short_phrase(text, "en", "zh") is None, text
