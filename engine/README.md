# engine/

Python 翻译服务。负责本地 NMT（CTranslate2 / OPUS-MT）、语种路由与后续 HTTP API。一期与 Windows 客户端配合；本目录应能独立安装、lint 和测试。

## 布局

```
engine/
  pyproject.toml          工程定义（由 Python 工程初始化 Issue 添加）
  src/suiyi_engine/       包源码，导入名 suiyi_engine
  tests/                  引擎单元测试（pytest）
```

引擎单测放在本目录 `tests/`，不要放到仓库根目录 `tests/`。根目录 `tests/` 只放跨组件样例和端到端测试，见 [tests/README.md](../tests/README.md)。

模型权重不放这里，也不进 git。本地缓存使用仓库根目录 `models/`。新增模型须先在 [docs/engine/](../docs/engine/README.md) 登记许可证，且不得默认打包 CC BY-NC 等非商用权重。

## 本地开发

基准版本：**Python 3.11**。

虚拟环境、`pip install`、`ruff` 与 `pytest` 的具体命令由 Python 工程初始化补全到本节（Windows PowerShell 与 bash 都要写）。在那之前，不要在本目录提交业务代码。
