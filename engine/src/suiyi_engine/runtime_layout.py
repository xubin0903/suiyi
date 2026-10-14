"""源码布局与 PyInstaller 冻结布局下的路径约定（#130）。

便携包约定（见 ``docs/client/M4-便携包.md``）：

```
<便携根>/
  Suiyi.exe
  engine/suiyi-engine.exe   # 本进程
  engine/_internal/…        # onedir 依赖
  models/                   # 不打进 exe，运行时读取
```

冻结后默认模型目录是 ``<便携根>/models``（exe 所在目录的上一级）。
客户端通常还会显式传入 ``--models-dir``。
"""

from __future__ import annotations

import sys
from pathlib import Path

__all__ = [
    "bundle_dir",
    "default_models_dir",
    "default_model_manifest_path",
    "default_ocr_manifest_path",
    "is_frozen",
    "meipass_dir",
    "repo_root",
]


def is_frozen() -> bool:
    """是否运行在 PyInstaller（或同类）冻结可执行文件里。"""

    return bool(getattr(sys, "frozen", False))


def meipass_dir() -> Path | None:
    """PyInstaller 解包目录（onedir 下通常是 ``_internal``）；非冻结时为 ``None``。"""

    raw = getattr(sys, "_MEIPASS", None)
    return Path(raw) if raw else None


def bundle_dir() -> Path:
    """冻结时为 ``suiyi-engine.exe`` 所在目录；源码时为 ``engine/``。"""

    if is_frozen():
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[2]


def repo_root() -> Path:
    """源码布局下的仓库根；冻结时退化为便携根（exe 的上一级）。"""

    if is_frozen():
        return Path(sys.executable).resolve().parent.parent
    return Path(__file__).resolve().parents[3]


def default_models_dir() -> Path:
    """``SUIYI_MODELS_DIR``，否则冻结时 ``<便携根>/models``，源码时仓库 ``models/``。"""

    import os

    raw = os.environ.get("SUIYI_MODELS_DIR", "").strip()
    if raw:
        return Path(raw)
    return repo_root() / "models"


def _manifest_candidates(name: str) -> list[Path]:
    paths: list[Path] = []
    meipass = meipass_dir()
    if meipass is not None:
        paths.append(meipass / name)
    paths.append(bundle_dir() / name)
    if not is_frozen():
        paths.append(Path(__file__).resolve().parents[2] / name)
    return paths


def default_model_manifest_path() -> Path:
    """翻译路由清单 ``model_manifest.json``。"""

    for path in _manifest_candidates("model_manifest.json"):
        if path.is_file():
            return path
    raise FileNotFoundError(
        "找不到模型清单 model_manifest.json（源码应在 engine/；"
        "打包产物应在 onedir 的 _internal/ 或 exe 同目录）"
    )


def default_ocr_manifest_path() -> Path:
    """OCR 模型清单 ``ocr_model_manifest.json``。"""

    for path in _manifest_candidates("ocr_model_manifest.json"):
        if path.is_file():
            return path
    if not is_frozen():
        # 与历史行为一致：从仓库根推 engine/ocr_model_manifest.json
        for parent in Path(__file__).resolve().parents:
            candidate = parent / "engine" / "ocr_model_manifest.json"
            if candidate.is_file():
                return candidate
            if (parent / "engine" / "pyproject.toml").is_file():
                break
    raise FileNotFoundError(
        "找不到 OCR 模型清单 ocr_model_manifest.json（源码应在 engine/；"
        "打包产物应在 onedir 的 _internal/ 或 exe 同目录）"
    )
