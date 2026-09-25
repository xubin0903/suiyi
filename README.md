# 随译（Suiyi）

[![CI](https://github.com/xubin0903/suiyi/actions/workflows/ci.yml/badge.svg)](https://github.com/xubin0903/suiyi/actions/workflows/ci.yml)

随意用、快捷、不收费、开源的翻译工具。

## 定位

- **核心**：一套本地开源 NMT 引擎 + 自研 Context Layer（切分 / 术语表 / 专名一致）
- **工作方式**：剪贴板复制翻译 · 快捷键裁剪区域 OCR 翻译
- **一期客户端**：Windows
- **不做（本期）**：商业云主路径、本地 LLM、输入法、视频字幕、无障碍读屏

## 状态

规划与调研阶段。规格见 `docs/`。

## 文档

- [贡献指南](CONTRIBUTING.md)（目录约定与开发流程）
- [文档索引](docs/README.md)
- [MVP 范围冻结](docs/research/MVP范围冻结-v0.1.md)
- [引擎验收标准](docs/research/引擎验收标准-v0.1.md)
- [剪贴板与截屏可行性](docs/research/剪贴板与截屏可行性一页纸.md)
- [开源引擎技术详解](docs/research/开源引擎技术详解.md)
- [M2 实机测试](docs/client/M2-实机测试.md)（Windows 从源码运行与验收清单）

## 许可证

应用代码：[MIT](LICENSE)。第三方模型权重按其原许可证按需下载，不默认打入本仓库。
