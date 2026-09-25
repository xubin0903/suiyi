"""文本行 → 段落：竖排判定、同行碎片合并、段落归并、阅读顺序（#52）。

纯 Python，不依赖模型，单测用构造的行数据。规则说明见 ``docs/engine/OCR核心.md``。

横排与竖排共用同一套归并逻辑：竖排的框先换到「流坐标」里——列当作行、从右到左当作从上到下，
列内从上到下当作行内从左到右——归并完再换回原图坐标。
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass, field

from suiyi_engine.ocr.types import Box, OcrLine, OcrParagraph


@dataclass(frozen=True)
class LayoutOptions:
    """合并阈值。长度类阈值都以字号为单位：横排是行高，竖排是列宽。"""

    vertical_ratio: float = 1.5
    """框高 ≥ 宽 × 该值，且文本至少 2 个字符，判为竖排列。"""
    same_row_overlap: float = 0.6
    """同一行的碎片：行方向上的重叠 ≥ 较小字号 × 该值。"""
    same_row_gap: float = 1.2
    """同一行的碎片：沿文字方向的间距 ≤ 字号 × 该值。"""
    line_gap: float = 0.9
    """同一段相邻两行：上一行底到下一行顶的距离 ≤ 字号 × 该值（再大就是段间距）。"""
    height_ratio: float = 1.35
    """同一段相邻两行的字号比（大/小）不超过该值，否则视为标题与正文。"""
    align: float = 0.8
    """同一段相邻两行的行首对齐容差（× 字号）。"""
    indent: float = 1.0
    """行首比上一行多缩进超过该值（× 字号），视为新段首行缩进。"""
    short_line: float = 2.0
    """上一行行尾比段落右缘短超过该值（× 字号），视为上一段已结束。"""
    short_row: float = 12.0
    """短行分段（#75）：相邻两行的长度都 ≤ 该值（× 各自字号），且上一行不以续接标点
    （:data:`CONTINUATION_END`）结尾，视为两个独立的 UI 条目（菜单项、列表项、设置项）。
    0 表示关闭（同时关闭段尾短行豁免）。"""
    tail_ratio: float = 1.7
    """段尾短行（#75）：长度 ≤ ``short_row``、比上一行短、字号比 ≤ 该值，且与上一行的间距
    不大于本段已有行距时，不按字号比分段。缩小检测图后，只有几个字的段尾行框常偏高（「对。」）。"""


DEFAULT_OPTIONS = LayoutOptions()


@dataclass
class _Row:
    """流坐标里的一行（竖排时是一列），由一个或多个同行碎片组成。"""

    indices: list[int]
    rect: Box
    size: float
    texts: list[str]

    @property
    def text(self) -> str:
        return join_lines(self.texts, same_row=True)


@dataclass
class _Para:
    rows: list[_Row] = field(default_factory=list)

    @property
    def last(self) -> _Row:
        return self.rows[-1]

    @property
    def right(self) -> float:
        return max(r.rect[2] for r in self.rows)


def is_vertical(line: OcrLine, options: LayoutOptions = DEFAULT_OPTIONS) -> bool:
    """按框的宽高比判断是否竖排列。单个字符的框接近正方形，无法判断，按横排处理。"""

    x0, y0, x1, y1 = line.rect
    width, height = x1 - x0, y1 - y0
    return len(line.text.strip()) >= 2 and height >= options.vertical_ratio * max(width, 1e-6)


def _is_column_tail(
    line: OcrLine, lines: Sequence[OcrLine], vertical: list[int], options: LayoutOptions
) -> bool:
    """单个字符的框属于竖排：紧接在某列正下方（列尾），或在某列左侧与列顶对齐（下一列的列首）。

    单字框接近正方形，:func:`is_vertical` 判不出方向；按横排处理会变成孤立的段落。
    """

    if len(line.text.strip()) != 1:
        return False
    x0, y0, x1, y1 = line.rect
    for index in vertical:
        c0, top, c1, bottom = lines[index].rect
        width = c1 - c0
        if x1 - x0 > options.vertical_ratio * width:  # 单字框偏胖，放宽到 1.5 倍列宽
            continue
        below = (
            _overlap(x0, x1, c0, c1) >= options.same_row_overlap * min(width, x1 - x0)
            and -0.5 * width <= y0 - bottom <= options.same_row_gap * width
        )
        next_head = (
            abs(y0 - top) <= options.align * width
            and -0.5 * width <= c0 - x1 <= options.line_gap * width
        )
        if below or next_head:
            return True
    return False


def merge_paragraphs(
    lines: Sequence[OcrLine], options: LayoutOptions = DEFAULT_OPTIONS
) -> list[OcrParagraph]:
    """把文本行合并成段落，按阅读顺序返回。

    ``low_confidence`` 的行和空文本行不参与合并。段落的 ``line_indices`` 指向 ``lines``。
    """

    horizontal: list[int] = []
    vertical: list[int] = []
    for index, line in enumerate(lines):
        if line.low_confidence or not line.text.strip():
            continue
        (vertical if is_vertical(line, options) else horizontal).append(index)
    tails = [i for i in horizontal if _is_column_tail(lines[i], lines, vertical, options)]
    if tails:
        vertical += tails
        horizontal = [i for i in horizontal if i not in tails]

    paragraphs = _build(lines, horizontal, False, options) + _build(lines, vertical, True, options)
    return _reading_order(paragraphs)


def join_lines(texts: Sequence[str], *, same_row: bool = False) -> str:
    """拼接行文本：中日文之间不加空格，西文之间加一个空格。

    跨行（``same_row=False``）时，行尾是「字母-」、下一行以小写字母开头，视为断词连字符，去掉并直接拼接；
    下一行以大写字母或数字开头时保留连字符、不加空格。
    """

    result = ""
    for raw in texts:
        text = raw.strip()
        if not text:
            continue
        word_hyphen = (
            not same_row
            and result.endswith("-")
            and len(result) >= 2
            and result[-2].isascii()
            and result[-2].isalnum()
        )
        if not result:
            result = text
        elif word_hyphen and text[0].isascii() and text[0].islower():
            result = result[:-1] + text  # 断词连字符：去掉
        elif word_hyphen and text[0].isascii() and text[0].isalnum():
            result += text  # 复合词跨行：保留连字符，不加空格
        elif _is_cjk(result[-1]) or _is_cjk(text[0]):
            result += text
        else:
            result += " " + text
    return result


def _is_cjk(char: str) -> bool:
    code = ord(char)
    return (
        0x2E80 <= code <= 0x2FDF  # 部首
        or 0x3000 <= code <= 0x30FF  # CJK 标点、平假名、片假名
        or 0x31F0 <= code <= 0x31FF  # 片假名扩展
        or 0x3400 <= code <= 0x4DBF  # 扩展 A
        or 0x4E00 <= code <= 0x9FFF  # 基本汉字
        or 0xF900 <= code <= 0xFAFF  # 兼容汉字
        or 0xFE30 <= code <= 0xFE4F  # 竖排标点
        or 0xFF00 <= code <= 0xFFEF  # 全角 ASCII、半角片假名
        or 0x20000 <= code <= 0x3134F  # 扩展 B 及以后
    )


# ---- 流坐标 -------------------------------------------------------------------


def _to_flow(rect: Box, vertical: bool) -> Box:
    """竖排：原图 (x, y) → 流坐标 (y, -x)。

    列从右到左变成行从上到下，列内从上到下变成行内从左到右。
    """

    if not vertical:
        return rect
    x0, y0, x1, y1 = rect
    return (y0, -x1, y1, -x0)


def _from_flow(rect: Box, vertical: bool) -> Box:
    if not vertical:
        return rect
    u0, v0, u1, v1 = rect
    return (-v1, u0, -v0, u1)


def _union(a: Box, b: Box) -> Box:
    return (min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3]))


def _overlap(a0: float, a1: float, b0: float, b1: float) -> float:
    return min(a1, b1) - max(a0, b0)


def _build(
    lines: Sequence[OcrLine], indices: list[int], vertical: bool, options: LayoutOptions
) -> list[OcrParagraph]:
    if not indices:
        return []
    rows = _merge_rows(lines, indices, vertical, options)
    paras = _group_rows(rows, options, vertical)
    result: list[OcrParagraph] = []
    for para in paras:
        flow_box = para.rows[0].rect
        for row in para.rows[1:]:
            flow_box = _union(flow_box, row.rect)
        ordered = [i for row in para.rows for i in row.indices]
        result.append(
            OcrParagraph(
                text=join_lines([row.text for row in para.rows]),
                box=_from_flow(flow_box, vertical),
                line_indices=tuple(ordered),
                vertical=vertical,
            )
        )
    return result


def _merge_rows(
    lines: Sequence[OcrLine], indices: list[int], vertical: bool, options: LayoutOptions
) -> list[_Row]:
    """把同一行（竖排是同一列）里被切开的碎片合成一行。"""

    items = []
    for index in indices:
        rect = _to_flow(lines[index].rect, vertical)
        items.append((index, rect, max(rect[3] - rect[1], 1e-6)))
    items.sort(key=lambda item: ((item[1][1] + item[1][3]) / 2, item[1][0]))

    rows: list[_Row] = []
    for index, rect, size in items:
        target = None
        for row in rows:
            size_min = min(size, row.size)
            vertical_overlap = _overlap(rect[1], rect[3], row.rect[1], row.rect[3])
            gap = max(rect[0] - row.rect[2], row.rect[0] - rect[2])
            if (
                vertical_overlap >= options.same_row_overlap * size_min
                and gap <= options.same_row_gap * max(size, row.size)
            ):
                target = row
                break
        if target is None:
            rows.append(_Row([index], rect, size, [lines[index].text]))
            continue
        pairs = sorted(
            [*zip(target.indices, target.texts, strict=True), (index, lines[index].text)],
            key=lambda pair: _to_flow(lines[pair[0]].rect, vertical)[0],
        )
        target.indices = [i for i, _t in pairs]
        target.texts = [t for _i, t in pairs]
        target.rect = _union(target.rect, rect)
        target.size = max(target.size, size)
    return rows


def _group_rows(rows: list[_Row], options: LayoutOptions, vertical: bool = False) -> list[_Para]:
    """按行距、对齐、缩进、字号和行尾长度把行归成段落（流坐标）。"""

    paras: list[_Para] = []
    for row in sorted(rows, key=lambda r: (r.rect[1], r.rect[0])):
        best: _Para | None = None
        best_gap = float("inf")
        for para in paras:
            last = para.last
            size = max(row.size, last.size)
            gap = row.rect[1] - last.rect[3]
            if gap < -0.5 * size or gap > options.line_gap * size:
                continue
            if _overlap(row.rect[0], row.rect[2], last.rect[0], last.rect[2]) <= 0:
                continue
            if gap < best_gap:
                best, best_gap = para, gap
        if best is not None and _continues(best, row, options, short_rows=not vertical):
            best.rows.append(row)
        else:
            paras.append(_Para([row]))
    return paras


CONTINUATION_END = tuple("，、,；：（(《「『—-")
"""行尾是这些字符时，下一行多半是同一句话的续行（逗号、顿号、全角冒号、左括号、连接号）。

不含半角 ``:`` ``;``：代码行常以它们结尾，却各自独立。"""


def _continues(para: _Para, row: _Row, options: LayoutOptions, *, short_rows: bool = True) -> bool:
    """``short_rows``：是否启用短行分段（只对横排；竖排 UI 很少见，竖排的短列按原规则处理）。"""

    last = para.last
    size = max(row.size, last.size)
    continued = last.text.rstrip().endswith(CONTINUATION_END)
    head = row.text.lstrip()[:1]
    lower_start = head.isascii() and head.islower()  # 西文句子跨行：下一行以小写字母开头
    if size > options.height_ratio * min(row.size, last.size) and not (
        len(row.text.strip()) == 1  # 单字框的尺寸随字形变化，不拿来比字号
        or _is_tail_row(para, row, size, options)
    ):
        return False  # 字号不同：标题与正文
    if row.rect[0] - last.rect[0] > options.indent * size:
        return False  # 首行缩进：新段
    first_line_indent = len(para.rows) == 1 and row.rect[0] < last.rect[0]
    if abs(row.rect[0] - last.rect[0]) > options.align * size and not first_line_indent:
        return False  # 行首不对齐
    if continued:
        return True  # 上一行以续接标点结尾：句子没写完，不按行长分段
    if (
        short_rows
        and options.short_row > 0
        and _width(last) <= options.short_row * last.size
        and _width(row) <= options.short_row * row.size
    ):
        return False  # 相邻两行都短：UI 条目，各自一段
    right = max(para.right, row.rect[2])
    short_line = options.short_line * (2 if lower_start else 1)  # 西文换行时行长差一个词很常见
    return not last.rect[2] < right - short_line * size  # 上一行明显短：段落已结束


def _width(row: _Row) -> float:
    return row.rect[2] - row.rect[0]


def _is_tail_row(para: _Para, row: _Row, size: float, options: LayoutOptions) -> bool:
    """段尾短行：比上一行短、字号比不大、且没有比本段已有行距更大的间距（标题前通常有段间距）。"""

    last = para.last
    if len(para.rows) < 2 or _width(row) >= _width(last):
        return False
    if _width(row) > options.short_row * row.size:
        return False  # 只豁免短的段尾行
    if size > options.tail_ratio * min(row.size, last.size):
        return False
    gaps = [b.rect[1] - a.rect[3] for a, b in zip(para.rows, para.rows[1:], strict=False)]
    return row.rect[1] - last.rect[3] <= max(gaps) + 0.1 * min(row.size, last.size)


# ---- 阅读顺序 -----------------------------------------------------------------


def _reading_order(paragraphs: list[OcrParagraph]) -> list[OcrParagraph]:
    """递归 XY 切分：先找贯穿的竖直空隙（分栏），没有再找水平空隙（上下分块）。

    竖直切分时，块里多数是竖排段落则从右到左，否则从左到右。这样标题 + 双栏不会交错，
    竖排的列从右到左。
    """

    if len(paragraphs) <= 1:
        return list(paragraphs)
    columns = _split(paragraphs, axis=0)
    if len(columns) > 1:
        vertical = sum(p.vertical for p in paragraphs) * 2 > len(paragraphs)
        if vertical:
            columns.reverse()
        return [p for column in columns for p in _reading_order(column)]
    bands = _split(paragraphs, axis=1)
    if len(bands) > 1:
        return [p for band in bands for p in _reading_order(band)]
    return sorted(paragraphs, key=lambda p: (p.box[1], p.box[0]))


def _split(paragraphs: list[OcrParagraph], axis: int) -> list[list[OcrParagraph]]:
    """沿 x（axis=0）或 y（axis=1）找没有任何段落跨过的空隙，按空隙切组，组按坐标升序。"""

    ordered = sorted(paragraphs, key=lambda p: (p.box[axis], p.box[axis + 2]))
    groups: list[list[OcrParagraph]] = [[ordered[0]]]
    end = ordered[0].box[axis + 2]
    for para in ordered[1:]:
        if para.box[axis] > end:
            groups.append([para])
        else:
            groups[-1].append(para)
        end = max(end, para.box[axis + 2])
    return groups
