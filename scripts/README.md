# scripts/

开发与运维脚本，例如模型下载与 CTranslate2 转换、样例集校验、评测和性能基准。

脚本应能从仓库根目录运行，并在文档或 `--help` 里写清参数。需要真实模型的脚本必须把缓存目录指到仓库根 `models/`（该目录已被 git 忽略），不要把权重提交进仓库。

| 脚本 | 用途 |
|------|------|
| `scripts/convert_models.py` | 按清单把 OPUS-MT 下载并转为 CTranslate2。说明见 [模型目录约定](../docs/engine/模型目录约定.md)。 |
