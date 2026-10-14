将打包后的翻译引擎可执行文件放在本目录，文件名：

  suiyi-engine.exe

约定见 docs/client/M4-便携包.md（PyInstaller onedir/onefile，CLI 与
python -m suiyi_engine 相同：suiyi-engine.exe serve --port …）。

未放置时，客户端在开发树里会回退到 Python（.venv / py -3.11）；
在纯便携包里则会启动失败并提示检查本文件。
