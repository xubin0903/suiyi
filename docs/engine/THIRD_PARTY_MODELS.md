# 第三方翻译模型署名（草稿）

随安装包或「关于」页分发。权重不打进本仓库，按 [engine/model_manifest.json](../../engine/model_manifest.json) 按需下载。转成 CTranslate2 int8 的文件是对原权重的改编，改编方为随译项目，不表示 Helsinki-NLP 认可量化结果。

核对日：2026-09-25。许可证原文：<https://creativecommons.org/licenses/by/4.0/legalcode>。

## 总述

随译的机器翻译使用赫尔辛基大学语言技术研究组（Helsinki-NLP）的 OPUS-MT 模型。预训练权重采用知识共享署名 4.0 国际许可协议（CC BY 4.0）。

请引用：

Tiedemann, Jörg, and Santhosh Thottingal. 2020. OPUS-MT — Building open translation services for the World. Proceedings of the 22nd Annual Conference of the European Association for Machine Translation (EAMT).

使用 Tatoeba-MT 发布包（中→日、英→日两颗）时同时引用：

Tiedemann, Jörg. 2020. The Tatoeba Translation Challenge — Realistic Data Sets for Low Resource and Multilingual MT. Proceedings of the Fifth Conference on Machine Translation (WMT).

## MVP 模型

下列修订号或发布包 sha256 与 manifest 中 `tier: "mvp"` 的条目一致。

### opus-mt-zh-en（中→英）

Helsinki-NLP OPUS-MT `opus-mt-zh-en`，修订 `cf109095479db38d6df799875e34039d4938aaa6`。CC BY 4.0。https://creativecommons.org/licenses/by/4.0/

### opus-mt-en-zh（英→中）

Helsinki-NLP OPUS-MT `opus-mt-en-zh`，修订 `408d9bc410a388e1d9aef112a2daba955b945255`。CC BY 4.0。https://creativecommons.org/licenses/by/4.0/

推理时源文本前加 `>>cmn_Hans<<`（简体）。这是调用方式，不是额外许可证。

### opus-mt-zho-jpn-tc-big-2022-07-28（中→日）

Helsinki-NLP Tatoeba-MT `zho-jpn` 发布包 `opusTCv20210807-sepvoc_transformer-big_2022-07-28.zip`，sha256 `bee6b96f9ab1abe50808374d926c2211798a2437ed684d700fc37f5da4d7d1da`。来源：https://object.pouta.csc.fi/Tatoeba-MT-models/zho-jpn/opusTCv20210807-sepvoc_transformer-big_2022-07-28.zip （说明：https://github.com/Helsinki-NLP/Tatoeba-Challenge/blob/master/models/zho-jpn/README.md ）。CC BY 4.0，依据是发布包内的 `LICENSE`（CC BY 4.0 全文），随模型目录一并分发。https://creativecommons.org/licenses/by/4.0/

### opus-mt-ja-en（日→英，亦用于日→中的第一跳）

Helsinki-NLP OPUS-MT `opus-mt-ja-en`，修订 `0770961a39ba6bd66305b149c3f4110bcafca2e6`。CC BY 4.0。https://creativecommons.org/licenses/by/4.0/

### opus-mt-eng-jpn-2021-02-18（英→日）

Helsinki-NLP Tatoeba-MT `eng-jpn` 发布包 `opus-2021-02-18.zip`，sha256 `921cbab703a5ed7b2df1c35b72c0e260aefca698ce2bd6c077833f8dd2af8fce`。来源：https://object.pouta.csc.fi/Tatoeba-MT-models/eng-jpn/opus-2021-02-18.zip （说明：https://github.com/Helsinki-NLP/Tatoeba-Challenge/blob/master/models/eng-jpn/README.md ）。CC BY 4.0，依据是发布包内的 `LICENSE`（CC BY 4.0 全文），随模型目录一并分发。https://creativecommons.org/licenses/by/4.0/

## 可直接粘贴的一段

```
机器翻译模型：Helsinki-NLP OPUS-MT（CC BY 4.0）。
https://creativecommons.org/licenses/by/4.0/
模型与修订（或发布包 sha256）见随译 model_manifest.json 的 tier=mvp 条目。
量化权重由随译自上述修订或发布包转换，Helsinki-NLP 未背书该转换。
请引用 Tiedemann & Thottingal, EAMT 2020；
中→日、英→日模型同时引用 Tiedemann, WMT 2020。
```

按需下载的 `optional` 模型使用同一许可和同一段署名，上架时把具体仓库名和修订补进清单即可。NLLB、SeamlessM4T 等 CC BY-NC 权重不在此列，默认不下载。
