# 第三方模型署名（草稿）

随安装包或「关于」页分发。权重不打进本仓库，按 [engine/model_manifest.json](../../engine/model_manifest.json) 按需下载。转成 CTranslate2 int8 的文件是对原权重的改编，改编方为随译项目，不表示 Helsinki-NLP 认可量化结果。

核对日：2026-09-25（en→zh 于 2026-09-26 按 #83 更新；`opus-mt-en-zh` 于 2026-10-11 按 #89 改为默认随包 bundled；en→ja 于 2026-10-11 按 #98 换成 FuguMT，CC BY-SA 4.0）。许可证原文：<https://creativecommons.org/licenses/by/4.0/legalcode>（OPUS-MT / Tatoeba-MT）、<https://creativecommons.org/licenses/by-sa/4.0/legalcode>（FuguMT）。

## 总述

随译的机器翻译主要使用赫尔辛基大学语言技术研究组（Helsinki-NLP）的 OPUS-MT / Tatoeba-MT 模型（预训练权重 CC BY 4.0）。英→日默认另用 FuguMT（CC BY-SA 4.0），见下方专节。仓库代码仍是 MIT：权重按各自许可证分发，SA 只约束该模型权重及其改编（如 int8），不要求把整仓改成 SA。

请引用：

Tiedemann, Jörg, and Santhosh Thottingal. 2020. OPUS-MT — Building open translation services for the World. Proceedings of the 22nd Annual Conference of the European Association for Machine Translation (EAMT).

使用 Tatoeba-MT 发布包（英→中、中→日等）时同时引用：

Tiedemann, Jörg. 2020. The Tatoeba Translation Challenge — Realistic Data Sets for Low Resource and Multilingual MT. Proceedings of the Fifth Conference on Machine Translation (WMT).

## MVP 模型

下列修订号或发布包 sha256 与 manifest 中 `tier: "mvp"` 的条目一致。其中 `fugumt-en-ja` 为 CC BY-SA 4.0，其余 OPUS/Tatoeba 条目为 CC BY 4.0。

### opus-mt-zh-en（中→英）

Helsinki-NLP OPUS-MT `opus-mt-zh-en`，修订 `cf109095479db38d6df799875e34039d4938aaa6`。CC BY 4.0。https://creativecommons.org/licenses/by/4.0/

### opus-mt-eng-zho-tc-big-2022-05-14（英→中，#83 起）

Helsinki-NLP Tatoeba-MT `eng-zho` 发布包 `opusTCv20210807+bt_transformer-big_2022-05-14.zip`，sha256 `9bfe340595d7c80fe869a2fe3dd4d03e2d9751514ff8c35724be464819b36a8e`。来源：https://object.pouta.csc.fi/Tatoeba-MT-models/eng-zho/opusTCv20210807+bt_transformer-big_2022-05-14.zip （说明：https://github.com/Helsinki-NLP/Tatoeba-Challenge/blob/master/models/eng-zho/README.md ）。CC BY 4.0，依据是发布包内的 `LICENSE`（CC BY 4.0 全文），随模型目录一并分发。https://creativecommons.org/licenses/by/4.0/

推理时源文本前加 `>>cmn_Hans<<`（简体）。这是调用方式，不是额外许可证。

### opus-mt-zho-jpn-tc-big-2022-07-28（中→日）

Helsinki-NLP Tatoeba-MT `zho-jpn` 发布包 `opusTCv20210807-sepvoc_transformer-big_2022-07-28.zip`，sha256 `bee6b96f9ab1abe50808374d926c2211798a2437ed684d700fc37f5da4d7d1da`。来源：https://object.pouta.csc.fi/Tatoeba-MT-models/zho-jpn/opusTCv20210807-sepvoc_transformer-big_2022-07-28.zip （说明：https://github.com/Helsinki-NLP/Tatoeba-Challenge/blob/master/models/zho-jpn/README.md ）。CC BY 4.0，依据是发布包内的 `LICENSE`（CC BY 4.0 全文），随模型目录一并分发。https://creativecommons.org/licenses/by/4.0/

### opus-mt-ja-en（日→英，亦用于日→中的第一跳）

Helsinki-NLP OPUS-MT `opus-mt-ja-en`，修订 `0770961a39ba6bd66305b149c3f4110bcafca2e6`。CC BY 4.0。https://creativecommons.org/licenses/by/4.0/

### fugumt-en-ja（英→日，#98 起）

[staka/fugumt-en-ja](https://huggingface.co/staka/fugumt-en-ja)，修订 `fbb91252110cf97d90291eb808629ab0b10f7928`。作者 s-taka；说明见 https://github.com/s-taka/fugumt 。**CC BY-SA 4.0**（模型卡 YAML `license: cc-by-sa-4.0`）。https://creativecommons.org/licenses/by-sa/4.0/

随译把该权重转成 CTranslate2 int8，属于改编：改编后的权重须以 CC BY-SA 4.0 署名分发（与原模型相同）。这**不**要求把随译仓库代码从 MIT 改成 SA，也**不**把 SA 传染到其他仍为 CC BY 4.0 的 OPUS-MT 权重。

使用 Tatoeba-MT / OPUS-MT（英→中、中→日等）时继续引用 Tiedemann；使用本模型时请同时保留上方 FuguMT 署名。

## 随包附带（`tier: "bundled"`）

与 `mvp` 一并默认下载（`python scripts/convert_models.py --tier mvp` 会带上），不是该方向的主翻译模型。分发了就要同样署名。

### opus-mt-en-zh（英→中短句兜底，#89 / #122）

Helsinki-NLP OPUS-MT `opus-mt-en-zh`，修订 `408d9bc410a388e1d9aef112a2daba955b945255`。CC BY 4.0。https://creativecommons.org/licenses/by/4.0/

#83 前曾是默认 en→zh；现在 int8 约 84 MB，用作表外极短句按需兜底与空译文 / 乱码回退，不替代 `opus-mt-eng-zho-tc-big-2022-05-14`。

## 可直接粘贴的一段

```
机器翻译模型：Helsinki-NLP OPUS-MT / Tatoeba-MT（CC BY 4.0）；
英→日默认另用 FuguMT fugumt-en-ja（CC BY-SA 4.0，https://huggingface.co/staka/fugumt-en-ja ）。
https://creativecommons.org/licenses/by/4.0/
https://creativecommons.org/licenses/by-sa/4.0/
模型与修订见随译 model_manifest.json 的 tier=mvp 条目。
量化权重由随译自上述修订转换；上游未背书该转换。FuguMT 的 int8 改编按 CC BY-SA 4.0 分发。
请引用 Tiedemann & Thottingal, EAMT 2020；
英→中、中→日模型同时引用 Tiedemann, WMT 2020。
```

按需下载的 `optional` 模型使用同一许可和同一段署名，上架时把具体仓库名和修订补进清单即可。NLLB、SeamlessM4T 等 CC BY-NC 权重不在此列，默认不下载。

## OCR 模型（框选翻译，#51）

框选翻译用 [RapidOCR](https://github.com/RapidAI/RapidOCR)（Apache-2.0）加载百度飞桨 PaddleOCR 的 PP-OCR 系列 ONNX 模型。模型与代码都是 Apache License 2.0，可以随安装包分发，须附许可证全文，并保留下面的来源说明。选型、体积与实测见 [OCR 选型与许可证](OCR选型与许可证.md)；下载地址与 sha256 见 [engine/ocr_model_manifest.json](../../engine/ocr_model_manifest.json)。

核对日：2026-09-25。许可证原文：<https://www.apache.org/licenses/LICENSE-2.0>。

### 默认组合（manifest `recommended`）

| 文件 | 上游模型 | 许可证依据 | sha256 |
|------|----------|-----------|--------|
| `PP-OCRv6_det_small.onnx` | PaddlePaddle `PP-OCRv6_small_det` | 模型卡 `license: apache-2.0`：<https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det> | `090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f` |
| `PP-OCRv6_rec_small.onnx` | PaddlePaddle `PP-OCRv6_small_rec` | 模型卡 `license: apache-2.0`：<https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec> | `6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884` |
| `ch_ppocr_mobile_v2.0_cls_mobile.onnx` | PaddleOCR `ch_ppocr_mobile_v2.0_cls` | 无独立模型卡，随 PaddleOCR 仓库 LICENSE（Apache-2.0）：<https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE> | `e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c` |

ONNX 文件由 RapidOCR 项目从 PaddleOCR 推理模型转换（写入了字符字典元数据），取自 ModelScope `RapidAI/RapidOCR` 仓库 `v3.9.2` 标签（仓库许可证 Apache License 2.0）。它们与 PaddlePaddle 在 Hugging Face 发布的 `*_onnx` 文件不是同一字节，属于对原模型的格式转换；随译未再修改。

### 可直接粘贴的一段

```
文字识别（OCR）：RapidOCR（Apache-2.0，https://github.com/RapidAI/RapidOCR），
模型为百度飞桨 PaddleOCR 的 PP-OCRv6 small 检测/识别与 PP-OCR v2.0 方向分类（Apache-2.0，
https://github.com/PaddlePaddle/PaddleOCR），ONNX 格式由 RapidOCR 项目转换。
Licensed under the Apache License, Version 2.0: https://www.apache.org/licenses/LICENSE-2.0
```

清单里 `tier: "candidate"` 的其余 OCR 模型同为 Apache-2.0，只用于对比，默认不下载。

## 简繁转换数据（#106）

中文译文检查（见 [翻译核心 · 中文译文检查](翻译核心.md#中文译文检查乱码与繁体106)）用 OpenCC 的字级繁→简表把模型偶尔输出的繁体字转成简体。只取数据文件，不引入 OpenCC 程序或任何 Python 依赖。

| 文件（随包分发） | 上游 | 许可证 | sha256 |
|------|------|--------|--------|
| `engine/src/suiyi_engine/data/opencc_ts_characters.txt` | [BYVoid/OpenCC](https://github.com/BYVoid/OpenCC) `ver.1.1.9` 的 `data/dictionary/TSCharacters.txt`，未修改 | Apache-2.0 | `6b5a0a799bea2bb22c001f635eaa3fc2904310f0c08addbff275477a80ecf09a` |
| `engine/src/suiyi_engine/data/opencc_LICENSE.txt` | 同一标签的 `LICENSE`（Apache License 2.0 全文） | — | `b534e465949558eec2597b04f5092b5e161236a68dfbfd04d547592ac3964308` |

核对日：2026-09-27。随安装包分发时附上 `opencc_LICENSE.txt`，并保留下面的来源说明：

```
简繁转换数据：OpenCC（Open Chinese Convert）TSCharacters.txt，Copyright (c) BYVoid 及贡献者，
Licensed under the Apache License, Version 2.0: https://www.apache.org/licenses/LICENSE-2.0
https://github.com/BYVoid/OpenCC
```

