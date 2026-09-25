# 贡献指南

随译（Suiyi）是开源桌面翻译工具，许可证为 [MIT](LICENSE)。一期只做 **Windows**。翻译服务用 Python + CTranslate2（`engine/`），客户端用 C# .NET 8 WPF（`client/`，M2 起）。规格以 [MVP 范围冻结 v0.1](docs/research/MVP范围冻结-v0.1.md) 与 [引擎验收标准 v0.1](docs/research/引擎验收标准-v0.1.md) 为准。

本文约定目录布局、分支、提交与 PR 流程。后续 Issue 按此落文件，不要另起一套结构。

## 目录结构约定

```
engine/                 Python 翻译服务（pyproject.toml 放这里）
  src/suiyi_engine/     包源码（导入名 suiyi_engine）
  tests/                引擎单元测试（pytest）
client/                 C# .NET 8 WPF 客户端（M2 起）
docs/
  research/             调研与规格（已有）
  engine/               引擎设计、HTTP API、模型清单等文档
scripts/                开发/运维脚本（模型转换、评测、基准）
tests/
  samples/              跨组件共享的固定样例集（如中文样例集）
  e2e/                  端到端测试（后续）
models/                 本地模型缓存（仅本地，git 忽略）
```

各顶层目录的用途说明见对应 `README.md`。文档总索引见 [docs/README.md](docs/README.md)。

**测试放哪：** 引擎单元测试放 `engine/tests/`，这样 `engine/` 可以当作独立 Python 工程安装、测试和打包。仓库根目录 `tests/` 只放跨组件样例（`tests/samples/`）和端到端测试（`tests/e2e/`），不要把引擎单测再复制一份到根目录。

**本约定与建议树的差异：** 无。嵌套目录（`engine/src/suiyi_engine/`、`engine/tests/`、`tests/samples/`、`tests/e2e/`）的具体文件由后续 Issue 创建；本仓库骨架只在 `engine/`、`client/`、`scripts/`、`tests/`、`docs/engine/` 放说明用途的 `README.md`，不放 `.gitkeep`。

## 开发流程

1. 用 Issue 描述需求或缺陷，写清范围和可勾选的验收标准。一个 Issue 只做一件事。
2. 从最新 `main` 拉功能分支。分支名：
   - `feat/<issue号>-<简述>`：新功能
   - `fix/<issue号>-<简述>`：缺陷修复
   - `docs/<issue号>-<简述>`：只改文档
   - `chore/<issue号>-<简述>`：工程、脚手架、杂项
   - 简述用小写英文短横线，例如 `feat/9-translate-service`。
3. 在该分支上提交，并开 Pull Request。正文写 `Closes #<issue号>`（或 `Fixes #`），并填写仓库里的 [PR 模板](.github/pull_request_template.md)。
4. 负责人审查范围是否越界、验收是否可核对。
5. 审查通过后由用户合并。**`main` 只经 PR 修改**（仓库初始化阶段的直接提交除外）。

云端开发代理同样只经 PR 改代码，不直接推 `main`。

## 提交信息规范

使用 [Conventional Commits](https://www.conventionalcommits.org/) 前缀，描述用中文，一行说清本次变更：

| 前缀 | 用途 |
|------|------|
| `feat:` | 用户可见的功能 |
| `fix:` | 缺陷修复 |
| `docs:` | 只改文档 |
| `test:` | 只改测试或样例 |
| `ci:` | CI 配置 |
| `refactor:` | 行为不变的结构调整 |
| `chore:` | 工程杂项（脚手架、依赖、忽略规则等） |

示例：`docs: 补充目录约定与贡献流程`。

## PR 要求

- **一 PR 对应一 Issue。** 不要把无关改动塞进同一个 PR。
- 填写现有 PR 模板：改了什么、怎么验证、相关 Issue。
- 正文关联 Issue（`Closes #n`），便于合并后自动关闭。
- **CI 须为绿**再请负责人审查。GitHub Actions 由后续 Issue 接入；接入前在 PR 里写明本地验证命令和结果，接入后以 CI 为准。
- 不提交模型权重、大文件或密钥。权重扩展名（`*.bin`、`*.onnx`、`*.gguf`、`*.argosmodel`）和根目录 `models/` 已被 `.gitignore` 忽略；`.env`、`*.pem` 同样不要入库。

## 模型与许可证纪律

- **模型权重不进 git。** 按需下载到本地缓存（仓库根目录 `models/`，已忽略）。安装包也不要默认打入权重。
- **新增或更换模型必须登记许可证**，再进入下载、转换或分发流程。人类可读说明放在 `docs/engine/`（模型选型、许可证、第三方署名）；机器可读清单与引擎工程放在一起，路径以模型选型 Issue 落地的文件为准。
- **不得默认打包 CC BY-NC 等非商用权重**，例如 NLLB、SeamlessM4T 的官方权重。这类模型最多记入「不采用」并写明原因，不能作为默认下载或随包分发的模型。
- OPUS-MT 等权重大多为 CC BY，可以按原许可证按需下载，但须逐个核对模型卡与仓库 LICENSE，不能只看标签。署名要求写进 `docs/engine/` 的第三方模型说明。
- 应用代码为 MIT。模型保持其原许可证，二者不要混为一谈。

## 本期非目标

一期不做下列能力。Issue 与 PR 不要把它们做进默认路径：

- LLM（含桌面可选的本地大模型）
- 商业云 API / BYOK 主路径
- 手机端（Android、iOS、鸿蒙）
- 输入法（IME）
- 视频字幕
- 无障碍读屏

[MVP 范围冻结](docs/research/MVP范围冻结-v0.1.md) 同时排除账号体系、付费和像素级多端一致。关闭一切「高级/可选」后，剪贴板翻译与快捷键裁剪 OCR 翻译仍须完整可用。

## 本地开发环境

详细命令由后续 Issue 补充，此处只固定版本基线：

- **翻译服务：** Python 3.11。工程在 `engine/`，依赖与 `ruff` / `pytest` 命令见 `engine/README.md`（Python 工程初始化后补全安装步骤）。
- **客户端（M2 起）：** .NET 8 SDK，工程在 `client/`。M2 之前不要在这里加解决方案或业务代码。
- **模型：** 不要提交到仓库。需要真实模型的测试应在模型目录未配置时跳过，避免 CI 下载权重。

当前占位：

```bash
# 引擎（目录与命令随 engine/ 工程初始化补全）
python3.11 --version

# 客户端（M2 起）
dotnet --version
```
