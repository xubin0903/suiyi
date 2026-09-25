"""模型转换脚本的单元测试。下载和转换都被替换，不访问网络。"""

import hashlib
import importlib
import json
import os
import shutil
import subprocess
import sys
import types
from pathlib import Path

import pytest

from suiyi_engine.tools import convert as convert_mod

ROOT = Path(__file__).resolve().parents[2]
EXAMPLE = ROOT / "engine" / "model_manifest.example.json"
ZH_EN_REVISION = "cf109095479db38d6df799875e34039d4938aaa6"
EN_ZH_REVISION = "408d9bc410a388e1d9aef112a2daba955b945255"


def _entry(model_id: str = "opus-mt-zh-en", **overrides: object) -> dict:
    entry: dict[str, object] = {
        "id": model_id,
        "src": "zh",
        "tgt": "en",
        "hf_repo": "Helsinki-NLP/opus-mt-zh-en",
        "hf_revision": ZH_EN_REVISION,
        "license": "CC-BY-4.0",
        "license_url": "https://creativecommons.org/licenses/by/4.0/legalcode",
        "attribution": "Helsinki-NLP OPUS-MT",
        "src_prefix_token": None,
        "tier": "mvp",
        "notes": "",
    }
    entry.update(overrides)
    return entry


def _manifest(*entries: dict, pivots: list | None = None) -> dict:
    return {
        "schema_version": 1,
        "models": list(entries) or [_entry()],
        "pivots": [] if pivots is None else pivots,
    }


def _write_manifest(directory: Path, document: dict | None = None) -> Path:
    path = directory / "manifest.json"
    payload = _manifest() if document is None else document
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return path


def _make_snapshot(directory: Path) -> Path:
    snapshot = directory / "snapshot"
    snapshot.mkdir()
    (snapshot / "source.spm").write_bytes(b"source-spm")
    (snapshot / "target.spm").write_bytes(b"target-spm")
    (snapshot / "vocab.json").write_text("{}\n", encoding="utf-8")
    return snapshot


def _install_fakes(monkeypatch: pytest.MonkeyPatch, snapshot: Path, *, fail: bool = False) -> dict:
    calls: dict[str, object] = {"download": [], "convert": []}

    def download(repo_id: str, revision: str) -> Path:
        calls["download"].append((repo_id, revision))
        return snapshot

    def convert(
        snapshot_dir: Path,
        output_dir: Path,
        quantization: str,
        copy_files: list[str],
    ) -> None:
        calls["convert"].append(
            {
                "snapshot": snapshot_dir,
                "quantization": quantization,
                "copy_files": list(copy_files),
            }
        )
        output_dir.mkdir(parents=True, exist_ok=True)
        if fail:
            (output_dir / "junk.bin").write_bytes(b"junk")
            raise RuntimeError("boom")
        (output_dir / "model.bin").write_bytes(b"bin")
        (output_dir / "config.json").write_text("{}\n", encoding="utf-8")
        (output_dir / "shared_vocabulary.txt").write_text("a\n", encoding="utf-8")
        for name in copy_files:
            shutil.copyfile(snapshot_dir / name, output_dir / name)

    monkeypatch.setattr(convert_mod, "download_snapshot", download)
    monkeypatch.setattr(convert_mod, "convert_snapshot", convert)
    monkeypatch.setattr(convert_mod, "current_ctranslate2_version", lambda: "4.6.0")
    monkeypatch.setattr(convert_mod, "utc_now_iso", lambda: "2026-09-25T00:00:00+00:00")
    return calls


def _run(manifest: Path, out_dir: Path, *extra: str) -> int:
    return convert_mod.main(
        ["--manifest", str(manifest), "--out-dir", str(out_dir), *extra],
    )


def test_example_manifest_pins_revisions_and_mvp_tier() -> None:
    manifest = convert_mod.load_manifest(EXAMPLE)
    by_id = {entry["id"]: entry for entry in manifest["models"]}
    assert set(by_id) == {"opus-mt-zh-en", "opus-mt-en-zh"}
    assert by_id["opus-mt-zh-en"]["hf_repo"] == "Helsinki-NLP/opus-mt-zh-en"
    assert by_id["opus-mt-zh-en"]["hf_revision"] == ZH_EN_REVISION
    assert by_id["opus-mt-zh-en"]["src_prefix_token"] is None
    assert by_id["opus-mt-en-zh"]["hf_repo"] == "Helsinki-NLP/opus-mt-en-zh"
    assert by_id["opus-mt-en-zh"]["hf_revision"] == EN_ZH_REVISION
    assert by_id["opus-mt-en-zh"]["src_prefix_token"] == ">>cmn_Hans<<"
    assert {entry["tier"] for entry in manifest["models"]} == {"mvp"}
    assert manifest["schema_version"] == 1
    assert manifest["pivots"] == []


def test_default_paths_follow_repo_root_and_env(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    root = convert_mod.repo_root_from_here()
    assert root == ROOT
    assert convert_mod.resolve_manifest(None, root) == root / "engine" / "model_manifest.json"
    monkeypatch.delenv("SUIYI_MODELS_DIR", raising=False)
    assert convert_mod.resolve_out_dir(None, root) == (root / "models").resolve()
    monkeypatch.setenv("SUIYI_MODELS_DIR", str(tmp_path / "from-env"))
    assert convert_mod.resolve_out_dir(None, root) == (tmp_path / "from-env").resolve()
    explicit = tmp_path / "flag"
    assert convert_mod.resolve_out_dir(explicit, root) == explicit.resolve()


def test_missing_official_manifest_points_at_example(tmp_path: Path) -> None:
    official = tmp_path / "model_manifest.json"
    example = tmp_path / "model_manifest.example.json"
    example.write_text("{}\n", encoding="utf-8")
    message = convert_mod.missing_manifest_message(official)
    assert "找不到清单" in message
    assert "--manifest" in message
    assert str(example) in message


def test_load_manifest_rejects_invalid_documents(tmp_path: Path) -> None:
    cases = [
        (_manifest(_entry(hf_revision="main")), "hf_revision"),
        (_manifest(_entry(id="../evil")), "id"),
        (_manifest(_entry(tier="experimental")), "tier"),
        (_manifest(_entry(src="cmn")), "src"),
        (_manifest(_entry(src_prefix_token=1)), "src_prefix_token"),
        ({"schema_version": 2, "models": [_entry()], "pivots": []}, "schema_version"),
        ({"schema_version": 1, "models": [_entry()]}, "pivots"),
        ({"schema_version": 1, "models": [_entry(), _entry()], "pivots": []}, "重复"),
    ]
    for document, expected in cases:
        path = _write_manifest(tmp_path, document)
        with pytest.raises(convert_mod.ConvertError, match=expected):
            convert_mod.load_manifest(path)
        path.unlink()


def test_download_snapshot_passes_pinned_revision(monkeypatch: pytest.MonkeyPatch) -> None:
    captured: dict[str, object] = {}

    def snapshot_download(**kwargs: object) -> str:
        captured.update(kwargs)
        return "/tmp/suiyi-snapshot"

    fake = types.ModuleType("huggingface_hub")
    fake.snapshot_download = snapshot_download
    monkeypatch.setitem(sys.modules, "huggingface_hub", fake)
    path = convert_mod.download_snapshot("Helsinki-NLP/opus-mt-zh-en", ZH_EN_REVISION)
    assert path == Path("/tmp/suiyi-snapshot")
    assert captured["repo_id"] == "Helsinki-NLP/opus-mt-zh-en"
    assert captured["revision"] == ZH_EN_REVISION
    allow = captured["allow_patterns"]
    assert isinstance(allow, list)
    assert "source.spm" in allow
    assert "target.spm" in allow
    assert "pytorch_model.bin" in allow
    assert "vocab.json" in allow


def test_convert_snapshot_uses_transformers_converter_and_copies_spm(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    snapshot = _make_snapshot(tmp_path)
    output = tmp_path / "out"
    seen: dict[str, object] = {}

    class FakeConverter:
        def __init__(
            self,
            model_name_or_path: str,
            copy_files: list[str] | None = None,
            low_cpu_mem_usage: bool = False,
        ) -> None:
            seen["path"] = model_name_or_path
            seen["copy_files"] = copy_files
            seen["low_cpu_mem_usage"] = low_cpu_mem_usage

        def convert(
            self,
            output_dir: str,
            quantization: str | None = None,
            force: bool = False,
        ) -> str:
            seen["output"] = output_dir
            seen["quantization"] = quantization
            seen["force"] = force
            Path(output_dir).mkdir(parents=True)
            (Path(output_dir) / "model.bin").write_bytes(b"m")
            return output_dir

    fake = types.ModuleType("ctranslate2")
    fake.converters = types.SimpleNamespace(TransformersConverter=FakeConverter)
    monkeypatch.setitem(sys.modules, "ctranslate2", fake)

    convert_mod.convert_snapshot(
        snapshot,
        output,
        "int8",
        ["source.spm", "target.spm", "vocab.json"],
    )
    assert seen["path"] == str(snapshot)
    assert seen["copy_files"] == ["source.spm", "target.spm", "vocab.json"]
    assert seen["quantization"] == "int8"
    assert seen["force"] is True
    assert seen["low_cpu_mem_usage"] is True
    assert (output / "source.spm").read_bytes() == b"source-spm"
    assert (output / "target.spm").read_bytes() == b"target-spm"
    assert (output / "vocab.json").read_text(encoding="utf-8") == "{}\n"


def test_missing_convert_extra(monkeypatch: pytest.MonkeyPatch) -> None:
    def explode(name: str) -> object:
        raise ImportError(name)

    monkeypatch.setattr(importlib, "import_module", explode)
    with pytest.raises(convert_mod.ConvertError, match=r"engine\[convert\]"):
        convert_mod.download_snapshot("Helsinki-NLP/opus-mt-zh-en", ZH_EN_REVISION)


def test_conversion_writes_metadata_and_directory_layout(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    snapshot = _make_snapshot(tmp_path)
    calls = _install_fakes(monkeypatch, snapshot)
    manifest = _write_manifest(
        tmp_path,
        _manifest(_entry(architecture="marian-base", weight_bytes=312087009)),
    )
    out_dir = tmp_path / "models"
    code = _run(manifest, out_dir, "--ids", "opus-mt-zh-en")
    captured = capsys.readouterr()
    assert code == 0
    assert calls["download"] == [("Helsinki-NLP/opus-mt-zh-en", ZH_EN_REVISION)]
    model_dir = out_dir / "opus-mt-zh-en"
    assert (model_dir / "model.bin").is_file()
    assert (model_dir / "config.json").is_file()
    assert (model_dir / "shared_vocabulary.txt").is_file()
    assert (model_dir / "source.spm").read_bytes() == b"source-spm"
    assert (model_dir / "target.spm").read_bytes() == b"target-spm"
    assert (model_dir / "vocab.json").is_file()
    metadata = json.loads((model_dir / "suiyi-model.json").read_text(encoding="utf-8"))
    assert metadata["id"] == "opus-mt-zh-en"
    assert metadata["hf_revision"] == ZH_EN_REVISION
    assert metadata["license"] == "CC-BY-4.0"
    assert metadata["src_prefix_token"] is None
    assert metadata["architecture"] == "marian-base"
    assert metadata["weight_bytes"] == 312087009
    assert metadata["quantization"] == "int8"
    assert metadata["ctranslate2_version"] == "4.6.0"
    assert metadata["converted_at"] == "2026-09-25T00:00:00+00:00"
    assert metadata["files"]["model.bin"] == {
        "sha256": hashlib.sha256(b"bin").hexdigest(),
        "bytes": 3,
    }
    assert "suiyi-model.json" not in metadata["files"]
    assert "完成 opus-mt-zh-en" in captured.out
    assert not (out_dir / ".opus-mt-zh-en.partial").exists()


def test_second_run_skips_when_revision_and_quantization_match(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    snapshot = _make_snapshot(tmp_path)
    calls = _install_fakes(monkeypatch, snapshot)
    manifest = _write_manifest(tmp_path)
    out_dir = tmp_path / "models"
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en") == 0
    capsys.readouterr()
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en") == 0
    captured = capsys.readouterr()
    assert "opus-mt-zh-en 已存在，跳过" in captured.out
    assert calls["download"] == [("Helsinki-NLP/opus-mt-zh-en", ZH_EN_REVISION)]


def test_force_rebuilds_existing_model(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    snapshot = _make_snapshot(tmp_path)
    calls = _install_fakes(monkeypatch, snapshot)
    manifest = _write_manifest(tmp_path)
    out_dir = tmp_path / "models"
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en") == 0
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en", "--force") == 0
    captured = capsys.readouterr()
    assert "强制重新转换 opus-mt-zh-en" in captured.out
    assert calls["download"] == [
        ("Helsinki-NLP/opus-mt-zh-en", ZH_EN_REVISION),
        ("Helsinki-NLP/opus-mt-zh-en", ZH_EN_REVISION),
    ]


def test_quantization_mismatch_requires_force(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    snapshot = _make_snapshot(tmp_path)
    calls = _install_fakes(monkeypatch, snapshot)
    manifest = _write_manifest(tmp_path)
    out_dir = tmp_path / "models"
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en") == 0
    code = _run(manifest, out_dir, "--ids", "opus-mt-zh-en", "--quantization", "float32")
    captured = capsys.readouterr()
    assert code == 2
    assert "--force" in captured.err
    assert len(calls["download"]) == 1
    meta_path = out_dir / "opus-mt-zh-en" / "suiyi-model.json"
    metadata = json.loads(meta_path.read_text(encoding="utf-8"))
    assert metadata["quantization"] == "int8"


def test_failed_conversion_removes_partial_and_keeps_previous(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    snapshot = _make_snapshot(tmp_path)
    _install_fakes(monkeypatch, snapshot)
    manifest = _write_manifest(tmp_path)
    out_dir = tmp_path / "models"
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en") == 0
    original = (out_dir / "opus-mt-zh-en" / "suiyi-model.json").read_text(encoding="utf-8")
    _install_fakes(monkeypatch, snapshot, fail=True)
    code = _run(manifest, out_dir, "--ids", "opus-mt-zh-en", "--force")
    captured = capsys.readouterr()
    assert code == 1
    assert "失败" in captured.err
    assert not (out_dir / ".opus-mt-zh-en.partial").exists()
    assert not (out_dir / "opus-mt-zh-en" / "junk.bin").exists()
    assert (out_dir / "opus-mt-zh-en" / "suiyi-model.json").read_text(encoding="utf-8") == original


def test_failed_first_conversion_leaves_no_model_dir(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    snapshot = _make_snapshot(tmp_path)
    _install_fakes(monkeypatch, snapshot, fail=True)
    manifest = _write_manifest(tmp_path)
    out_dir = tmp_path / "models"
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en") == 1
    assert not (out_dir / "opus-mt-zh-en").exists()
    assert not (out_dir / ".opus-mt-zh-en.partial").exists()


def test_tier_selects_only_matching_models(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    snapshot = _make_snapshot(tmp_path)
    calls = _install_fakes(monkeypatch, snapshot)
    optional = _entry(
        "opus-mt-en-zh",
        src="en",
        tgt="zh",
        hf_repo="Helsinki-NLP/opus-mt-en-zh",
        hf_revision=EN_ZH_REVISION,
        tier="optional",
        src_prefix_token=">>cmn_Hans<<",
    )
    manifest = _write_manifest(tmp_path, _manifest(_entry(), optional))
    out_dir = tmp_path / "models"
    assert _run(manifest, out_dir, "--tier", "mvp") == 0
    assert (out_dir / "opus-mt-zh-en").is_dir()
    assert not (out_dir / "opus-mt-en-zh").exists()
    assert calls["download"] == [("Helsinki-NLP/opus-mt-zh-en", ZH_EN_REVISION)]
    assert _run(manifest, out_dir, "--tier", "optional") == 0
    assert calls["download"] == [
        ("Helsinki-NLP/opus-mt-zh-en", ZH_EN_REVISION),
        ("Helsinki-NLP/opus-mt-en-zh", EN_ZH_REVISION),
    ]


def test_unknown_id_and_bad_quantization_do_not_download(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    snapshot = _make_snapshot(tmp_path)
    calls = _install_fakes(monkeypatch, snapshot)
    manifest = _write_manifest(tmp_path)
    out_dir = tmp_path / "models"
    assert _run(manifest, out_dir, "--ids", "missing-model") == 2
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en", "--quantization", "int4") == 2
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en", "--tier", "mvp") == 2
    assert _run(manifest, out_dir) == 2
    captured = capsys.readouterr()
    assert "missing-model" in captured.err
    assert "int4" in captured.err
    assert calls["download"] == []


def test_list_prints_models_without_creating_output(
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    monkeypatch.delenv("SUIYI_MODELS_DIR", raising=False)
    models_dir = ROOT / "models"
    existed = models_dir.exists()
    code = convert_mod.main(["--manifest", str(EXAMPLE), "--list"])
    captured = capsys.readouterr()
    assert code == 0
    assert "opus-mt-zh-en" in captured.out
    assert "opus-mt-en-zh" in captured.out
    assert ZH_EN_REVISION in captured.out
    assert "tier=mvp" in captured.out
    assert models_dir.exists() is existed
    assert convert_mod.main(["--manifest", str(EXAMPLE), "--list", "--ids", "opus-mt-zh-en"]) == 2


def test_utc_now_iso_includes_offset() -> None:
    text = convert_mod.utc_now_iso()
    assert text.endswith("+00:00")
    assert "T" in text


def test_script_help_and_list() -> None:
    script = ROOT / "scripts" / "convert_models.py"
    help_run = subprocess.run(
        [sys.executable, str(script), "--help"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert help_run.returncode == 0
    assert "--manifest" in help_run.stdout
    assert "--tier" in help_run.stdout
    listed = subprocess.run(
        [sys.executable, str(script), "--manifest", str(EXAMPLE), "--list"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert listed.returncode == 0
    assert "opus-mt-zh-en" in listed.stdout
    assert EN_ZH_REVISION in listed.stdout


def test_model_paths_are_gitignored() -> None:
    paths = [
        "models/opus-mt-zh-en/model.bin",
        "models/opus-mt-zh-en/config.json",
        "models/opus-mt-zh-en/source.spm",
        "models/opus-mt-zh-en/target.spm",
        "models/opus-mt-zh-en/suiyi-model.json",
        "models/opus-mt-zh-en/shared_vocabulary.txt",
    ]
    for relative in paths:
        completed = subprocess.run(
            ["git", "check-ignore", "-q", relative],
            check=False,
            cwd=ROOT,
        )
        assert completed.returncode == 0, relative


def test_missing_sentencepiece_cleans_partial(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    snapshot = tmp_path / "snapshot"
    snapshot.mkdir()
    _install_fakes(monkeypatch, snapshot)
    manifest = _write_manifest(tmp_path)
    out_dir = tmp_path / "models"
    assert _run(manifest, out_dir, "--ids", "opus-mt-zh-en") == 1
    assert not (out_dir / "opus-mt-zh-en").exists()
    assert not (out_dir / ".opus-mt-zh-en.partial").exists()


def test_runtime_dependencies_do_not_include_convert_stack() -> None:
    text = (ROOT / "engine" / "pyproject.toml").read_text(encoding="utf-8")
    runtime, _, optional = text.partition("[project.optional-dependencies]")
    assert "dependencies = []" in runtime
    runtime_code = "\n".join(
        line for line in runtime.splitlines() if line.strip() and not line.strip().startswith("#")
    )
    for name in ("torch", "transformers", "ctranslate2", "sentencepiece", "huggingface_hub"):
        assert name not in runtime_code
        assert name in optional


@pytest.mark.model
def test_converted_zh_en_translates_sample() -> None:
    """真实模型冒烟。模型不在 SUIYI_MODELS_DIR 时跳过，CI 不会下载。"""
    model_dir = Path(os.environ["SUIYI_MODELS_DIR"]) / "opus-mt-zh-en"
    meta_path = model_dir / "suiyi-model.json"
    if not meta_path.is_file():
        pytest.skip("尚未转换 opus-mt-zh-en")
    metadata = json.loads(meta_path.read_text(encoding="utf-8"))
    assert metadata["hf_revision"] == ZH_EN_REVISION
    assert metadata["quantization"] == "int8"
    for name in ("model.bin", "config.json", "source.spm", "target.spm"):
        assert (model_dir / name).is_file()

    import ctranslate2
    import sentencepiece as spm

    translator = ctranslate2.Translator(str(model_dir), device="cpu")
    source_spm = spm.SentencePieceProcessor(model_file=str(model_dir / "source.spm"))
    target_spm = spm.SentencePieceProcessor(model_file=str(model_dir / "target.spm"))
    pieces = source_spm.encode("今天天气很好。", out_type=str)
    if not pieces or pieces[-1] != "</s>":
        pieces = [*pieces, "</s>"]
    result = translator.translate_batch([pieces])
    hypothesis = list(result[0].hypotheses[0])
    if hypothesis and hypothesis[-1] == "</s>":
        hypothesis = hypothesis[:-1]
    text = target_spm.decode(hypothesis).strip()
    lowered = text.lower()
    assert any(word in lowered for word in ("weather", "today", "nice", "good", "day"))
