"""翻译后端。运行时默认实现是 CTranslate2 / OPUS-MT。"""

from suiyi_engine.backends.base import TranslationBackend

__all__ = ["TranslationBackend"]
