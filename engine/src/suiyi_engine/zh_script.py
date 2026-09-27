"""中文译文的乱码与繁体检测、繁转简（#106）。

tc-big en→zh 的词表里有训练语料带进来的「Big5 字节被当成 GBK 解码」的乱码字（如「這是測試」→
「硂琌代刚」），模型偶尔会选中这些 token；它也偶尔输出繁体字。这里只做检测和字级繁转简，
重译与回退在 :mod:`suiyi_engine.zh_guard`。

- **乱码字**：GBK/4 区的生僻汉字（见 :func:`is_mojibake_char`），这类字几乎只出现在 Big5→GBK
  错解里；Big5 首字节 0xA4–0xA9 错解出的假名、希腊、西里尔、注音、制表符、拼音字母；
  U+FFFD 与 SentencePiece 的未知字符「⁇」。原文里有的字不算（例如原文本身带希腊字母）。
- **繁体字**：OpenCC ``TSCharacters.txt``（Apache-2.0，见 ``data/opencc_LICENSE.txt``）里
  有简体对应、自身不在 GB2312 里的字。一对多的条目取第一个候选。
- 原文里出现过的字（原文就是繁体、verbatim 原样保留的片段、专名）一律不算、不改。
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache
from importlib import resources

REPLACEMENT_CHARS = frozenset({"\ufffd", "\u2047"})
"""U+FFFD 与「⁇」（SentencePiece 把 ``<unk>`` 解码成的字符）。"""

_PINYIN_LETTERS = frozenset("āáǎàēéěèêīíǐìōóǒòūúǔùǖǘǚǜüɑḿńňǹɡ")
"""GB2312 第 8 区的拼音字母。Big5 常用字的首字节 0xA8 按 GBK 解码会落到这里（如「ê琌代刚」）。"""


def _is_odd_script(char: str) -> bool:
    """Big5 首字节 0xA4–0xA9 按 GBK 解码落到的非汉字区。

    假名、希腊、西里尔、注音、制表符、拼音字母。
    """

    code = ord(char)
    return (
        0x0370 <= code <= 0x04FF  # 希腊、西里尔（如「我想你」→「и稱」）
        or 0x3040 <= code <= 0x30FF  # 假名
        or 0x3100 <= code <= 0x312F  # 注音
        or 0x2500 <= code <= 0x257F  # 制表符
        or char in _PINYIN_LETTERS
    )


def _is_han(char: str) -> bool:
    code = ord(char)
    return 0x3400 <= code <= 0x4DBF or 0x4E00 <= code <= 0x9FFF or 0xF900 <= code <= 0xFAFF


@lru_cache(maxsize=8192)
def _in_gb2312(char: str) -> bool:
    try:
        char.encode("gb2312")
    except UnicodeEncodeError:
        return False
    return True


@lru_cache(maxsize=8192)
def is_mojibake_char(char: str) -> bool:
    """``char`` 是否像 Big5→GBK 错解产生的乱码字（或替换字符）。

    判据：落在 GBK/4 区（首字节 0xAA–0xFE、尾字节 0x40–0xA0）的汉字，既不是有简体对应的繁体字，
    也不是某个繁体字的简体写法（GB2312 以外的规范简体字，如「镕」）。
    Big5 的双字节被按 GBK 解码时，尾字节 0x40–0x7E 的字全都落进这个区，里面都是简体中文几乎不用的
    生僻字；正常简体里的 GBK 扩展字（如人名用字「喆」「堃」）在 GBK/3 区（首字节 0x81–0xA0）。
    """

    if char in REPLACEMENT_CHARS or _is_odd_script(char):
        return True
    if not _is_han(char) or _in_gb2312(char) or is_traditional_char(char):
        return False
    if char in _known_simplified():
        return False  # 某个繁体字的简体写法（如「镕」←「鎔」），是正常简体字
    try:
        raw = char.encode("gbk")
    except UnicodeEncodeError:
        return False
    return len(raw) == 2 and 0xAA <= raw[0] <= 0xFE and 0x40 <= raw[1] <= 0xA0


@lru_cache(maxsize=1)
def _t2s_table() -> dict[str, str]:
    text = (
        resources.files("suiyi_engine")
        .joinpath("data", "opencc_ts_characters.txt")
        .read_text("utf-8")
    )
    table: dict[str, str] = {}
    for line in text.splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        trad, _, simp = line.partition("\t")
        candidates = [c for c in simp.split() if c != trad]
        if len(trad) != 1 or not candidates:
            continue
        if _in_gb2312(trad):
            continue  # GB2312 里有的字（如「著」「後」）在简体中文里也用，不算繁体
        table[trad] = candidates[0]
    return table


@lru_cache(maxsize=1)
def _known_simplified() -> frozenset[str]:
    """OpenCC 表里出现过的全部简体候选（含 GB2312 以外的规范字，如「镕」「啰」）。"""

    text = (
        resources.files("suiyi_engine")
        .joinpath("data", "opencc_ts_characters.txt")
        .read_text("utf-8")
    )
    return frozenset(
        char
        for line in text.splitlines()
        for char in line.partition("\t")[2].split()
        if len(char) == 1
    )


def is_traditional_char(char: str) -> bool:
    return char in _t2s_table()


@dataclass(frozen=True)
class ZhIssues:
    """一句中文译文里的问题字（按出现顺序，去重）。"""

    mojibake: tuple[str, ...] = ()
    traditional: tuple[str, ...] = ()

    @property
    def ok(self) -> bool:
        return not self.mojibake and not self.traditional

    @property
    def score(self) -> tuple[int, int]:
        """越小越好：先比乱码字数，再比繁体字数。"""

        return (len(self.mojibake), len(self.traditional))


def find_issues(output: str, source: str = "") -> ZhIssues:
    """找出 ``output`` 里的乱码字与繁体字；``source`` 里出现过的字不算。"""

    keep = set(source)
    mojibake: dict[str, None] = {}
    traditional: dict[str, None] = {}
    for char in output:
        if char in keep:
            continue
        if is_mojibake_char(char):
            mojibake[char] = None
        elif is_traditional_char(char):
            traditional[char] = None
    return ZhIssues(tuple(mojibake), tuple(traditional))


def to_simplified(text: str, source: str = "") -> str:
    """字级繁转简；``source`` 里出现过的字保持原样。"""

    table = _t2s_table()
    keep = set(source)
    return "".join(table[char] if char in table and char not in keep else char for char in text)


def repair_big5_mojibake(text: str, source: str = "") -> str | None:
    """把连续的乱码字按 GBK 编码、Big5 解码还原，再繁转简；还原不了返回 ``None``。

    只处理整段都是可疑字（乱码字或 GB2312 里的汉字）的连续片段，并且片段里至少一个乱码字。
    这是没有别的办法时的最后手段（见 :mod:`suiyi_engine.zh_guard`）。
    """

    keep = set(source)
    out: list[str] = []
    run: list[str] = []
    changed = False

    def flush() -> bool:
        nonlocal changed
        if not run:
            return True
        piece = "".join(run)
        run.clear()
        if not any(is_mojibake_char(c) and c not in keep for c in piece):
            out.append(piece)
            return True
        try:
            restored = piece.encode("gbk").decode("big5")
        except (UnicodeEncodeError, UnicodeDecodeError):
            return False
        if not all(_is_han(c) for c in restored):
            return False
        out.append(to_simplified(restored))
        changed = True
        return True

    for char in text:
        if _is_han(char) and char not in keep:
            run.append(char)
            continue
        if not flush():
            return None
        out.append(char)
    if not flush():
        return None
    repaired = "".join(out)
    return repaired if changed and not find_issues(repaired, source).mojibake else None
