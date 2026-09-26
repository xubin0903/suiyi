"""中文译文的标点规范化（#97）：把夹在中文里的半角标点改成全角。

tc-big 的 en→zh 输出常带半角标点（「开源系统,用于」「CNCF(Cloud Native …)主办」「部署;扩展」）。
这里只改「旁边是中文」的半角标点，其余一律不动：

- 逗号 / 分号 / 冒号 / 问号 / 叹号：前一个或后一个非空白字符是汉字（或全角标点）时改成全角，
  并去掉两侧的空格。两边都是数字时不改（``2,000``、``12:30``）。
- 括号：成对出现、且外侧紧挨着中文或括号里有中文时改成全角。括号里没有中文时还要求
  括号内容以大写字母或数字开头（小写开头时要求左边紧挨着中文），且不是紧跟在标识符后面的
  无空格内容，所以 ``O(n log n)``、``foo(bar)``、``f(X)`` 保持原样。
- URL、邮箱、反引号里的代码、Windows 路径整段跳过。
- 只处理半角 ``, ; : ? ! ( )``。引号、句点（句末已由 ``restore_final_punct`` 处理）不动。
"""

from __future__ import annotations

import re

__all__ = ["normalize_zh_punct"]

_HAN = "\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff"
_FULL_PUNCT = "，。、；：？！“”‘’（）《》【】「」『』…—"
_CHINESE = re.compile(rf"[{_HAN}{_FULL_PUNCT}]")
_HAN_ONLY = re.compile(rf"[{_HAN}]")
_SKIP = re.compile(
    r"`[^`\n]*`"  # 行内代码
    r"|(?:https?|ftp|file)://[^\s\u3000-\u303f\uff00-\uffef" + _HAN + r"]+"
    r"|www\.[^\s\u3000-\u303f\uff00-\uffef" + _HAN + r"]+"
    r"|[\w.+-]+@[\w-]+(?:\.[\w-]+)+"
    r"|[A-Za-z]:\\[^\s\u3000-\u303f\uff00-\uffef" + _HAN + r"]*"
)
_SIMPLE = {",": "，", ";": "；", ":": "：", "?": "？", "!": "！"}
_TARGET = frozenset(_SIMPLE) | {"(", ")"}
_GAP = " \t\u00a0"


def normalize_zh_punct(text: str) -> str:
    """把中文语境里的半角标点改成全角。没有需要改的标点时原样返回。"""

    if not any(char in _TARGET for char in text) or _HAN_ONLY.search(text) is None:
        return text
    chars = list(text)
    frozen = [False] * len(chars)
    for match in _SKIP.finditer(text):
        for index in range(match.start(), match.end()):
            frozen[index] = True
    replace: dict[int, str] = {}
    changed = True
    while changed:  # 「, ,求」这种连着的半角标点：一个改了之后，旁边的也跟着改
        changed = False
        view = [replace.get(index, char) for index, char in enumerate(chars)]
        for index, char in enumerate(chars):
            if frozen[index] or char not in _SIMPLE or index in replace:
                continue
            before = _neighbor(view, index, -1)
            after = _neighbor(view, index, 1)
            if before is not None and after is not None and before.isdigit() and after.isdigit():
                continue
            if char == ":" and after == "/":
                continue
            if _is_chinese(before) or _is_chinese(after):
                replace[index] = _SIMPLE[char]
                changed = True
    for start, end in _paren_pairs(chars, frozen):
        if _convert_parens(chars, start, end):
            replace[start] = "（"
            replace[end] = "）"
    if not replace:
        return text
    drop: set[int] = set()
    for index in replace:
        for step in (-1, 1):
            cursor = index + step
            while 0 <= cursor < len(chars) and chars[cursor] in _GAP and not frozen[cursor]:
                drop.add(cursor)
                cursor += step
    pieces = []
    for index, char in enumerate(chars):
        if index in drop:
            continue
        pieces.append(replace.get(index, char))
    return "".join(pieces)


def _neighbor(chars: list[str], index: int, step: int) -> str | None:
    cursor = index + step
    while 0 <= cursor < len(chars):
        if chars[cursor] not in _GAP:
            return chars[cursor]
        cursor += step
    return None


def _is_chinese(char: str | None) -> bool:
    return char is not None and _CHINESE.match(char) is not None


def _paren_pairs(chars: list[str], frozen: list[bool]) -> list[tuple[int, int]]:
    stack: list[int] = []
    pairs: list[tuple[int, int]] = []
    for index, char in enumerate(chars):
        if frozen[index]:
            continue
        if char == "(":
            stack.append(index)
        elif char == ")" and stack:
            pairs.append((stack.pop(), index))
    return pairs


def _convert_parens(chars: list[str], start: int, end: int) -> bool:
    inner = "".join(chars[start + 1 : end]).strip()
    if not inner:
        return False
    outside = _is_chinese(_neighbor(chars, start, -1)) or _is_chinese(_neighbor(chars, end, 1))
    if _HAN_ONLY.search(inner):
        return True
    if not outside:
        return False
    if not (inner[0].isupper() or inner[0].isdigit()):
        # 小写开头只在左边紧挨着中文时改（「混淆矩阵(confusion matrix)」），O(n log n) 保持原样
        return _is_chinese(_neighbor(chars, start, -1))
    glued = start > 0 and (
        chars[start - 1].isascii() and (chars[start - 1].isalnum() or chars[start - 1] == "_")
    )
    return not (glued and not any(char.isspace() for char in inner))  # foo(Bar)、f(X)
