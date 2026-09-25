"""翻译核心的公开异常。"""

from __future__ import annotations

from collections.abc import Sequence


class UnsupportedPairError(Exception):
    """请求的语向没有可加载的模型。

    ``missing_ids`` 是清单里对应该走的、但模型目录中还不存在的模型 id。
    上层（例如 HTTP）用它提示「未下载语向」，不要只显示语种代码。

    Args:
        src: 归一化后的源语种。
        tgt: 归一化后的目标语种。
        missing_ids: 缺失的模型 id。清单里没有对应条目时为空。
    """

    def __init__(self, src: str, tgt: str, missing_ids: Sequence[str] = ()) -> None:
        self.src = src
        self.tgt = tgt
        self.missing_ids = tuple(missing_ids)
        if self.missing_ids:
            shown = "、".join(self.missing_ids)
        else:
            shown = "（清单未定义该语向的模型 id）"
        super().__init__(f"不支持的语向 {src}→{tgt}，未下载模型：{shown}")
