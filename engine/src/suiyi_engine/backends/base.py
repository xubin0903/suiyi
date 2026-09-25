"""可替换的翻译后端接口。

Context Layer 与 HTTP 只依赖 :class:`TranslationBackend`，不依赖 CTranslate2。
以后换成 Bergamot 或其他引擎时，新增一个实现即可，调用方不用改。
"""

from __future__ import annotations

from typing import Protocol

__all__ = ["TranslationBackend"]


class TranslationBackend(Protocol):
    """一个翻译方向的后端。每个实例只服务一个模型目录。"""

    def translate_batch(self, sentences: list[str]) -> list[str]:
        """翻译一批句子。

        Args:
            sentences: 已分句的源文本，顺序即调用方的顺序。

        Returns:
            与 ``sentences`` 等长、顺序一致的译文。空句子应返回空字符串。
        """
