# client/packaging

Windows 便携包脚本与占位文件。说明见 [docs/client/M4-便携包.md](../../docs/client/M4-便携包.md)。

| 文件 | 作用 |
|------|------|
| `publish-portable.ps1` / `.sh` | `dotnet publish` 客户端并补齐 `engine/`、`models/` 占位 |
| `bundle-engine.ps1` / `.sh` | 把引擎 PyInstaller onedir 内容拼进便携根的 `engine\`（#132） |
| `settings.portable.example.json` | 相对路径指向引擎 exe 的示例设置 |
| `engine/README.txt` | 引擎目录说明（CI 拼包后仍保留） |
| `models/README.txt` | 模型按需下载说明 |
