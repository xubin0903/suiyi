# 中文质量固定样例集

`zh_core_v1.jsonl` 是随译引擎的固定回归样例，服务验收标准 v0.1「C. 中文效果（优先）」。模型选型、回归评测和 Context Layer 都用这一份，不随模型更换而改写。

覆盖 MVP 必测方向：中↔英、中↔日、英↔日。句子按真实用法来写：随手复制的短消息、口语、专名和术语、截屏 OCR 的轻微噪声，以及偶尔出现的短段落。

## 文件

| 文件 | 说明 |
| --- | --- |
| `zh_core_v1.jsonl` | UTF-8、无 BOM，一行一个 JSON 对象 |
| `../../scripts/validate_samples.py` | 格式与覆盖校验，只用 Python 标准库 |

当前版本是 `v1`。条数以下限为准，实际计数以校验脚本输出为准。

## 字段

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 全局唯一。形式为 `{src}-{tgt}-{category}-{序号}`，例如 `zh-en-short-001` |
| `src_lang` / `tgt_lang` | 是 | ISO 639-1：`zh`、`en`、`ja` |
| `category` | 是 | `short`、`colloquial`、`proper_noun`、`ocr_noise`、`paragraph` |
| `source` | 是 | 原文，非空 |
| `reference` | 是 | 参考译文。允许空字符串；`short` 和 `proper_noun` 必须非空。本版全部给了译文 |
| `must_keep` | 是 | 译文里必须按固定写法出现的专名或术语，见下文 |
| `reference_status` | 是 | `draft`（开发起草）或 `reviewed`（负责人审过）。本版均为 `draft` |
| `origin` | 是 | 来源说明。本版全部为 `original (Suiyi zh_core_v1, MIT)` |
| `notes` | 否 | 口语含义、译名约定或 OCR 噪声类型 |
| `clean_id` | 仅 `ocr_noise` | 指向同一方向的干净条目 |

`must_keep` 只用一种格式：对象数组。

```json
"must_keep": [
  {"src": "张伟", "tgt": "Zhang Wei"},
  {"src": "Python", "tgt": "Python"}
]
```

- `src`：规范原文里的专名或术语，必须能在本条原文里找到；`ocr_noise` 写干净写法，不写错字，并到 `clean_id` 那条原文里核对。
- `tgt`：参考译文里必须连续出现的固定译法。
- 不需要保留专名时用空数组 `[]`。`proper_noun` 不能为空。
- 不使用 `["OpenAI", "上海"]` 这种纯字符串形式。很多专名必须翻译，字符串形式无法表达译名。

译名在本集里是评测约定，不是唯一合法译法。没有更多上下文时按 `notes` 和 `must_keep` 固定，例如北京大学用通行英文名 Peking University，Kenji 固定作「健二」。

## 分类

| category | 标准 |
| --- | --- |
| `short` | 短句。原文不超过 30 个 Unicode 码位（含标点和空格） |
| `colloquial` | 口语或网络用语，语体和原文对齐，例如「靠谱」「绝绝子」「マジで」 |
| `proper_noun` | 人名、地名、机构、产品，或需要原样保留的数字、日期、金额、中英混排术语 |
| `ocr_noise` | 轻微 OCR 噪声：错字、漏标点、多余空格或换行、全半角混用、形近字（0/O、1/i、ニ/二、ー/一） |
| `paragraph` | 2–5 句的短段落，按句末标点 `。！？.!?` 计数 |

`ocr_noise` 的参考译文按干净原文翻译，并且必须和 `clean_id` 那条的 `reference` 相同，方便比较噪声对译文的影响。

## 方向与数量下限

| 方向 | 条数下限 | 含中文时的分类下限 |
| --- | --- | --- |
| zh→en | 30 | short / colloquial / proper_noun / ocr_noise 各 ≥ 5，paragraph ≥ 3 |
| en→zh | 30 | 同上 |
| zh→ja | 25 | 同上 |
| ja→zh | 25 | 同上 |
| en→ja | 10 | 无额外分类下限 |
| ja→en | 10 | 无额外分类下限 |

全文件合计至少 130 条。

## 示例

短句：

```json
{"id":"zh-en-short-001","src_lang":"zh","tgt_lang":"en","category":"short","source":"明天上午九点开会。","reference":"The meeting is at 9 a.m. tomorrow.","must_keep":[{"src":"上午九点","tgt":"9 a.m."}],"reference_status":"draft","origin":"original (Suiyi zh_core_v1, MIT)"}
```

对应的 OCR 噪声（`午` 被识别成 `牛`）：

```json
{"id":"zh-en-ocr_noise-001","src_lang":"zh","tgt_lang":"en","category":"ocr_noise","source":"明天上牛九点开会。","reference":"The meeting is at 9 a.m. tomorrow.","must_keep":[{"src":"上午九点","tgt":"9 a.m."}],"reference_status":"draft","origin":"original (Suiyi zh_core_v1, MIT)","notes":"错字：午被识别为牛。","clean_id":"zh-en-short-001"}
```

## 如何新增

v1 已有条目的 `id`、`source`、`reference`、`category`、`must_keep` 不要原地改。

- 新句子追加到 `zh_core_v1.jsonl` 末尾，使用新的 `id`，然后重新跑校验。
- 负责人抽查后，只允许把 `reference_status` 从 `draft` 改成 `reviewed`，也可以补充 `notes`。这不算改样例内容。
- 如果要改正文或参考译文，新建 `zh_core_v2.jsonl`，不要改 v1。v1 继续作为已发布基线。

参考译文请人工撰写，对齐原文语体：短句和段落用自然书面语，口语保留口语。不要调用翻译模型或在线翻译服务生成参考译文。

## 来源与版权

本文件全部原文和参考译文都是为随译 `zh_core_v1` 原创撰写的，每条 `origin` 均为 `original (Suiyi zh_core_v1, MIT)`。

没有整段复制新闻、小说、歌词、网页文章或其他受版权保护的文本。人名、地名、机构和产品名只用公开通用称谓和通行译名，句子本身是新写的。

样例与仓库同属 [MIT](../../LICENSE) 许可，可以再分发。若以后追加外部句子，只接受与 MIT 兼容、允许再分发的来源（例如 CC0、CC BY），并在该条 `origin` 写明许可证、作者和出处；同时把说明补进本节。

## 校验

```bash
python scripts/validate_samples.py tests/samples/zh_core_v1.jsonl
```

环境里如果只有 `python3`，把上面的 `python` 换成 `python3`。脚本只用标准库。

脚本检查：

- 每行是合法 JSON 对象，编码为无 BOM 的 UTF-8
- 必填字段、`id` 唯一且与方向和分类一致
- `src_lang` / `tgt_lang` / `category` / `reference_status` 的取值
- `short`、`proper_noun` 的参考译文非空；`proper_noun` 的 `must_keep` 非空
- `must_keep` 使用 `{src, tgt}` 对象，且 `tgt` 出现在参考译文中
- 每条 `ocr_noise` 的 `clean_id` 指向同一方向的干净条目，参考译文与干净版本一致
- 六个方向的条数下限，以及含中文方向的分类下限
- 标准输出打印方向 × 分类计数表

退出码：`0` 通过，`1` 校验失败，`2` 参数或文件错误。
