"""随译本地翻译引擎。

翻译入口是 :class:`suiyi_engine.translator.Translator`。
本机 HTTP 服务用 ``python -m suiyi_engine serve`` 启动。
"""

# 必须在任何模块导入 ctranslate2 之前：带 AMX 的 CPU 上设 MKL_ENABLE_INSTRUCTIONS（#103）；
# 记下用户是否设了 CT2_PACKED_GEMM；是否预打包在第一次加载翻译模型时再决定（#113 / #119）
from suiyi_engine.cpu_isa import configure_mkl_isa, pack_governor

configure_mkl_isa()
pack_governor()

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
