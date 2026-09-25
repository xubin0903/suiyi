# engine/

Python 翻译服务（包名 `suiyi-engine`，导入名 `suiyi_engine`）。负责本地 NMT（CTranslate2 / OPUS-MT）、语种路由与后续 HTTP API。一期与 Windows 客户端配合；本目录应能独立安装、lint 和测试。

翻译核心在 `suiyi_engine.translator`：可替换后端、CTranslate2 / OPUS-MT、按语种对路由（直连或英文中转）、分句后批量翻译再拼回。本机 HTTP 服务用 `python -m suiyi_engine serve` 启动，只监听回环地址。后续用 PyInstaller 打包为 Windows 本机 exe。设计说明见 [翻译核心](../docs/engine/翻译核心.md) 与 [HTTP API](../docs/engine/HTTP-API.md)。

## 布局

与 [CONTRIBUTING.md](../CONTRIBUTING.md) 的目录约定一致：

```
engine/
  pyproject.toml          工程定义（构建、ruff、pytest）
  src/suiyi_engine/       包源码，导入名 suiyi_engine（src 布局，避免测试误导入工作目录）
  tests/                  引擎单元测试（pytest）
```

引擎单测放在本目录 `tests/`，不要放到仓库根目录 `tests/`。根目录 `tests/` 只放跨组件样例和端到端测试，见 [tests/README.md](../tests/README.md)。

模型权重不放这里，也不进 git。本地缓存使用仓库根目录 `models/`。新增模型须先在 [docs/engine/](../docs/engine/README.md) 登记许可证，且不得默认打包 CC BY-NC 等非商用权重。

语种检测在 `suiyi_engine.langdetect`。方案、许可证和实测性能见 [语种检测](../docs/engine/语种检测.md)。`py3langid` 自带的识别模型随该包分发，不进本仓库。

## 本地开发

基准版本：**Python 3.11**。安装要求 `requires-python >= 3.10`。

构建后端：**hatchling**（`src/` 布局，版本读自 `suiyi_engine.__version__`）。运行时依赖是语种检测用的 `py3langid`（BSD-3-Clause，会安装 `numpy`），翻译推理用的 `ctranslate2` 与 `sentencepiece`，以及本机 HTTP 用的 `fastapi` 与 `uvicorn`（不加 standard extra）。不引入 `torch` / `transformers`。模型转换依赖在可选组 `convert`（另含 `huggingface_hub`、`transformers`、CPU 版 `torch`）。

标准环境流程是 `python -m venv` + `pip`。下面的命令都在**仓库根目录**执行。

### 环境准备

#### bash

```bash
python -m venv .venv
source .venv/bin/activate
python -m pip install -U pip
pip install -e "engine[dev]"
```

#### Windows PowerShell

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -U pip
pip install -e "engine[dev]"
```

PowerShell 里 `engine[dev]` 必须加引号，否则方括号会被当成通配符。

### Lint、format、test

在已激活的虚拟环境中执行。

#### bash

```bash
ruff check engine
ruff format --check engine
pytest engine
python -m suiyi_engine --version
```

#### Windows PowerShell

```powershell
ruff check engine
ruff format --check engine
pytest engine
python -m suiyi_engine --version
```

`python -m suiyi_engine --version` 应输出 `0.0.1`。

翻译一段文本（需要本机已经按 [模型目录约定](../docs/engine/模型目录约定.md) 放好 int8 模型）：

```bash
python -m suiyi_engine translate --src zh --tgt en "今天天气很好。我们去公园吧。"
```

标准输出是译文、`route:`（模型 id，中转时两个）和 `elapsed_ms:`。未下载该语向时退出码为 1，stderr 里带缺失的模型 id。

### 本机 HTTP 服务

只监听回环地址。接口、错误码和示例见 [HTTP API](../docs/engine/HTTP-API.md)。

#### bash

```bash
python -m suiyi_engine serve
curl -sS http://127.0.0.1:18780/health
```

#### Windows PowerShell

```powershell
python -m suiyi_engine serve
Invoke-RestMethod http://127.0.0.1:18780/health
```

`--host` 只接受 `127.0.0.1`、`::1`、`localhost`。端口可用 `--port` 或环境变量 `SUIYI_PORT`（默认 `18780`）。`--dev` 才打开 `/docs`。

### 需要真实模型的测试

带 `@pytest.mark.model` 的测试，在未设置环境变量 `SUIYI_MODELS_DIR`，或该路径不是已存在目录时自动跳过。CI 因此不会下载模型。本地模型缓存是仓库根目录的 `models/`。

#### bash

```bash
export SUIYI_MODELS_DIR=/path/to/models
pytest engine
```

#### Windows PowerShell

```powershell
$env:SUIYI_MODELS_DIR = "C:\path\to\models"
pytest engine
```

### 可选：uv

可以用 [uv](https://docs.astral.sh/uv/) 建环境，贡献时的标准流程仍是上面的 venv + pip。

#### bash

```bash
uv venv
source .venv/bin/activate
uv pip install -e "engine[dev]"
```

#### Windows PowerShell

```powershell
uv venv
.\.venv\Scripts\Activate.ps1
uv pip install -e "engine[dev]"
```

## 模型转换

把 OPUS-MT 转成 CTranslate2 的命令、输出目录和 `suiyi-model.json` 见 [模型目录约定](../docs/engine/模型目录约定.md)。转换依赖不在上面的 `engine[dev]` 里：

```bash
pip install -e "engine[convert]"
python scripts/convert_models.py --manifest engine/model_manifest.example.json --list
```

正式清单 `engine/model_manifest.json` 由模型选型提供。该文件合并前，用 `--manifest` 指向示例清单。权重写入仓库根 `models/`，不要提交。