"""冻结 / 源码布局路径（#130）。"""

from __future__ import annotations

from pathlib import Path

import pytest

from suiyi_engine import runtime_layout as layout


def test_source_layout_repo_and_manifests(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("SUIYI_MODELS_DIR", raising=False)
    assert layout.is_frozen() is False
    root = layout.repo_root()
    assert (root / "engine" / "pyproject.toml").is_file()
    assert layout.default_models_dir() == root / "models"
    assert layout.default_model_manifest_path().name == "model_manifest.json"
    assert layout.default_model_manifest_path().is_file()
    assert layout.default_ocr_manifest_path().name == "ocr_model_manifest.json"
    assert layout.default_ocr_manifest_path().is_file()


def test_frozen_models_dir_is_sibling_of_engine(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    portable = tmp_path / "Suiyi"
    engine_dir = portable / "engine"
    engine_dir.mkdir(parents=True)
    exe = engine_dir / "suiyi-engine.exe"
    exe.write_bytes(b"fake")
    meipass = engine_dir / "_internal"
    meipass.mkdir()
    (meipass / "model_manifest.json").write_text("{}", encoding="utf-8")
    (meipass / "ocr_model_manifest.json").write_text("{}", encoding="utf-8")

    monkeypatch.setattr(layout.sys, "frozen", True, raising=False)
    monkeypatch.setattr(layout.sys, "executable", str(exe), raising=False)
    monkeypatch.setattr(layout.sys, "_MEIPASS", str(meipass), raising=False)
    monkeypatch.delenv("SUIYI_MODELS_DIR", raising=False)

    assert layout.is_frozen() is True
    assert layout.repo_root() == portable.resolve()
    assert layout.default_models_dir() == portable.resolve() / "models"
    assert layout.bundle_dir() == engine_dir.resolve()
    assert layout.default_model_manifest_path() == meipass / "model_manifest.json"
    assert layout.default_ocr_manifest_path() == meipass / "ocr_model_manifest.json"


def test_packaging_materials_exist() -> None:
    root = layout.repo_root()
    packaging = root / "engine" / "packaging"
    assert (packaging / "entry.py").is_file()
    assert (packaging / "suiyi-engine.spec").is_file()
    assert (packaging / "build_windows.ps1").is_file()
    assert (packaging / "build_windows.sh").is_file()
    spec = (packaging / "suiyi-engine.spec").read_text(encoding="utf-8")
    assert "suiyi-engine" in spec
    assert "torch" in spec and "excludes" in spec
    assert "transformers" in spec
    assert "model_manifest.json" in spec
    docs = root / "docs" / "engine" / "Windows打包.md"
    assert docs.is_file()
    body = docs.read_text(encoding="utf-8")
    assert "便携根" in body and "suiyi-engine.exe" in body


def test_langid_export_command_uses_subcommand_when_frozen(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    from suiyi_engine.langid_slim import _export_command

    exe = tmp_path / "suiyi-engine.exe"
    monkeypatch.setattr("suiyi_engine.langid_slim.sys.frozen", True, raising=False)
    monkeypatch.setattr("suiyi_engine.langid_slim.sys.executable", str(exe), raising=False)
    cmd = _export_command(tmp_path / "cache.bin", ["en", "de"])
    assert cmd[:2] == [str(exe), "langid-export"]
    monkeypatch.setattr("suiyi_engine.langid_slim.sys.frozen", False, raising=False)
    cmd2 = _export_command(tmp_path / "cache.bin", ["en"])
    assert cmd2[1:3] == ["-m", "suiyi_engine.langid_slim"]
