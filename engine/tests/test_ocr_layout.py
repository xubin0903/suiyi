"""OCR 行 → 段落合并规则（#52）。纯构造数据，不需要模型、numpy 或 PIL。"""

from __future__ import annotations

import random

from suiyi_engine.ocr import OcrLine, is_vertical, join_lines, merge_paragraphs


def line(text: str, x0: float, y0: float, x1: float, y1: float, **kwargs: object) -> OcrLine:
    box = ((x0, y0), (x1, y0), (x1, y1), (x0, y1))
    return OcrLine(text=text, box=box, score=0.95, **kwargs)  # type: ignore[arg-type]


def texts(lines: list[OcrLine]) -> list[str]:
    return [p.text for p in merge_paragraphs(lines)]


# ---- 拼接 ---------------------------------------------------------------------


def test_join_cjk_without_spaces_and_latin_with_spaces() -> None:
    assert join_lines(["离线翻译", "不需要网络。"]) == "离线翻译不需要网络。"
    assert join_lines(["Suiyi keeps every", "step local."]) == "Suiyi keeps every step local."
    assert join_lines(["按下", "Ctrl+Alt+T", "即可"]) == "按下Ctrl+Alt+T即可"
    assert join_lines(["オフライン", "翻訳"]) == "オフライン翻訳"


def test_join_hyphenation() -> None:
    assert join_lines(["a compact trans-", "lation model"]) == "a compact translation model"
    assert join_lines(["the Wi-", "Fi network"]) == "the Wi-Fi network"
    assert join_lines(["ends with -", "dash"]) == "ends with - dash"
    # 同一行的碎片不做断词处理
    assert join_lines(["co-", "op"], same_row=True) == "co- op"


def test_join_skips_blank_fragments() -> None:
    assert join_lines(["  ", "hello ", "", " world"]) == "hello world"
    assert join_lines([]) == ""


# ---- 横排 ---------------------------------------------------------------------


def test_lines_of_one_paragraph_merge() -> None:
    lines = [
        line("很多人在阅读外文资料时会顺手复制", 10, 10, 610, 34),
        line("一段文字去翻译。如果翻译服务在云", 10, 42, 610, 66),
        line("端，这段文字就会离开你的电脑。", 10, 74, 400, 98),
    ]
    paragraphs = merge_paragraphs(lines)
    assert len(paragraphs) == 1
    para = paragraphs[0]
    assert para.text == (
        "很多人在阅读外文资料时会顺手复制一段文字去翻译。如果翻译服务在云端，这段文字就会离开你的电脑。"
    )
    assert para.line_indices == (0, 1, 2)
    assert para.box == (10, 10, 610, 98)
    assert para.vertical is False


def test_input_order_does_not_matter() -> None:
    lines = [
        line("first line of text that is long", 10, 10, 500, 30),
        line("second line of text that is long", 10, 36, 500, 56),
        line("third and last.", 10, 62, 200, 82),
    ]
    shuffled = [lines[2], lines[0], lines[1]]
    assert texts(shuffled) == [
        "first line of text that is long second line of text that is long third and last."
    ]
    assert merge_paragraphs(shuffled)[0].line_indices == (1, 2, 0)


def test_large_gap_splits_paragraphs() -> None:
    lines = [
        line("第一段第一行文字", 10, 10, 400, 30),
        line("第一段第二行", 10, 36, 300, 56),
        line("第二段第一行文字", 10, 90, 400, 110),
    ]
    assert texts(lines) == ["第一段第一行文字第一段第二行", "第二段第一行文字"]


def test_title_with_larger_font_is_its_own_paragraph() -> None:
    lines = [
        line("Offline translation", 10, 10, 420, 44),
        line("Suiyi keeps every step on your own machine and never", 10, 54, 700, 74),
        line("sends text to a server.", 10, 80, 300, 100),
    ]
    assert texts(lines) == [
        "Offline translation",
        "Suiyi keeps every step on your own machine and never sends text to a server.",
    ]


def test_short_line_before_wider_line_ends_paragraph() -> None:
    # 同字号、同行距：只有「上一行明显短」能说明上一段已经结束
    lines = [
        line("设置", 10, 10, 60, 30),
        line("在这里修改翻译方向和快捷键，修改后立即生效。", 10, 36, 500, 56),
    ]
    assert texts(lines) == ["设置", "在这里修改翻译方向和快捷键，修改后立即生效。"]


def test_first_line_indent_starts_new_paragraph() -> None:
    lines = [
        line("　　第一段首行缩进两个字，后面还有", 50, 10, 500, 30),
        line("一些文字把这一行写满写满写满写满写", 10, 36, 500, 56),
        line("满写满写满写满写满写满写满写满写满", 10, 62, 500, 82),
        line("第二段首行同样缩进两个字，后面还", 50, 88, 500, 108),
        line("有文字。", 10, 114, 100, 134),
    ]
    paragraphs = texts(lines)
    assert len(paragraphs) == 2
    assert paragraphs[0].startswith("第一段") and paragraphs[0].endswith("写满")
    assert paragraphs[1] == "第二段首行同样缩进两个字，后面还有文字。"


def test_misaligned_line_is_not_merged() -> None:
    lines = [
        line("左边一行很长很长很长很长", 10, 10, 400, 30),
        line("右边错开的一行", 200, 36, 600, 56),
    ]
    assert len(merge_paragraphs(lines)) == 2


def test_same_row_fragments_are_joined() -> None:
    lines = [
        line("插入", 100, 10, 140, 30),
        line("文件 编辑", 10, 10, 90, 30),
        line("格式", 150, 11, 190, 29),
        line("Hello", 10, 50, 60, 70),
        line("world", 72, 50, 130, 70),
    ]
    paragraphs = merge_paragraphs(lines)
    assert [p.text for p in paragraphs] == ["文件 编辑插入格式", "Hello world"]
    assert paragraphs[0].line_indices == (1, 0, 2)


def test_two_columns_read_column_by_column() -> None:
    lines = []
    for i in range(3):
        y = 10 + 26 * i
        lines.append(line(f"left{i} words words words", 10, y, 300, y + 20))
        lines.append(line(f"right{i} words words words", 360, y, 650, y + 20))
    paragraphs = texts(lines)
    assert paragraphs == [
        "left0 words words words left1 words words words left2 words words words",
        "right0 words words words right1 words words words right2 words words words",
    ]


def test_two_columns_with_aligned_paragraph_gaps_do_not_interleave() -> None:
    def para(prefix: str, x0: float, y0: float) -> list[OcrLine]:
        return [
            line(f"{prefix} a a a a a a", x0, y0, x0 + 280, y0 + 20),
            line(f"{prefix} b b b b b b", x0, y0 + 26, x0 + 280, y0 + 46),
        ]

    lines = para("L1", 10, 10) + para("R1", 360, 10) + para("L2", 10, 100) + para("R2", 360, 100)
    order = [p.text.split()[0] for p in merge_paragraphs(lines)]
    assert order == ["L1", "L2", "R1", "R2"]


def test_title_spanning_two_columns_comes_first() -> None:
    lines = [
        line("A title across both columns", 10, 10, 650, 50),
        line("left column text text", 10, 70, 300, 90),
        line("right column text text", 360, 70, 650, 90),
    ]
    assert texts(lines) == [
        "A title across both columns",
        "left column text text",
        "right column text text",
    ]


# ---- 竖排 ---------------------------------------------------------------------


def column(text: str, x0: float, y0: float, width: float = 30, char: float = 32) -> OcrLine:
    return line(text, x0, y0, x0 + width, y0 + char * len(text))


def test_is_vertical_by_aspect_ratio() -> None:
    assert is_vertical(column("縦書きの列", 100, 10))
    assert not is_vertical(line("横书きの行", 10, 10, 200, 30))
    # 单字的框接近正方形，不能判断，按横排
    assert not is_vertical(line("字", 10, 10, 30, 30))
    assert not is_vertical(line("字", 10, 10, 20, 40))


def test_vertical_columns_read_right_to_left() -> None:
    lines = [
        column("春の朝は空気が", 200, 10),
        column("やわらかく窓を", 150, 10),
        column("開ける", 100, 10),
    ]
    random.Random(0).shuffle(lines)
    paragraphs = merge_paragraphs(lines)
    assert len(paragraphs) == 1
    para = paragraphs[0]
    assert para.vertical is True
    assert para.text == "春の朝は空気がやわらかく窓を開ける"
    assert [lines[i].text for i in para.line_indices] == [
        "春の朝は空気が",
        "やわらかく窓を",
        "開ける",
    ]
    assert para.box == (100, 10, 230, 10 + 32 * 7)


def test_vertical_paragraphs_separated_by_blank_column() -> None:
    lines = [
        column("春の朝は空気が", 300, 10),
        column("やわらかい", 250, 10),
        # x=200 一列空白
        column("窓を開けると鳥", 150, 10),
        column("の声がする", 100, 10),
    ]
    assert texts(lines) == ["春の朝は空気がやわらかい", "窓を開けると鳥の声がする"]


def test_short_vertical_column_ends_paragraph() -> None:
    lines = [
        column("一段落目の一列目", 200, 10),
        column("終わり", 150, 10),
        column("二段落目の一列目", 100, 10),
    ]
    assert texts(lines) == ["一段落目の一列目終わり", "二段落目の一列目"]


def test_vertical_fragments_in_one_column_join_top_to_bottom() -> None:
    # 标点处被切成上下两段的同一列（RapidOCR 对「、」常这样切）
    lines = [
        column("家で静かに本", 100, 10 + 32 * 8),
        column("今日は雨なので", 100, 10),
        column("を読む", 50, 10),
    ]
    paragraphs = merge_paragraphs(lines)
    assert [p.text for p in paragraphs] == ["今日は雨なので家で静かに本を読む"]
    assert paragraphs[0].line_indices == (1, 0, 2)


def test_separate_vertical_blocks_read_right_block_first() -> None:
    lines = [
        column("左の吹き出し", 20, 10),
        column("右の吹き出し", 400, 10),
    ]
    assert texts(lines) == ["右の吹き出し", "左の吹き出し"]


def test_horizontal_title_above_vertical_text() -> None:
    lines = [
        line("タイトル", 10, 10, 200, 40),
        column("本文の一列目です", 150, 60),
        column("本文の二列目", 100, 60),
    ]
    assert texts(lines) == ["タイトル", "本文の一列目です本文の二列目"]


# ---- 过滤 ---------------------------------------------------------------------


def test_low_confidence_and_blank_lines_are_skipped() -> None:
    lines = [
        line("看得清的一行字", 10, 10, 300, 30),
        line("糊成一片", 10, 36, 300, 56, low_confidence=True),
        line("   ", 10, 62, 300, 82),
    ]
    paragraphs = merge_paragraphs(lines)
    assert [p.text for p in paragraphs] == ["看得清的一行字"]
    assert paragraphs[0].line_indices == (0,)


def test_empty_input() -> None:
    assert merge_paragraphs([]) == []
    assert merge_paragraphs([line("x", 0, 0, 10, 10, low_confidence=True)]) == []


def test_single_char_column_head_and_tail_join_vertical_paragraph() -> None:
    # #74：缩小检测图后，列首/列尾的单个字常被单独切出，框偏胖（接近正方形）
    head = [column("宿題は最後の三日間で片づけ", 200, 10), line("た", 150, 8, 188, 50)]
    assert texts(head) == ["宿題は最後の三日間で片づけた"]
    tail = [column("春の朝は空気がやわらか", 200, 10), line("い", 198, 10 + 32 * 11 + 4, 232, 400)]
    assert texts(tail) == ["春の朝は空気がやわらかい"]
    # 远离竖排列的单字仍按横排
    lone = [column("春の朝は空気が", 200, 10), line("字", 20, 300, 50, 330)]
    assert sorted(texts(lone)) == ["字", "春の朝は空気が"]


def test_single_char_row_is_not_compared_by_font_size() -> None:
    # 单字行的框随字形变胖，不因字号比把它从段落里切出去
    lines = [line("第一行文字比较长一些", 10, 10, 210, 30), line("对。", 10, 36, 50, 64)]
    assert len(texts(lines)) == 2  # 两个字：照常按字号比切开
    lines = [line("第一行文字比较长一些", 10, 10, 210, 30), line("。", 10, 36, 40, 64)]
    assert texts(lines) == ["第一行文字比较长一些。"]
