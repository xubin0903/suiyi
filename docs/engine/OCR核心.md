# OCR 核心（#52）

`suiyi_engine.ocr` 把一张截图变成「文本行 → 段落 → 全文」，供 #53 的 `/ocr`、`/ocr_translate` 使用。与 HTTP 无关，也可以在脚本里直接用。选型、模型与许可证见 [OCR 选型与许可证](OCR选型与许可证.md)。

```python
from pathlib import Path
from suiyi_engine.ocr import OcrEngine

engine = OcrEngine(Path("models"))        # <models_dir>/ocr/ 下放清单里的三个 ONNX
engine.warmup()                           # 可选：加载模型并跑一次小图
result = engine.recognize(png_bytes)      # bytes / 文件路径 / PIL 图片 / BGR ndarray
result.text                               # 段落以 "\n" 连接
result.to_dict()                          # {"lines", "paragraphs", "text", "image", "elapsed_ms"}
```

## 依赖与导入

- 需要 `pip install -e "engine[ocr]"`（rapidocr + onnxruntime，带入 numpy、opencv、Pillow）。不依赖 torch / paddle / transformers，CI 的 OCR 步骤在干净 venv 里审计。
- `import suiyi_engine.ocr` 不会导入 rapidocr、onnxruntime、cv2、PIL；第一次 `load()` / `recognize()` 才导入。没装 OCR 依赖时，翻译服务照常启动，调用 OCR 才报 `OcrError`（提示安装命令）。

## 模型加载与离线

- 模型目录与翻译模型共用 `SUIYI_MODELS_DIR`，OCR 文件放在 `<models_dir>/ocr/`（见 [模型目录约定](模型目录约定.md)）。清单默认读仓库里的 `engine/ocr_model_manifest.json`，也可以用 `OcrEngine(manifest=...)` 传入。**M4 打包时需要把清单随程序分发**（或改成包内数据），目前依赖可编辑安装的仓库布局。
- 加载前先按清单校验三个文件的字节数与 SHA256。缺失或损坏时在导入 rapidocr 之前抛 `OcrModelsMissingError`（继承 `OcrModelError`），消息里带模型 id 和目录，不会触发任何下载。
- det / cls / rec 三个阶段都显式给 `model_path`，RapidOCR 不会进入下载分支。
- 图片由 `decode_image` 自己解码成 BGR 数组再交给 RapidOCR。**从不把字符串交给 RapidOCR**：它会把 `http` 开头的字符串当 URL 去请求。`decode_image` 的字符串参数只当本地路径。
- 测试在 `forbid_network()`（进程内禁止 socket 连接）下完成加载与识别。

## 线程与性能

- 一个进程共用一个 `OcrEngine`。模型只加载一次，并发的首次调用会等同一次加载（双重检查锁）。
- 推理串行（一把锁）。onnxruntime 内部已经多线程（`intra_op_num_threads` 默认 `min(4, CPU 核数)`，可用 `threads=` 调整；`inter_op_num_threads=1`），并发推理不会更快，还会多占内存；串行也避开了 RapidOCR 前后处理里的共享状态。框选翻译一次一张图，排队的影响可以忽略。
- 检测参数沿用 #51：`limit_type=max`（只缩不放）、`use_cls=false`。
  #54 实测发现 RapidOCR 3.9 在 `limit_type=max` 时忽略 `limit_side_len`（按原图长边选 960/1500/2000），≤ 2000 px 的截图检测时不缩小。
- **运行参数（#74，`OcrRuntimeOptions`，默认 `DEFAULT_RUNTIME`）**：RapidOCR 没有「只缩小检测图」的参数（`Global.max_side_len` 会连识别裁切一起缩小），
  所以后端拆成两步，只用 RapidOCR 的公开对象、不改其源码：
  1. 长边 > `det_max_side`（1024）时用 `cv2.INTER_AREA` 缩图，交给 `RapidOCR(use_rec=False)` 只做检测；缩放时关闭 DB 后处理的 2×2 膨胀（`det_dilation=None`）。
  2. 框按比例换回原图坐标，**从原图裁切**（沿短边外扩 `crop_pad / scale` 像素，补回膨胀的余量），交给 `engine.text_rec` 识别，批大小 `rec_batch=1`。
  3. 返回给分段的行框沿短边每侧收回 `box_shrink × (1/scale − 1)` 像素（缩放补偿：DB 外扩在检测图上是常数像素，换回原图被放大，会吃掉段间距）。不缩放时为 0。
  - onnxruntime：`enable_cpu_mem_arena=False`；`enable_mem_pattern=False`（RapidOCR 不暴露，创建会话期间临时替换其会话选项构造函数）。
  - 取值依据与前后对比见 [OCR 评测 · #74 内存与耗时调优](OCR评测.md#74-内存与耗时调优)。`LEGACY_RUNTIME` 保留 #74 之前的行为，供评测对比（`--det small+legacy`）。
- `OcrResult.stats` 给出 `load_decode_ms` / `ocr_ms` / `layout_ms`。段落合并是纯 Python，几十行的截图在 1 ms 以内。

## 数据结构

坐标都是原图像素，原点在左上角。

| 类型 | 字段 | 说明 |
| --- | --- | --- |
| `OcrLine` | `text`、`box`、`score`、`low_confidence` | `box` 是四点框 `[[x, y] × 4]`，顺序同 RapidOCR（左上、右上、右下、左下）。竖排时一个 `OcrLine` 是一列 |
| `OcrParagraph` | `text`、`box`、`line_indices`、`vertical` | `box` 是外接矩形 `[x0, y0, x1, y1]`；`line_indices` 指向 `lines`，按段内阅读顺序 |
| `OcrResult` | `lines`、`paragraphs`、`width`、`height`、`elapsed_ms`、`text` | `paragraphs` 的顺序就是阅读顺序；`text` = 段落以 `\n` 连接 |

`to_dict()` 的字段名与 #53 草案 / 客户端 `OcrDraftContract.cs` 的 `lines`、`paragraphs`、`text`、`image`、`elapsed_ms` 一致；客户端把 `box` 当原始 JSON 接收，四点框与矩形都能接。额外的 `low_confidence`、`line_indices`、`vertical` 客户端会忽略。

## 置信度

- RapidOCR 默认丢掉置信度 < 0.5 的行（`Global.text_score`）。这里把它设成 0，让所有行都回到 `OcrEngine`，再按 `min_score`（默认 0.5）标记 `low_confidence`。
- 低置信度的行**保留在 `lines` 里**，只是不进入段落和 `text`。即使全部行都低于阈值，`lines` 仍然完整，调用方可以提示「识别不清」而不是当作空白。
- `result.low_confidence_count` 给出被排除的行数。

## 语言提示

`recognize(..., lang=)` 接受 `auto` / `zh` / `en` / `ja`，其他值抛 `OcrError`。当前是一个中英日共用的识别模型（PP-OCRv6 small），提示只做校验，不改变识别。以后换成分语种模型时再用上。

## 行 → 段落（`layout.py`）

纯 Python，单测全部用构造数据（`engine/tests/test_ocr_layout.py`）。长度阈值都以「字号」为单位：横排是行高，竖排是列宽。默认值在 `LayoutOptions` 里。

### 1. 横竖判定

框高 ≥ 宽 × 1.5 且文本至少 2 个字符，判为竖排列。单个字符的框接近正方形，无法判断，按横排处理；
例外（#74）：单字框紧接在某个竖排列正下方（列尾），或在某列左侧紧邻且与列顶对齐（下一列的列首），宽度不超过列宽 × 1.5，归入竖排。
缩小检测图后，换列只剩一个字的列常被单独切出，按横排处理会变成孤立段落。横排与竖排的行分开合并，最后一起排阅读顺序。

### 2. 流坐标

横排、竖排共用同一套合并逻辑。竖排的框先换到「流坐标」：原图 `(x, y)` → `(y, -x)`。于是「列从右到左」变成「行从上到下」，「列内从上到下」变成「行内从左到右」，合并完再换回原图坐标。所以竖排天然是**从右到左读列**。

### 3. 同行碎片

RapidOCR 有时把一行切成几段（UI 菜单、中英混排、竖排里的「、」处）。行方向重叠 ≥ 较小字号 × 0.6、沿文字方向间距 ≤ 字号 × 1.2 的碎片合成一行，按文字方向排序。

### 4. 段落归并

按从上到下处理每一行，在已有段落里找「上一行」与它横向有重叠、行距在 `[-0.5, 0.9] × 字号` 内、最近的一个。找到后还要同时满足：

| 条件 | 不满足时 |
| --- | --- |
| 字号比 ≤ 1.35（新行只有一个字时不比：单字框的尺寸随字形变化，#74） | 标题与正文，分段 |
| 行首比上一行多缩进 ≤ 1 × 字号 | 首行缩进，新段 |
| 行首与上一行对齐（容差 0.8 × 字号）；段落只有一行且新行更靠左时例外（上一行是缩进的首行） | 分段 |
| 上一行行尾不比段落右缘短 2 × 字号以上 | 上一行是段尾（或短标题），分段 |

竖排经流坐标后，这些规则对应：列距 ≤ 0.9 × 列宽（空一列就分段）、列宽相近、列顶对齐、上一列明显短就分段。

### 5. 拼接

- 中日文字符（含全角标点、假名）两侧不加空格，西文之间加一个空格：`按下` + `Ctrl+Alt+T` → `按下Ctrl+Alt+T`。
- 跨行时行尾是「字母-」：下一行小写字母开头就去掉连字符直接拼（`trans-` + `lation` → `translation`）；大写字母或数字开头保留连字符、不加空格（`Wi-` + `Fi` → `Wi-Fi`）。`segment` 的 OCR 断行合并对 `-\n` 一律去掉连字符；这里拼好的段落不含换行，不会再被 `segment` 处理一次。

### 6. 阅读顺序

对段落外接矩形做递归 XY 切分：

1. 先找没有任何段落跨过的**竖直空隙**（分栏）。有就按栏切开，从左到右；这一块里多数段落是竖排时从右到左。
2. 没有竖直空隙再找**水平空隙**，从上到下切开。
3. 两种都没有时按 `(y0, x0)` 排。

先竖后横，双栏里左右两栏的段间距恰好对齐时也不会交错；横贯两栏的标题挡住竖直空隙，会先被水平切出来，排在最前面。

## 已知限制

- 行距阈值 0.9 × 行高偏向合并：行高 2.0 的中文网页正文段内行距约 0.6~0.7 × 框高，阈值再小会把段落切碎（对翻译伤害更大）。代价是行距同样大的 UI 文本会被合进一段，例如样例 `zh_ui_small_01` 的菜单栏与下一行状态文字（间距 0.67 × 行高）。
- 代码截图按缩进切成多段，只保证文字正确、顺序正确，不保留缩进。
- 表格、键值对这类左右对齐的块，会被竖直切分成「整列左边，再整列右边」。
- 竖排里的标点（「、」「。」）识别率低，常被漏掉或把一列切开；列会合并回来，但标点可能缺失。
- 旋转文本、弧形文本、横竖混排的同一段落不处理。

## 测试

| 文件 | 内容 | CI |
| --- | --- | --- |
| `test_ocr_layout.py` | 合并规则：拼接、段落、缩进、标题、分栏、竖排、碎片、过滤 | 主测试步骤 |
| `test_ocr_engine.py` | 假后端：只加载一次、4 线程并发、低置信度、缺模型异常且不联网、导入不带重依赖、图片解码 | 主测试步骤（解码字节的用例需要 PIL，在 OCR 步骤里跑全） |
| `test_ocr_model.py` | 真实模型（`@pytest.mark.model`）：中/英/日横排相似度 ≥ 0.9 且段数正确、竖排从右到左、4 线程结果一致、加载与识别期间禁网 | OCR 步骤（Linux + Windows），用 rapidocr wheel 自带的同一批 ONNX；设 `SUIYI_OCR_TESTS_REQUIRED=1`，缺模型直接失败而不是跳过 |

样例图片见 `engine/tests/fixtures/ocr/`（自制，CC0，共约 200 KB）。本地跑真实模型测试：

```bash
python scripts/download_ocr_models.py download
SUIYI_MODELS_DIR=models pytest engine/tests/test_ocr_model.py
```
