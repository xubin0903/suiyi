# 引擎 Windows 打包（#130）

产出 PyInstaller **onedir** 的 `suiyi-engine.exe`，供 [M4 便携包](../../docs/client/M4-便携包.md) 使用。

| 文件 | 作用 |
|------|------|
| `entry.py` | 冻结入口，等价 `python -m suiyi_engine` |
| `suiyi-engine.spec` | PyInstaller 规格（排除 torch/transformers，打入 ctranslate2） |
| `build_windows.ps1` | Windows 一键构建 + 冒烟 |
| `build_windows.sh` | Linux 校验打包材料（不产出 Windows exe） |

步骤与目录约定见 [docs/engine/Windows打包.md](../../docs/engine/Windows打包.md)。
