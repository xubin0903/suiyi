将打包后的翻译引擎 onedir 内容放在本目录，使下列文件存在：

  suiyi-engine.exe
  _internal\          （PyInstaller onedir 依赖目录）

构建与拷贝步骤见 docs/engine/Windows打包.md（#130）。
CLI 与 python -m suiyi_engine 相同：suiyi-engine.exe serve --port …

未放置时，客户端在开发树里会回退到 Python（.venv / py -3.11）；
在纯便携包里则会启动失败并提示检查本文件。
