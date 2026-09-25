# suyi-engine

随译的本地翻译引擎 Python 包（导入名 `suiyi_engine`）。当前是可安装、可 lint、可测试的工程骨架；翻译、HTTP 服务与模型加载在后续 Issue 中加入。一期在 Windows 上以本机 HTTP 服务运行，后续用 PyInstaller 打包为 exe。

- 开发基准：**Python 3.11**
- 安装要求：`requires-python >= 3.10`
- 构建后端：**hatchling**（`src/` 布局，版本读自 `suiyi_engine.__version__`）
- 运行时依赖：本骨架留空。`ctranslate2`、`sentencepiece`、`fastapi` 等由后续 Issue 按需添加

标准环境流程是 `python -m venv` + `pip`。下面的命令都在**仓库根目录**执行。

## 环境准备

### bash

```bash
python -m venv .venv
source .venv/bin/activate
python -m pip install -U pip
pip install -e "engine[dev]"
```

### Windows PowerShell

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -U pip
pip install -e "engine[dev]"
```

PowerShell 里 `engine[dev]` 必须加引号，否则方括号会被当成通配符。

## Lint、format、test

在已激活的虚拟环境中执行。

### bash

```bash
ruff check engine
ruff format --check engine
pytest engine
python -m suyi_engine --version
```

### Windows PowerShell

```powershell
ruff check engine
ruff format --check engine
pytest engine
python -m suyi_engine --version
```

`python -m suyi_engine --version` 应输出 `0.0.1`。

## 需要真实模型的测试

带 `@pytest.mark.model` 的测试，在未设置环境变量 `SUIYI_MODELS_DIR`，或该路径不是已存在目录时自动跳过。CI 因此不会下载模型。

### bash

```bash
export SUIYI_MODELS_DIR=/path/to/models
pytest engine
```

### Windows PowerShell

```powershell
$env:SUIYI_MODELS_DIR = "C:\path\to\models"
pytest engine
```

## 可选：uv

可以用 [uv](https://docs.astral.sh/uv/) 建环境，贡献时的标准流程仍是上面的 venv + pip。

### bash

```bash
uv venv
source .venv/bin/activate
uv pip install -e "engine[dev]"
```

### Windows PowerShell

```powershell
uv venv
.\.venv\Scripts\Activate.ps1
uv pip install -e "engine[dev]"
```

## 目录

```
engine/
  pyproject.toml          构建、ruff、pytest 配置
  src/suiyi_engine/       包源码（src 布局，避免测试误导入工作目录）
  tests/                  pytest 测试
```
