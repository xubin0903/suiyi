# scripts/

开发与运维脚本，例如模型下载与 CTranslate2 转换、样例集校验、评测和性能基准。

脚本应能从仓库根目录运行，并在文档或 `--help` 里写清参数。需要真实模型的脚本必须把缓存目录指到仓库根 `models/`（该目录已被 git 忽略），不要把权重提交进仓库。

| 脚本 | 用途 |
|------|------|
| `scripts/convert_models.py` | 按清单把 OPUS-MT 下载并转为 CTranslate2。说明见 [模型目录约定](../docs/engine/模型目录约定.md)。 |
| `scripts/validate_samples.py` | 校验 `tests/samples/` 里的固定样例集。 |
| `scripts/eval_samples.py` | 对固定样例集跑 chrF / BLEU、专名保留率与延迟，写出报告。说明见 [评测](../docs/engine/评测.md)。 |
| `scripts/bench_service.py` | 经本机 HTTP 服务测量冷启动、延迟与内存。说明见 [性能基线](../docs/engine/性能基线.md)。 |
| `scripts/download_ocr_models.py` | 下载并校验 OCR 模型、检查本地模型、离线冒烟。说明见 [OCR 选型与许可证](../docs/engine/OCR选型与许可证.md)。 |
