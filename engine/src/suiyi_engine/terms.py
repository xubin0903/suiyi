"""运行时术语保护（#83）：内置术语表 + 用户术语表，占位符保护与回退。

接口约定见 Issue #83 的约定评论和 ``docs/engine/术语保护.md``。要点：

- 内置表随引擎发布（``data/glossary_zh_en.json``，#79 术语表的 en / zh 部分）。
- 用户表是 UTF-8 的 TSV：``源词<TAB>目标词<TAB>可选方向``，``#`` 开头的行是注释。
  文件改了之后下一次翻译自动重读（最多每秒检查一次修改时间和大小），也可以调 :meth:`reload`。
- 术语表永远不影响翻译：格式错误只跳过对应的行或整个用户表，原因见 :meth:`GlossaryStore.status`。
- 翻译时先正常翻一遍；原文里的术语在译文里没按约定写法出现时，只把这些术语换成占位符再翻一遍，
  写回规范写法。占位符丢失或重复时这一句用第一遍的译文，并记一条日志（只含术语 id 和原因）。
"""

from __future__ import annotations

import json
import logging
import os
import re
import threading
import time
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from importlib import resources
from pathlib import Path

from suiyi_engine.glossary import (
    FIXED,
    KEEP,
    GlossaryError,
    Protected,
    Term,
    find_terms,
    has_placeholder_like,
    parse_terms,
    protect_matches,
    restore,
    target_form,
    term_present,
)

__all__ = [
    "GlossaryStore",
    "MAX_USER_BYTES",
    "MAX_USER_ENTRIES",
    "SUPPORTED_DIRECTIONS",
    "default_user_glossary_path",
    "load_builtin",
    "parse_bool",
    "parse_user_glossary",
    "TermStats",
    "translate_with_terms",
]

logger = logging.getLogger(__name__)

SUPPORTED_DIRECTIONS = frozenset({("en", "zh"), ("zh", "en")})
MAX_USER_BYTES = 1024 * 1024
MAX_USER_ENTRIES = 5000
MAX_WARNINGS = 20
RELOAD_INTERVAL_S = 1.0
USER_FILE_NAME = "glossary.tsv"
_BUILTIN_RESOURCE = "glossary_zh_en.json"
_CJK_CHAR = re.compile(r"[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]")
_ACRONYM = re.compile(r"^[A-Z][A-Z0-9]*[A-Z][A-Z0-9]*$")
_TRUE = frozenset({"1", "true", "on"})
_FALSE = frozenset({"0", "false", "off"})


def parse_bool(raw: str | None) -> bool | None:
    """``1/0``、``true/false``、``on/off``（不区分大小写）。空或认不出时返回 ``None``。"""

    if raw is None:
        return None
    value = raw.strip().lower()
    if value in _TRUE:
        return True
    if value in _FALSE:
        return False
    return None


def default_user_glossary_path(env: Mapping[str, str] | None = None) -> Path:
    """默认用户术语表：``SUIYI_CONFIG_DIR`` → Windows ``%APPDATA%\\suiyi`` →
    ``$XDG_CONFIG_HOME/suiyi``（默认 ``~/.config/suiyi``），文件名 ``glossary.tsv``。

    与客户端设置目录（#27 的 ``SettingsPaths``）一致。
    """

    environ = os.environ if env is None else env
    override = environ.get("SUIYI_CONFIG_DIR", "").strip()
    if override:
        return Path(override) / USER_FILE_NAME
    if os.name == "nt" and environ.get("APPDATA", "").strip():
        return Path(environ["APPDATA"].strip()) / "suiyi" / USER_FILE_NAME
    xdg = environ.get("XDG_CONFIG_HOME", "").strip()
    base = Path(xdg) if xdg else Path.home() / ".config"
    return base / "suiyi" / USER_FILE_NAME


def load_builtin() -> tuple[Term, ...]:
    """随引擎发布的 zh↔en 内置术语表，id 加前缀 ``builtin:``。"""

    raw = resources.files("suiyi_engine").joinpath("data", _BUILTIN_RESOURCE).read_text("utf-8")
    data = json.loads(raw)
    if not isinstance(data, Mapping) or not isinstance(data.get("terms"), list):
        raise GlossaryError("内置术语表格式错误：缺少 terms 数组")
    items = [dict(item, id=f"builtin:{item.get('id')}") for item in data["terms"]]
    return parse_terms(items)


def _has_cjk(text: str) -> bool:
    return _CJK_CHAR.search(text) is not None


def parse_user_glossary(text: str) -> tuple[tuple[Term, ...], list[str], int]:
    """解析用户术语表文本。返回（条目，行级警告，按方向展开后的条数）。

    - 每行 ``源词<TAB>目标词<TAB>可选方向``，方向只能是 ``en-zh`` / ``zh-en``；
    - 省略方向时双向生效：含中文字符的一列是中文一侧；两列都不含中文时第 1 列当英文、
      第 2 列当中文里的写法（如 ``k8s<TAB>Kubernetes``、``Kubernetes<TAB>Kubernetes``）；
      两列都含中文时跳过；
    - ``#`` 开头的行（允许前导空白）和空行忽略，不支持行内注释；
    - 英文一侧是 2 个字母以上的全大写缩写（API、PR）时区分大小写，其余不区分。
    """

    terms: list[Term] = []
    warnings: list[str] = []
    entries = 0
    body = text[1:] if text.startswith("\ufeff") else text
    for number, line in enumerate(body.splitlines(), 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        columns = [column.strip() for column in line.split("\t")]
        if len(columns) > 3:
            warnings.append(f"第 {number} 行：多于 3 列（列之间要用 Tab 分隔）")
            continue
        if len(columns) < 2 or not columns[0] or not columns[1]:
            warnings.append(f"第 {number} 行：缺少目标词（源词和目标词之间要用 Tab 分隔）")
            continue
        first, second = columns[0], columns[1]
        raw_direction = columns[2].lower() if len(columns) == 3 else ""
        direction: tuple[str, str] | None
        if raw_direction:
            if raw_direction not in ("en-zh", "zh-en"):
                warnings.append(f"第 {number} 行：方向只能是 en-zh 或 zh-en，收到 {columns[2]!r}")
                continue
            src, tgt = raw_direction.split("-")
            direction = (src, tgt)
            forms = {src: first, tgt: second}
            if _has_cjk(forms["en"]):
                warnings.append(f"第 {number} 行：英文一侧含中文字符，方向是否写反了？")
                continue
        else:
            direction = None
            first_cjk, second_cjk = _has_cjk(first), _has_cjk(second)
            if first_cjk and second_cjk:
                warnings.append(f"第 {number} 行：两列都含中文，无法判断方向，请在第 3 列写 zh-en")
                continue
            forms = {"zh": first, "en": second} if first_cjk else {"en": first, "zh": second}
        english = forms["en"]
        kind = FIXED if _has_cjk(forms["zh"]) else KEEP
        terms.append(
            Term(
                id=f"user:{number}",
                kind=kind,
                domain="user",
                forms={"en": (english,), "zh": (forms["zh"],)},
                case_sensitive=_ACRONYM.match(english) is not None,
                direction=direction,
            )
        )
        entries += 1 if direction else 2
    return tuple(terms), warnings, entries


def _source_key(term: Term, src: str) -> str:
    form = term.forms[src][0]
    return form if term.source_case_sensitive() else form.casefold()


@dataclass(frozen=True, slots=True)
class _UserState:
    terms: tuple[Term, ...] = ()
    entries: int = 0
    error: str | None = None
    warnings: tuple[str, ...] = ()


class GlossaryStore:
    """内置表 + 用户表，线程安全，用户表按修改时间热加载。"""

    def __init__(
        self,
        user_path: Path | str | None,
        *,
        enabled: bool = True,
        builtin: Sequence[Term] | None = None,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self.user_path = Path(user_path) if user_path is not None else None
        self._requested = bool(enabled)
        self._clock = clock
        self._lock = threading.Lock()
        self._builtin_error: str | None = None
        if builtin is None:
            try:
                builtin = load_builtin()
            except (OSError, ValueError) as exc:  # 理论上不会发生；发生了只关掉术语保护
                self._builtin_error = f"内置术语表加载失败：{exc}"
                logger.warning("%s，术语保护已关闭", self._builtin_error)
                builtin = ()
        self._builtin = tuple(builtin)
        self._builtin_entries = sum(
            sum(1 for pair in SUPPORTED_DIRECTIONS if term.applies(*pair)) for term in self._builtin
        )
        self._user = _UserState()
        self._user_key: tuple[int, int] | None = None
        self._checked_at: float | None = None
        self._version = 0
        self._merged: dict[tuple[str, str, int], tuple[Term, ...]] = {}
        self.refresh(force=True)

    @property
    def enabled(self) -> bool:
        """服务端默认是否开启（内置表加载失败时强制关闭）。"""

        return self._requested and self._builtin_error is None

    def refresh(self, *, force: bool = False) -> None:
        """用户表的修改时间或大小变了就重读。``force`` 时不管间隔、不管有没有变都重读。"""

        with self._lock:
            now = self._clock()
            if not force and self._checked_at is not None:
                if now - self._checked_at < RELOAD_INTERVAL_S:
                    return
            self._checked_at = now
            key = self._stat_key()
            if not force and key == self._user_key:
                return
            self._user_key = key
            self._user = self._read_user(key)
            self._version += 1
            self._merged.clear()

    def reload(self) -> dict[str, object]:
        """立即重读用户表，返回 :meth:`status`。"""

        self.refresh(force=True)
        return self.status()

    def status(self) -> dict[str, object]:
        """``/health`` 的 ``glossary_*`` 字段。"""

        with self._lock:
            user = self._user
            errors = [item for item in (self._builtin_error, user.error) if item]
            return {
                "glossary_enabled": self.enabled,
                "glossary_builtin_entries": self._builtin_entries,
                "glossary_user_path": None if self.user_path is None else str(self.user_path),
                "glossary_user_entries": user.entries,
                "glossary_error": "；".join(errors) if errors else None,
                "glossary_warnings": list(user.warnings),
            }

    def terms_for(self, src: str, tgt: str) -> tuple[Term, ...]:
        """这个方向要用的条目：用户条目在前；同一源词两边都有时只留用户条目。"""

        if (src, tgt) not in SUPPORTED_DIRECTIONS:
            return ()
        self.refresh()
        with self._lock:
            cache_key = (src, tgt, self._version)
            cached = self._merged.get(cache_key)
            if cached is not None:
                return cached
            user = [term for term in self._user.terms if term.applies(src, tgt)]
            overridden = {_source_key(term, src) for term in user}
            builtin = [
                term
                for term in self._builtin
                if term.applies(src, tgt)
                and not any(
                    (form if term.source_case_sensitive() else form.casefold()) in overridden
                    for form in term.forms[src]
                )
            ]
            merged = tuple(user + builtin)
            self._merged[cache_key] = merged
            return merged

    def _stat_key(self) -> tuple[int, int] | None:
        if self.user_path is None:
            return None
        try:
            info = self.user_path.stat()
        except OSError:
            return None
        return (info.st_mtime_ns, info.st_size)

    def _read_user(self, key: tuple[int, int] | None) -> _UserState:
        path = self.user_path
        if path is None or key is None:
            if path is not None and path.exists() and not path.is_file():
                return self._user_error(f"用户术语表不是文件：{path}")
            return _UserState()
        if not path.is_file():
            return self._user_error(f"用户术语表不是文件：{path}")
        if key[1] > MAX_USER_BYTES:
            return self._user_error(f"用户术语表超过 {MAX_USER_BYTES // 1024} KiB：{path}")
        try:
            text = path.read_bytes().decode("utf-8-sig")
        except UnicodeDecodeError as exc:
            return self._user_error(f"用户术语表不是 UTF-8 编码（第 {exc.start} 字节）：{path}")
        except OSError as exc:
            return self._user_error(f"无法读取用户术语表：{exc}")
        terms, warnings, entries = parse_user_glossary(text)
        if len(terms) > MAX_USER_ENTRIES:
            return self._user_error(f"用户术语表超过 {MAX_USER_ENTRIES} 条：{path}")
        for warning in warnings[:MAX_WARNINGS]:
            logger.warning("用户术语表 %s", warning)
        return _UserState(terms, entries, None, tuple(warnings[:MAX_WARNINGS]))

    @staticmethod
    def _user_error(message: str) -> _UserState:
        logger.warning("%s；只使用内置术语表", message)
        return _UserState(error=message)


# ---------------------------------------------------------------- 翻译流程

# 保护方式（#83 全量 388 条中 en↔zh 部分实测，见 docs/engine/术语保护.md）：原句和「术语换成占位符」
# 的句子放进同一批一起翻（不多一次串行调用）；原句译文里术语已按约定写法出现、或只差空格 / 连字符
# （dead lock → deadlock）时用原句译文，否则用占位符版写回规范写法。能不用占位符就不用，
# 占位符周围的语法就不会变差。实测对比过「只翻占位符版」（upfront）和「先翻原句、没译对再串行翻
# 占位符版」（两遍）：前者 COMET 低约 0.5，后者段落 P95 超过 200 ms。


@dataclass(slots=True)
class TermStats:
    """术语保护的累计计数，供评测和排查使用（不含原文）。"""

    sentences: int = 0
    matched: int = 0
    protected: int = 0
    slots: int = 0
    variant_fixes: int = 0
    fallbacks: int = 0

    def reset(self) -> None:
        for name in self.__slots__:  # type: ignore[attr-defined]
            setattr(self, name, 0)


def translate_with_terms(
    sentences: list[str],
    src: str,
    tgt: str,
    terms: Sequence[Term],
    translate_batch: Callable[[list[str]], list[str]],
    stats: TermStats | None = None,
) -> list[str]:
    """按术语表翻译一批句子（方式见上面的注释）。

    占位符丢失或重复时这一句用不保护的译文，并记一条日志（术语 id 与原因，不含原文）。
    """

    record = stats if stats is not None else TermStats()
    record.sentences += len(sentences)
    protected: dict[int, Protected] = {}
    for index, source in enumerate(sentences):
        if not source.strip() or has_placeholder_like(source):
            continue
        matches = [
            match
            for match in find_terms(source, src, terms, every=True)
            if target_form(match.term, match.surface, tgt)
        ]
        if not matches:
            continue
        record.matched += 1
        item = protect_matches(source, matches, tgt)
        if item.slots:
            protected[index] = item
    if not protected:
        return translate_batch(sentences)
    return _speculative(sentences, protected, src, tgt, translate_batch, record)


def _speculative(
    sentences: list[str],
    protected: Mapping[int, Protected],
    src: str,
    tgt: str,
    translate_batch: Callable[[list[str]], list[str]],
    record: TermStats,
) -> list[str]:
    order = sorted(protected)
    raw = translate_batch(list(sentences) + [protected[index].text for index in order])
    if len(raw) != len(sentences) + len(order):
        return raw[: len(sentences)]
    outputs = list(raw[: len(sentences)])
    for index, candidate in zip(order, raw[len(sentences) :], strict=True):
        item = protected[index]
        fixed = _fix_first_pass(outputs[index], item, tgt)
        if fixed is not None:
            if fixed != outputs[index]:
                record.variant_fixes += 1
            outputs[index] = fixed
            continue
        candidate = _strip_placeholder_suffix(candidate, item, tgt)
        restored = _restore_or_log(candidate, item, src, tgt, record)
        if restored is None:
            continue
        repeated = _new_repeat(restored, outputs[index], item, tgt)
        if repeated:
            record.fallbacks += 1
            ids = " ".join(slot.term.id for slot in item.slots)
            logger.info("术语保护回退 %s→%s %s repeat", src, tgt, ids)
            continue
        outputs[index] = restored
    return outputs


# tc-big 把占位符当成型号，常在后面加「型」（「ZXQ型在CI管道通过后被合并」）。原文里术语后面
# 本来就跟着 type / model 之类的词时保留。
_TYPE_WORDS = re.compile(r"\s*(?:type|types|model|models|series|class|variant)\b", re.IGNORECASE)
_HAN_RUN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]+")
_REPEAT_N = 3


def _strip_placeholder_suffix(raw: str, item: Protected, tgt: str) -> str:
    if tgt not in ("zh", "ja"):
        return raw
    text = raw
    for slot in item.slots:
        position = item.text.find(slot.placeholder)
        after = item.text[position + len(slot.placeholder) :] if position >= 0 else ""
        if _TYPE_WORDS.match(after):
            continue
        pattern = rf"(?<![A-Za-z0-9])({re.escape(slot.placeholder)})(?![0-9])[ \t]*型"
        text = re.sub(pattern, r"\1", text, flags=re.IGNORECASE)
    return text


def _new_repeat(restored: str, first: str, item: Protected, tgt: str) -> str | None:
    """占位符版多出了首遍译文里没有的重复片段（「不稳定测试被隔离了，他们被隔离了」）时返回该片段。

    术语本身的写法不算（同一术语出现两次是正常的）。只看中日文译文里连续 3 个以上的汉字。
    """

    if tgt not in ("zh", "ja"):
        return None
    targets = sorted({slot.target for slot in item.slots}, key=len, reverse=True)

    def grams(text: str) -> dict[str, int]:
        for target in targets:
            text = text.replace(target, "|")
        counts: dict[str, int] = {}
        for run in _HAN_RUN.findall(text):
            for start in range(len(run) - _REPEAT_N + 1):
                gram = run[start : start + _REPEAT_N]
                counts[gram] = counts.get(gram, 0) + 1
        return counts

    before = grams(first)
    for gram, count in grams(restored).items():
        if count >= 2 and before.get(gram, 0) < count:
            return gram
    return None


def _restore_or_log(raw: str, item: Protected, src: str, tgt: str, record: TermStats) -> str | None:
    record.protected += 1
    record.slots += len(item.slots)
    restored, failed = restore(raw, item, tgt)
    if not failed:
        return restored
    record.fallbacks += 1
    reasons = " ".join(
        f"{slot.term.id}:{'missing' if _count(raw, slot.placeholder) == 0 else 'duplicate'}"
        for slot in failed
    )
    logger.info("术语保护回退 %s→%s %s", src, tgt, reasons)
    return None


def _fix_first_pass(output: str, item: Protected, tgt: str) -> str | None:
    """不保护的译文里术语都已按约定写法出现（只差空格 / 连字符的就地改成规范写法）时返回它，
    否则返回 ``None``。"""

    text = output
    for slot in item.slots:
        term = slot.term
        if term.kind == KEEP and slot.target == slot.source:
            if slot.source.casefold() in text.casefold():
                continue
            return None
        if term_present(text, term, tgt):
            continue
        replaced = _replace_spacing_variant(text, slot.target, tgt)
        if replaced is None:
            return None
        text = replaced
    return text


def _replace_spacing_variant(text: str, target: str, tgt: str) -> str | None:
    """英文里把「deadlock」写成「dead lock / dead-lock」这类只差空格或连字符的写法改回规范写法。"""

    if tgt != "en":
        return None
    letters = re.sub(r"[\s\-]+", "", target)
    if len(letters) < 6 or not letters.isalpha():
        return None
    body = r"[\s\-]?".join(re.escape(char) for char in letters)
    pattern = re.compile(rf"(?<![A-Za-z]){body}(?![A-Za-z])", re.IGNORECASE)
    found = list(pattern.finditer(text))
    if len(found) != 1:
        return None
    match = found[0]
    value = target
    if match.group(0)[:1].isupper() and value[:1].islower():
        value = value[:1].upper() + value[1:]
    return text[: match.start()] + value + text[match.end() :]


def _count(text: str, placeholder: str) -> int:
    pattern = re.compile(rf"(?<![A-Za-z0-9]){re.escape(placeholder)}(?![0-9])", re.IGNORECASE)
    return len(pattern.findall(text))
