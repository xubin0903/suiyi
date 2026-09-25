# OCR 选型与许可证（#51）

框选翻译（M3）的 OCR 选型结论。核对日 2026-09-25。**已测** = 本文作者在下述环境里实际运行过；**推测** = 依据文档、模型卡或同类数据推断，尚未实测。

## 结论

| 项 | 选择 |
|---|---|
| 推理包 | `rapidocr`（3.x，onnxruntime 后端）+ `onnxruntime`。旧包 `rapidocr-onnxruntime`（最后版本 1.4.4，2025-01，Python < 3.13）不再使用 |
| 版本范围 | `rapidocr>=3.9.2,<3.10`、`onnxruntime>=1.20`（已测 rapidocr 3.9.2、onnxruntime 1.30.0） |
| 检测 det | `PP-OCRv6_det_small`，9.9 MB |
| 识别 rec | `PP-OCRv6_rec_small`，21.2 MB。**一个模型覆盖中（简/繁）/英/日**，不按语种切换模型，`lang` 参数可以忽略 |
| 方向分类 cls | 关闭（`use_cls=false`）。RapidOCR 3.9.2 仍要求 cls 文件存在，放 `ch_ppocr_mobile_v2.0_cls_mobile`，0.6 MB |
| 合计 | **31.7 MB**（≤ 60 MB） |
| 检测缩放 | 截图场景用 `Det.limit_type=max`、`Det.limit_side_len=960`（默认 `min`/736 会把小选区放大，见下文） |
| 许可证 | RapidOCR 代码、全部候选模型均为 **Apache-2.0**，可默认分发。没有「不可默认分发」的候选 |
| 离线 | det/cls/rec 各传本地 `model_path`，RapidOCR 不会进入下载分支；缺文件时我们先报缺失的模型 id，不联网 |
| 依赖 | 不含 torch / paddlepaddle / transformers（已测：Linux 本机，Windows x64 与 Linux 的 CI 干净 venv） |
| 放置 | `<models_dir>/ocr/`，见 [模型目录约定](模型目录约定.md#ocr-模型目录51) |

推荐组合与 rapidocr 3.9.2 的默认模型是同一组文件（wheel 里自带同样三个 ONNX，sha256 一致），上游默认、社区使用最多，升级路径清楚。

## 候选模型

所有文件取自 ModelScope `RapidAI/RapidOCR` 仓库的 `v3.9.2` 标签，URL 形如 `https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/<路径>`。sha256 已与 rapidocr 3.9.2 包内 `default_models.yaml` 逐个核对一致（已测）。完整 URL 见 [engine/ocr_model_manifest.json](../../engine/ocr_model_manifest.json)。

### 检测 det

| id | 体积 | sha256 | 覆盖 | 上游 / 许可证 |
|---|---:|---|---|---|
| **PP-OCRv6_det_small**（推荐） | 9,929,594 B | `090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f` | 多语言通用 | [PP-OCRv6_small_det](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det)，Apache-2.0 |
| PP-OCRv6_det_tiny | 1,829,618 B | `f42c0fbd294d95eac1a550e131b277dac97462c8025fa4b6c3cec1b7894bd3d5` | 多语言通用 | [PP-OCRv6_tiny_det](https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det)，Apache-2.0 |
| PP-OCRv6_det_medium | 62,119,454 B | `92078b7355007ccfffcd4c8cd441a3afd4538904d06881b29a155e1e679907c2` | 多语言通用 | [PP-OCRv6_medium_det](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det)，Apache-2.0 |
| ch_PP-OCRv5_det_mobile | 4,819,576 B | `4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae` | 中英日 | [PP-OCRv5_mobile_det](https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det)，Apache-2.0 |
| ch_PP-OCRv5_det_server | 88,118,768 B | `0f8846b1d4bba223a2a2f9d9b44022fbc22cc019051a602b41a7fda9667e4cad` | 中英日 | [PP-OCRv5_server_det](https://huggingface.co/PaddlePaddle/PP-OCRv5_server_det)，Apache-2.0 |
| en_PP-OCRv3_det_mobile | 2,421,707 B | `ea07c15d38ac40cd69da3c493444ec75b44ff23840553ff8ba102c1219ed39c2` | 英文 | [PaddleOCR 推理模型](https://paddleocr.bj.bcebos.com/PP-OCRv3/english/en_PP-OCRv3_det_infer.tar)，随 [PaddleOCR LICENSE](https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE) Apache-2.0 |

### 识别 rec

| id | 体积 | sha256 | 覆盖 | 上游 / 许可证 |
|---|---:|---|---|---|
| **PP-OCRv6_rec_small**（推荐） | 21,234,383 B | `6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884` | 简中、繁中、英、日及拉丁字母系共 50 种 | [PP-OCRv6_small_rec](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec)，Apache-2.0 |
| PP-OCRv6_rec_tiny | 4,489,813 B | `e16e242de5937ad92609223f19bc2aff3727ee40b095f996907c24749bad251b` | 同上但**不含日文** | [PP-OCRv6_tiny_rec](https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_rec)，Apache-2.0 |
| PP-OCRv6_rec_medium | 76,629,984 B | `eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b` | 同 small | [PP-OCRv6_medium_rec](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec)，Apache-2.0 |
| ch_PP-OCRv5_rec_mobile | 16,631,306 B | `5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5` | 简中、繁中、英、日、拼音 | [PP-OCRv5_mobile_rec](https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_rec)，Apache-2.0 |
| ch_PP-OCRv5_rec_server | 84,577,022 B | `e09385400eaaaef34ceff54aeb7c4f0f1fe014c27fa8b9905d4709b65746562a` | 同上 | [PP-OCRv5_server_rec](https://huggingface.co/PaddlePaddle/PP-OCRv5_server_rec)，Apache-2.0 |
| en_PP-OCRv5_rec_mobile | 7,872,351 B | `c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8` | 英文 | [en_PP-OCRv5_mobile_rec](https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec)，Apache-2.0 |
| en_PP-OCRv4_rec_mobile | 7,653,044 B | `e8770c967605983d1570cdf5352041dfb68fa0c21664f49f47b155abd3e0e318` | 英文 | [en_PP-OCRv4_mobile_rec](https://huggingface.co/PaddlePaddle/en_PP-OCRv4_mobile_rec)，Apache-2.0 |
| japan_PP-OCRv4_rec_mobile | 9,753,335 B | `e1075a67dba758ecfc7ebc78a10ae61c95ac8fb66a9c86fab5541e33f085cb7a` | 日文 | [PaddleOCR 推理模型](https://paddleocr.bj.bcebos.com/PP-OCRv4/multilingual/japan_PP-OCRv4_rec_infer.tar)，随 [PaddleOCR LICENSE](https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE) Apache-2.0（HF 上只有 v3 版的[模型卡](https://huggingface.co/PaddlePaddle/japan_PP-OCRv3_mobile_rec)，同为 Apache-2.0） |

### 方向分类 cls

| id | 体积 | sha256 | 上游 / 许可证 |
|---|---:|---|---|
| **ch_ppocr_mobile_v2.0_cls_mobile**（推荐放置，默认不启用） | 585,532 B | `e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c` | [PaddleOCR 推理模型](https://paddleocr.bj.bcebos.com/dygraph_v2.0/ch/ch_ppocr_mobile_v2.0_cls_infer.tar)，随 [PaddleOCR LICENSE](https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE) Apache-2.0 |
| ch_PP-LCNet_x0_25_textline_ori_cls_mobile | 1,018,508 B | `54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7` | [PP-LCNet_x0_25_textline_ori](https://huggingface.co/PaddlePaddle/PP-LCNet_x0_25_textline_ori)，Apache-2.0 |

这些 ONNX 由 RapidOCR 从 PaddleOCR 推理模型转换，并把字符字典写进了元数据（因此 rec 不需要额外的字典文件）。与 PaddlePaddle 在 Hugging Face 发布的 `PP-OCRv6_*_onnx` 不是同一字节（例如 `PP-OCRv6_small_rec_onnx/inference.onnx` 为 21,159,378 B、sha256 `5435fd74…`），属于格式转换，不影响许可证。

## 许可证

| 组件 | 许可证 | 出处 |
|---|---|---|
| RapidOCR（`rapidocr` 3.9.2） | Apache-2.0 | [GitHub LICENSE](https://github.com/RapidAI/RapidOCR/blob/main/LICENSE)；wheel 元数据 `License-Expression: Apache-2.0` |
| ModelScope 模型仓库 `RapidAI/RapidOCR` | Apache License 2.0 | [ModelScope 页面](https://www.modelscope.cn/models/RapidAI/RapidOCR)（API 字段 `License`） |
| PP-OCR 模型（PaddleOCR） | Apache-2.0 | 各 [PaddlePaddle 模型卡](https://huggingface.co/PaddlePaddle) YAML `license: apache-2.0`；无模型卡的按 [PaddleOCR LICENSE](https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE) |
| onnxruntime | MIT | [PyPI](https://pypi.org/project/onnxruntime/) |
| opencv-python / opencv-python-headless | Apache-2.0 | [PyPI](https://pypi.org/project/opencv-python/)（wheel 内另含 FFmpeg 等第三方组件，见其 LICENSE-3RD-PARTY） |
| numpy | BSD-3-Clause 等 | wheel 元数据 |
| pillow | MIT-CMU | wheel 元数据 |
| pyclipper | MIT | wheel 元数据 |
| shapely | BSD-3-Clause | wheel 元数据 |
| omegaconf / antlr4-python3-runtime | BSD | wheel 元数据 |
| PyYAML、six、colorlog | MIT | wheel 元数据 |
| requests、flatbuffers、packaging | Apache-2.0（packaging 为 Apache-2.0 OR BSD-2-Clause） | wheel 元数据 |
| protobuf | BSD-3-Clause | wheel 元数据 |
| tqdm | MPL-2.0 AND MIT | wheel 元数据。MPL-2.0 是文件级弱 copyleft，不修改 tqdm 源码时只需随附许可证 |

分发要求：随安装包附 Apache License 2.0 全文和 [THIRD_PARTY_MODELS.md](THIRD_PARTY_MODELS.md#ocr-模型框选翻译51) 里的来源说明。所有候选都是宽松许可，**没有需要标注「不可默认分发」的模型**。

## 安装与依赖（已测：Linux x86_64，Python 3.11.16，干净 venv）

```bash
python -m venv .venv-ocr && . .venv-ocr/bin/activate
pip install rapidocr onnxruntime
pip list
```

```
antlr4-python3-runtime 4.9.3    omegaconf       2.3.1     pyclipper  1.4.0
certifi                2026.7.22 onnxruntime    1.30.0    PyYAML     6.0.3
charset-normalizer     3.5.1    opencv-python   5.0.0.93  rapidocr   3.9.2
colorlog               6.12.0   packaging       26.3      requests   2.34.2
flatbuffers            25.12.19 pillow          12.3.0    shapely    2.1.2
idna                   3.20     pip             26.2.1    six        1.17.0
numpy                  2.4.6    protobuf        7.36.2    tqdm       4.70.1
                                                          urllib3    2.8.0
```

**不含 torch、paddlepaddle、paddleocr、transformers。** `rapidocr` 的必需依赖里没有推理框架；paddle / torch / openvino / tensorrt 后端都是运行时按需导入，不装就不会加载。

安装体积（site-packages，不含 pip）约 399 MB：

| 包 | 体积 |
|---|---:|
| opencv-python | 186.9 MB |
| numpy | 68.1 MB |
| onnxruntime | 65.0 MB |
| rapidocr（含自带的 3 个默认 ONNX，31 MB） | 31.6 MB |
| pillow | 20.2 MB |
| shapely | 11.7 MB |
| 其余 15 个 | 约 15 MB |

与引擎一起装（`pip install "engine[ocr]"`）是 39 个包，约 574 MB，其中 ctranslate2 占 133 MB。`python scripts/download_ocr_models.py audit` 会列出当前环境的包与体积，并在出现 torch / paddle / transformers 时返回非零。

**Windows x64（已测，CI windows-latest，Python 3.11，干净 venv 安装 `engine[ocr]`）**：CI 的 engine 矩阵新增「OCR 依赖审计」步骤，在 windows-latest 与 ubuntu-latest 上各跑一次。它打印 `pip list`，执行 `audit`、`check` 和禁网 `smoke`，模型用 rapidocr wheel 自带的三个 ONNX，不走网络。首次通过的结果（PR #65 的 CI）：

```
annotated-doc 0.0.5, annotated-types 0.8.0, antlr4-python3-runtime 4.9.3, anyio 4.15.1, certifi 2026.7.22,
charset-normalizer 3.5.1, click 8.5.0, colorama 0.4.6, colorlog 6.12.0, ctranslate2 4.8.2, fastapi 0.141.1,
flatbuffers 25.12.19, h11 0.16.0, idna 3.20, numpy 2.4.6, omegaconf 2.3.1, onnxruntime 1.30.0,
opencv-python 5.0.0.93, packaging 26.3, pillow 12.3.0, pip 26.2.1, protobuf 7.36.2, py3langid 0.4.0,
pyclipper 1.4.0, pydantic 2.13.5, pydantic_core 2.46.5, PyYAML 6.0.3, rapidocr 3.9.2, requests 2.34.2,
sentencepiece 0.2.2, setuptools 65.5.0, shapely 2.1.2, six 1.17.0, starlette 1.7.0, suiyi-engine 0.0.1,
tqdm 4.70.1, typing_extensions 4.16.0, typing-inspection 0.4.4, urllib3 2.8.0, uvicorn 0.54.0
合计 40 个包，376.1 MB（opencv-python 112.6 MB、numpy 53.5 MB、onnxruntime 44.3 MB、rapidocr 31.7 MB）
未发现 torch / paddlepaddle / paddleocr / transformers
正常  PP-OCRv6_det_small / ch_ppocr_mobile_v2.0_cls_mobile / PP-OCRv6_rec_small
模型目录：D:\a\_temp\ocr-models\ocr
加载 356 ms，识别 80 ms，1 行
  [1.00] Suiyi offline OCR 2026
```

所有依赖在 PyPI 上都有 cp311 `win_amd64` 或 `py3-none-any` wheel。注意 numpy 2.5 起要求 Python ≥ 3.12，Python 3.11 下 pip 会解析到 numpy 2.4.x。Windows runner 的管道默认 cp1252，脚本输出中文需要 `PYTHONUTF8=1`（CI 已设；交互式 PowerShell 控制台不受影响，推测）。

### opencv-python 能否换成 headless

`rapidocr` 3.9.2 声明依赖 `opencv_python`（带 GUI 的版本）。已测：先装 `opencv-python-headless` 与其余依赖，再 `pip install --no-deps rapidocr==3.9.2`，识别正常（`import cv2` 是同一个模块名）。pip 会提示依赖冲突，但不影响运行。Linux 上体积从 186.9 MB 降到 152.1 MB（`opencv_python.libs` 116 MB → 81 MB）。去掉 OpenCV 不可行：检测后处理、透视裁剪都用它。建议：开发期照常装；M4 打包时改用 headless + `--no-deps`，在打包脚本里固定依赖列表。

## 离线与关闭自动下载

RapidOCR 的加载逻辑（`rapidocr/inference_engine/onnxruntime/main.py`，已读源码并实测）：

- 某阶段给了 `model_path`：直接加载该文件，不联网。
- 没给 `model_path`：按 `ocr_version`/`lang_type`/`model_type` 在 `default_models.yaml` 里查 URL，在 `model_root_dir`（默认是包内 `models/`）下找同名文件，没有或 sha256 不符就**从 ModelScope 下载**。
- rec 模型的字符字典在 ONNX 元数据里，不会再去下载字典；只有非 ONNX 后端才下载字典。
- 字体只在画可视化结果时下载，我们不用。
- cls 无论 `use_cls` 是否开启都会构造，所以 cls 也要给本地路径，否则会走下载分支。

所以随译的做法：det/cls/rec 各给一个 `<models_dir>/ocr/` 下的 `model_path`；调用 RapidOCR 之前先按清单校验三个文件的字节数与 sha256，缺失或不符就抛出带模型 id 的错误（`OcrModelsMissingError`），不构造 RapidOCR。参数见 `suiyi_engine.tools.ocr_models.rapidocr_params`，#52 的 `OcrEngine` 沿用。

反例（已测）：在无网络的命名空间里，不给 `model_path`、只指定 `Rec.lang_type=japan`，RapidOCR 会打印 `Initiating download: https://www.modelscope.cn/...` 并抛 `DownloadFileException`。

### 验证：断网识别（已测）

用 `unshare -rn` 进入没有网卡（只有 down 的 `lo`）的网络命名空间，DNS 和任何连接都会失败；`smoke` 自身还在进程内把 `socket.connect` / `create_connection` / `getaddrinfo` 换成立即报错。两层都没有触发，说明识别过程没有任何网络请求。

```bash
$ unshare -rn sh -c 'ip -brief link; python scripts/download_ocr_models.py --models-dir /workspace/suiyi-models smoke samples/zh_web_02.png'
lo               DOWN           00:00:00:00:00:00 <LOOPBACK>
模型目录：/workspace/suiyi-models/ocr
  det: PP-OCRv6_det_small.onnx
  cls: ch_ppocr_mobile_v2.0_cls_mobile.onnx
  rec: PP-OCRv6_rec_small.onnx
加载 221 ms，识别 288 ms，4 行
  [1.00] 三步完成框选翻译
  [1.00] 第一步，按下快捷键进入框选模式，屏幕会稍微变暗。第二步，用鼠标拖出一个矩形，把想看的文字圈起来。第
  [1.00] 三步，松开鼠标，译文会出现在选区旁边的浮窗里。
  [1.00] 如果选区里没有文字，浮窗会提示未识别到内容，而不是显示一段空白。
```

缺模型时（同样断网）：

```
$ unshare -rn python scripts/download_ocr_models.py --models-dir /tmp/empty smoke x.png
缺少 OCR 模型：PP-OCRv6_det_small、ch_ppocr_mobile_v2.0_cls_mobile、PP-OCRv6_rec_small（目录 /tmp/empty/ocr）。请在开发机执行 python scripts/download_ocr_models.py 下载，运行时不会自动联网下载。
```

Windows 上没有 `unshare`，CI 的审计步骤依靠进程内禁网，并且模型取自 wheel 自带文件，不走网络。

## 下载

```bash
python scripts/download_ocr_models.py                 # 推荐组合 → <models_dir>/ocr/
python scripts/download_ocr_models.py --model PP-OCRv6_det_tiny
python scripts/download_ocr_models.py --all           # 全部 16 个候选，约 400 MB
python scripts/download_ocr_models.py check           # 只检查，不联网
```

```powershell
python scripts\download_ocr_models.py --models-dir C:\path\to\models
python scripts\download_ocr_models.py --models-dir C:\path\to\models check
```

`<models_dir>` 默认是 `SUIYI_MODELS_DIR`，否则为仓库根 `models/`。下载使用标准库，不需要安装 `ocr` 组；每个文件先写 `.<file>.partial`，字节数与 sha256 都对上才改名，否则删除并失败。完成后写 `suiyi-ocr.json`。实测推荐组合下载用时 12.8 s。

## 粗测

### 方法

- 样例：12 张自制截图（PIL 渲染原创文字，字体 Noto Sans/Serif CJK、DejaVu Sans Mono）：中文网页 2、中文 12 px UI 小字 1、深色低对比 1、英文文档 1、英文代码 1、日文横排 2、日文竖排 3、中英混排 1。它们是 #54 样例集的起点，不在本 PR 里提交，清单见 PR 说明。
- CER：去掉全部空白后逐字符的编辑距离 / 期望长度（所以英文词间空格的丢失不计入）。输出行按 RapidOCR 顺序拼接。竖排另算「列逆序」CER，即把输出行倒过来再比。
- 机器：Intel Xeon（8 vCPU）、15 GB 内存、Linux；onnxruntime `intra_op_num_threads=4`（对齐 4 核参考机）。每张图先预热 1 次，再取 3 次的中位数。**不是参考机**，Windows 数据留给 #54。
- 进程内禁网；每个组合单独一个进程，RSS 为 psutil 读数。

### 结果（已测）

截图调参（`Det.limit_type=max`、`limit_side_len=960`、`use_cls=false`）：

| 组合 | 中文 CER | 英文 CER | 日文横排 CER | 日文竖排 CER（列逆序后） | 单张中位 / 最大 | 加载 | RSS 加载增量 / 峰值增量 |
|---|---:|---:|---:|---:|---:|---:|---:|
| **v6 small det + v6 small rec（推荐）** | 0.0% | 0.0% | 0.4% | 79.0%（6.6%） | 139 / 349 ms | 223 ms | 83 / 408 MB |
| v6 tiny det + v6 small rec | 0.0% | 0.0% | 0.4% | 79.0%（6.6%） | 106 / 248 ms | 217 ms | 78 / 371 MB |
| v5 mobile det + v5 mobile rec | 0.8% | 0.0% | 0.0% | 81.8%（3.9%） | 149 / 363 ms | 221 ms | 70 / 361 MB |
| v6 tiny det + v6 tiny rec | 0.7% | 0.1% | 69.4% | 97.5%（67.3%） | 33 / 86 ms | 135 ms | 46 / 167 MB |
| v6 medium det + v6 medium rec | 0.0% | 1.3% | 0.4% | 81.8%（3.9%） | 4162 / 10036 ms | 489 ms | 234 / 663 MB |

RapidOCR 默认检测缩放（`limit_type=min`、736）：

| 组合 | 中文 CER | 英文 CER | 日文横排 CER | 日文竖排（列逆序后） | 单张中位 / 最大 |
|---|---:|---:|---:|---:|---:|
| v6 small，cls 关 | 0.0% | 0.8% | 0.4% | 80.5%（21.4%） | 873 / 999 ms |
| v6 small，cls 开（v2.0） | 0.0% | **12.2%** | 0.4% | 80.5%（21.4%） | 877 / 1003 ms |
| v5 server det + rec | 1.2% | 0.1% | 5.4% | 84.0%（45.0%） | 15788 / 25544 ms（加载 888 ms，峰值增量 1948 MB） |
| v5 mobile det + japan v4 rec | 29.1% | 4.8% | 0.0% | 77.6%（25.2%） | 654 / 741 ms |
| v5 mobile det + en v5 rec | 87.6% | 2.0% | 100% | 100% | 684 / 745 ms |
| v5 mobile det + en v4 rec | 86.5% | 1.9% | 100% | 100% | 829 / 972 ms |
| v6 medium det + rec | — | — | — | — | 7 分钟未跑完，中止 |

读法：

- v6 small 在中/英/日横排上都接近 0 错误，一个模型覆盖三种语言。v5 mobile 精度接近但小 4.6 MB，中文略差（一张网页正文 4.1%）。上游公布的指标里 v6 small 识别平均 81.3，v5 mobile 73.7；检测平均 84.1，v5 mobile 75.2（推测：我们的 12 张干净样例区分不出这个差距，#54 的难样例会更明显）。
- 单语模型（japan / en）只认自己的字符集，遇到其他语言整行错，不适合「框到什么都要认」的场景。
- v6 tiny rec 不支持日文（日文横排 CER 约 70%）。
- server / medium 在 CPU 上太慢，不考虑。
- **方向分类要关**：v2.0 cls 把英文文档里的一行判成 180° 翻转，CER 从 0.8% 升到 12.2%。屏幕截图基本不会有倒置文字。
- **检测缩放要改**：默认 `limit_type=min` 会把短边放大到 736 px，小选区反而最慢。改成只缩不放后单张中位从 873 ms 降到 139 ms，CER 不变（12 px UI 小字仍为 0%）。

### 固定尺寸区域（已测，推荐组合，4 线程，1 次预热 + 10 次）

区域是把样例拼到白底上的文字密集截图（1920×1080 共 35 行）。

| 区域 | 默认缩放 P50 / 最大 | 截图调参 P50 / 最大 | 调参 + tiny det P50 / 最大 |
|---|---:|---:|---:|
| 400×150 | 547 / 581 ms | 89 / 113 ms | 59 / 70 ms |
| 1280×720 | 882 / 926 ms | 876 / 912 ms | 648 / 718 ms |
| 1920×1080 | 1700 / 1870 ms | 1701 / 1728 ms | 1164 / 1318 ms |

大区域里 det 与 rec 各占一半左右（1080p：det 约 0.7 s、rec 约 0.9 s），调参只影响小区域。加大 `rec_batch_num` 到 16 反而更慢。峰值 RSS 增量约 530 MB（1080p 文字密集时）。

对照 #54 的目标（推测，需在参考机上确认）：1080p P95 ≤ 1500 ms、≤ 1280×720 P95 ≤ 800 ms、RSS 增量 ≤ 300 MB，在这台机器上推荐组合**都没达到**。换 `PP-OCRv6_det_tiny`（1.8 MB，上游检测平均 80.6 对 84.1，日文 76.6 对 82.3）能达到两个耗时目标，样例 CER 不变。先保留 small 为推荐，#54 在参考机上测完再决定是否切 tiny det；清单里已有 tiny det 条目，切换只改 `recommended.det`。

## 日文竖排

结论：**可识别但顺序错**（已测，3 张样例）。检测能把每一列框出来，识别能认出列内文字（个别漏字），但 RapidOCR 把列按从左到右输出，与竖排的从右到左相反；竖排标点（︑︒）大多丢失。列顺序倒过来后 CER 为 6.6%。

| 样例 | 期望 | 输出（按 RapidOCR 顺序） |
|---|---|---|
| ja_vertical_02 | 小さな町の本屋には、古い地図と新しい物語が並んでいる。 | `が並んでいる` / `古い地図と新しい物語` / `小さな町の本屋には` |
| ja_vertical_01 | 春の朝は空気がやわらかく／窓を開けると鳥の声が聞こえる | `える` / `窓を開けると鳥の声が聞こ` / `春の朝は空気がわらかく`（漏「や」） |

写入已知限制：M3 不专门处理竖排。#52 的段落合并如果发现框的高明显大于宽，可以按从右到左排列列，这只是排序，不需要换模型。（#52 已实现，见 [OCR 核心](OCR核心.md)。）

## 目录方案

二选一里选「OCR 单独子目录」：`<models_dir>/ocr/<file>.onnx` + `suiyi-ocr.json`。理由：

- 翻译模型目录的 `suiyi-model.json` 必须有 `src`/`tgt`，扫描时按语种对建索引；OCR 模型不是语种对，塞进去要么改翻译扫描逻辑，要么写假字段。
- 三个 ONNX 总是一起用，放一个目录便于校验、删除和 M4 按需下载。
- 翻译扫描只认含 `suiyi-model.json` 的子目录，`ocr/` 不会被误认。

## 建议的依赖声明

`engine/pyproject.toml` 新增可选组（本 PR 已加）：

```toml
ocr = [
  "rapidocr>=3.9.2,<3.10",
  "onnxruntime>=1.20",
]
```

M3 期间不进默认运行时：#52/#53 接入后，再按「框选翻译是否默认开启」决定。上限 `<3.10` 是因为 3.x 的默认模型与参数会随小版本变化（3.9.2 的默认已是 PP-OCRv6），升级前先重跑本文粗测，并核对清单 sha256。

## 备选（不接入）

- PaddleOCR 原生：需要 paddlepaddle 运行时（数百 MB），违反运行时约定。
- Tesseract：Apache-2.0，但中日文精度明显低于 PP-OCR，需要另装原生程序与语言包。
- Windows.Media.Ocr：系统自带、零体积，但依赖用户安装的系统语言包，日文/中文可用性不可控，且只能在 Windows 上测，保留为 M4 以后的可选后端。

## 已知限制与遗留

- 粗测样例是干净的合成截图，CER 偏乐观；真实网页、低分辨率、抗锯齿差的截图留给 #54。
- 1080p 文字密集区域在本机约 1.7 s，峰值 RSS 增量约 530 MB，可能达不到 #54 目标；对策见上（tiny det、限制选区尺寸）。
- 日文竖排顺序错，见上。
- `rapidocr` 依赖 `requests`/`tqdm`（用于它自己的下载功能），我们不用但无法去掉。
- Windows 上的耗时与内存未测（推测与 Linux 同量级），#54 在参考机上补。
