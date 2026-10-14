#!/usr/bin/env bash
# Linux/macOS 辅助：校验打包材料；真正的 Windows exe 请在 Windows 上跑 build_windows.ps1（#130）。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

echo "仓库根: $ROOT"
test -f engine/packaging/entry.py
test -f engine/packaging/suiyi-engine.spec
test -f engine/packaging/build_windows.ps1
test -f engine/model_manifest.json
test -f engine/ocr_model_manifest.json
test -f docs/engine/Windows打包.md

python - <<'PY'
from pathlib import Path
spec = Path("engine/packaging/suiyi-engine.spec").read_text(encoding="utf-8")
for needle in ("torch", "transformers", "suiyi-engine", "collect_all(\"suiyi_engine\")", "model_manifest.json"):
    assert needle in spec, needle
print("spec 关键字检查通过")
from suiyi_engine.runtime_layout import default_models_dir, is_frozen, repo_root
assert is_frozen() is False
assert (repo_root() / "engine" / "pyproject.toml").is_file()
print("runtime_layout 源码布局 OK，default models ->", default_models_dir())
PY

echo "Linux 侧材料检查完成。请在 Windows 上执行:"
echo "  .\\engine\\packaging\\build_windows.ps1"
