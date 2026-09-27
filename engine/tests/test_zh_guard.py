"""中文译文的乱码 / 繁体检查（#106）。

判定函数、``ZhOutputGuard`` 的重译顺序，以及经 ``Translator`` 的直连 en→zh 与 ja→en→zh 中转。
假后端复现 tc-big 的真实输出（「This is a test.」→「硂琌代刚」），不需要模型。
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from suiyi_engine.backends.ct2_opus import zh_suppressed_tokens
from suiyi_engine.registry import ModelRecord
from suiyi_engine.translator import Translator
from suiyi_engine.zh_guard import ZhGuardStats, ZhOutputGuard
from suiyi_engine.zh_script import (
    find_issues,
    is_mojibake_char,
    is_traditional_char,
    repair_big5_mojibake,
    to_simplified,
)

ROOT = Path(__file__).resolve().parents[2]

# ---- 判定 ------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "mojibake"),
    [
        ("硂琌代刚。", "硂琌"),  # 本机 tc-big 的原始输出：「這是測試」的 Big5 按 GBK 解码
        ("硞琀代刚。", "硞琀"),  # 负责人笔记本上的输出
        ("и稱", "и"),  # 「我想你」
        ("ê琌代刚", "ê琌"),
        ("簆", "簆"),  # 「抱歉」
        ("这是 ⁇ 测试", "⁇"),
    ],
)
def test_detects_big5_mojibake(text: str, mojibake: str) -> None:
    assert "".join(find_issues(text).mojibake) == mojibake


def test_big5_as_gbk_is_exactly_the_reported_output() -> None:
    assert "這是測試".encode("big5").decode("gbk") == "硂琌代刚"


@pytest.mark.parametrize(
    "text",
    [
        "这是一个测试。",
        "张喆和王堃在镕基路",
        "啰嗦",
        "著作权",
        "後",
        "Kubernetes 集群",
        "café 与 α 值",
    ],
)
def test_normal_simplified_is_clean(text: str) -> None:
    assert find_issues(text, "café α").ok


def test_traditional_detection_and_conversion() -> None:
    issues = find_issues("這是測試")
    assert issues.traditional == ("這", "測", "試") and not issues.mojibake
    assert to_simplified("這是測試。") == "这是测试。"
    assert is_traditional_char("體") and not is_traditional_char("体")
    assert not is_mojibake_char("這")  # 真正的繁体字不算乱码


def test_characters_from_source_are_kept() -> None:
    """原文就有的繁体 / 专名不算问题，也不转换。"""

    assert find_issues("位于臺北的办公室", "The 臺北 office").ok
    assert to_simplified("臺北的體育館", "臺北") == "臺北的体育馆"
    assert find_issues("α 值", "alpha (α)").ok


def test_repair_big5_mojibake() -> None:
    assert repair_big5_mojibake("硂琌代刚。") == "这是测试。"
    assert repair_big5_mojibake("这是一个测试。") is None  # 没有乱码不动


def test_no_false_positives_on_real_chinese() -> None:
    """领域评测集的中文参考译文与原文、内置术语表的中文：一个问题字都不应报。"""

    rows = [
        json.loads(line)
        for line in (ROOT / "engine" / "eval" / "domain" / "domain_v1.jsonl")
        .read_text(encoding="utf-8")
        .splitlines()
        if line.strip()
    ]
    texts = [r["reference"] for r in rows if r["tgt_lang"] == "zh"]
    texts += [r["source"] for r in rows if r["src_lang"] == "zh"]
    glossary = ROOT / "engine" / "src" / "suiyi_engine" / "data" / "glossary_zh_en.json"
    texts.append(glossary.read_text(encoding="utf-8"))
    flagged = {t[:40]: find_issues(t) for t in texts if not find_issues(t).ok}
    assert flagged == {}


def test_suppressed_tokens_from_vocabulary() -> None:
    vocab = ["<unk>", "</s>", ">>cmn_Hant<<", "▁这是", "硂", "▁這是", "測試", "и", "代", "▁café"]
    assert zh_suppressed_tokens(vocab) == ("硂", "▁這是", "測試", "и")


# ---- ZhOutputGuard ---------------------------------------------------------------


class TcBigLike:
    """按 (beam, suppress) 返回预设译文，记录调用。"""

    def __init__(self, table: dict[tuple[int, bool], dict[str, str]]) -> None:
        self.table = table
        self.calls: list[tuple[int, bool, list[str]]] = []

    def _lookup(self, key: tuple[int, bool], sentences: list[str]) -> list[str]:
        self.calls.append((key[0], key[1], list(sentences)))
        mapping = self.table.get(key, {})
        base = self.table[(2, False)]
        return [mapping.get(s, base.get(s, f"译:{s}")) for s in sentences]

    def translate_batch(self, sentences: list[str]) -> list[str]:
        return self._lookup((2, False), sentences)

    def translate_variant(
        self, sentences: list[str], *, beam_size: int, suppress_zh: bool = False
    ) -> list[str]:
        return self._lookup((beam_size, suppress_zh), sentences)


class Fixed:
    def __init__(self, mapping: dict[str, str]) -> None:
        self.mapping = mapping
        self.calls = 0

    def translate_batch(self, sentences: list[str]) -> list[str]:
        self.calls += 1
        return [self.mapping.get(s, f"旧:{s}") for s in sentences]


def test_clean_output_is_untouched_without_retries() -> None:
    inner = TcBigLike({(2, False): {"Hello.": "你好。"}})
    guard = ZhOutputGuard(inner, fallback=lambda: pytest.fail("不该回退"))
    assert guard.translate_batch(["Hello.", "Fine."]) == ["你好。", "译:Fine."]
    assert len(inner.calls) == 1 and guard.stats.retried == 0


def test_mojibake_is_retranslated_with_other_beam() -> None:
    inner = TcBigLike(
        {
            (2, False): {"This is a test.": "硂琌代刚", "Hello.": "你好。"},
            (4, False): {"This is a test.": "这是一个测试"},
        }
    )
    guard = ZhOutputGuard(inner)
    assert guard.translate_batch(["Hello.", "This is a test."]) == ["你好。", "这是一个测试"]
    assert inner.calls[1] == (4, False, ["This is a test."])  # 只重译有问题的那句
    assert guard.stats.fixed_by == {"beam4": 1}


def test_fallback_model_then_suppression() -> None:
    inner = TcBigLike(
        {
            (2, False): {"I miss you.": "и稱", "I'm sorry.": "簆"},
            (4, False): {"I miss you.": "稱", "I'm sorry.": "簆"},
            (4, True): {"I miss you.": "我会想你的", "I'm sorry.": "对不起"},
        }
    )
    old = Fixed({"I miss you.": "我想念你"})
    stats = ZhGuardStats()
    guard = ZhOutputGuard(inner, fallback=lambda: old, stats=stats)
    out = guard.translate_batch(["I miss you.", "I'm sorry."])
    # 「稱」只剩繁体也不算干净；旧模型修好第一句，第二句（旧模型仍给乱码时）靠禁词重译
    assert out[0] == "我想念你"
    assert out[1] == "旧:I'm sorry." or out[1] == "对不起"
    assert stats.fixed_by.get("fallback_model", 0) >= 1


def test_suppression_when_no_fallback_model_installed() -> None:
    inner = TcBigLike(
        {
            (2, False): {"I miss you.": "и稱"},
            (4, False): {"I miss you.": "稱"},
            (4, True): {"I miss you.": "我会想你的"},
        }
    )
    guard = ZhOutputGuard(inner, fallback=lambda: None)
    assert guard.translate_batch(["I miss you."]) == ["我会想你的"]
    assert guard.stats.fixed_by == {"suppress": 1}


def test_unresolved_traditional_is_converted_and_mojibake_repaired() -> None:
    inner = TcBigLike(
        {
            (2, False): {"a": "這是測試", "b": "硂琌代刚"},
            (4, False): {"a": "這是測試", "b": "硂琌代刚"},
            (4, True): {"a": "這是測試", "b": "硂琌代刚"},
        }
    )
    guard = ZhOutputGuard(inner)
    assert guard.translate_batch(["a", "b"]) == ["这是测试", "这是测试"]
    assert guard.stats.converted == 1 and guard.stats.fixed_by == {"big5_repair": 1}


def test_failing_fallback_does_not_break_translation() -> None:
    inner = TcBigLike(
        {(2, False): {"x": "硂琌"}, (4, False): {"x": "硂琌"}, (4, True): {"x": "这"}}
    )

    def broken() -> object:
        raise OSError("模型损坏")

    assert ZhOutputGuard(inner, fallback=broken).translate_batch(["x"]) == ["这"]


# ---- 经 Translator：直连与 ja→en→zh 中转 ---------------------------------------------


def _install(root: Path, model_id: str, src: str, tgt: str) -> None:
    directory = root / model_id
    directory.mkdir(parents=True, exist_ok=True)
    payload = {"id": model_id, "src": src, "tgt": tgt, "src_prefix_token": None}
    (directory / "suiyi-model.json").write_text(json.dumps(payload), encoding="utf-8")
    (directory / "model.bin").write_bytes(b"fake")


TC_BIG = "opus-mt-eng-zho-tc-big-2022-05-14"


def _factory(made: dict[str, object]):
    """tc-big 复现真实的乱码输出；ja→en 把「これはテストです。」译成 "This is a test."。"""

    def factory(record: ModelRecord):
        if record.id == TC_BIG:
            backend: object = TcBigLike(
                {
                    (2, False): {
                        "This is a test.": "硂琌代刚",
                        "This is just a test.": "这只是一个测试",
                        "Run ZXQ now.": "现在运行 ZXQ",
                    },
                    (4, False): {"This is a test.": "这是一个测试"},
                }
            )
        elif record.id == "opus-mt-ja-en":
            backend = Fixed({"これはテストです。": "This is a test."})
        else:
            backend = Fixed({})
        made[record.id] = backend
        return backend

    return factory


@pytest.fixture
def models(tmp_path: Path) -> Path:
    _install(tmp_path, TC_BIG, "en", "zh")
    _install(tmp_path, "opus-mt-ja-en", "ja", "en")
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    return tmp_path


def test_translator_en_zh_this_is_a_test(models: Path) -> None:
    made: dict[str, object] = {}
    translator = Translator(models, backend_factory=_factory(made), glossary=None)
    result = translator.translate("This is a test.", "en", "zh")
    assert result.text == "这是一个测试。"
    assert result.route == (TC_BIG,) or list(result.route) == [TC_BIG]
    assert translator.translate("This is just a test.", "en", "zh").text == "这只是一个测试。"
    assert translator.zh_guard_stats.fixed_by == {"beam4": 1}


def test_translator_ja_zh_via_english_pivot(models: Path) -> None:
    made: dict[str, object] = {}
    translator = Translator(models, backend_factory=_factory(made), glossary=None)
    result = translator.translate("これはテストです。", "ja", "zh")
    assert list(result.route) == ["opus-mt-ja-en", TC_BIG]
    assert result.text == "这是一个测试。"


def test_fallback_model_is_transient(models: Path) -> None:
    """同方向装了旧模型时回退用它，但不进缓存、不占常驻名额。"""

    _install(models, "opus-mt-en-zh", "en", "zh")
    made: dict[str, object] = {}

    def factory(record: ModelRecord):
        if record.id == TC_BIG:
            backend: object = TcBigLike({(2, False): {"x.": "硂琌"}, (4, False): {"x.": "硂琌"}})
        else:
            backend = Fixed({"x.": "这是旧模型"})
        made[record.id] = backend
        return backend

    translator = Translator(models, backend_factory=factory, glossary=None)
    assert translator.translate("x.", "en", "zh").text == "这是旧模型。"
    assert "opus-mt-en-zh" in made
    assert translator.loaded_model_ids() == [TC_BIG]  # 旧模型没有常驻


def test_verbatim_traditional_span_is_not_converted(models: Path) -> None:
    """verbatim 保护的片段（这里是反引号代码）里的繁体字原样保留。"""

    made: dict[str, object] = {}
    translator = Translator(models, backend_factory=_factory(made), glossary=None)
    result = translator.translate("Run `臺灣.exe` now.", "en", "zh")
    assert "臺灣.exe" in result.text


def test_single_rare_character_is_not_big5_repaired() -> None:
    """单个可疑字（可能是正常生僻字「蹚」）重译都去不掉时原样保留，不按 Big5 乱改。"""

    table = {
        (2, False): {"x": "别蹚浑水"},
        (4, False): {"x": "别蹚浑水"},
        (4, True): {"x": "别蹚浑水"},
    }
    guard = ZhOutputGuard(TcBigLike(table))
    assert guard.translate_batch(["x"]) == ["别蹚浑水"]
    assert guard.stats.unresolved == 1
