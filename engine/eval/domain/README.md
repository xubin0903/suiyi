# 专业领域测试集 domain_v1（#78）

用来比较翻译候选在专业 / 科技文本上的质量，并算术语准确率。评测脚本和报告见
[docs/engine/专业领域评测.md](../../../docs/engine/专业领域评测.md)。

## 文件

| 文件 | 内容 |
| --- | --- |
| `domain_v1.jsonl` | 测试集，UTF-8，一行一条 |
| `glossary.json` | 术语表：专有名词不译（`keep`）+ 固定译法（`fixed`），英 / 中 / 日三语写法 |
| `candidates.json` | 候选模型清单（后端、模型路径、许可证），只用于评测 |

## 规模

| 方向 | 条数 | 其中段落（3–5 句） |
| --- | ---: | ---: |
| en→zh | 154 | 15 |
| zh→en | 154 | 14 |
| ja→zh | 20 | 2 |
| ja→en | 20 | 2 |
| zh→ja | 20 | 1 |
| en→ja | 20 | 0 |

en↔zh 的段落都是 3–5 句；日文方向的段落为 2–3 句。

en↔zh 每个方向覆盖 7 个领域：`code`（编程 / 云原生 / DevOps，30 条）、`ai`（AI / ML，22）、`hw`（硬件 / 电子，20）、
`med`（医学，20）、`legal`（法律，20）、`fin`（金融，20）、`ui`（UI / 产品文案，22）。日文方向每个领域 2–4 条。
ja→zh 与 ja→en 用同一批 20 句日文原文；zh→ja、en→ja 的原文取自 zh→en、en→zh 两部分，另配日文参考译文。

术语表 287 条，测试集原文里共出现 419 次（按「术语 × 条目」计，同一条里重复出现只算一次）。

## 字段

在 `tests/samples/zh_core_v1.jsonl` 字段的基础上增加 `domain`、`terms`、`license`，因此现有的
`scripts/eval_samples.py` 也能直接读这份测试集（只跑基线）。

| 字段 | 说明 |
| --- | --- |
| `id` | `{src}-{tgt}-{domain}-{序号}`，如 `en-zh-code-009` |
| `src_lang` / `tgt_lang` | `en` / `zh` / `ja` |
| `domain` | `code` / `ai` / `hw` / `med` / `legal` / `fin` / `ui` |
| `category` | `sentence`（单句或 UI 短语）/ `paragraph`（3–5 句） |
| `source` / `reference` | 原文 / 参考译文 |
| `terms` | 原文里出现的术语 id（按术语表自动匹配，长的优先），用于术语准确率 |
| `must_keep` | 与 `terms` 一一对应：`src` 为原文里的写法，`tgt` 为参考译文里的写法（供旧评测脚本用） |
| `reference_status` | `draft`：开发起草，待负责人抽查；`published`：取自上游人工翻译的已发布译文 |
| `origin` / `license` | 来源与许可证 |
| `source_url` / `reference_url` | 仅外部来源：原文与参考译文的出处 |

测试 `engine/tests/test_domain_eval.py` 会重新按术语表匹配每条原文，检查 `terms` 与 `must_keep` 没有漂移，
且参考译文里确实出现了术语表写法。改术语表或测试集后跑一次 `pytest engine` 即可。

## 术语准确率怎么算

对每条样例，原文里匹配到的每个术语，如果译文里出现了该术语在目标语下的**任一**写法，就算命中。

- `keep`：专有名词原样保留（Kubernetes、CNCF、gRPC、I2C……）；
- `fixed`：固定译法（container orchestration ↔ 容器编排 ↔ コンテナオーケストレーション）。
  常见的等价写法列为变体，也算对（如「竞态条件 / 竞争条件」「仪表板 / 仪表盘」）；第一种写法是规范写法，
  术语保护原型强制用它。

匹配规则见 `engine/src/suiyi_engine/glossary.py` 的模块说明（英文按词边界、允许复数与连字符变体；中日文忽略空白）。

## 来源与许可证

- **原创（MIT）**：绝大多数条目是为本测试集新写的句子和段落（`origin` 为 `original (Suiyi domain_v1)`），
  随仓库以 MIT 许可发布。没有复制新闻、论文、说明书或其他受版权保护的文本；产品、机构和药品只用通用名称。
  参考译文由开发人工撰写，**没有**调用 Google / DeepL 或任何候选模型生成，状态为 `draft`。负责人已抽查 14 条，全部准确，`draft` 标记保留。
- **Kubernetes 文档（CC BY 4.0）**：8 条 en→zh 与 5 条 en→ja 取自 Kubernetes 官方文档「概述」页
  （[英文原文](https://github.com/kubernetes/website/blob/main/content/en/docs/concepts/overview/_index.md)），
  参考译文用官方中文 / 日文本地化的已发布译文
  （[zh-cn](https://github.com/kubernetes/website/blob/main/content/zh-cn/docs/concepts/overview/_index.md)、
  [ja](https://github.com/kubernetes/website/blob/main/content/ja/docs/concepts/overview/_index.md)），
  均为人工翻译。Kubernetes 文档以 [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) 发布，
  © The Kubernetes Authors；这些条目的 `license` 为 `CC-BY-4.0`，并带 `source_url` / `reference_url`。
  文字未作修改（只去掉了 Markdown 链接和换行）。
- 用户反馈的例句「container orchestration engine … CNCF」是据反馈新写的一句（`en-zh-code-009`），不是摘录。

## 如何修改

`domain_v1` 发布后，已有条目的 `source` / `reference` 不原地改；要改正文或参考译文就新建 `domain_v2.jsonl`。
允许的原地修改：`reference_status` 由 `draft` 改为 `reviewed`；给术语表补变体写法（补完跑 `pytest engine`，
测试会提示哪些条目的 `terms` 需要同步）。
