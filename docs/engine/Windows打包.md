# Windows 引擎打包（PyInstaller）

面向 M4 便携包：产出 `suiyi-engine.exe`，由客户端放在解压根的 `engine\` 下启动。关联 #130 / #131、#127、#132。

## 产物形态

**PyInstaller onedir**（不是 onefile）：

```
suiyi-engine/                 # build 输出的 onedir 根
  suiyi-engine.exe            # 入口
  _internal/                  # Python 运行时、ctranslate2 原生库、依赖
    model_manifest.json       # 打进包（路由清单，不是权重）
    ocr_model_manifest.json
    …
```

**不**打入：

- 翻译 / OCR **模型权重**（`models/`）
- `torch` / `transformers` / `huggingface_hub` / paddle / tensorflow

**打入**：运行时依赖（含 `ctranslate2` 原生库）；可选 OCR 运行时（`onnxruntime` + `rapidocr`，无 ONNX 权重）。

## 本机构建（Windows）

在仓库根、已安装 Python 3.11 时：

```powershell
.\engine\packaging\build_windows.ps1
# 或指定输出目录
.\engine\packaging\build_windows.ps1 -OutputDir D:\out\suiyi-engine-win-x64
```

脚本会：安装 `engine[dev]` + PyInstaller + OCR 运行时（opencv-headless）→ 执行 spec → 对产物跑 `--version` / `serve --help` → 检查未打入 torch 等目录。

默认输出：`artifacts/suiyi-engine-win-x64/dist/suiyi-engine/`。

Linux 上可先跑材料检查（不产出 Windows exe）：

```bash
./engine/packaging/build_windows.sh
```

## 放到便携包

便携根布局见 [M4 便携包](../client/M4-便携包.md)。把 onedir **目录内的全部内容**拷进 `engine\`：

```powershell
# 假设便携根是 D:\Suiyi，onedir 在 artifacts\...\dist\suiyi-engine
Copy-Item -Recurse -Force .\artifacts\suiyi-engine-win-x64\dist\suiyi-engine\* D:\Suiyi\engine\
```

结果应为：

```
D:\Suiyi\
  Suiyi.exe
  engine\
    suiyi-engine.exe
    _internal\
  models\          # 空或按需转换/下载的权重
```

客户端未配置 `engine.command` 时，若存在 `engine\suiyi-engine.exe` 会自动发现。

## 运行与模型目录

CLI 与 `python -m suiyi_engine` 相同：

```powershell
.\engine\suiyi-engine.exe --version
.\engine\suiyi-engine.exe serve --port 18780
.\engine\suiyi-engine.exe serve --port 18780 --models-dir .\models --preload zh-en,en-zh
```

- 客户端拉起时会带上 `--models-dir`（相对便携根解析后的绝对路径）和工作目录（解压根）。
- 未传 `--models-dir` 且未设 `SUIYI_MODELS_DIR` 时，冻结布局默认使用 **便携根下的 `models\`**（exe 在 `engine\` 时即上一级的 `models\`）。
- 模型为空时服务仍可启动；`/health` 可用；预加载缺失语向会按现有逻辑告警。

冒烟：

```powershell
# 终端 1（在便携根）
.\engine\suiyi-engine.exe serve --port 18780
# 终端 2
Invoke-RestMethod http://127.0.0.1:18780/health
```

## CI

`.github/workflows/ci.yml` 在 **Windows** 的 engine job 中于测试后执行打包，并上传 artifact：

- 名称：`suiyi-engine-win-x64`
- 内容：onedir 目录（含 `suiyi-engine.exe` 与 `_internal`），**无模型权重**

同次 workflow 的 **拼便携包** job（#132）会把该 onedir 自动拷进最终制品 `suiyi-portable-win-x64` 的 `engine\`，一般无需再手动拼。若只要引擎、单独调试，仍可只下 `suiyi-engine-win-x64`。

## 相关文件

| 路径 | 说明 |
|------|------|
| `engine/packaging/entry.py` | 冻结入口 |
| `engine/packaging/suiyi-engine.spec` | PyInstaller 规格 |
| `engine/packaging/build_windows.ps1` | Windows 构建 |
| `engine/src/suiyi_engine/runtime_layout.py` | 冻结 / 源码路径约定 |
