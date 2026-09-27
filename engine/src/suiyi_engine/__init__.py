"""随译本地翻译引擎。

翻译入口是 :class:`suiyi_engine.translator.Translator`。
本机 HTTP 服务用 ``python -m suiyi_engine serve`` 启动。
"""

# 必须在任何模块导入 ctranslate2 之前：带 AMX 的 CPU 上设 MKL_ENABLE_INSTRUCTIONS（#103）；
# 系统可提交内存不足时关掉 MKL 权重预打包，少提交约 1.6 GiB（#113）
from suiyi_engine.cpu_isa import configure_mkl_isa, configure_packed_gemm

configure_mkl_isa()
configure_packed_gemm()

from suiyi_engine.translator import (  # noqa: E402
    TranslationResult,
    Translator,
    UnsupportedPairError,
)

__all__ = [
    "TranslationResult",
    "Translator",
    "UnsupportedPairError",
    "__version__",
]

__version__ = "0.0.1"
