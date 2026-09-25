"""生成 OCR 评测样例集（#54）：engine/eval/ocr_samples/。

全部文字为原创；字体为 Noto Sans/Serif CJK（SIL OFL 1.1）与 DejaVu Sans Mono（Bitstream Vera 许可），
渲染出的图片不受字体许可约束，样例按 CC0-1.0 发布。画布固定三种尺寸：400×150、1280×720、1920×1080。

需要 Linux 上的 fonts-noto-cjk 与 fonts-dejavu（Debian/Ubuntu 包名）。图片已随仓库提交，
评测不需要重新生成；改了本脚本再运行：python scripts/make_ocr_samples.py
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1] / "engine" / "eval" / "ocr_samples"
FONT_DIRS = (Path("/usr/share/fonts/opentype/noto"), Path("/usr/share/fonts/truetype/dejavu"))
SANS = "NotoSansCJK-Regular.ttc"
BOLD = "NotoSansCJK-Bold.ttc"
SERIF = "NotoSerifCJK-Regular.ttc"
MONO = "DejaVuSansMono.ttf"
TTC_INDEX = {"ja": 0, "zh": 2, "en": 2}  # NotoSansCJK.ttc：0 JP，2 SC
SIZES = {"small": (400, 150), "720p": (1280, 720), "1080p": (1920, 1080)}
VERTICAL_FORMS = {"、": "︑", "。": "︒", "「": "﹁", "」": "﹂", "ー": "丨", "（": "︵", "）": "︶"}


def font(name: str, size: int, lang: str) -> ImageFont.FreeTypeFont:
    for base in FONT_DIRS:
        path = base / name
        if path.is_file():
            if name.endswith(".ttc"):
                return ImageFont.truetype(str(path), size, index=TTC_INDEX[lang])
            return ImageFont.truetype(str(path), size)
    raise FileNotFoundError(f"找不到字体 {name}（需要 fonts-noto-cjk / fonts-dejavu）")


def wrap(draw: ImageDraw.ImageDraw, text: str, f: ImageFont.FreeTypeFont, width: int) -> list[str]:
    cjk = any(ord(ch) > 0x2E80 for ch in text)
    units = list(text) if cjk else text.split(" ")
    lines: list[str] = []
    cur = ""
    for unit in units:
        cand = cur + unit if cjk else (unit if not cur else cur + " " + unit)
        if cur and draw.textlength(cand, font=f) > width:
            lines.append(cur)
            cur = unit
        else:
            cur = cand
    if cur:
        lines.append(cur)
    return lines


@dataclass
class Para:
    text: str
    size: int = 18
    face: str = SANS
    kind: str = "body"  # body / title / mono（mono：按 \n 断行，不自动换行，每行一段）


@dataclass
class Block:
    x: int
    y: int
    width: int
    paras: list[Para]
    line_gap: float = 1.6
    para_gap: float = 1.0  # × 字号
    fg: str | None = None


@dataclass
class Sample:
    id: str
    lang: str
    category: str
    size: str
    blocks: list[Block] = field(default_factory=list)
    bg: str = "#ffffff"
    fg: str = "#202124"
    note: str = ""
    vertical: list[str] | None = None  # 日文竖排段落
    vertical_size: int = 30
    split: str = "dev"  # dev：调参可看；holdout：留出集（#75），调参时不看


def render(sample: Sample) -> tuple[Image.Image, list[str]]:
    width, height = SIZES[sample.size]
    img = Image.new("RGB", (width, height), sample.bg)
    draw = ImageDraw.Draw(img)
    expected: list[str] = []
    if sample.vertical is not None:
        expected = _vertical(draw, sample, width, height)
    for block in sample.blocks:
        y = block.y
        for para in block.paras:
            face = BOLD if para.kind == "title" else (MONO if para.kind == "mono" else para.face)
            f = font(face, para.size, sample.lang)
            if para.kind == "mono":
                lines = para.text.split("\n")
                expected.extend(line.strip() for line in lines if line.strip())
            else:
                lines = wrap(draw, para.text, f, block.width)
                expected.append(para.text)
            step = int(para.size * block.line_gap)
            for line in lines:
                if y + step > height:
                    raise ValueError(f"{sample.id}：文字超出画布")
                draw.text((block.x, y), line, font=f, fill=block.fg or sample.fg)
                y += step
            y += int(para.size * block.para_gap)
    return img, expected


def _vertical(draw: ImageDraw.ImageDraw, sample: Sample, width: int, height: int) -> list[str]:
    size = sample.vertical_size
    f = font(SERIF, size, "ja")
    margin = 40
    rows = (height - 2 * margin) // int(size * 1.15)
    pitch = int(size * 1.7)
    x = width - margin - pitch
    for index, para in enumerate(sample.vertical or []):
        if index:
            x -= pitch  # 段落之间空一列
        for start in range(0, len(para), rows):
            if x < margin:
                raise ValueError(f"{sample.id}：竖排超出画布")
            for r, ch in enumerate(para[start : start + rows]):
                y = margin + int(size * 1.15) * r
                draw.text((x + (pitch - size) // 2, y), VERTICAL_FORMS.get(ch, ch), font=f, fill=sample.fg)
            x -= pitch
    return list(sample.vertical or [])


# ---- 原创文本 -------------------------------------------------------------------

ZH = [
    "很多人在阅读外文资料时会顺手复制一段文字去翻译。如果翻译服务在云端，这段文字就会离开你的电脑。随译把模型放在本机，复制、翻译、显示都在同一台机器上完成，断网时也能照常使用。",
    "离线并不意味着慢。经过量化的小模型在普通笔记本上翻译一句话通常只需要几十毫秒，比等待网络往返还要快。",
    "第一步，按下快捷键进入框选模式，屏幕会稍微变暗。第二步，用鼠标拖出一个矩形，把想看的文字圈起来。第三步，松开鼠标，译文会出现在选区旁边的浮窗里。",
    "如果选区里没有文字，浮窗会提示未识别到内容，而不是显示一段空白。识别结果和译文可以一起复制，方便粘贴到笔记里。",
    "模型文件放在用户目录下的模型文件夹中，升级程序时不会被删除。需要更换语向时，只要下载对应的模型包，重启服务即可生效。",
    "图书馆的旧报纸已经全部扫描成电子版，读者可以在阅览室的电脑上按日期检索。部分版面因为年代久远，字迹比较模糊，识别时需要人工校对。",
    "周末的市集从早上八点开始，摊位沿着河边一字排开。卖菜的阿姨会把刚摘的青菜码得整整齐齐，旁边的面包店总是最早卖完。",
    "为了减少耗电，笔记本在使用电池时会自动降低处理器频率。这时翻译和识别都会变慢一些，插上电源后就会恢复正常速度。",
]
EN = [
    "Suiyi keeps every step on your own machine. When you copy a sentence, the text is sent to a small local service instead of a remote server, translated by a compact neural model, and shown in a floating window next to your cursor.",
    "Because nothing leaves the computer, the tool keeps working on a plane, in a hotel with unreliable Wi-Fi, or inside a company network that blocks cloud services.",
    "To translate part of the screen, press the shortcut, drag a rectangle around the text, and release the mouse. The recognized text and its translation appear together, so you can check both at a glance.",
    "Models are stored in a folder under your user profile and survive upgrades. Adding a new language pair only requires downloading the matching model package and restarting the service.",
    "The river market opens at eight on Saturday mornings. Farmers arrive before sunrise to stack crates of greens, and the bakery at the corner usually sells out of bread before ten.",
    "Battery saver lowers the processor clock when the laptop is unplugged. Recognition and translation become slightly slower in that mode and return to full speed once power is connected.",
    "Old newspapers in the archive have been scanned and can be searched by date from any reading room terminal. Some pages are faded, so the recognized text may need a manual check.",
    "Short sentences are translated almost instantly, while long documents are split into sentences first and then joined back together in the original order.",
]
JA = [
    "随訳はすべての処理を手元のパソコンで行います。文章をコピーすると、小さな翻訳サービスがそれを受け取り、軽量なモデルで訳してから、カーソルの近くに結果を表示します。",
    "インターネットに接続していなくても使えるので、移動中や社内ネットワークでも安心です。",
    "設定画面では、翻訳先の言語とショートカットキーを変更できます。変更はすぐに反映され、アプリを再起動する必要はありません。",
    "画面の一部を翻訳するときは、ショートカットを押して文字を四角で囲み、マウスを離します。認識した文章と訳文が並んで表示されます。",
    "土曜日の朝市は八時に始まります。川沿いに屋台が並び、角のパン屋はいつも十時前に売り切れてしまいます。",
    "電池で動いているときは、消費電力を抑えるために処理が少し遅くなります。電源につなぐと元の速さに戻ります。",
]
JA_V = [
    "春の朝は空気がやわらかく、窓を開けると鳥の声が聞こえる。",
    "小さな町の本屋には、古い地図と新しい物語が並んでいる。",
    "今日は雨なので、家で静かに本を読むことにした。",
    "駅前の喫茶店では、毎朝同じ席に同じ人が座っている。",
    "夏休みの宿題は、最後の三日間でまとめて片づけた。",
    "山の上から見る夕焼けは、町の灯りと同じ色をしていた。",
]
CODE = (
    'def translate(text: str, target: str = "en") -> str:\n'
    "    if not text.strip():\n"
    '        return ""\n'
    "    pairs = registry.route(detect(text), target)\n"
    "    return engine.run(pairs, text, beam_size=2)"
)
CODE_LONG = CODE + (
    "\n\n"
    "class OcrEngine:\n"
    "    def __init__(self, models_dir: Path) -> None:\n"
    "        self.models_dir = models_dir\n"
    "        self._backend = None\n"
    "\n"
    "    def recognize(self, image: bytes) -> OcrResult:\n"
    "        backend = self.load()\n"
    "        lines = backend(decode_image(image))\n"
    "        return OcrResult(lines, merge_paragraphs(lines))"
)


def P(text: str, size: int = 18, **kw: object) -> Para:  # noqa: N802 - 简写
    return Para(text, size, **kw)  # type: ignore[arg-type]


def T(text: str, size: int = 26) -> Para:  # noqa: N802
    return Para(text, size, kind="title")


def ui_rows(rows: list[str], size: int, x: int, y: int, gap: float = 1.6) -> Block:
    """UI 行：每行一段（菜单、状态栏、设置项）。行距与正文相同，是段落合并最难的情形。"""

    return Block(x, y, 2000, [P(r, size) for r in rows], line_gap=gap, para_gap=0.0)


def samples() -> list[Sample]:
    s: list[Sample] = []
    # ---- 400×150 -------------------------------------------------------------
    s += [
        Sample("zh_ui_small_01", "zh", "中文 UI 小字", "small",
               [ui_rows(["文件  编辑  视图  插入  格式  工具  帮助", "自动保存已开启，上次保存于下午 3:42",
                         "共 1,284 字，选中 36 字，语言：简体中文（中国）"], 12, 10, 12, 1.9)],
               note="12px 菜单与状态栏，三行各自一段，行距与正文相同"),
        Sample("zh_ui_small_02", "zh", "中文 UI 小字", "small",
               [ui_rows(["启动时自动运行", "关闭窗口时最小化到托盘", "检查更新（每周一次）", "显示翻译耗时"], 12, 16, 14, 2.2)],
               note="12px 设置项列表"),
        Sample("zh_ui_small_03", "zh", "中文 UI 小字", "small",
               [Block(12, 12, 376, [P("网络连接已断开，正在使用本地模型翻译。", 13)], line_gap=1.5),
                Block(12, 60, 376, [P("确定", 13)]), Block(90, 60, 376, [P("取消", 13)]),
                Block(12, 104, 376, [P("提示：可以在设置中关闭此通知。", 12)])],
               note="对话框：正文、两个按钮、底部提示"),
        Sample("en_ui_small_01", "en", "英文 UI 小字", "small",
               [ui_rows(["File  Edit  View  Insert  Format  Tools  Help", "Autosave is on. Last saved at 3:42 PM",
                         "1,284 words, 36 selected, English (United States)"], 12, 10, 12, 1.9)],
               note="12px 菜单与状态栏"),
        Sample("ja_ui_small_01", "ja", "日文 UI 小字", "small",
               [ui_rows(["ファイル  編集  表示  挿入  書式  ツール  ヘルプ", "自動保存がオンです。最終保存：午後 3:42",
                         "1,284 文字、36 文字を選択、日本語"], 12, 10, 12, 1.9)],
               note="12px 菜单与状态栏"),
        Sample("zh_dark_small_01", "zh", "低对比/深色主题", "small",
               [Block(14, 16, 372, [T("深色主题下的提示", 16), P("当前网络不可用，已切换到本地模型。翻译质量不受影响。", 13)], line_gap=1.6, para_gap=0.8)],
               bg="#1e1f22", fg="#8a8d93", note="深灰底浅灰字，约 4:1"),
        Sample("en_dark_small_01", "en", "低对比/深色主题", "small",
               [Block(14, 16, 372, [P("Offline mode: translations use the local model. Glossary sync resumes when the connection is back.", 13)])],
               bg="#202124", fg="#7d8288", note="深色 UI，对比度约 3.8:1"),
        Sample("zh_lowcontrast_small_01", "zh", "低对比/深色主题", "small",
               [Block(14, 20, 372, [P(ZH[1], 14)])], bg="#f4f4f4", fg="#a0a0a0", note="浅灰底浅灰字，约 2.3:1（低于无障碍标准）"),
        Sample("zh_web_small_01", "zh", "中文网页正文", "small",
               [Block(14, 14, 372, [P(ZH[3], 15)])], note="小选区里的一段正文"),
        Sample("en_doc_small_01", "en", "英文文档", "small",
               [Block(14, 14, 372, [P(EN[1], 15)])]),
        Sample("mixed_small_01", "zh", "中英混排", "small",
               [Block(14, 14, 372, [P("在 Windows 11 上按 Ctrl+Alt+T 即可调出翻译窗口；快捷键冲突时请在 Settings 里修改。", 15)])]),
        Sample("en_code_small_01", "en", "英文代码/等宽", "small",
               [Block(12, 12, 376, [Para("for pair in registry.pairs():\n    print(pair.src, pair.tgt)", 14, kind="mono")], line_gap=1.5)]),
    ]
    # ---- 1280×720 ------------------------------------------------------------
    s += [
        Sample("zh_web_720_01", "zh", "中文网页正文", "720p",
               [Block(60, 50, 1000, [T("本地翻译为什么要离线运行", 30), P(ZH[0], 20), P(ZH[1], 20)])]),
        Sample("zh_web_720_02", "zh", "中文网页正文", "720p",
               [Block(80, 60, 900, [T("三步完成框选翻译", 28), P(ZH[2], 19, face=SERIF), P(ZH[3], 19, face=SERIF)], line_gap=1.8)],
               note="宋体，行高 1.8"),
        Sample("zh_web_720_03", "zh", "中文网页正文", "720p",
               [Block(60, 60, 1100, [P(ZH[5], 18), P(ZH[6], 18), P(ZH[4], 18)], line_gap=2.0, para_gap=1.2)],
               note="行高 2.0（中文网站常见），段间距仅略大于行距"),
        Sample("en_doc_720_01", "en", "英文文档", "720p",
               [Block(60, 50, 1000, [T("Offline translation in practice", 30), P(EN[0], 19), P(EN[1], 19)])]),
        Sample("en_doc_720_02", "en", "英文文档", "720p",
               [Block(80, 60, 900, [P(EN[2], 18, face=SERIF), P(EN[3], 18, face=SERIF), P(EN[7], 18, face=SERIF)], line_gap=1.7)]),
        Sample("ja_web_720_01", "ja", "日文网页横排", "720p",
               [Block(60, 50, 1000, [T("オフライン翻訳のしくみ", 30), P(JA[0], 20), P(JA[1], 20)])]),
        Sample("ja_web_720_02", "ja", "日文网页横排", "720p",
               [Block(80, 60, 900, [P(JA[2], 19, face=SERIF), P(JA[3], 19, face=SERIF)], line_gap=1.8)], note="明朝体"),
        Sample("zh_twocol_720_01", "zh", "双栏", "720p",
               [Block(60, 40, 1160, [T("图书馆与市集", 28)]),
                Block(60, 110, 540, [P(ZH[5], 17), P(ZH[7], 17)]),
                Block(680, 110, 540, [P(ZH[6], 17), P(ZH[4], 17)])],
               note="横贯标题 + 双栏"),
        Sample("en_code_720_01", "en", "英文代码/等宽", "720p",
               [Block(40, 40, 1200, [Para(CODE, 18, kind="mono")], line_gap=1.5)], bg="#fafafa"),
        Sample("zh_dark_720_01", "zh", "低对比/深色主题", "720p",
               [Block(60, 60, 1000, [T("离线模式", 26), P(ZH[4], 18), P(ZH[7], 18)])],
               bg="#1e1f22", fg="#8a8d93"),
        Sample("mixed_720_01", "zh", "中英混排", "720p",
               [Block(60, 60, 1000, [P("在 Windows 11 上按 Ctrl+Alt+T 即可调出翻译窗口；如果快捷键被其他程序占用，请在 Settings 里修改。", 19),
                                     P("模型使用 CTranslate2 的 int8 量化版本，首次加载约 1.2 秒，之后每句翻译通常在 100 ms 以内。", 19),
                                     P("OCR 基于 RapidOCR 与 PP-OCRv6，支持中文、English 和日本語。", 19)])]),
        Sample("ja_vertical_720_01", "ja", "日文竖排", "720p", vertical=JA_V[:2], vertical_size=30, bg="#fbf8f1", fg="#1a1a1a"),
        Sample("ja_vertical_720_02", "ja", "日文竖排", "720p", vertical=JA_V[2:5], vertical_size=26, bg="#fbf8f1", fg="#1a1a1a",
               note="三段，字号 26"),
    ]
    # ---- 1920×1080 -----------------------------------------------------------
    s += [
        Sample("zh_dense_1080_01", "zh", "密集文字", "1080p",
               [Block(60, 50, 1800, [T("离线翻译常见问题", 30)] + [P(t, 18) for t in ZH])],
               note="整屏中文正文，8 段"),
        Sample("en_dense_1080_01", "en", "密集文字", "1080p",
               [Block(60, 50, 1800, [T("Frequently asked questions", 30)] + [P(t, 18) for t in EN])]),
        Sample("ja_dense_1080_01", "ja", "日文网页横排", "1080p",
               [Block(60, 50, 1500, [T("よくある質問", 30)] + [P(t, 20) for t in JA])], note="整屏日文"),
        Sample("en_twocol_1080_01", "en", "双栏", "1080p",
               [Block(80, 50, 1760, [T("Notes from the archive and the market", 32)]),
                Block(80, 130, 820, [P(EN[6], 19), P(EN[5], 19), P(EN[0], 19)]),
                Block(1020, 130, 820, [P(EN[4], 19), P(EN[3], 19), P(EN[2], 19)])]),
        Sample("zh_ui_1080_01", "zh", "中文 UI 小字", "1080p",
               [ui_rows(["文件  编辑  视图  插入  格式  工具  帮助"], 13, 12, 8),
                Block(40, 60, 400, [T("设置", 20)]),
                ui_rows(["常规", "翻译", "框选翻译", "快捷键", "模型", "关于"], 14, 40, 110, 2.4),
                Block(320, 110, 1200, [T("翻译", 18), P("目标语言：简体中文。检测到原文已经是中文时，改译为英文。", 14),
                                       P("复制后自动翻译：开启。连续复制相同内容时不会重复翻译。", 14),
                                       P("浮窗位置：跟随鼠标。按 Esc 关闭浮窗。", 14)], line_gap=1.7, para_gap=1.2),
                Block(12, 1050, 300, [P("就绪", 12)]),
                Block(900, 1050, 400, [P("模型：opus-mt-zh-en", 12)]),
                Block(1780, 1050, 200, [P("内存 312 MB", 12)])],
               note="整屏设置窗口：菜单、侧栏列表、正文、状态栏"),
        Sample("en_code_1080_01", "en", "英文代码/等宽", "1080p",
               [Block(40, 40, 1800, [Para(CODE_LONG, 18, kind="mono")], line_gap=1.5)], bg="#fafafa"),
        Sample("ja_vertical_1080_01", "ja", "日文竖排", "1080p", vertical=JA_V, vertical_size=34, bg="#fbf8f1", fg="#1a1a1a",
               note="六段，字号 34"),
    ]
    return s + holdout_samples()


def holdout_samples() -> list[Sample]:
    """#75 留出集：UI 类样例，布局与文字都不同于上面的样例。调段落规则时不看，只在最后单独报指标。"""

    h: list[Sample] = [
        Sample("ho_en_menu_small_01", "en", "英文 UI 小字", "small",
               [ui_rows(["Cut", "Copy", "Paste", "Select all", "Translate selection", "Search the web"], 12, 20, 8, 1.75)],
               note="右键菜单，6 项"),
        Sample("ho_zh_menu_small_01", "zh", "中文 UI 小字", "small",
               [ui_rows(["新建标签页", "新建窗口", "历史记录", "下载内容", "书签管理器"], 13, 24, 10, 2.0)],
               note="浏览器菜单，5 项"),
        Sample("ho_ja_status_small_01", "ja", "日文 UI 小字", "small",
               [ui_rows(["保存  元に戻す  やり直し  印刷", "オフラインで作業中", "変更は自動的に保存されます"], 12, 12, 16, 2.0)],
               note="工具栏 + 状态两行"),
        Sample("ho_zh_chat_720_01", "zh", "中文 UI 小字", "720p",
               [ui_rows(["王小明", "李华", "产品讨论组", "文件传输助手"], 15, 30, 40, 3.2),
                Block(420, 60, 260, [P("明天下午三点的评审改到四点了，会议室不变，记得带上打印好的材料", 15)], line_gap=1.5),
                Block(420, 200, 260, [P("好的收到，我顺便把上周的数据也整理一下", 15)], line_gap=1.5)],
               note="聊天：联系人列表 + 两个窄气泡（气泡内换行的短行属于同一段）"),
        Sample("ho_en_settings_720_01", "en", "英文 UI 小字", "720p",
               [Block(40, 30, 400, [T("Preferences", 20)]),
                ui_rows(["General", "Appearance", "Shortcuts", "Languages", "Privacy", "Updates"], 14, 40, 90, 2.3),
                Block(320, 90, 700, [T("Appearance", 18),
                                     P("Choose how the floating window looks. The theme follows the system setting unless you pick one below, and the font size applies to both the original text and the translation.", 14)],
                      line_gap=1.6, para_gap=1.2),
                ui_rows(["Theme: System", "Font size: 14", "Window opacity: 95%"], 14, 320, 260, 2.2)],
               note="设置页：侧栏 + 说明正文 + 选项行"),
        Sample("ho_zh_filemgr_1080_01", "zh", "中文 UI 小字", "1080p",
               [ui_rows(["桌面", "下载", "文档", "图片", "音乐", "回收站"], 14, 24, 80, 2.4),
                ui_rows(["季度报告终稿.docx", "会议纪要 0921.txt", "产品截图（高清）.png", "安装包备份.zip", "旅行照片"], 14, 260, 80, 2.2),
                Block(24, 1050, 600, [P("5 个项目，已选择 1 个", 12)])],
               note="文件管理器：侧栏 + 文件列表 + 状态栏"),
        Sample("ho_en_lang_small_01", "en", "英文 UI 小字", "small",
               [ui_rows(["English (United States)", "简体中文", "日本語", "Deutsch", "Français"], 12, 16, 10, 1.9)],
               note="语言下拉列表（混合文字）"),
        Sample("ho_zh_form_720_01", "zh", "中文 UI 小字", "720p",
               [Block(440, 120, 400, [T("登录随译账户", 22)]),
                ui_rows(["用户名", "密码", "记住我，下次自动登录", "忘记密码？"], 15, 440, 200, 2.6),
                Block(440, 420, 400, [P("登录即表示你同意用户协议和隐私政策，本地翻译功能无需登录也可使用。", 13)], line_gap=1.6)],
               note="登录表单：标签行 + 底部说明（一段正文）"),
        Sample("ho_ja_settings_720_01", "ja", "日文 UI 小字", "720p",
               [ui_rows(["一般", "表示", "ショートカット", "言語", "詳細設定"], 15, 40, 60, 2.4),
                Block(300, 60, 800, [T("ショートカット", 18),
                                     P("範囲を選んで翻訳：Ctrl+Alt+T", 14), P("クリップボードを翻訳：Ctrl+Alt+C", 14),
                                     P("翻訳ウィンドウを閉じる：Esc", 14)], line_gap=1.7, para_gap=0.7)],
               note="日文设置：侧栏 + 快捷键列表"),
        Sample("ho_mixed_toolbar_1080_01", "zh", "中文 UI 小字", "1080p",
               [ui_rows(["开始  插入  设计  布局  引用  审阅  视图"], 13, 16, 10),
                ui_rows(["宋体", "五号", "加粗", "居中"], 13, 16, 50, 2.0),
                Block(400, 200, 1100, [T("第三章 实验结果", 24),
                                       P("本章介绍离线翻译在三种硬件上的实验结果，包括平均耗时、峰值内存和译文质量评分。所有实验均在断网环境下完成，每组重复五次取中位数。", 16)],
                      line_gap=1.7, para_gap=1.0),
                Block(16, 1052, 400, [P("第 3 页，共 12 页", 12)]),
                Block(1700, 1052, 200, [P("字数：4,862", 12)])],
               note="文档编辑器：功能区 + 竖排工具项 + 正文 + 状态栏"),
    ]
    for sample in h:
        sample.split = "holdout"
    return h


def main() -> None:
    images = ROOT / "images"
    images.mkdir(parents=True, exist_ok=True)
    manifest = []
    for sample in samples():
        img, expected = render(sample)
        path = images / f"{sample.id}.png"
        img.save(path, optimize=True)
        manifest.append(
            {
                "id": sample.id,
                "file": f"images/{sample.id}.png",
                "lang": sample.lang,
                "category": sample.category,
                "size_class": sample.size,
                "width": img.width,
                "height": img.height,
                "bytes": path.stat().st_size,
                "paragraphs": expected,
                "source": "自制：scripts/make_ocr_samples.py 用 PIL 渲染，文字原创",
                "license": "CC0-1.0",
                "note": sample.note,
                "split": sample.split,
            }
        )
    out = {"schema_version": 1, "samples": manifest}
    (ROOT / "samples.json").write_text(json.dumps(out, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    total = sum(item["bytes"] for item in manifest)
    print(f"{len(manifest)} 张，共 {total / 1024:.0f} KB → {ROOT}")


if __name__ == "__main__":
    main()
