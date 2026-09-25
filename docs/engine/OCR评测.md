# OCR 评测与性能基线（#54）

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
| `--det small,tiny` | 要对比的检测模型变体，第一个视为当前默认。写法 `检测模型[@长边上限][+rb<N>][+nomp]`，见[内存与调优实验](#内存与调优实验) |
| `--repeats 5 --warmup 1` | 每张样例计时次数 / 不计时预热次数 |
| `--quick` | 6 张样例、repeats=2，只验证脚本可用（CI 用） |
| `--only id1,id2` | 只跑指定样例 |
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
| 内存 | 子进程在导入 numpy / PIL / 引擎之后、加载 OCR 之前取基线 RSS；报告加载后增量与**进程峰值增量**（Linux `VmHWM`，Windows `peak_wset`） |

## 样例集

32 张，文字全部原创、PIL 渲染、CC0：400×150 12 张、1280×720 13 张、1920×1080 7 张；覆盖中文网页正文、
中/英/日 UI 小字（12px）、低对比/深色主题、英文文档、英文代码、日文横排、日文竖排（3 张）、中英混排、双栏、密集文字。
每张都标注了期望段落（UI 的每个菜单行、按钮、状态栏字段各自一段；代码每行一段）。PNG 合计约 1.5 MB，随仓库提交。

渲染图比真实截图干净（无 ClearType 彩边、无 JPEG 噪点、无图标混排），所以**这里的 CER 是乐观值**；
段落切分和耗时/内存不受此影响。真实截图样例留作后续补充（需要逐张确认许可）。

## Linux 实测（2026-09-25）

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

**切换作为可选项交负责人拍板**，触发条件建议：Windows 笔记本补测 1080p P95 > 1500 ms。切换只需改一行并多下载一个模型：

```diff
 // engine/ocr_model_manifest.json
-    "det": "PP-OCRv6_det_small",
+    "det": "PP-OCRv6_det_tiny",
```

```bash
python scripts/download_ocr_models.py download --model PP-OCRv6_det_tiny
```

**Windows 推测（未实测）**：同代 4 核笔记本的单核性能通常不低于这台虚拟机，onnxruntime 在 Windows 的 CPU 算子与 Linux 相同，
预计 small 的 1080p P95 在 1.2–2.0 s 之间，**处于目标边缘**；插电/电池模式、杀毒软件都会放大波动。
所以 small 是否守得住 1500 ms 必须以笔记本实测为准。

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

## 内存与调优实验

RSS 目标（增量 ≤ 300 MB）在**所有**配置下都没达到。原因（Linux 实测）：

- 峰值来自 onnxruntime 的中间激活，不是模型权重（加载后只增 ~123 MB）。
- **RapidOCR 3.9 在 `limit_type=max` 时忽略 `limit_side_len`**：按原图长边选 960 / 1500 / 2000，所以 ≤ 2000 px 的截图
  检测时从不缩小。#51 设的 `Det.limit_side_len=960` 实际不起作用，720p / 1080p 都按原尺寸检测。
- 识别按批（默认 6 行）补齐到批内最宽的行，长行多的截图激活很大。
- 不同尺寸的输入会让 onnxruntime / glibc 留下更多内存：单张 1080p 峰值约 +600–740 MB，32 张跑三轮后峰值 +1.0 GB、常驻 +519 MB。

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
（区分峰值与常驻，并考虑 OCR 空闲一段时间后卸载模型）。

## 已知限制

- **日文竖排：可用，但标点丢失**。CER 约 9–11%（det small 11.4%），段落顺序（从右到左）3/3 正确。
  错误集中在：句读点「、。」基本全丢；拗音/促音小字（ゃ、っ）偶尔丢；换列后只剩一两个字的列会认错（如「°」「N°」）或被 tiny 拆成独立段落。
  对翻译影响有限（丢的主要是标点），但不适合需要逐字准确的场景。
- 样例是渲染图，CER 偏乐观；真实截图（ClearType、缩放模糊、图标混排）待补。
- UI 小字误合并是当前段落切分的主要问题（见上节），会直接影响译文。
- 内存增量超过 300 MB 目标（见上节）。
- 耗时是 onnxruntime 4 线程、单请求串行的数字；与翻译并发时会互相抢核。

## CI 环境数据

CI 在 `engine（ubuntu-latest / windows-latest）` 的「OCR 评测快速子集」步骤里跑 `--quick`（6 张、repeats 2，
wheel 自带的 det small + 一个调优变体），报告写进该 job 的 Summary 并上传为 `ocr-eval-<os>` 构件。
**这些耗时是 GitHub 托管 runner 的数据，只用于确认脚本在 Windows / Linux 上可用，不作为性能基线。**
CI 不下载 det tiny，完整对比需要本地跑。

<!-- CI_DATA -->

## Windows 笔记本补测

请在要验收的笔记本上（插电、关闭其他大程序）按下面做，然后把结果贴回 Issue #54。

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

4. 最后两行会打印报告路径，例如 `reports\ocr-eval-20260926-101500\report.md`。把 **`report.md` 全文**贴到 Issue #54 的评论里，
   `report.json` 作为附件上传（里面有每张样例的识别行，可以复算段落切分）。

想同时测调优变体（内存实验）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\eval_ocr.ps1 --det "small,tiny,small@1280+rb1+nomp"
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
| CER 中文网页正文 / 英文文档 / 日文横排 | ≤ 5% / 5% / 10% | | |
| 峰值 RSS 增量 | ≤ 300 MB | | |
| 段落完全正确 | — | | |
