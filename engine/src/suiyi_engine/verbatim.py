"""不翻译片段（#101）：找出原文里要逐字保留的代码、标识符、路径等，并把整段切成「照抄」和「要翻译」两类。

分两层：

- **行 / 块**（:func:`split_blocks`）：围栏代码块（```` ``` ```` / ``~~~``）、代码行、
  JSON / YAML 行、堆栈行、命令行整行照抄，不送模型；
  日志行的「时间戳 + 级别 + 线程 / logger」前缀和 Markdown 的列表、标题、引用标记照抄，
  后面的消息照常翻译。代码块里的注释也不翻译：代码块是拿来复制运行的，
  改了注释会让代码和原文对不上，而且单独一行注释缺上下文，模型容易译错。
- **行内片段**（:func:`find_spans`）：包名、全限定类名、驼峰 / 下划线标识符、函数调用、Windows /
  Unix 路径、URL、邮箱、版本号、命令与参数、环境变量、哈希 / UUID、行内代码、行内 JSON、HTML 标签、
  正则、错误码、emoji。翻译时由 :mod:`suiyi_engine.terms` 负责保证它们原样出现在译文里。

:func:`is_verbatim_text` 判断整段是否几乎全是代码或标识符：是的话服务直接原样返回，不经过模型。

只用标准库和正则，不做语法分析。规则宁可漏，不可错：普通句子里的 e.g.、i.e.、「读/写」、
「PNG/JPEG」、句末的「word.」都不会被当成代码（见单测）。
"""

from __future__ import annotations

import re
from collections.abc import Sequence
from dataclasses import dataclass

__all__ = ["Block", "Span", "find_spans", "is_verbatim_text", "split_blocks"]

_CJK = "\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff"
_FULL = "\u3000-\u303f\uff00-\uffef"
_HAS_CJK = re.compile(rf"[{_CJK}]")
_STOP = rf"\s<>\"'`{_CJK}{_FULL}"


@dataclass(frozen=True, slots=True)
class Span:
    """原文里要逐字保留的一段：``text[start:end]``，``kind`` 是类别（评测与日志用）。"""

    start: int
    end: int
    kind: str


@dataclass(frozen=True, slots=True)
class Block:
    """整段切出来的一块。``translate`` 为假时原样照抄（含换行、缩进）。"""

    text: str
    translate: bool


# ---------------------------------------------------------------- 行内片段

_EXT = (
    r"json|ya?ml|py|pyi|java|kt|kts|js|mjs|cjs|ts|tsx|jsx|gradle|xml|toml|ini|cfg|conf|md|txt|"
    r"log|sh|bash|zsh|ps1|psm1|bat|cmd|exe|dll|msi|apk|aab|ipa|so|dylib|c|cc|cpp|h|hpp|cs|"
    r"csproj|sln|go|rs|rb|php|html?|css|scss|less|vue|sql|csv|tsv|lock|zip|gz|tgz|7z|tar|"
    r"png|jpe?g|gif|svg|webp|ico|pdf|docx?|xlsx?|pptx?|jar|war|plist|pem|crt|key|env|mm|swift|"
    r"dart|lua|pl|r|ipynb|bin|onnx|spm|npz|safetensors|gguf"
)
_CMD_STRONG = (
    "git|npm|npx|pnpm|yarn|pip|pip3|pipx|uv|poetry|conda|docker|podman|kubectl|helm|adb|fastboot|"
    "brew|sudo|apt|apt-get|yum|dnf|pacman|curl|wget|cargo|rustup|mvn|gradle|gradlew|./gradlew|"
    "pod|flutter|dotnet|winget|choco|scoop|ffmpeg|ffprobe|systemctl|journalctl|ssh|scp|rsync|"
    "gh|kubeadm|terraform|ansible|vagrant|nginx|redis-cli|psql|mysql|sqlite3|openssl|keytool|"
    "cmake|ninja|bazel|go|rustc|gcc|g\\+\\+|clang|javac|jar|node|deno|bun|python|python3|py|"
    "java|ruby|perl|php|make|cd|ls|cp|mv|rm|mkdir|rmdir|chmod|chown|cat|grep|find|tar|unzip|"
    "zip|echo|export|setx|touch|kill|pkill|ps|top|df|du|ln|which|whoami|ping|nslookup|ipconfig|"
    "ifconfig|netstat|tracert|traceroute|reg|sc|net|wsl|powershell|pwsh|cmd"
)
# 这些命令名也是常见英文单词或太短：后面必须紧跟一个像代码的参数才算命令
_CMD_WEAK = frozenset(
    "go make cd ls cp mv rm cat find echo export touch kill ps top df du ln which ping reg sc net "
    "cmd py java node python python3 ruby perl php jar zip tar grep mkdir unzip ssh scp pod helm "
    "nginx mysql psql terraform ansible vagrant gcc clang cmake ninja bazel deno bun brew".split()
)
_EN_STOPWORDS = frozenset(
    "to and then before after for in on with the a an if when which that is are was were will "
    "would can could should from again first instead or but so until while because as of at by "
    "it this these those your you we they he she its their our once also".split()
)

_PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    ("inline_code", re.compile(r"`[^`\n]+`")),
    (
        "html",
        re.compile(
            r"</?[A-Za-z][A-Za-z0-9-]*(?:\s+[A-Za-z_:][\w:.-]*(?:\s*=\s*(?:\"[^\"\n]*\"|'[^'\n]*'"
            r"|[^\s>\"']+))?)*\s*/?>"
        ),
    ),
    ("url", re.compile(rf"(?:(?:https?|ftp|file|wss?)://|www\.)[^{_STOP}]+", re.IGNORECASE)),
    ("email", re.compile(r"(?<![\w.+-])[\w.+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")),
    (
        "path_win",
        re.compile(
            r"(?:(?<![\w%])[A-Za-z]:\\|%[A-Za-z_][\w()]*%\\|\\\\[\w.$-]+\\)"
            r"(?:[^\\\s<>\"|?*\u3000-\u303f\uff00-\uffef" + _CJK + r"]+"
            r"(?:\x20[^\\\s<>\"|?*\u3000-\u303f\uff00-\uffef" + _CJK + r"]+)*\\)*"
            r"[^\\\s<>\"|?*\u3000-\u303f\uff00-\uffef" + _CJK + r"]*"
        ),
    ),
    (
        "uuid",
        re.compile(
            r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b"
        ),
    ),
    ("env", re.compile(r"%[A-Za-z_][\w()]*%|\$\{[A-Za-z_]\w*\}|\$env:[A-Za-z_]\w*|\$[A-Za-z_]\w*")),
    ("assign", re.compile(r"(?<![\w.-])[A-Za-z_][\w.-]*=(?![\"'])[^\s,;<>\"'，。；：、）]+")),
    ("pkg_version", re.compile(r"(?<![\w.-])[A-Za-z][\w.-]*(?:==|>=|<=|~=|@)\d[\w.+-]*")),
    (
        "path_unix",
        re.compile(
            r"(?:(?<![\w/.~:-])(?:~|\.{1,2})?/(?:[\w.@+-]+/)*[\w.@+-]*[\w/]"
            rf"|(?<![\w/.-])[\w.-]+(?:/[\w.@+-]+)+\.(?:{_EXT})\b)"
        ),
    ),
    ("hex", re.compile(r"\b0x[0-9A-Fa-f]+\b")),
    (
        "error_code",
        re.compile(r"\bCVE-\d{4}-\d{4,}\b|\b[A-Z]{2,5}-\d{3,6}\b|\b[A-Z]{1,3}\d{3,5}\b"),
    ),
    (
        "version",
        re.compile(
            r"(?<![\w.])v?\d+(?:\.\d+){2,3}(?:[-+][0-9A-Za-z]+(?:[.+-][0-9A-Za-z]+)*)?(?![\w.]*\w)|\bv\d+\.\d+\b"
        ),
    ),
    (
        "flag",
        re.compile(
            r"(?<![\w-])--[A-Za-z][\w-]*(?:=[^\s，。；、）]+|\x20(?!-)(?=[^\s]*[\d,./:=])[^\s，。；、）]+)?"
        ),
    ),
    (
        "call",
        re.compile(
            r"(?<![\w.$])[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*\((?!(?:e?s)\))[^()\n]{0,60}\)"
            r"(?:\.[A-Za-z_$][\w$]*\([^()\n]{0,60}\))*"
        ),
    ),
    ("dotted", re.compile(r"(?<![\w.@/-])[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*){2,}(?![\w(])")),
    (
        "package",
        re.compile(
            r"(?<![\w.@/-])(?:com|org|net|io|cn|me|dev|app|android|androidx|java|javax|kotlin|kotlinx)"
            r"\.[a-z_][\w$]*(?![\w(])"
        ),
    ),
    ("file", re.compile(rf"(?<![\w./-])[\w-]+(?:\.[\w-]+)*\.(?:{_EXT})\b(?![.\w])")),
    ("hash", re.compile(r"\b(?=[0-9a-f]*\d)(?=[0-9a-f]*[a-f])[0-9a-f]{7,64}\b")),
    ("snake", re.compile(r"(?<![\w$])[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]*[A-Za-z0-9](?![\w$])")),
    (
        "camel",
        re.compile(
            r"\b(?!(?:macOS|iOS|iPadOS|watchOS|tvOS|visionOS|iPhone|iPad|iPod|iCloud|iTunes|eBay|jQuery|eSIM|iMac)\b)[a-z]+[A-Z][A-Za-z0-9]*\b"
        ),
    ),
    (
        "pascal",
        re.compile(
            r"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b|\b[A-Z]{2,}[a-z][a-z0-9]+(?:[A-Z][a-z0-9]*)*\b"
        ),
    ),
    (
        "regex",
        re.compile(
            r"(?<!\S)(?=[^\s]*(?:\\[dwsbDWSB]|\[[^\]\s]+\][*+?{]|\(\?[imsx:=!<]|\^\[|\]\{\d))"
            r"[^\s，。；：、）（]+"
        ),
    ),
    (
        "emoji",
        re.compile(
            "(?:[\U0001f000-\U0001faff\u2600-\u27bf\u2b00-\u2bff\u2300-\u23ff]\ufe0f?"
            "(?:\u200d[\U0001f000-\U0001faff\u2600-\u27bf]\ufe0f?)*)+"
        ),
    ),
]
_CMD_START = re.compile(rf"(?<![\w./=:-])(?:{_CMD_STRONG})(?![\w-])")
_CODEISH = re.compile(r"[-./=:~@\\$&|<>\d_*]")
_TRAIL = ".,;:!?"


def _json_spans(text: str) -> list[tuple[int, int]]:
    """行内 JSON 对象 / 数组：``{"k": v}``、``[{...}]``，括号配平且在同一行。"""

    spans: list[tuple[int, int]] = []
    index = 0
    while index < len(text):
        char = text[index]
        if char in "{[" and re.match(r"[{\[]\s*[\"{\[]", text[index:]):
            depth = 0
            quote = False
            for cursor in range(index, len(text)):
                current = text[cursor]
                if current == "\n":
                    break
                if quote:
                    if current == "\\":
                        continue
                    if current == '"':
                        quote = False
                    continue
                if current == '"':
                    quote = True
                elif current in "{[":
                    depth += 1
                elif current in "}]":
                    depth -= 1
                    if depth == 0:
                        body = text[index : cursor + 1]
                        if ":" in body or body.startswith("["):
                            spans.append((index, cursor + 1))
                        index = cursor
                        break
        index += 1
    return spans


def _command_spans(text: str) -> list[tuple[int, int]]:
    """行内命令：命令名后面跟着的参数一起保留。

    中文里一直延伸到下一个中日文字符或全角标点；英文里遇到常见虚词（to、and、before……）
    或句末标点就停。``go`` / ``make`` / ``cd`` 这类也是普通单词的命令，后面必须紧跟像代码的参数。
    """

    spans: list[tuple[int, int]] = []
    for match in _CMD_START.finditer(text):
        name = match.group(0)
        tokens = list(re.finditer(r"\S+", text[match.end() :]))
        end = match.end()
        pieces: list[str] = []
        for token in tokens:
            gap = text[end : match.end() + token.start()]
            if not re.fullmatch(r" +", gap):
                break
            word = token.group(0)
            if _HAS_CJK.search(word) or re.search(rf"[{_FULL}]", word):
                cut = re.search(rf"[{_CJK}{_FULL}]", word)
                if cut and cut.start() > 0:
                    pieces.append(word[: cut.start()])
                    end = match.end() + token.start() + cut.start()
                break
            bare = word.rstrip(_TRAIL)
            if bare.lower() in _EN_STOPWORDS or (bare[:1].isupper() and not _CODEISH.search(bare)):
                break
            if not bare:
                break
            pieces.append(bare)
            end = match.end() + token.start() + len(bare)
            if bare != word:  # 句末标点：命令到此为止
                break
        if not pieces:
            continue
        if (
            name in _CMD_WEAK
            and not _CODEISH.search(pieces[0])
            and not _weak_trigger(text, match.start())
        ):
            continue
        if not any(_CODEISH.search(piece) for piece in pieces) and len(pieces) > 3:
            continue
        spans.append((match.start(), end))
    return spans


_TRIGGER_WORDS = frozenset(
    "run execute type enter use try then 运行 执行 输入 用 使用 敲 先".split()
)


def _weak_trigger(text: str, start: int) -> bool:
    """``cd`` / ``make`` 这类命令在行首、中文后面、冒号 / 反引号后面，或 run / 运行之后才算命令。"""

    before = text[:start].rstrip()
    if not before or before.endswith(("\n", ":", "：", "`", "$", "&&", ";", "|")):
        return True
    if _HAS_CJK.match(before[-1]):
        return True
    word = re.search(r"(\S+)$", before)
    return word is not None and word.group(1).lower().strip(",") in _TRIGGER_WORDS


def find_spans(text: str) -> list[Span]:
    """行内要逐字保留的片段，按位置排序、互不重叠（长的优先）。"""

    candidates: list[Span] = []
    for kind, pattern in _PATTERNS:
        for match in pattern.finditer(text):
            start, end = match.start(), match.end()
            if kind in ("url", "path_unix", "path_win", "regex", "file", "assign"):
                end = _trim_tail(text, start, end, kind)
            if kind == "hash" and _looks_like_word(match.group(0)):
                continue
            if kind == "pascal" and match.group(0).endswith("s") and match.group(0)[:-1].isupper():
                continue  # IDs、APIs
            if end > start:
                candidates.append(Span(start, end, kind))
    candidates += [Span(start, end, "json") for start, end in _json_spans(text)]
    candidates += [Span(start, end, "cli") for start, end in _command_spans(text)]
    candidates.sort(key=lambda span: (-(span.end - span.start), span.start))
    chosen: list[Span] = []
    for span in candidates:
        if any(span.start < other.end and other.start < span.end for other in chosen):
            continue
        chosen.append(span)
    chosen.sort(key=lambda span: span.start)
    merged: list[Span] = []
    for span in chosen:  # 紧挨着的片段（<p></p>、🎉🚀）合成一个，少用占位符
        if merged and merged[-1].end == span.start:
            merged[-1] = Span(merged[-1].start, span.end, merged[-1].kind)
        else:
            merged.append(span)
    return merged


def _trim_tail(text: str, start: int, end: int, kind: str) -> int:
    while end > start and text[end - 1] in _TRAIL:
        end -= 1
    if kind in ("url", "path_unix", "path_win", "regex") and end > start and text[end - 1] == ")":
        body = text[start:end]
        if body.count("(") < body.count(")"):
            end -= 1
    return end


def _looks_like_word(token: str) -> bool:
    """``decade``、``facade`` 这类只由 a–f 组成的单词不是哈希（至少要有数字，由正则保证）。"""

    return len(token) < 7


# ---------------------------------------------------------------- 行 / 块

_FENCE = re.compile(
    r"^[ \t]*(```|~~~)[^\n]*\n(?:.*?\n)?[ \t]*\1[ \t]*(?:\n|$)", re.MULTILINE | re.DOTALL
)
_LOG_PREFIX = re.compile(
    r"""^\s*(?:
        \[?\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?\]?
        (?:\s+\[?(?:TRACE|DEBUG|INFO|NOTICE|WARN|WARNING|ERROR|FATAL|CRITICAL|SEVERE)\]?)
        (?:\s+\[[^\]\n]*\])?
        (?:\s+[\w.$/-]+(?:\[\d+\])?(?:\s+-|:))?
      |
        \d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+\d+\s+\d+\s+[VDIWEFA]\s+[^:\n]+:
      |
        [VDIWEF]/[\w.$-]+\(\s*\d+\):
    )\s+""",
    re.VERBOSE,
)
_STACK = re.compile(
    r"""^\s*(?:
        at\s+[\w$.<>/]+\(.*\)\s*$
      | \.\.\.\s+\d+\s+more\s*$
      | File\s+"[^"]+",\s+line\s+\d+(?:,\s+in\s+\S+)?\s*$
      | Traceback\s+\(most\s+recent\s+call\s+last\):\s*$
      | \#\d+\s+0x[0-9a-fA-F]+\s
    )""",
    re.VERBOSE,
)
_MD_PREFIX = re.compile(r"^(\s*(?:#{1,6}\s+|[-*+]\s+(?:\[[ xX]\]\s+)?|\d{1,3}[.)]\s+|>\s+))(?=\S)")
_KEYWORD = re.compile(
    r"^\s*(?:def|class|import|from\s+[\w.]+\s+import|return|elif|else|try|except|finally|with|"
    r"yield|raise|async|await|public|private|protected|internal|static|final|const|let|var|"
    r"function|package|using|namespace|fn|func|val|interface|struct|enum|impl|#include|#define|"
    r"@\w+|if|for|while|switch|case|do)\b"
)
_SQL = re.compile(
    r"^\s*(?:SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|WITH|GRANT)\b.*\b(?:FROM|INTO|SET|TABLE|"
    r"WHERE|VALUES|ON|AS)\b"
)
_YAML_KV = re.compile(
    r"""^\s*(?:-\s+)?(?:[a-z_][\w.-]*|"[^"\n]+"|'[^'\n]+'):(?:\s+(?:"[^"\n]*"|'[^'\n]*'|[^\s#]+))?\s*(?:\#.*)?$"""
)
_YAML_ITEM = re.compile(r"""^\s*-\s+(?:"[^"\n]*"|'[^'\n]*'|[^\s]+)\s*$""")
_COMMENT = re.compile(r"^\s*(?://|#|--|;|/\*|\*|\*/)")
_ASSIGN = re.compile(r"^\s*[A-Za-z_$][\w$.\[\]'\"]*\s*(?:[-+*/%|&^]|<<|>>|\?\?)?=(?!=)\s*\S")
_CODE_END = re.compile(r"(?:[{;(\[,]|=>|->|\):|:\s*#.*)\s*$")
_CLOSERS_ONLY = re.compile(r"^\s*[)\]}]+[;,)]*\s*$")
_PROMPT = re.compile(r"^\s*(?:\$|PS [^>\n]*>|C:\\[^>\n]*>|>>>|\.\.\.)\s+\S")
_SENT_END = re.compile(r"[.!?。！？]\s*$")
_PLAIN_WORDS = re.compile(r"(?:\b[A-Za-z]{2,}\b[,]?\s+){3}\b[A-Za-z]{2,}\b")


def _line_kind(line: str) -> str:
    """``code`` / ``comment`` / ``prose`` / ``blank``。"""

    stripped = line.strip()
    if not stripped:
        return "blank"
    if _STACK.match(line) or _PROMPT.match(line):
        return "code"
    has_cjk = _HAS_CJK.search(stripped) is not None
    if _COMMENT.match(line) and (not _MD_PREFIX.match(line) or line[:1] in " \t"):
        return "comment"
    if _COMMENT.match(line) and stripped.startswith(("//", "/*", "*/")):
        return "comment"
    if has_cjk:
        return "prose" if _covered_ratio(stripped) < 1.0 else "code"
    if _CLOSERS_ONLY.match(line) or _SQL.match(line):
        return "code"
    if _YAML_KV.match(line) or (_YAML_ITEM.match(line) and _CODEISH.search(stripped)):
        return "code"
    if stripped.startswith(("{", "[")) and stripped.endswith(("{", "[", "}", "]", "},", "],", ",")):
        return "code"
    if re.match(r'^\s*"[^"\n]+"\s*:', line):
        return "code"
    if _KEYWORD.match(line) and (
        _CODE_END.search(stripped) or re.search(r"[()=:{}\[\];]", stripped)
    ):
        if not _SENT_END.search(stripped) or stripped.endswith(";"):
            return "code"
    if _ASSIGN.match(line) and not _PLAIN_WORDS.search(stripped):
        return "code"
    if (
        _CODE_END.search(stripped)
        and re.search(r"[(){}\[\];=]", stripped)
        and not _PLAIN_WORDS.search(stripped)
    ):
        return "code"
    if _command_line(stripped):
        return "code"
    if _covered_ratio(stripped) >= 1.0:
        return "code"
    return "prose"


def _command_line(stripped: str) -> bool:
    spans = _command_spans(stripped)
    return bool(spans) and spans[0][0] == 0 and _rest_is_code(stripped, spans)


def _rest_is_code(text: str, spans: list[tuple[int, int]]) -> bool:
    cursor = 0
    rest = []
    for start, end in spans:
        rest.append(text[cursor:start])
        cursor = end
    rest.append(text[cursor:])
    leftover = "".join(rest)
    return not re.search(r"[A-Za-z]{2,}", leftover) or all(
        span_ok for span_ok in [_covered_ratio(leftover.strip()) >= 1.0 or not leftover.strip()]
    )


def _covered_ratio(text: str) -> float:
    """``text`` 里去掉行内片段、空白和标点后还剩多少文字：全被覆盖时返回 1.0。"""

    if not text:
        return 1.0
    spans = find_spans(text)
    remaining = []
    cursor = 0
    for span in spans:
        remaining.append(text[cursor : span.start])
        cursor = span.end
    remaining.append(text[cursor:])
    leftover = "".join(remaining)
    letters = [char for char in leftover if char.isalnum() or _HAS_CJK.match(char)]
    if not letters:
        return 1.0
    total = [char for char in text if char.isalnum() or _HAS_CJK.match(char)]
    return 1.0 - len(letters) / max(1, len(total))


def split_blocks(text: str) -> list[Block]:
    """把整段切成照抄块和翻译块，拼起来与原文逐字相同。

    翻译块不含首尾空白（空白放进相邻的照抄块），连续的散文行留在同一块里，交给分句器按原来的规则
    合并断行。
    """

    blocks: list[tuple[str, bool]] = []
    cursor = 0
    for fence in _FENCE.finditer(text):
        if fence.start() > cursor:
            blocks += _split_lines(text[cursor : fence.start()])
        blocks.append((fence.group(0), False))
        cursor = fence.end()
    if cursor < len(text):
        blocks += _split_lines(text[cursor:])
    return _finish(blocks)


def _split_lines(text: str) -> list[tuple[str, bool]]:
    lines = text.splitlines(keepends=True)
    kinds = [_line_kind(line) for line in lines]
    # 注释行：上下相邻（跳过空行）有代码行时算代码，否则按散文（如 Markdown 标题「# 安装」）
    for index, kind in enumerate(kinds):
        if kind != "comment":
            continue
        kinds[index] = "code" if _near_code(kinds, index) else "prose"
    # YAML 的「key:」单独一行：下一行缩进更深或也是代码时才算代码
    for index, line in enumerate(lines):
        if kinds[index] == "code" and re.match(r"^\s*[A-Za-z_][\w.-]*:\s*$", line):
            if not _near_code(kinds, index):
                kinds[index] = "prose"
    out: list[tuple[str, bool]] = []
    for line, kind in zip(lines, kinds, strict=True):
        if kind in ("code", "blank"):
            out.append((line, False))
            continue
        body = line.rstrip("\r\n")
        ending = line[len(body) :]
        prefix = _LOG_PREFIX.match(body) or _MD_PREFIX.match(body)
        if prefix and prefix.end() < len(body):
            out.append((body[: prefix.end()], False))
            out.append((body[prefix.end() :], True))
            out.append((ending, False))
            out.append(("", False))  # 前缀行独占一块，不和下一行合并
            continue
        out.append((body, True))
        out.append((ending, False))
    return out


def _near_code(kinds: list[str], index: int) -> bool:
    for step in (-1, 1):
        cursor = index + step
        while 0 <= cursor < len(kinds) and kinds[cursor] == "blank":
            cursor += step
        if 0 <= cursor < len(kinds) and kinds[cursor] in ("code", "comment"):
            if kinds[cursor] == "code":
                return True
    return False


def _finish(raw: list[tuple[str, bool]]) -> list[Block]:
    """合并相邻的同类块；翻译块首尾空白挪进照抄块；只含空白的翻译块并进照抄块。

    相邻两个翻译块之间只有一个换行时合并成一块（散文断行），之间是前缀、代码或空行时分开。
    """

    merged: list[list] = []
    for piece, translate in raw:
        if translate:
            lead = len(piece) - len(piece.lstrip())
            tail = len(piece.rstrip())
            if lead:
                merged.append([piece[:lead], False, False])
            core = piece[lead:tail]
            if core:
                merged.append([core, True, False])
            if tail < len(piece):
                merged.append([piece[tail:], False, False])
        else:
            merged.append([piece, False, piece == ""])
    blocks: list[Block] = []
    for index, (piece, translate, _barrier) in enumerate(merged):
        if not blocks:
            if piece:
                blocks.append(Block(piece, translate))
            continue
        last = blocks[-1]
        if translate and last.translate:
            blocks[-1] = Block(last.text + piece, True)
        elif not translate and not last.translate:
            blocks[-1] = Block(last.text + piece, False)
        elif translate and not last.translate:
            # 「散文\n散文」：中间只有一个换行、且没有前缀 / 屏障时，并回上一个翻译块
            if (
                len(blocks) >= 2
                and blocks[-2].translate
                and re.fullmatch(r"\r?\n", last.text)
                and not merged[index - 1][2]
                and not _barrier_before(merged, index)
            ):
                blocks[-2:] = [Block(blocks[-2].text + last.text + piece, True)]
            else:
                blocks.append(Block(piece, True))
        elif piece:
            blocks.append(Block(piece, False))
    return blocks


def _barrier_before(merged: list[list], index: int) -> bool:
    cursor = index - 1
    while cursor >= 0 and not merged[cursor][1]:
        if merged[cursor][2]:
            return True
        cursor -= 1
    return False


def is_verbatim_text(text: str) -> bool:
    """整段几乎全是代码或标识符（没有要翻译的自然语言）时为真，服务直接原样返回。"""

    if not text.strip():
        return False
    return not any(block.translate for block in split_blocks(text))


def space_spans(text: str, keeps: Sequence[str]) -> str:
    """中文译文里，以 ASCII 开头 / 结尾的片段紧贴汉字时在两者之间补一个空格。

    占位符还原后模型常把片段和汉字连写（「设置`tsconfig.json`中」），与第一遍译文
    （「在 tsconfig.json 中」）风格不一致。只处理不翻译片段本身，不动别的中西文混排；
    emoji 和 HTML 标签不加。
    """

    for keep in sorted(set(keeps), key=len, reverse=True):
        if not keep or not keep[0].isascii() or not keep[-1].isascii():
            continue
        if keep.startswith("<") and keep.endswith(">"):
            continue  # HTML 标签紧贴内容：「<strong>粗体</strong>」不加空格
        escaped = re.escape(keep)
        text = re.sub(rf"(?<=[{_CJK}])(?={escaped})", " ", text)
        text = re.sub(rf"(?<={escaped})(?=[{_CJK}])", " ", text)
    return text
