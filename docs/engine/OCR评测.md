# OCR 评测与性能基线（#54 / #74 / #75）

框选翻译 OCR（RapidOCR + PP-OCRv6 ONNX，见 [OCR 核心](OCR核心.md)）的准确率、段落切分、耗时与内存。
样例集、指标实现与脚本都在仓库里，任何人可以用一条命令复现。

- 样例：`engine/eval/ocr_samples/`（32 张，CC0，说明见其 [README](../../engine/eval/ocr_samples/README.md)）
- 指标：`engine/src/suiyi_engine/eval/ocr_metrics.py`（CER、段落切分；单测 `engine/tests/test_ocr_metrics.py`）
- 编排：`engine/src/suiyi_engine/eval/ocr_bench.py`（单测 `engine/tests/test_ocr_eval.py`）
- 入口：`scripts/eval_ocr.py`；Windows 包装 `scripts/eval_ocr.ps1`

> **数据来源标注**：本文「Linux 实测」来自开发代理的 Linux 虚拟机；「CI 环境数据」来自 GitHub 托管 runner 的快速子集，
> 只证明脚本可用；「Windows 推测」是根据 Linux 数据的推断，**不是实测**，需要在笔记本上补测（见[下文](#windows-笔记本补测)）。

## 怎么跑

### Linux / macOS

```bash
pip install -e "engine[ocr,eval]"
python scripts/download_ocr_models.py download                          # 推荐组合：det small + rec small + cls
python scripts/download_ocr_models.py download --model PP-OCRv6_det_tiny  # 对比用
python scripts/eval_ocr.py                                               # 默认对比 det small 与 det tiny
```

### Windows（一条命令）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\eval_ocr.ps1 -Setup -Label "机型 CPU 内存"
```

输出在 `reports/ocr-eval-<时间>/`：`report.md`（给人看）与 `report.json`（全部原始数字，含每张样例的识别行 `ocr_lines`，便于复算）。
`reports/` 已被 git 忽略。

常用参数（`python scripts/eval_ocr.py --help`）：

| 参数 | 说明 |
|---|---|
| `--det small,tiny` | 要对比的检测模型变体，第一个视为当前默认。写法 `检测模型[@长边上限][+legacy][+rb<N>][+mp\|+nomp][+dil\|+nodil][+sh<X>][+pad<X>]`（`@0` = 不缩放，`+legacy` = #74 之前的行为），见 [#74 调优](#74-内存与耗时调优) |
| `--repeats 5 --warmup 1` | 每张样例计时次数 / 不计时预热次数 |
| `--quick` | 6 张样例、repeats=2，只验证脚本可用（CI 用） |
| `--only id1,id2` | 只跑指定样例 |
| `--split dev\|holdout` | 只跑 dev（原 32 张）或留出集（#75）；默认都跑，汇总只算 dev，留出集单列 |
| `--line-gaps 0.5,0.6,...` | 段落合并 `line_gap` 扫描值 |
| `--threads N` | onnxruntime 线程数（默认 min(4, CPU 数)，与服务一致） |
| `--models-dir` | 模型根目录（默认 `SUIYI_MODELS_DIR`，否则仓库根 `models/`） |
| `--label` | 机器说明，写进报告 |

每个检测模型变体在**独立子进程**里加载和计时，内存互不干扰。子进程与主进程都强制 UTF-8（`PYTHONUTF8=1`），
Windows 控制台不会因为中文输出报 `UnicodeEncodeError`。

## 指标定义

| 指标 | 定义 |
|---|---|
| CER | 期望段落按阅读顺序拼接后，与识别出的段落文本比较：NFKC 归一（竖排标点、全角英数折回常规字符）、去掉全部空白，再算字符编辑距离 / 期望字符数。按类别/语种/尺寸汇总时按字符数加权。**阅读顺序错误也计入 CER** |
| 段落切分 | 期望段落与识别段落各自拼成一串，期望串里的每个段落边界在识别串里找对应位置（字符对齐，容差 ±2 字）。**误合并** = 漏掉的期望边界（两段并成一段）；**误拆分** = 多出来的边界。**完全正确** = 该样例没有任何误合并/误拆分 |
| 耗时 | `OcrEngine.recognize` 的墙钟时间：解码 PNG + 检测 + 识别 + 段落合并，不含读文件与翻译。每张样例预热 1 次、计时 5 次，按尺寸汇总 P50 / P95（线性插值） |
| 内存（#74 口径） | **单请求峰值增量** = 单次 `recognize` 期间峰值 RSS − OCR 已预热后的空闲 RSS（Linux 每次请求前经 `/proc/self/clear_refs` 重置 `VmHWM`，精确；Windows/macOS 用 psutil 每 2 ms 采样，近似）。**常驻**另报：空闲 RSS，以及跑完全部样例后空闲 RSS 的增长。另记加载前基线、模型+预热增量 |

## #74 内存与耗时调优

**内存口径（#74 起）**：**单请求峰值增量** = 单次 `recognize` 期间的峰值 RSS − OCR 已加载且预热后、空闲时的 RSS，目标 ≤ 400 MB；
**常驻**另报（空闲 RSS 绝对值，以及跑完全部样例后空闲 RSS 的增长），不设目标。#54 的「进程峰值 − 加载前基线 ≤ 300 MB」口径作废。

### 为什么长边上限之前不生效

RapidOCR 3.9 没有「只缩小检测图」的参数：

- `Det.limit_type=max` 时忽略 `Det.limit_side_len`，按原图长边选 960 / 1500 / 2000（`ch_ppocr_det/main.py: get_preprocess`），≤ 2000 px 的截图检测时从不缩小；
- `limit_type=min` 只放大不缩小；
- `Global.max_side_len` 在整条流水线最前面缩图，识别裁切也一起变小，小字 CER 会变差。

所以引擎后端（`suiyi_engine.ocr.engine.rapidocr_backend`）把流程拆成 RapidOCR 的公开对象，**不改 RapidOCR 源码**：
长边 > `det_max_side` 时先用 `cv2.INTER_AREA` 缩图，交给 `RapidOCR(use_rec=False)` 只做检测（缩好的图 ≤ 1024，RapidOCR 选 1500 档、只做 32 对齐）；
框按比例换回原图坐标，**从原图裁切**后交给 `engine.text_rec`。onnxruntime 的 memory pattern RapidOCR 不暴露，
在创建会话期间临时替换其会话选项构造函数关闭（只影响本实例）。参数集中在 `OcrRuntimeOptions`（默认 `DEFAULT_RUNTIME`），
`LEGACY_RUNTIME` 保留 #74 之前的行为，评测里写 `--det small+legacy`。

### 选定的默认参数

| 参数 | 默认 | 之前 | 理由（Linux 实测，见下表） |
|---|---|---|---|
| `det_max_side` 检测长边上限 | **1024** | 不缩放（≤2000 px） | 1280 时 720p 不缩放，峰值 426 MB 超标；960 峰值最低但段落 20/32；1024 峰值 333 MB、段落 23/32 |
| `rec_batch` 识别批大小 | **1** | 6 | 批内按最宽行补齐，批越大激活越大；#54 实验 1 批更省也更快，CER 不变 |
| `mem_pattern` | **关** | 开 | 关后峰值 362 → 333 MB、常驻增长 223 → 106 MB，耗时差异在噪声内 |
| `enable_cpu_mem_arena` | 关 | 关 | 沿用 |
| `det_dilation` 检测膨胀 | **缩放时关** | 开 | 缩小后 2×2 膨胀相对变大、行框变胖，段间距被吃掉：开膨胀段落 20/32（误合并 28） |
| `box_shrink` 缩放补偿 | **2**（检测图像素） | — | 见下节；识别不受影响 |
| `crop_pad` 裁切外扩 | **1**（检测图像素） | — | 关膨胀后框变紧，下划线被切（`self._backend` → `self. backend`）；外扩 1 px 补回大部分 |

### 段落切分的补偿（按缩放比例，不改 line_gap）

缩图后段落变差的原因（Linux 实测）：DB 后处理的外扩在检测图上是常数像素（约 2–3 px），换回原图后放大 `1/scale` 倍，
行框变高、段间空隙变小。例如 `zh_dense_1080` 行框 20 → 24–26 px、段间空隙 26 → 20 px，两段被并成一段。补偿：

1. **行框收缩**：交给分段的行框沿短边每侧收回 `box_shrink × (1/scale − 1)` 原图像素（1080p 缩到 1024 时约 1.75 px，720p 缩到 1024 时约 0.5 px；不缩放时为 0，行为与之前完全一致）。
   识别仍用未收缩（且外扩 `crop_pad / scale`）的框，CER 不受影响。`line_gap`（0.9）等阈值不变。
2. **竖排单字列**（`layout.py`）：缩图后换列只剩一个字的列（如「…片づけ｜た」）常被单独切出，单字框接近正方形，被当成横排孤立成段。
   现在单字框紧接在某竖排列正下方（列尾）或在列左侧紧邻、与列顶对齐（下一列列首）时归入竖排；单字行不参与字号比判断（单字框尺寸随字形变化）。
   这条规则与缩放无关，对旧行为也成立（旧行为是这个字直接漏识别：日文竖排 CER 11.4% → 8.9%）。

分段效果（Linux 实测，全部 32 张）：长边 960 无补偿 17/32 → 加行框收缩 19/32 → 再加单字列规则 20/32；长边 1024 同样组合 23/32。

### 前后对比（Linux 实测，2026-09-25）

机器同 #54：开发代理 Linux 虚拟机 `Intel(R) Xeon(R) Processor`，`taskset -c 0-3` 限 4 核，onnxruntime 4 线程，rapidocr 3.9.2 / onnxruntime 1.30.0，
全部 32 张，每张预热 1 次、计时 5 次。命令：

```bash
taskset -c 0-3 python scripts/eval_ocr.py --repeats 5 \
  --det small,small+legacy,small+mp,small+dil,small@1280,small@960
```

| 指标 | 目标 | **调优后（默认）** | 调优前（`+legacy`） |
|---|---|---:|---:|
| P95 400×150 | ≤ 800 ms | 157 ms | 157 ms |
| P95 1280×720 | ≤ 800 ms | 486 ms | 627 ms |
| P95 1920×1080 | ≤ 1500 ms | **739 ms** | 1578 ms |
| P95 1080p 密集文字 | ≤ 1500 ms | **788 ms** | 1653 ms |
| P50 400×150 / 720p / 1080p | — | 126 / 386 / 475 ms | 130 / 539 / 1222 ms |
| 单请求峰值增量（最大） | ≤ 400 MB | **333 MB** | 976 MB |
| 单请求峰值增量 P50 / P95（全部 160 次） | — | 167 / 289 MB | 386 / 739 MB |
| 单请求峰值增量最大：400×150 / 720p / 1080p | — | 49 / 303 / 333 MB | 128 / 576 / 976 MB |
| 空闲 RSS（加载+预热后） | 另报 | 169 MB | 169 MB |
| 常驻增长（32 张 × 6 次后空闲 RSS − 预热后空闲 RSS） | 另报 | 106 MB | 217 MB |
| 冷加载 | — | 468 ms | 509 ms |
| CER 全部 | 不劣于 0.6% | **0.5%** | 0.6% |
| CER 中文网页 / 英文文档 / 日文横排 | ≤ 5% / 5% / 10% | 0.0% / 0.1% / 0.3% | 0.0% / 0.1% / 0.3% |
| CER 日文竖排 | 不劣于 11.4% | 8.9% | 11.4% |
| CER 英文代码 | — | 0.3% | 0.0% |
| 段落完全正确 | ≥ 21/32 | **23/32** | 21/32 |
| 误合并 / 误拆分（期望边界 101） | — | 15 / 3 | 21 / 1 |
| 段落 F1 | — | 0.91 | 0.88 |

逐样例的段落变化（调优前 → 后，误合并/误拆分）：`en_doc_720_02` 1/0 → 0/0，`mixed_720_01` 1/0 → 0/0，`en_dense_1080_01` 2/0 → 0/0，
`en_code_1080_01` 2/0 → 0/0；变差的两张：`zh_twocol_720_01` 0/0 → 0/1（段尾短行「对。」框偏高、字号比 1.5 被拆出），
`en_twocol_1080_01` 0/0 → 0/1（段尾短行「release the …」被拆出）。英文代码 CER 0.0% → 0.3%：`return ""` 被检测切成两个框，`""` 单独识别成 `" I`（2 个字符）。

其他变体（同一次运行，用于选参数）：

| 变体 | 峰值增量最大 | 常驻增长 | 1080p P95 | 720p P95 | CER | 段落完全正确 |
|---|---:|---:|---:|---:|---:|---:|
| **small（默认：1024、rb1、关 mp、缩放时关膨胀、补偿）** | **333 MB** | **106 MB** | **739 ms** | 486 ms | 0.5% | **23/32** |
| small+mp（开 memory pattern） | 362 MB | 223 MB | 754 ms | 454 ms | 0.5% | 23/32 |
| small+dil（缩放时也膨胀） | 348 MB | 245 MB | 720 ms | 460 ms | 0.4% | 20/32 |
| small@1280 | 426 MB ❌ | 309 MB | 853 ms | 600 ms | 0.5% | 23/32 |
| small@960 | 321 MB | 215 MB | 702 ms | 424 ms | 0.5% | 20/32 |
| small+legacy（调优前） | 976 MB ❌ | 217 MB | 1578 ms ❌ | 627 ms | 0.6% | 21/32 |

- 峰值几乎全是检测网络的激活，和检测图面积成正比：1280 时 720p 按原尺寸检测（1280×736），单张峰值就到 ~420 MB。
- 常驻增长在不同变体之间波动较大（glibc 不一定把释放的内存还给系统），只作参考；单请求峰值增量是精确值（Linux 每次请求前经 `/proc/self/clear_refs` 重置 `VmHWM`）。
- memory pattern、膨胀对耗时的影响在噪声内（±5%）；1080p 耗时减半来自检测图面积（1920×1088 → 1024×576）。

## #75 UI 短行分段

规则与阈值见 [OCR 核心 · 段落归并](OCR核心.md#4-段落归并)。摘要：
- **短行分段**：相邻两行都 ≤ 12 × 字号（`short_row`），且上一行不以续接标点结尾 → 分段（只对横排）；
- **段尾短行豁免**：字号比 ≤ 1.7、长度 ≤ `short_row`、间距不超出本段行距 → 不按字号比拆出；
- **小写续行**：下一行以小写拉丁字母开头时，「上一行明显短」的阈值从 2 放宽到 4 × 字号；
- **同行相接框合并**（后端，识别前）：修 `return ""` → `" I`。

### 防过拟合：留出集

新增 10 张 CC0 自制 UI 样例（`split: holdout`，说明见[样例集 README](../../engine/eval/ocr_samples/README.md)）。
它们是在写规则之前画好的，调参时只用 dev 集（原 32 张）的识别行离线重跑分段，**没有看留出集的识别结果**；
最后跑一次完整评测单独报告。留出集里特意放了反例：聊天窄气泡里换行的正文（每行约 17 × 字号、最后一行只有两个字「一下」），应保持一段。

### 调参（dev 集，只重跑分段，Linux 实测）

| `short_row` | 完全正确 | 误合并 | 误拆分 | UI 误合并 |
|---:|---:|---:|---:|---:|
| 0（关闭） | 24/32 | 15 | 2 | 13 |
| 6 / 8 | 25/32 | 8 | 1 | 6 |
| 10 / **12（默认）** | 26/32 | 7 | 1 | 5 |
| 14 | 27/32 | 6 | 1 | 4 |
| 16 | 28/32 | 3 | 2 | 1 |
| 20 | 25/32 | 3 | 7 | 1 |

（`tail_ratio` = 1.7；1.35 / 1.5 时误拆分 +1，1.7 与 2.0 相同，取较小的 1.7。）14–16 在 dev 集上分数更高，但 16 起误拆分开始增加、20 时正文窄栏被大量拆开，
取 10–14 这段平台的中间值 12，不追 dev 集最高分。

### 前后对比（Linux 实测，2026-09-26）

同 #74 的机器与命令（`taskset -c 0-3`，4 线程，repeats 5）。「改前」是 main（#74 合入后）的代码，用同一份样例清单跑（`--only` 指定 dev / 留出集 id）；
「改后」是本 PR。汇总只算 dev（原 32 张）；留出集单列。

| 指标 | 目标 | 改前（#74） | **改后（#75）** |
|---|---|---:|---:|
| dev 段落完全正确 | ≥ 23/32 | 23/32 | **26/32** |
| dev 误合并 / 误拆分 | 误拆分 ≤ 1 | 15 / 3 | **7 / 1** |
| dev UI 误合并（6 张 UI 样例） | — | 13 | **5** |
| dev 正文误合并 / 误拆分（16 张） | 不退化 | 1 / 2 | 1 / 0 |
| dev 段落 F1 | — | 0.91 | 0.96 |
| **留出集** 完全正确（10 张 UI） | 明显改善 | 1/10 | **8/10** |
| **留出集** 误合并 / 误拆分 | 误拆分不增加 | 36 / 2 | **2 / 2** |
| 留出集 F1 | — | 0.59 | 0.97 |
| CER 全部（dev） | 不劣于 0.5% | 0.5% | 0.5% |
| CER 中文网页 / 英文文档 / 日文横排 / 日文竖排 | | 0.0 / 0.1 / 0.3 / 8.9% | 0.0 / 0.1 / 0.3 / 8.9% |
| CER 英文代码 | — | 0.3% | **0.2%**（`return ""` 修好） |
| CER 留出集 | — | 4.7% | 4.7% |
| P95 400×150 / 720p / 1080p（两次运行） | 800 / 800 / 1500 ms | 148–167 / 484–488 / 796–803 | 159–162 / 477–494 / 749–800 |
| P95 1080p 密集文字（两次） | ≤ 1500 ms | 818–893 ms | 791–815 ms |
| 单请求峰值增量 P95 / 最大（两次） | ≤ 400 MB | 292–293 / 325–340 MB | 294–296 / 347–359 MB |
| 常驻增长（两次） | 另报 | 158–270 MB | 186–228 MB |

- 耗时、内存的差异都在两次运行之间的波动范围内：同一份 main 代码跑两次，单请求峰值 P50 就是 170 / 240 MB、最大 340 / 325 MB。
  改动只动了分段（纯 Python，< 1 ms）和识别前的框合并（32 张里只有 `en_code_720_01` 触发），不影响 onnxruntime 的输入尺寸。
- dev 剩余的误合并：3 张小图的「菜单栏 + 状态栏」（整行菜单比 12 × 字号长，短行规则管不到）、代码里 `pairs = …` 与 `return engine.run(…)`
  （两行都长、下一行小写开头，和西文换行在几何上无法区分）、日文密集正文 1 处；误拆分 1 处是 `zh_ui_1080_01` 的**阅读顺序**（「就绪」被排到正文前）。
- 留出集剩余 2 张不完全正确，都是**阅读顺序**：左下角状态栏被排进左侧栏那一列（`ho_zh_filemgr_1080_01`、`ho_mixed_toolbar_1080_01`），
  不是分段规则的问题。留出集 CER 4.7% 也主要来自这两张的顺序错位（CER 计入阅读顺序）。聊天窄气泡（反例）保持一段。

### 译文对比（zh→en，`opus-mt-zh-en`，Linux 实测）

| 样例 | 改前（识别成一段） | 改后（每项一段） |
|---|---|---|
| `zh_ui_small_02` 设置项 | 关闭窗口时最小化到托盘检查更新（每周一次）显示翻译耗时 → *Minimize window closing to tray check updates (once a week) to show translation time* | *Minimize to Tray when Close Window* / *Check for updates (once a week)* / *Show translation time* |
| `zh_ui_1080_01` 侧栏 | 设置常规翻译框选翻译快捷键模型关于 → *Sets the normal translation box selection translation shortcut model for* | *Settings* / *General* / *Translation* / *Box Translation* / *Shortcut Keys* / *Model* / *About* |
| `ho_zh_menu_small_01`（留出） | 新建标签页新建窗口历史记录下载内容书签管理器 → *New Tab New Window History Downloading Bookmark Manager* | *New Tab* / *New Window* / *Historical records* / *Download Contents* / *Bookmark Manager* |
| `ho_zh_filemgr_1080_01`（留出）文件列表 | 季度报告终稿.docx会议纪要0921.txt产品截图（高清）png安装包备份.zip → *Final quarterly report. Summary of docx meetings 0921.txt Product Screenshot (high clearance) png installation package back-up.zip* | *Final quarterly report.docx* / *Summary of meetings 0921.txt* / *Product Screenshot (HQ)png* / *Backup of install package.zip* |
| `ho_zh_form_720_01`（留出） | 用户名密码 → *Username password* | *Username* / *Password* |
| `ho_zh_chat_720_01`（留出，反例） | 两个气泡各一段 | 不变（气泡内换行没有被拆开） |

### 长边降到 960 时（备用数据，只评测、不改默认）

同一次运行里加了 `--det small@960`（其余参数同默认，新规则生效）：

| 指标 | 1024（默认） | 960 + 新规则 | 960（#74 数据，无新规则） |
|---|---:|---:|---:|
| dev 完全正确 | 26/32 | **22/32** | 20/32 |
| dev 误合并 / 误拆分 | 7 / 1 | 11 / 2 | 18 / 3 |
| 留出集 完全正确 / 误合并 / 误拆分 | 8/10 / 2 / 2 | 6/10 / 4 / 3 | — |
| CER（dev） | 0.5% | 0.5% | 0.5% |
| P95 720p / 1080p | 494 / 800 ms | 505 / 733 ms | 424 / 702 ms |
| 单请求峰值增量最大 | 359 MB | 313 MB | 321 MB |

结论：新规则把 960 从 20/32 提到 22/32，**还差 1 张到 23/32**。960 多出的错误是缩放带来的正文误合并/误拆分
（`zh_web_720_03`「校对。」、`en_doc_720_02`、`mixed_720_01`、`ja_dense_1080_01`），不是 UI 短行。
离线重跑分段：960 下 `short_row=14` 可到 23/32（误拆分 2）、16 可到 25/32（误拆分 3），但都超出「误拆分 ≤ 1」且更靠近误拆分陡增区，默认不采用；若以后确要降到 960，可把 `short_row` 提到 14 并复核留出集。

## 样例集

dev 集 32 张（另有 #75 的 10 张 UI 留出集，见上），文字全部原创、PIL 渲染、CC0：400×150 12 张、1280×720 13 张、1920×1080 7 张；覆盖中文网页正文、
中/英/日 UI 小字（12px）、低对比/深色主题、英文文档、英文代码、日文横排、日文竖排（3 张）、中英混排、双栏、密集文字。
每张都标注了期望段落（UI 的每个菜单行、按钮、状态栏字段各自一段；代码每行一段）。PNG 合计约 1.5 MB，随仓库提交。

渲染图比真实截图干净（无 ClearType 彩边、无 JPEG 噪点、无图标混排），所以**这里的 CER 是乐观值**；
段落切分和耗时/内存不受此影响。真实截图样例留作后续补充（需要逐张确认许可）。

## #54 基线：Linux 实测（2026-09-25，调优前）

> 本节是 #74 调优**之前**的数据（等同现在的 `--det small+legacy`），内存行是旧口径，保留作对照。

| 项 | 值 |
|---|---|
| 机器 | 开发代理 Linux 虚拟机，`Intel(R) Xeon(R) Processor`（未暴露具体型号），8 vCPU，15.6 GiB |
| 限核 | 整条命令 `taskset -c 0-3`，模拟 4 核参考机；onnxruntime 线程数 4 |
| 软件 | Linux 6.12，glibc 2.41，Python 3.13.5，rapidocr 3.9.2，onnxruntime 1.30.0 |
| 模型 | rec `PP-OCRv6_rec_small`，cls 关闭（清单 `use_cls=false`）；det 见各列 |
| 命令 | `taskset -c 0-3 python scripts/eval_ocr.py --models-dir <models> --repeats 5 --warmup 1 --det small,tiny,...` |

### 目标对照

| 指标 | 目标 | det small（当前默认） | det tiny | 结果 |
|---|---|---:|---:|---|
| P95 400×150 | ≤ 800 ms | 149 ms | 144 ms | 都达标 |
| P95 1280×720 | ≤ 800 ms | 610 ms | 410 ms | 都达标 |
| P95 1920×1080 | ≤ 1500 ms | **1475 ms** | 932 ms | 都达标；small 余量只有 2% |
| CER 中文网页正文 | ≤ 5% | 0.0% | 0.0% | 都达标 |
| CER 英文文档 | ≤ 5% | 0.1% | 0.0% | 都达标 |
| CER 日文网页横排 | ≤ 10% | 0.3% | 0.3% | 都达标 |
| RSS 增量（全部样例跑完的峰值） | ≤ 300 MB | **1156 MB** | **1167 MB** | **都不达标** |
| RSS 增量（三轮后常驻） | ≤ 300 MB | 约 519 MB | — | **不达标** |

冷加载（三个 ONNX）约 480 ms，首次识别（小图预热）7 ms。

### CER（按类别）

| 类别 | 字符数 | det small | det tiny |
|---|---:|---:|---:|
| 中文 UI 小字 | 262 | 1.5% | 1.5% |
| 中文网页正文 | 516 | 0.0% | 0.0% |
| 中英混排 | 201 | 0.0% | 0.0% |
| 低对比/深色主题 | 285 | 0.0% | 0.0% |
| 双栏 | 1229 | 0.0% | 0.0% |
| 密集文字 | 1753 | 0.0% | 0.0% |
| 日文 UI 小字 | 58 | 0.0% | 1.7% |
| 日文竖排 | 280 | 11.4% | 10.7% |
| 日文网页横排 | 611 | 0.3% | 0.3% |
| 英文 UI 小字 | 106 | 0.0% | 0.0% |
| 英文代码/等宽 | 592 | 0.0% | 0.0% |
| 英文文档 | 938 | 0.1% | 0.0% |
| **全部** | 6831 | 0.6% | 0.5% |

按语种：en 0.0% / 0.0%，zh 0.2% / 0.2%，ja 3.6% / 3.5%。中文 UI 小字的 1.5% 主要来自阅读顺序（1080p 设置页的状态栏「就绪」被排到正文之前），不是认错字。

### 耗时（按尺寸，每格 n = 样例数 × 5）

| 尺寸 | det | n | P50 | P95 | max | 目标 P95 |
|---|---|---:|---:|---:|---:|---:|
| 400×150 | small | 60 | 128 ms | 149 ms | 159 ms | ≤ 800 ms |
| 400×150 | tiny | 60 | 100 ms | 144 ms | 176 ms | ≤ 800 ms |
| 1280×720 | small | 65 | 515 ms | 610 ms | 656 ms | ≤ 800 ms |
| 1280×720 | tiny | 65 | 301 ms | 410 ms | 440 ms | ≤ 800 ms |
| 1920×1080 | small | 35 | 1127 ms | 1475 ms | 1496 ms | ≤ 1500 ms |
| 1920×1080 | tiny | 35 | 570 ms | 932 ms | 939 ms | ≤ 1500 ms |

耗时几乎全在 onnxruntime（检测 + 识别）；PNG 解码与段落合并合计最多约 27 ms（1080p）。

### 段落切分（line_gap = 0.9，当前默认）

| 类别 | 样例 | 完全正确 small / tiny | 期望边界 | 误合并 small / tiny | 误拆分 small / tiny |
|---|---:|---:|---:|---:|---:|
| 中文 UI 小字 | 4 | 1 / 1 | 22 | 10 / 11 | 1 / 1 |
| 英文 UI 小字 | 1 | 0 / 0 | 2 | 1 / 1 | 0 / 0 |
| 日文 UI 小字 | 1 | 0 / 0 | 2 | 2 / 2 | 0 / 0 |
| 中文网页正文 | 4 | 4 / 3 | 6 | 0 / 0 | 0 / 1 |
| 英文文档 | 3 | 2 / 2 | 4 | 1 / 1 | 0 / 0 |
| 日文网页横排 | 3 | 2 / 2 | 9 | 1 / 2 | 0 / 0 |
| 日文竖排 | 3 | 3 / 1 | 8 | 0 / 0 | 0 / 3 |
| 密集文字 | 2 | 1 / 0 | 16 | 2 / 6 | 0 / 0 |
| 双栏 | 2 | 2 / 2 | 10 | 0 / 0 | 0 / 0 |
| 中英混排 | 2 | 1 / 1 | 2 | 1 / 1 | 0 / 0 |
| 低对比/深色主题 | 4 | 4 / 4 | 3 | 0 / 0 | 0 / 0 |
| 英文代码/等宽 | 3 | 1 / 1 | 17 | 3 / 3 | 0 / 0 |
| **全部** | 32 | **21 / 17** | 101 | **21 / 27** | **1 / 5** |

## small vs tiny：结论

| | det small | det tiny |
|---|---|---|
| 模型体积 | 9.9 MB | 1.8 MB |
| CER | 0.6% | 0.5%（差异在噪声内） |
| 1080p P95（Linux 实测） | 1475 ms（目标 1500） | 932 ms |
| 720p P95（Linux 实测） | 610 ms | 410 ms |
| 段落完全正确 | 21/32 | 17/32 |
| 误合并 / 误拆分 | 21 / 1 | 27 / 5 |
| 日文竖排完全正确 | 3/3 | 1/3（行尾单字列被拆成独立段落） |
| 内存峰值 | 1156 MB | 1167 MB（检测模型小，但峰值来自激活，基本不变） |

**建议：本 PR 不切换，默认仍是 det small。** 两者 CER 相同、都满足 Linux 耗时目标；tiny 快约 1.6 倍，
但段落切分明显更差（完全正确 −4 张、误合并 +6、误拆分 +4，竖排行尾单字列会被拆成「。」「る」独立成段），
而段落切分直接决定译文质量（见下节）；tiny 也解决不了内存超标。

**切换作为可选项交负责人拍板**，触发条件建议：Windows 笔记本补测 720p P95 > 800 ms 或 1080p P95 > 1500 ms。切换只需改一行并多下载一个模型：

```diff
 // engine/ocr_model_manifest.json
-    "det": "PP-OCRv6_det_small",
+    "det": "PP-OCRv6_det_tiny",
```

```bash
python scripts/download_ocr_models.py download --model PP-OCRv6_det_tiny
```

**Windows 推测（未在笔记本实测）**：CI 的 Windows runner（4 vCPU）跑快速子集时，det small 比同批 Linux runner 慢约 2 倍，
720p P95 已到 860 ms（略超 800 ms），1080p 单张 1365 ms（见 [CI 环境数据](#ci-环境数据)）。据此推测普通 4 核笔记本上
small 的 720p / 1080p P95 **处于目标边缘甚至超标**，插电/电池模式、杀毒软件会放大波动；tiny 在 Linux 上快约 1.6 倍，
预计能把 Windows 拉回目标内。small 是否守得住必须以笔记本实测为准——这正是建议的切换触发条件。

## 行距阈值（line_gap）与 UI 小字误合并

### 误合并对译文的影响（zh→en，`opus-mt-zh-en`，Linux 实测）

| 样例 | 识别成一段（当前） | 期望的分段 |
|---|---|---|
| 设置页侧栏（`zh_ui_1080_01`） | 设置常规翻译框选翻译快捷键模型关于 → *Sets the normal translation box selection translation shortcut model for* | 设置 → *Settings*，常规 → *General*，翻译 → *Translation*，框选翻译 → *Box Translation*，快捷键 → *Shortcut Keys*，模型 → *Model*，关于 → *About* |
| 设置项（`zh_ui_small_02`） | 关闭窗口时最小化到托盘检查更新（每周一次）显示翻译耗时 → *Minimize window closing to tray check updates (once a week) to show translation time* | *Minimize to Tray when Close Window* / *Inspection updates (on a weekly basis)* / *Show translation time* |
| 菜单 + 状态（`zh_ui_small_01`） | … 格式 工具帮助自动保存已开启 … → *File Edit View Insert Format Help AutoSave Started …*（「工具」丢了） | *File Edit View Insert Format Tool Help* / *Autosave enabled, …* |
| 正文两段（`mixed_720_01`） | 两段并成一段 → 译文与分开翻译几乎相同 | — |

结论：**UI 小字被误合并会明显破坏译文**（没有句末标点，分句器无法补救，模型把几条短标签当成一句话硬译，还会丢词）；
**正文段落被误合并基本不影响译文**（每句有句末标点，分句后逐句翻译），只影响浮窗里的分段显示。

### line_gap 扫描（det small，只重跑段落合并）

| line_gap | 完全正确 | 误合并 | 误拆分 | UI 误合并 | 正文 误合并 |
|---|---:|---:|---:|---:|---:|
| 0.5 | 25/32 | 15 | 1 | 12 | 0 |
| 0.6 | 25/32 | 15 | 1 | 12 | 0 |
| 0.7 | 23/32 | 18 | 1 | 13 | 2 |
| 0.8 | 21/32 | 20 | 1 | 13 | 4 |
| **0.9（默认）** | 21/32 | 21 | 1 | 13 | 5 |
| 1.0 | 20/32 | 25 | 1 | 15 | 7 |
| 1.2 | 20/32 | 26 | 1 | 15 | 8 |

同段相邻行的实测间距（行框间距 / 行高）最大 0.62（det small）/ 0.52（det tiny），出现在 720p 中文正文；
段间距在 0.9 左右的样例会被默认值合并。UI 行与行的间距和正文行距相同（这是样例刻意设计的，也是真实界面的常态），
**任何 line_gap 都分不开它们**：0.9 → 0.6 时 UI 误合并只从 13 降到 12。

### 结论与建议

1. **本 PR 不改 line_gap。** 调到 0.6 在本样例集上最好（完全正确 21 → 25），但减少的全是正文误合并——对译文几乎无影响；
   而 0.6 已低于实测的同段行距上限 0.62，行高更松（line-height ≥ 1.8）的真实网页会被误拆分。若一定要调，**0.7 是有余量的折中**（21 → 23，无新增误拆分）。
2. **解决 UI 误合并要靠新规则，不是行距。** 原型（未合入）：「相邻两行都短于 N 个行高、且上一行不以 `，、,;：(` 等续接标点结尾 → 另起一段」。
   在同一批识别行上：N = 8–12 时 UI 误合并 13 → 5、误拆分不变；配合 line_gap 0.7，完全正确 21 → 24。
   该规则是在本样例集上调的，有过拟合风险，**建议另开 issue**（改 `suiyi_engine.ocr.layout`，补真实截图样例后再定 N）。

## #54 内存实验（历史，已由 #74 取代）

旧口径（进程峰值 − 加载前基线 ≤ 300 MB）在**所有**配置下都没达到。原因（Linux 实测）：

- 峰值来自 onnxruntime 的中间激活，不是模型权重（加载后只增 ~123 MB）。
- **RapidOCR 3.9 在 `limit_type=max` 时忽略 `limit_side_len`**：按原图长边选 960 / 1500 / 2000，所以 ≤ 2000 px 的截图
  检测时从不缩小。#51 设的 `Det.limit_side_len=960` 实际不起作用，720p / 1080p 都按原尺寸检测。
- 识别按批（默认 6 行）补齐到批内最宽的行，长行多的截图激活很大。
- 不同尺寸的输入会让 onnxruntime / glibc 留下更多内存：单张 1080p 峰值约 +600–740 MB，32 张跑三轮后峰值 +1.0 GB、常驻 +519 MB。

> 下表是 #54 时的实验实现（缩整张图、无缩放补偿、旧内存口径），与现在同名的 `--det` 写法含义不同，只作历史对照。

实验开关（只在评测子进程里生效，不改引擎默认；`--det` 变体写法）：`@N` 检测前把长边缩到 N，`+rb1` 识别批大小 1，
`+nomp` 关闭 onnxruntime memory pattern。全部 32 张、repeats 5、4 核：

| 变体 | 峰值增量 | 1080p P95 | 720p P95 | CER | 段落完全正确（0.9 / 0.6） |
|---|---:|---:|---:|---:|---:|
| small（默认） | 1156 MB | 1475 ms | 610 ms | 0.6% | 21 / 25 |
| small+rb1 | 959 MB | 1373 ms | 548 ms | 0.5% | 19 / 23 |
| small+rb1+nomp | 865 MB | 1535 ms | 579 ms | 0.5% | 19 / 23 |
| small@1280+rb1+nomp | 539 MB | 791 ms | 564 ms | 0.5% | 19 / 22 |
| small@960+rb1+nomp | 398 MB | 804 ms | 697 ms | 0.4% | 18 / 20 |
| tiny@960+rb1+nomp | 393 MB | 845 ms | 293 ms | 0.4% | 19 / 21 |

三轮复跑（同一进程、32 张 × 3）：`small@960+rb1+nomp` 峰值 +431 MB、常驻 +250–340 MB；默认配置峰值 +1027 MB、常驻 +519 MB。
每次识别后调用 `malloc_trim(0)` 只能压低常驻（+519 → +197–407 MB），压不住峰值。

**建议另开 issue 做内存调优**：检测长边上限 1280 + 识别批 1 能把峰值压到约一半、1080p 耗时减半，CER 不变，
但缩小检测图会让行框变松、段落误合并增加，需要和上面的段落规则一起调；300 MB 目标本身也建议复核
（**已由 #74 完成**：目标改为单请求峰值增量 ≤ 400 MB，见 [#74 调优](#74-内存与耗时调优)）
（区分峰值与常驻，并考虑 OCR 空闲一段时间后卸载模型）。

## 已知限制

- **日文竖排：可用，但标点丢失**。CER 8.9%（#74 调优后；调优前 11.4%），段落顺序（从右到左）3/3 正确。
  错误集中在：句读点「、。」基本全丢；拗音/促音小字（ゃ、っ）偶尔丢；换列后只剩一两个字的列会认错（如「°」「N°」）或被 tiny 拆成独立段落。
  对翻译影响有限（丢的主要是标点），但不适合需要逐字准确的场景。
- 样例是渲染图，CER 偏乐观；真实截图（ClearType、缩放模糊、图标混排）待补。
- UI 小字误合并：#75 后 dev UI 误合并 13 → 5、留出集 36 → 2；剩余的是整行菜单栏 + 状态栏（见[待办](#待办)）。
- #74 后单请求峰值增量 ≤ 400 MB（Linux 实测最大 333 MB）；常驻增长受 glibc 影响有波动，未做模型空闲卸载。
- 缩放补偿（`box_shrink`、单字列规则）是在本样例集（渲染图）上验证的，真实截图待补后复核。
- #74 后段尾短行偶尔被拆出（`zh_twocol_720_01`「对。」、`en_twocol_1080_01`：误拆分 1 → 3）：短行的框相对偏高，字号比越过 1.35。
- 英文代码里 `return ""` 这类「单词 + 引号」会被检测切成两个框，引号单独识别出错（英文代码 CER 0.0% → 0.3%）。
- 耗时是 onnxruntime 4 线程、单请求串行的数字；与翻译并发时会互相抢核。

## 待办

- **缩放补偿参数待真实截图复核**（#74 遗留）：`box_shrink`、`crop_pad`、单字列规则，以及 #75 的 `short_row` / `tail_ratio`，
  都只在 CC0 自制渲染图上验证过。补真实截图样例（需逐张确认许可）后重跑 `scripts/eval_ocr.py` 复核。
- **常驻内存波动**（#74 遗留）：跑完全部样例后的常驻增长在不同运行之间差 100 MB 以上（106–270 MB），来自 glibc / onnxruntime 的内存保留，只作参考、不设目标。
- **OCR 空闲卸载**：留到 M4。
- **Windows 720p P95 处于边缘**（CI runner 810 ms）：等用户笔记本实测数据再定，暂不改参数。
- **阅读顺序**：左下角状态栏会被排进左侧栏那一列（dev 1 张、留出集 2 张），XY 切分需要识别「贯穿底部的状态栏」。
- **整行菜单栏 + 状态栏**：两行都比 12 × 字号长，短行规则分不开（dev 3 张小图）。
- **带 AMX 的 CPU / 虚拟机上的数值正确性**（#78 发现，不单开 Issue）：在评测机（Xeon，KVM 虚拟机）上，CTranslate2 的 int8 推理走 MKL 的 AMX
  路径时，多线程或有其他进程争用 CPU 时会算出乱码。设 `MKL_ENABLE_INSTRUCTIONS=AVX512_E1` 关掉 AMX 后恢复正常（已测，见
  [专业领域评测](专业领域评测.md#部署注意事项)）。OCR 走 onnxruntime 的 fp32 模型，一般用不到 AMX 的 int8 / bf16 路径，
  而且 #74 / #75 的 CER 在多次运行之间保持一致，推测不受影响。待办：在带 AMX 的机器上部署前，用 `scripts/eval_ocr.py --quick`
  连跑两次并比对 CER，确认结果一致。

## CI 环境数据

CI 在 `engine（ubuntu-latest / windows-latest）` 的「OCR 评测快速子集」步骤里跑 `--quick`（6 张、repeats 2，
wheel 自带的 det small + 一个调优变体），报告写进该 job 的 Summary 并上传为 `ocr-eval-<os>` 构件。
**这些耗时是 GitHub 托管 runner 的数据，只用于确认脚本在 Windows / Linux 上可用，不作为性能基线。**
CI 不下载 det tiny，完整对比需要本地跑。

PR #73 首次运行（2026-09-25，run 36147474920）的快速子集（6 张：400×150 ×3、720p ×2、1080p ×1；每张预热 1 次、计时 2 次，
n 很小，P95 基本等于最大值）：

| 项 | windows-latest | ubuntu-latest |
|---|---|---|
| CPU / 核数 / 内存 | Intel Xeon Platinum 8573C / 4 / 16 GB | AMD EPYC 9V45 / 4 / 15.6 GB |
| 系统 / Python | Windows Server（10.0.26100）/ 3.11.9 | Linux 6.17（Azure）/ 3.11.16 |

| 指标 | 目标 | Windows small | Windows small@960+rb1+nomp | Linux small | Linux small@960+rb1+nomp |
|---|---|---:|---:|---:|---:|
| P95 400×150 | ≤ 800 ms | 314 ms | 402 ms | 142 ms | 109 ms |
| P95 1280×720 | ≤ 800 ms | **860 ms** | **826 ms** | 338 ms | 249 ms |
| P95 1920×1080 | ≤ 1500 ms | 1365 ms | 822 ms | 523 ms | 254 ms |
| 冷加载 | — | 719 ms | 712 ms | 387 ms | 377 ms |
| 峰值 RSS 增量 | ≤ 300 MB | 467 MB | 167 MB | 551 MB | 228 MB |
| CER（6 张合计） | — | 与 Linux 相同 | 与 Linux 相同 | — | — |

- 脚本在 Windows 上一条命令跑通，中文输出与 UTF-8 报告正常，`peak_wset` 正常取到。
- Windows runner 比同批 Linux runner 慢约 2 倍（CPU 型号不同，不能全归因于系统），**720p P95 已略超 800 ms**：
  这是「small 在 Windows 上耗时处于目标边缘」的第一个旁证，但样本太少、机器是共享虚拟机，仍以笔记本实测为准。
- 调优变体在 Windows 上同样把 1080p 耗时和峰值内存压低（1365 → 822 ms，467 → 167 MB）。


**#74（PR #76，2026-09-25，run 36155753738）**：CI 快速子集改为 `--det small,small+legacy`（当前默认 vs #74 之前），同样 6 张 × 2 次：

| 指标 | 目标 | Windows 默认 | Windows +legacy | Linux 默认 | Linux +legacy |
|---|---|---:|---:|---:|---:|
| CPU | — | AMD EPYC 9V74 / 4 | 同左 | AMD EPYC 7763 / 4 | 同左 |
| P95 400×150 | ≤ 800 ms | 343 ms | 355 ms | 272 ms | 277 ms |
| P95 1280×720 | ≤ 800 ms | **810 ms** | **912 ms** | 549 ms | 737 ms |
| P95 1920×1080 | ≤ 1500 ms | 831 ms | 1136 ms | 551 ms | 940 ms |
| 单请求峰值增量（最大） | ≤ 400 MB | 87 MB | 377 MB | 115 MB | 424 MB |
| 峰值测法 | — | psutil 采样 + `peak_wset` | 同左 | `VmHWM`（精确） | 同左 |

- 快速子集的 6 张比完整集轻（1080p 只有 1 张且不是密集文字），峰值明显低于完整集，只能看相对变化：两个平台都是默认参数把峰值压到 legacy 的约 1/4、1080p 耗时降 27–41%。
- Windows runner 720p P95 仍略超 800 ms（912 → 810 ms，2 张 × 2 次，共享虚拟机）：**推测**普通 4 核 Windows 笔记本上 720p 处于目标边缘，需要笔记本实测确认；
  若超标，可选项是把 `det_max_side` 降到 960（Linux 实测 720p P95 486 → 424 ms，但段落完全正确 23 → 20/32）或换 det tiny。

## Windows 笔记本补测

请在要验收的笔记本上（插电、关闭其他大程序）按下面做，然后把结果贴到 Issue #74（已关闭时新开 issue 引用 #74）。

1. 安装 Python 3.11 或更新版本（python.org 安装版，勾选 *Add python.exe to PATH*），并装好 Git。
2. 打开 PowerShell，拉代码到本地（已有仓库就 `git pull`）：

   ```powershell
   git clone https://github.com/xubin0903/suiyi.git
   cd suiyi
   ```

3. 一条命令完成：建 `.venv-ocr`、安装 `engine[ocr,eval]`、下载推荐 OCR 模型与 det tiny（约 35 MB，写入 `models\ocr\`）、跑完整评测：

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\eval_ocr.ps1 -Setup -Label "机型 / CPU / 内存 / 插电"
   ```

   以后再跑不用 `-Setup`。大约 5–10 分钟。想先试一下脚本可用，加 `--quick`（约 1 分钟）。
   脚本已设置 `PYTHONUTF8=1`；如果自己直接调用 `python scripts\eval_ocr.py`，请先执行 `$env:PYTHONUTF8 = "1"`。

4. 最后两行会打印报告路径，例如 `reports\ocr-eval-20260926-101500\report.md`。把 **`report.md` 全文**贴到该 Issue 的评论里，
   `report.json` 作为附件上传（里面有每张样例的识别行，可以复算段落切分）。

想同时对比 #74 之前的行为（`+legacy`）与 det tiny：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\eval_ocr.ps1 --det "small,small+legacy,tiny"
```

### Windows 数据（待补）

| 项 | 值 |
|---|---|
| 机型 / CPU / 内存 | |
| 系统 / Python / onnxruntime | |
| 电源 | 插电 / 电池 |

| 指标 | 目标 | det small | det tiny |
|---|---|---:|---:|
| P95 400×150 | ≤ 800 ms | | |
| P95 1280×720 | ≤ 800 ms | | |
| P95 1920×1080 | ≤ 1500 ms | | |
| P95 1080p 密集文字 | ≤ 1500 ms | | |
| CER 中文网页正文 / 英文文档 / 日文横排 | ≤ 5% / 5% / 10% | | |
| 单请求峰值增量（最大） | ≤ 400 MB | | |
| 常驻增长 | 另报 | | |
| 段落完全正确 | — | | |
