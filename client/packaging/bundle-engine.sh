#!/usr/bin/env bash
# 将引擎 PyInstaller onedir 内容拼进便携根的 engine\（#132）。
# 用法（仓库根或任意目录）：
#   ./client/packaging/bundle-engine.sh <便携根> <引擎 onedir 根>
# 引擎 onedir 根须直接含 suiyi-engine.exe 与 _internal/。
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "用法: $0 <便携根> <引擎 onedir 根>" >&2
  exit 2
fi

PORTABLE="$(cd "$1" && pwd)"
ENGINE_SRC="$(cd "$2" && pwd)"
ENGINE_DST="$PORTABLE/engine"

if [[ ! -f "$ENGINE_SRC/suiyi-engine.exe" ]]; then
  echo "未找到引擎入口: $ENGINE_SRC/suiyi-engine.exe" >&2
  exit 1
fi
if [[ ! -d "$ENGINE_SRC/_internal" ]]; then
  echo "未找到 onedir 依赖目录: $ENGINE_SRC/_internal" >&2
  exit 1
fi

mkdir -p "$ENGINE_DST"
# 保留占位 README；用 onedir 内容覆盖同名项
if [[ -f "$ENGINE_DST/README.txt" ]]; then
  :
elif [[ -f "$(dirname "$0")/engine/README.txt" ]]; then
  cp "$(dirname "$0")/engine/README.txt" "$ENGINE_DST/README.txt"
fi

# 拷贝 onedir 全部内容（exe + _internal + 其它）
cp -a "$ENGINE_SRC/." "$ENGINE_DST/"

test -f "$ENGINE_DST/suiyi-engine.exe"
test -d "$ENGINE_DST/_internal"

echo "已拼入引擎: $ENGINE_DST/suiyi-engine.exe"
echo "  _internal 条目数: $(find "$ENGINE_DST/_internal" | wc -l)"
