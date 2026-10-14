本目录应含引擎 PyInstaller onedir 内容：

  suiyi-engine.exe
  _internal\          （依赖目录）

CI 的「拼便携包」job（#132）会自动把同次构建的引擎拷入本目录。
本机构建与拷贝步骤见 docs/engine/Windows打包.md。
CLI 与 python -m suiyi_engine 相同：suiyi-engine.exe serve --port …

未放置时，客户端在开发树里会回退到 Python（.venv / py -3.11）；
在纯便携包里则会启动失败并提示检查本文件。
