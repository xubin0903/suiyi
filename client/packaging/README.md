# client/packaging

Windows 便携包骨架脚本与占位文件。说明见 [docs/client/M4-便携包.md](../../docs/client/M4-便携包.md)。

| 文件 | 作用 |
|------|------|
| `publish-portable.ps1` / `.sh` | `dotnet publish` 客户端并补齐 `engine/`、`models/` 占位 |
| `settings.portable.example.json` | 相对路径指向引擎 exe 的示例设置 |
| `engine/README.txt` | 无 `suiyi-engine.exe` 时的说明 |
| `models/README.txt` | 模型按需下载说明 |
