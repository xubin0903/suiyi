# 文档索引

随译的规格、调研和后续设计文档从这里进入。应用代码许可证为 MIT；模型权重不在本仓库，按其原许可证按需下载。

贡献流程与目录约定见 [贡献指南](../CONTRIBUTING.md)。

## 规格（当前有效）

| 文档 | 内容 |
|------|------|
| [MVP 范围冻结 v0.1](research/MVP范围冻结-v0.1.md) | 一期做与不做、Windows 客户端、本地 NMT + Context Layer |
| [引擎验收标准 v0.1](research/引擎验收标准-v0.1.md) | 架构、必测语种、中文效果、上下文层、部署 |

实现与评审以这两份为准。

## 调研

| 文档 | 内容 |
|------|------|
| [剪贴板与截屏可行性一页纸](research/剪贴板与截屏可行性一页纸.md) | 复制译与裁剪 OCR 译在 Windows / 移动端的可行性 |
| [开源引擎技术详解](research/开源引擎技术详解.md) | Bergamot、OPUS-MT + CTranslate2、许可证边界 |
| [引擎深度调研报告](research/引擎深度调研报告.md) | 早期候选对比（含云 API）；产品已冻结为本地 NMT，不以云为主路径 |
| [开源引擎对比表](research/开源引擎对比表.csv) | 开源引擎的体积、许可证与优先级 |
| [引擎对比表](research/引擎对比表.csv) | 更广的候选清单（含商业 API 快照） |

## 引擎文档

[docs/engine/](engine/README.md) 存放引擎设计、HTTP API、模型清单与第三方模型署名。已有 [模型目录约定](engine/模型目录约定.md)（转换脚本的输出布局）、[语种检测](engine/语种检测.md)、[翻译核心](engine/翻译核心.md) 和 [HTTP API](engine/HTTP-API.md)。其余文档由后续 Issue 加在该目录，不在调研目录里另起一份。

## 代码目录说明

| 路径 | 说明 |
|------|------|
| [engine/](../engine/README.md) | Python 翻译服务 |
| [client/](../client/README.md) | C# .NET 8 WPF 客户端（M2 起） |
| [scripts/](../scripts/README.md) | 模型转换、评测、基准等脚本 |
| [tests/](../tests/README.md) | 跨组件样例与端到端测试 |
