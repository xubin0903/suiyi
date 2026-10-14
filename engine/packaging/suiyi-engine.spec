# -*- mode: python ; coding: utf-8 -*-
# PyInstaller onedir spec for Windows suiyi-engine.exe (#130).
# Build from repo root (or engine/packaging) via build_windows.ps1.
# Do NOT collect torch / transformers / huggingface_hub.

from __future__ import annotations

import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_all, collect_dynamic_libs

SPECDIR = Path(SPEC).resolve().parent
REPO = SPECDIR.parents[1]
ENGINE = REPO / "engine"
ENTRY = SPECDIR / "entry.py"

datas: list[tuple[str, str]] = [
    (str(ENGINE / "model_manifest.json"), "."),
    (str(ENGINE / "ocr_model_manifest.json"), "."),
]
binaries: list[tuple[str, str]] = []
hiddenimports: list[str] = [
    "suiyi_engine",
    "suiyi_engine.__main__",
    "suiyi_engine.serve",
    "suiyi_engine.api",
    "suiyi_engine.api_ocr",
    "suiyi_engine.ocr_worker",
    "suiyi_engine.backends.ct2_opus",
    "ctranslate2",
    "sentencepiece",
    "uvicorn.logging",
    "uvicorn.loops",
    "uvicorn.loops.auto",
    "uvicorn.protocols",
    "uvicorn.protocols.http",
    "uvicorn.protocols.http.auto",
    "uvicorn.protocols.websockets",
    "uvicorn.protocols.websockets.auto",
    "uvicorn.lifespan",
    "uvicorn.lifespan.on",
    "fastapi",
    "py3langid",
]

# Package data under suiyi_engine/data (short phrases, glossary, opencc table).
pkg_datas, pkg_binaries, pkg_hidden = collect_all("suiyi_engine")
datas += pkg_datas
binaries += pkg_binaries
hiddenimports += pkg_hidden

# CTranslate2 native libraries.
binaries += collect_dynamic_libs("ctranslate2")

# py3langid ships model.npz.xz under data/ — must collect datas.
for mod in ("py3langid",):
    d, b, h = collect_all(mod)
    datas += d
    binaries += b
    hiddenimports += h

# Optional OCR runtime (no model weights). Fail soft if not installed.
for mod in ("rapidocr", "onnxruntime", "cv2"):
    try:
        d, b, h = collect_all(mod)
        datas += d
        binaries += b
        hiddenimports += h
    except Exception:
        pass

block_cipher = None

a = Analysis(
    [str(ENTRY)],
    pathex=[str(ENGINE / "src")],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "torch",
        "torchvision",
        "torchaudio",
        "transformers",
        "huggingface_hub",
        "tensorflow",
        "paddle",
        "paddlepaddle",
        "tkinter",
        "matplotlib",
        "IPython",
        "notebook",
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

# Double-check excludes did not sneak in via collect_all.
_BLOCK = ("torch", "transformers", "huggingface_hub", "paddle", "tensorflow")

def _blocked(name: str) -> bool:
    lower = name.replace("\\", "/").lower()
    return any(part in lower for part in _BLOCK)

a.datas = [item for item in a.datas if not _blocked(str(item[0]))]
a.binaries = [item for item in a.binaries if not _blocked(str(item[0]))]
a.pure = [item for item in a.pure if not _blocked(str(item[0]))]

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="suiyi-engine",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="suiyi-engine",
)
