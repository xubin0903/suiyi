"""随译本地翻译引擎。

翻译入口是 :class:`suiyi_engine.translator.Translator`。HTTP 服务尚未实现。
"""

from suiyi_engine.translator import TranslationResult, Translator, UnsupportedPairError

__all__ = [
    "TranslationResult",
    "Translator",
    "UnsupportedPairError",
    "__version__",
]

__version__ = "0.0.1"
