#!/usr/bin/env bash
# 在仓库根执行：发布 Windows 便携目录骨架（可不含引擎 exe / 模型）。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${1:-$ROOT/artifacts/suiyi-portable}"
SELF_CONTAINED=false
if [[ "${2:-}" == "--self-contained" ]]; then
  SELF_CONTAINED=true
fi

mkdir -p "$OUT"
PUBLISH_ARGS=(publish "$ROOT/client/src/Suiyi.App/Suiyi.App.csproj" -c Release -r win-x64 -o "$OUT")
if [[ "$SELF_CONTAINED" == "true" ]]; then
  PUBLISH_ARGS+=(--self-contained true)
else
  PUBLISH_ARGS+=(--self-contained false)
fi
dotnet "${PUBLISH_ARGS[@]}"

mkdir -p "$OUT/engine" "$OUT/models"
cp "$ROOT/client/packaging/engine/README.txt" "$OUT/engine/README.txt"
cp "$ROOT/client/packaging/models/README.txt" "$OUT/models/README.txt"
cp "$ROOT/client/packaging/settings.portable.example.json" "$OUT/settings.portable.example.json"

echo "便携骨架已输出到: $OUT"
echo "下一步：将 suiyi-engine.exe 放入 $OUT/engine/（见 docs/client/M4-便携包.md）"
