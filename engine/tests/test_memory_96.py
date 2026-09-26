"""#96：同时常驻模型上限（LRU）、OCR 空闲卸载、配置解析。"""

from __future__ import annotations

import json
import threading
from pathlib import Path

import pytest

from suiyi_engine import memory
from suiyi_engine.api_ocr import OcrProvider
from suiyi_engine.ocr import OcrEngine
from suiyi_engine.registry import ModelRecord, ModelRegistry
from suiyi_engine.serve import ServeError, resolve_max_loaded_models


class Backend:
    def __init__(self, record: ModelRecord) -> None:
        self.record = record

    def translate_batch(self, sentences: list[str]) -> list[str]:
        return sentences


def _registry(tmp_path: Path, max_loaded: int) -> ModelRegistry:
    for model_id, src, tgt in (
        ("opus-mt-zh-en", "zh", "en"),
        ("opus-mt-en-zh", "en", "zh"),
        ("opus-mt-en-ja", "en", "ja"),
        ("opus-mt-ja-en", "ja", "en"),
    ):
        directory = tmp_path / model_id
        directory.mkdir()
        meta = {"id": model_id, "src": src, "tgt": tgt, "src_prefix_token": None}
        (directory / "suiyi-model.json").write_text(json.dumps(meta), encoding="utf-8")
    return ModelRegistry(tmp_path, backend_factory=Backend, max_loaded=max_loaded)


def test_registry_evicts_least_recently_used_before_loading(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    clock = [0.0]
    monkeypatch.setattr("suiyi_engine.registry.time.monotonic", lambda: clock[0])
    registry = _registry(tmp_path, 2)
    registry.get("opus-mt-en-zh")
    clock[0] = 1
    registry.get("opus-mt-zh-en")
    clock[0] = 2
    registry.get("opus-mt-en-zh")  # 再用一次：zh-en 变成最久没用的
    clock[0] = 3
    registry.get("opus-mt-en-ja")
    assert registry.loaded_model_ids() == ["opus-mt-en-ja", "opus-mt-en-zh"]


def test_pivot_route_keeps_both_legs_with_limit_two(tmp_path: Path) -> None:
    registry = _registry(tmp_path, 2)
    registry.get("opus-mt-zh-en")
    records = registry.resolve("ja", "zh")  # ja→en→zh
    assert [record.id for record in records] == ["opus-mt-ja-en", "opus-mt-en-zh"]
    backends = [registry.get(record.id) for record in records]
    assert registry.loaded_model_ids() == ["opus-mt-en-zh", "opus-mt-ja-en"]
    assert all(backend is not None for backend in backends)


def test_zero_means_unlimited_and_bad_values_are_rejected(tmp_path: Path) -> None:
    registry = _registry(tmp_path, 0)
    for model_id in ("opus-mt-zh-en", "opus-mt-en-zh", "opus-mt-en-ja"):
        registry.get(model_id)
    assert len(registry.loaded_model_ids()) == 3
    with pytest.raises(ValueError):
        ModelRegistry(tmp_path, backend_factory=Backend, max_loaded=-1)


def test_resolve_max_loaded_models_cli_env_default(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("SUIYI_MAX_LOADED_MODELS", raising=False)
    assert resolve_max_loaded_models(None) == 2
    monkeypatch.setenv("SUIYI_MAX_LOADED_MODELS", "3")
    assert resolve_max_loaded_models(None) == 3
    assert resolve_max_loaded_models(0) == 0  # 命令行优先
    for bad in ("1", "-2", "x"):
        monkeypatch.setenv("SUIYI_MAX_LOADED_MODELS", bad)
        with pytest.raises(ServeError):
            resolve_max_loaded_models(None)
    with pytest.raises(ServeError):
        resolve_max_loaded_models(1)


# ---------------------------------------------------------------- OCR 空闲卸载


def _fake_engine(loads: list[int]) -> OcrEngine:
    def factory(paths: object, manifest: object, threads: int) -> object:
        loads.append(1)
        return lambda array: []

    engine = OcrEngine(Path("unused"), backend_factory=factory)  # type: ignore[arg-type]
    engine.check = lambda: {}  # type: ignore[method-assign]
    return engine


def test_ocr_unloads_after_idle_and_reloads_on_next_use(monkeypatch: pytest.MonkeyPatch) -> None:
    clock = [100.0]
    monkeypatch.setattr("suiyi_engine.api_ocr.time.monotonic", lambda: clock[0])
    loads: list[int] = []
    engine = _fake_engine(loads)
    provider = OcrProvider(Path("unused"), engine_factory=lambda: engine)
    assert provider.unload_idle(60) is False  # 从没用过
    provider.engine()
    assert provider.loaded and provider.last_activity() == 100.0
    assert provider.unload_idle(60, now=159.0) is False
    assert provider.unload_idle(60, now=160.0) is True
    assert not provider.loaded
    provider.engine()  # 下一次使用重新加载
    assert provider.loaded and len(loads) == 2


def test_ocr_is_not_unloaded_while_recognizing() -> None:
    engine = _fake_engine([])
    engine.load()
    with engine._run_lock:  # 模拟识别进行中
        assert engine.unload() is False
    assert engine.loaded
    assert engine.unload() is True
    assert engine.unload() is False  # 已经卸载


class FakeRegistry:
    def unload_idle(self, idle_s: float, *, now: float | None = None) -> list[str]:
        return []

    def last_activity(self) -> float | None:
        return None


class FakeOcr:
    def __init__(self) -> None:
        self.calls: list[float] = []
        self.last: float | None = 5.0

    def unload_idle(self, idle_s: float, *, now: float | None = None) -> bool:
        self.calls.append(idle_s)
        return True

    def last_activity(self) -> float | None:
        return self.last


def test_janitor_unloads_ocr_and_trims_after_ocr_activity() -> None:
    trims: list[int] = []
    ocr = FakeOcr()
    janitor = memory.ModelJanitor(
        FakeRegistry(),
        threading.Lock(),
        30,
        clock=lambda: 10.0,
        trimmer=lambda: trims.append(1) or True,
        ocr=ocr,
    )
    assert janitor.tick() == ["ocr"]
    assert ocr.calls == [30] and trims == [1]
    off = memory.ModelJanitor(FakeRegistry(), threading.Lock(), 0, ocr=ocr)
    assert off.tick() == [] and ocr.calls == [30]  # 0 表示不卸载
