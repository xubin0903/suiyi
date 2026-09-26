"""#92：分配器设置、后台整理、模型空闲卸载、精简语种检测。"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
from pathlib import Path

import pytest

from suiyi_engine import langdetect, langid_slim, memory
from suiyi_engine.api import ApiSettings
from suiyi_engine.registry import ModelRecord, ModelRegistry
from suiyi_engine.serve import ServeError, resolve_model_idle_unload

# ---------------------------------------------------------------- 分配器


def test_configure_allocator_sets_mkl_default_without_overriding_user() -> None:
    env: dict[str, str] = {}
    applied = memory.configure_allocator(env)
    assert env["MKL_DISABLE_FAST_MM"] == "1"
    assert applied["mkl_disable_fast_mm"] == "1"
    assert env["OPENBLAS_NUM_THREADS"] == "1"  # #96
    user = {"MKL_DISABLE_FAST_MM": "0", "MALLOC_ARENA_MAX": "4"}
    applied = memory.configure_allocator(user)
    assert user["MKL_DISABLE_FAST_MM"] == "0"
    assert "malloc_arena_max" not in applied  # 用户设了 MALLOC_ARENA_MAX 就不动


def test_trim_matches_platform() -> None:
    # glibc 上 malloc_trim；Windows 上 HeapCompact + _heapmin（#96）；其他平台空操作
    assert memory.trim() is (sys.platform.startswith("linux") or sys.platform == "win32")


# ---------------------------------------------------------------- 模型空闲卸载


class Backend:
    closed = 0

    def __init__(self, record: ModelRecord) -> None:
        self.record = record

    def translate_batch(self, sentences: list[str]) -> list[str]:
        return sentences

    def close(self) -> None:
        Backend.closed += 1


def _registry(tmp_path: Path) -> ModelRegistry:
    for model_id, src, tgt in (("opus-mt-zh-en", "zh", "en"), ("opus-mt-en-zh", "en", "zh")):
        directory = tmp_path / model_id
        directory.mkdir()
        meta = {"id": model_id, "src": src, "tgt": tgt, "src_prefix_token": None}
        (directory / "suiyi-model.json").write_text(json.dumps(meta), encoding="utf-8")
    return ModelRegistry(tmp_path, backend_factory=Backend)


def test_registry_unloads_only_idle_models(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    clock = [100.0]
    monkeypatch.setattr("suiyi_engine.registry.time.monotonic", lambda: clock[0])
    registry = _registry(tmp_path)
    assert registry.last_activity() is None
    registry.get("opus-mt-zh-en")
    clock[0] = 150.0
    registry.get("opus-mt-en-zh")
    assert registry.last_activity() == 150.0
    Backend.closed = 0
    assert registry.unload_idle(60, now=159.9) == []  # zh-en 空闲 59.9 秒
    assert registry.unload_idle(60, now=160.0) == ["opus-mt-zh-en"]  # 刚满 60 秒
    assert registry.loaded_model_ids() == ["opus-mt-en-zh"]
    assert Backend.closed == 1
    assert registry.unload("opus-mt-en-zh") is True
    assert registry.unload("opus-mt-en-zh") is False
    assert registry.loaded_model_ids() == []
    again = registry.get("opus-mt-zh-en")  # 卸载后再用会重新加载
    assert registry.loaded_model_ids() == ["opus-mt-zh-en"] and again is not None


class FakeRegistry:
    def __init__(self) -> None:
        self.last: float | None = None
        self.unload_calls: list[float] = []
        self.to_unload: list[str] = []

    def unload_idle(self, idle_s: float, *, now: float | None = None) -> list[str]:
        self.unload_calls.append(idle_s)
        unloaded, self.to_unload = self.to_unload, []
        return unloaded

    def last_activity(self) -> float | None:
        return self.last


def test_janitor_trims_once_after_quiet_period_and_skips_while_translating() -> None:
    clock = [0.0]
    trims: list[float] = []
    registry = FakeRegistry()
    lock = threading.Lock()
    janitor = memory.ModelJanitor(
        registry,
        lock,
        0,
        clock=lambda: clock[0],
        trimmer=lambda: trims.append(clock[0]) or True,
    )
    janitor.tick()
    assert trims == [] and registry.unload_calls == []  # 没用过模型、不卸载
    registry.last = 10.0
    clock[0] = 11.0
    janitor.tick()
    assert trims == []  # 还没安静够 2 秒
    clock[0] = 12.5
    with lock:  # 翻译进行中：这一轮什么都不做
        janitor.tick()
    assert trims == []
    janitor.tick()
    assert trims == [12.5]
    clock[0] = 20.0
    janitor.tick()
    assert trims == [12.5]  # 同一次活动只整理一次


def test_janitor_unloads_idle_models_and_trims() -> None:
    clock = [1000.0]
    trims: list[float] = []
    registry = FakeRegistry()
    janitor = memory.ModelJanitor(
        registry,
        threading.Lock(),
        600,
        clock=lambda: clock[0],
        trimmer=lambda: trims.append(1) or True,
    )
    registry.to_unload = ["opus-mt-zh-en"]
    assert janitor.tick() == ["opus-mt-zh-en"]
    assert registry.unload_calls == [600] and trims == [1]


def test_janitor_thread_starts_and_stops() -> None:
    janitor = memory.ModelJanitor(FakeRegistry(), threading.Lock(), 0, interval_s=0.01)
    janitor.start()
    janitor.start()  # 重复启动无害
    janitor.stop()
    assert janitor._thread is None


# ---------------------------------------------------------------- 配置


def test_model_idle_unload_cli_over_env_over_default(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("SUIYI_MODEL_IDLE_UNLOAD", raising=False)
    assert resolve_model_idle_unload(None) == 600
    monkeypatch.setenv("SUIYI_MODEL_IDLE_UNLOAD", "0")
    assert resolve_model_idle_unload(None) == 0
    monkeypatch.setenv("SUIYI_MODEL_IDLE_UNLOAD", "120")
    assert resolve_model_idle_unload(None) == 120
    assert resolve_model_idle_unload(30) == 30
    for bad in ("x", "-1", "1.5"):
        monkeypatch.setenv("SUIYI_MODEL_IDLE_UNLOAD", bad)
        with pytest.raises(ServeError, match="SUIYI_MODEL_IDLE_UNLOAD"):
            resolve_model_idle_unload(None)
    with pytest.raises(ServeError, match="--model-idle-unload"):
        resolve_model_idle_unload(-5)


def test_api_settings_validates_idle_unload() -> None:
    assert ApiSettings(model_idle_unload_s=600).model_idle_unload_s == 600
    for bad in (-1, True, 1.5):
        with pytest.raises(ValueError, match="model_idle_unload_s"):
            ApiSettings(model_idle_unload_s=bad)  # type: ignore[arg-type]


def test_serve_passes_idle_unload_to_health_and_janitor(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    from suiyi_engine.__main__ import main

    seen: dict[str, object] = {}

    def fake_uvicorn(app: object, _sock: object) -> None:
        seen["settings"] = app.state.settings  # type: ignore[attr-defined]

    started: list[float] = []
    real_start = memory.ModelJanitor.start

    def spy_start(self: memory.ModelJanitor) -> None:
        started.append(self.idle_unload_s)
        real_start(self)

    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fake_uvicorn)
    monkeypatch.setattr("suiyi_engine.serve.langdetect.warmup", lambda: 1.0)
    monkeypatch.setattr(memory.ModelJanitor, "start", spy_start)
    monkeypatch.setenv("SUIYI_MODEL_IDLE_UNLOAD", "90")
    import socket

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    assert main(["serve", "--models-dir", str(tmp_path), "--port", str(port)]) == 0
    assert seen["settings"].model_idle_unload_s == 90  # type: ignore[attr-defined]
    assert started == [90]
    assert "模型空闲卸载 90 秒" in capsys.readouterr().out


# ---------------------------------------------------------------- 精简语种检测

TEXTS = [
    "",
    "12345 !!!",
    "HELLO WORLD",
    "The quick brown fox jumps over the lazy dog.",
    "Bonjour tout le monde, comment allez-vous aujourd'hui ?",
    "Guten Morgen, wie geht es dir heute? Die Straße ist schön.",
    "¿Dónde está la estación de tren más cercana?",
    "Kubernetes is an open-source container orchestration engine.",
    "naïve café résumé",
]


@pytest.fixture(scope="module")
def slim_cache(tmp_path_factory: pytest.TempPathFactory) -> Path:
    return langid_slim.ensure_cache(tmp_path_factory.mktemp("lid"), ("de", "en", "es", "fr"))


def test_slim_identifier_matches_py3langid(slim_cache: Path) -> None:
    slim = langid_slim.SlimIdentifier(slim_cache)
    full = langdetect._load_py3langid()
    try:
        for text in TEXTS:
            ours = slim.rank(text)
            theirs = {lang: float(p) for lang, p in full.rank(text)}
            assert (
                ours[0][0] == max(theirs, key=theirs.__getitem__) or len(set(theirs.values())) == 1
            )
            for lang, prob in ours:
                assert prob == pytest.approx(theirs[lang], abs=1e-4)
    finally:
        slim.close()


def test_ensure_cache_reuses_existing_file(
    slim_cache: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    def boom(*_args: object, **_kwargs: object) -> None:
        raise AssertionError("缓存已存在，不应再起子进程")

    monkeypatch.setattr(langid_slim.subprocess, "run", boom)
    assert langid_slim.ensure_cache(slim_cache.parent, ("de", "en", "es", "fr")) == slim_cache


def test_corrupt_cache_is_rejected(tmp_path: Path) -> None:
    bad = tmp_path / "bad.bin"
    bad.write_bytes(b"not a cache file at all")
    with pytest.raises(ValueError):
        langid_slim.SlimIdentifier(bad)


def test_langdetect_falls_back_when_cache_dir_unusable(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, caplog: pytest.LogCaptureFixture
) -> None:
    blocker = tmp_path / "file"
    blocker.write_text("x", encoding="utf-8")
    monkeypatch.setenv("SUIYI_CACHE_DIR", str(blocker / "sub"))  # 父路径是文件，建不了目录
    assert langdetect._load_slim() is None
    assert "改用 py3langid" in caplog.text
    monkeypatch.setenv("SUIYI_LANGID_SLIM", "0")
    assert langdetect._load_slim() is None


def test_default_cache_dir_order(tmp_path: Path) -> None:
    assert langid_slim.default_cache_dir({"SUIYI_CACHE_DIR": str(tmp_path)}) == tmp_path
    xdg = langid_slim.default_cache_dir({"XDG_CACHE_HOME": str(tmp_path)})
    if sys.platform != "win32":
        assert xdg == tmp_path / "suiyi"


def test_warm_detector_does_not_import_numpy(slim_cache: Path) -> None:
    """服务进程用缓存时不导入 numpy（#92 内存）。在子进程里验证，避免测试进程已导入。"""

    code = (
        "import sys; from suiyi_engine import langdetect; langdetect.warmup(); "
        "print(type(langdetect._identifier).__name__, 'numpy' in sys.modules, "
        "langdetect.detect('Guten Morgen, wie geht es dir heute?').lang)"
    )
    env = dict(os.environ, SUIYI_CACHE_DIR=str(slim_cache.parent))
    done = subprocess.run(
        [sys.executable, "-c", code], env=env, capture_output=True, text=True, check=True
    )
    assert done.stdout.split() == ["SlimIdentifier", "False", "de"]
