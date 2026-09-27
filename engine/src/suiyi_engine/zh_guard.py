"""中文译文检查（#106）：发现乱码或繁体时转简体、重译或回退。

包在中文目标的最后一跳后端外面（直连 en→zh 或 ja→en→zh 的第二跳），看得到这一跳的输入
（英文，术语 / verbatim 片段已换成占位符）和中文输出，逐句检查：

1. 没问题：原样返回（正常路径只多一次逐字查表，微秒级）。
2. 有乱码字或繁体字（``>>cmn_Hans<<`` 下出繁体说明模型走偏了，「我想你」的乱码就是
   「и稱」，只剩「稱」时看起来像繁体）：依次尝试，取第一个既没有乱码也没有繁体的结果——
   a. 同一模型换 beam（默认 4）重译；
   b. 同方向的另一个已安装模型（如 ``opus-mt-en-zh``），临时构造、用完即丢，不常驻、
      不占 ``--max-loaded-models`` 名额；
   c. 同一模型换 beam 并禁止生成乱码 / 繁体 token（CTranslate2 ``suppress_sequences``）；
   d. 都不干净时取问题最少的候选（原译文优先）：只剩繁体就字级转简体（OpenCC 数据，
      见 :mod:`suiyi_engine.zh_script`）；还有两个以上乱码字就试着按 Big5 还原，再不行原样转简体。

这一跳输入里出现过的字不算问题、不改：原文里本来就有的繁体字或专名照样保留；
verbatim 片段与术语此时是占位符，检查之后才换回，也不会被改动。
"""

from __future__ import annotations

import logging
from collections.abc import Callable
from dataclasses import dataclass, field

from suiyi_engine.zh_script import find_issues, repair_big5_mojibake, to_simplified

logger = logging.getLogger(__name__)

RETRY_BEAM = 4


@dataclass
class ZhGuardStats:
    """累计计数（进程内）。"""

    checked: int = 0
    converted: int = 0
    """重译后仍只剩繁体、字级转成简体的句数。"""
    retried: int = 0
    """发现乱码或繁体、重译过的句数。"""
    fixed_by: dict[str, int] = field(default_factory=dict)
    unresolved: int = 0


class ZhOutputGuard:
    """包住一个中文目标后端，接口同 ``TranslationBackend``。"""

    def __init__(
        self,
        inner: object,
        *,
        fallback: Callable[[], object | None] | None = None,
        stats: ZhGuardStats | None = None,
        retry_beam: int = RETRY_BEAM,
    ) -> None:
        self.inner = inner
        self._fallback = fallback
        self.stats = stats if stats is not None else ZhGuardStats()
        self.retry_beam = retry_beam

    def translate_batch(self, sentences: list[str]) -> list[str]:
        outputs = list(self.inner.translate_batch(sentences))  # type: ignore[attr-defined]
        if len(outputs) != len(sentences):
            return outputs
        self.stats.checked += len(sentences)
        bad = [
            index
            for index, (source, output) in enumerate(zip(sentences, outputs, strict=True))
            if not find_issues(output, source).ok
        ]
        if bad:
            self._repair(sentences, outputs, bad)
        return outputs

    # ---- 乱码 / 繁体

    def _repair(self, sentences: list[str], outputs: list[str], bad: list[int]) -> None:
        self.stats.retried += len(bad)
        candidates: dict[int, list[str]] = {index: [outputs[index]] for index in bad}
        pending = list(bad)
        for name, attempt in self._attempts():
            if not pending:
                break
            try:
                results = attempt([sentences[index] for index in pending])
            except Exception:  # 回退失败不能让整次翻译失败
                logger.warning("中文译文检查：%s 重译失败", name, exc_info=True)
                continue
            if results is None or len(results) != len(pending):
                continue
            still: list[int] = []
            for index, result in zip(pending, results, strict=True):
                candidates[index].append(result)
                if find_issues(result, sentences[index]).ok:
                    outputs[index] = result
                    self._count(name)
                else:
                    still.append(index)
            pending = still
        unresolved = 0
        for index in pending:
            source = sentences[index]
            # 候选按问题排序（先比乱码字数，再比繁体字数），同分取靠前的（原译文优先）
            best = min(candidates[index], key=lambda text: find_issues(text, source).score)
            left = find_issues(best, source).mojibake
            if left:
                # 至少两个乱码字才按 Big5 还原：单个可疑字可能是误判的正常生僻字（如「蹚」）
                repaired = repair_big5_mojibake(best, source) if len(left) >= 2 else None
                if repaired is not None:
                    outputs[index] = repaired
                    self._count("big5_repair")
                    continue
                unresolved += 1
            else:
                self.stats.converted += 1  # 只剩繁体：字级转简体
            outputs[index] = to_simplified(best, source)
        self.stats.unresolved += unresolved
        logger.info(
            "中文译文检查：%d 句有乱码或繁体，累计处理 %s，转简体 %d，本次仍有乱码 %d 句",
            len(bad),
            self.stats.fixed_by,
            self.stats.converted,
            unresolved,
        )

    def _count(self, name: str) -> None:
        self.stats.fixed_by[name] = self.stats.fixed_by.get(name, 0) + 1

    def _attempts(self) -> list[tuple[str, Callable[[list[str]], list[str] | None]]]:
        variant = getattr(self.inner, "translate_variant", None)
        attempts: list[tuple[str, Callable[[list[str]], list[str] | None]]] = []
        if variant is not None:
            attempts.append(
                (f"beam{self.retry_beam}", lambda batch: variant(batch, beam_size=self.retry_beam))
            )
        if self._fallback is not None:
            attempts.append(("fallback_model", self._translate_with_fallback))
        if variant is not None:
            attempts.append(
                (
                    "suppress",
                    lambda batch: variant(batch, beam_size=self.retry_beam, suppress_zh=True),
                )
            )
        return attempts

    def _translate_with_fallback(self, batch: list[str]) -> list[str] | None:
        assert self._fallback is not None
        backend = self._fallback()
        if backend is None:
            return None
        try:
            return list(backend.translate_batch(batch))  # type: ignore[attr-defined]
        finally:
            del backend  # 不常驻：用完即释放，并把释放的内存还给系统
            from suiyi_engine import memory

            memory.trim()
