"""不翻译片段保护（#101）：片段识别、按行 / 块切分、原样返回、占位符与降级、接口开关。"""

from __future__ import annotations

import json
import os
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from suiyi_engine.api import ApiSettings, create_app
from suiyi_engine.glossary import placeholder
from suiyi_engine.langdetect import Detection
from suiyi_engine.registry import ModelRecord
from suiyi_engine.segment import split_sentences
from suiyi_engine.serve import resolve_verbatim_enabled
from suiyi_engine.terms import MAX_VERBATIM_SLOTS, TermStats, translate_with_terms
from suiyi_engine.translator import Translator, restore_final_punct
from suiyi_engine.verbatim import find_spans, is_verbatim_text, space_spans, split_blocks
from suiyi_engine.zh_punct import normalize_zh_punct


def _spans(text: str) -> list[str]:
    return [text[span.start : span.end] for span in find_spans(text)]


# ---------------------------------------------------------------- 行内片段：每类至少一条


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        # Java / Android 包名
        ("请先卸载 com.tencent.mm，然后重新安装。", ["com.tencent.mm"]),
        ("Install org.mozilla.firefox from the store.", ["org.mozilla.firefox"]),
        # 全限定类名
        (
            "It throws java.lang.IllegalStateException in com.example.app.MainActivity.",
            ["java.lang.IllegalStateException", "com.example.app.MainActivity"],
        ),
        # 驼峰 / 下划线 / 帕斯卡标识符
        (
            "Call getUserProfile before rendering; see user_profile_cache.",
            ["getUserProfile", "user_profile_cache"],
        ),
        ("Implement the HttpClientFactory class.", ["HttpClientFactory"]),
        # 函数调用
        ("先调用 init()，再调用 connect(host, port)。", ["init()", "connect(host, port)"]),
        # Windows 路径（中间段可含空格）
        (
            r"Delete D:\Games\Steam\steamapps\downloading and restart.",
            [r"D:\Games\Steam\steamapps\downloading"],
        ),
        (r"把 C:\Program Files\Git\bin 加到 PATH 里。", [r"C:\Program Files\Git\bin"]),
        # Unix 路径
        (
            "Edit /etc/nginx/nginx.conf and ~/.bashrc then reload.",
            ["/etc/nginx/nginx.conf", "~/.bashrc"],
        ),
        # URL / 邮箱
        ("See https://example.com/a?b=1&c=2 for details.", ["https://example.com/a?b=1&c=2"]),
        ("Mail john.smith+billing@company.co.uk now.", ["john.smith+billing@company.co.uk"]),
        # 版本号
        ("Upgrade to v1.2.3-rc.1 before Friday.", ["v1.2.3-rc.1"]),
        # 命令行与参数
        ("Pass --max-batch-size=8 if memory is low.", ["--max-batch-size=8"]),
        ("Run git rebase -i HEAD~3 to squash.", ["git rebase -i HEAD~3"]),
        ("run cd ios && pod install again", ["cd ios && pod install"]),
        # 环境变量
        ("Set $HOME and %APPDATA% first.", ["$HOME", "%APPDATA%"]),
        # 哈希 / UUID
        (
            "The checksum is 9bfe340595d7c80fe869a2fe3dd4d03e2d9751514ff8c35724be464819b36a8e.",
            ["9bfe340595d7c80fe869a2fe3dd4d03e2d9751514ff8c35724be464819b36a8e"],
        ),
        (
            "Request id 550e8400-e29b-41d4-a716-446655440000 failed.",
            ["550e8400-e29b-41d4-a716-446655440000"],
        ),
        # Markdown 行内代码
        ("Set `beam_size` to 2.", ["`beam_size`"]),
        # 行内 JSON
        ('Returns {"error": "unsupported_pair"} on failure.', ['{"error": "unsupported_pair"}']),
        # HTML 标签
        ('Wrap it in <div class="note"> tags.', ['<div class="note">']),
        # 正则
        ("用 (?i)error|fatal 过滤日志。", ["(?i)error|fatal"]),
        ("Use ^\\d{3}-\\d{4}$ to validate.", ["^\\d{3}-\\d{4}$"]),
        # 错误码
        (
            "It fails with ERR_CONNECTION_REFUSED or E1234 or CVE-2024-3094.",
            ["ERR_CONNECTION_REFUSED", "E1234", "CVE-2024-3094"],
        ),
        # emoji
        ("Great job 🎉 ready 🚀.", ["🎉", "🚀"]),
    ],
)
def test_find_spans_per_category(text: str, expected: list[str]) -> None:
    assert _spans(text) == expected


@pytest.mark.parametrize(
    "text",
    [
        "Use it e.g. for tests, i.e. unit tests.",
        "Supports PNG/JPEG and read/write access.",
        "This is the end of the word. Next sentence here.",
        "Select the file(s) to upload.",
        "Status: done. Mr. Smith arrived at 3 p.m.",
        "The meeting ends at 10.30 and costs 3.5 dollars.",
        "I use macOS and iPhone daily.",
        "Download the models and unzip them.",
        "The pod restarts when nginx returns 502.",
        "Make sure you find the right one.",
        "他说：“好的（真的）……”——然后走了。",
    ],
)
def test_find_spans_leaves_ordinary_sentences_alone(text: str) -> None:
    assert _spans(text) == []


# ---------------------------------------------------------------- 行 / 块


def _blocks(text: str) -> list[tuple[bool, str]]:
    return [(block.translate, block.text) for block in split_blocks(text)]


def test_fenced_code_block_is_copied_and_prose_translated() -> None:
    text = 'Add this:\n\n```json\n{"a": 1}\n```\n\nThen restart.'
    assert _blocks(text) == [
        (True, "Add this:"),
        (False, '\n\n```json\n{"a": 1}\n```\n\n'),
        (True, "Then restart."),
    ]
    assert "".join(block.text for block in split_blocks(text)) == text


def test_bare_code_with_comment_is_copied_whole() -> None:
    text = "def main():\n    # parse args\n    args = parse_args()\n    return run(args)"
    assert _blocks(text) == [(False, text)]
    assert is_verbatim_text(text)


def test_log_prefix_and_stack_trace_are_kept_message_translated() -> None:
    text = (
        "2026-09-27 10:05:11 ERROR [main] c.e.App - Failed to start server\n"
        "java.lang.NullPointerException: boom\n"
        "    at com.example.App.run(App.java:42)"
    )
    assert _blocks(text) == [
        (False, "2026-09-27 10:05:11 ERROR [main] c.e.App - "),
        (True, "Failed to start server"),
        (False, "\njava.lang.NullPointerException: boom\n    at com.example.App.run(App.java:42)"),
    ]


def test_yaml_and_config_lines_between_prose() -> None:
    assert is_verbatim_text("services:\n  web:\n    image: nginx:1.25")
    text = "在配置里写上：\nretries: 3\ntimeout_ms: 1500\n这样超时后会自动重试。"
    assert _blocks(text) == [
        (True, "在配置里写上："),
        (False, "\nretries: 3\ntimeout_ms: 1500\n"),
        (True, "这样超时后会自动重试。"),
    ]


def test_markdown_heading_and_list_markers_are_kept() -> None:
    assert _blocks("# Installation\n\nRun the installer.") == [
        (False, "# "),
        (True, "Installation"),
        (False, "\n\n"),
        (True, "Run the installer."),
    ]
    assert _blocks("- First item.\n- Second item.") == [
        (False, "- "),
        (True, "First item."),
        (False, "\n- "),
        (True, "Second item."),
    ]


@pytest.mark.parametrize(
    ("text", "verbatim"),
    [
        ("com.tencent.mm", True),
        ("getUserProfile", True),
        ("https://github.com/xubin0903/suiyi/issues/101", True),
        ("git clone https://github.com/x/y.git\ncd y\npip install -e .", True),
        ('[\n  {"id": 1},\n  {"id": 2}\n]', True),
        ("Hello world.", False),
        ("Status: done", False),
        ("你好，世界。", False),
    ],
)
def test_is_verbatim_text(text: str, verbatim: bool) -> None:
    assert is_verbatim_text(text) is verbatim


# ---------------------------------------------------------------- 分句、标点、空格


def test_sentence_enders_inside_spans_do_not_split() -> None:
    text = "用 (?i)error|fatal 过滤日志里的错误行。"
    assert len(split_sentences(text, "zh")) == 2  # 默认行为不变
    assert [s.text for s in split_sentences(text, "zh", keep_spans=True)] == [text]


def test_normalize_zh_punct_keeps_spans() -> None:
    text = "先调用 connect(host, port)，再发请求。"
    assert normalize_zh_punct(text, ["connect(host, port)"]) == text


def test_space_spans_between_han_and_ascii_span() -> None:
    assert space_spans("设置`tsconfig.json`中的值", ["`tsconfig.json`"]) == (
        "设置 `tsconfig.json` 中的值"
    )
    assert space_spans("点👍表示同意", ["👍"]) == "点👍表示同意"  # emoji 不加空格
    assert space_spans("（com.tencent.mm）", ["com.tencent.mm"]) == "（com.tencent.mm）"
    assert (
        space_spans("用<strong>粗体</strong>", ["<strong>", "</strong>"])
        == "用<strong>粗体</strong>"
    )


def test_restore_final_punct_after_emoji() -> None:
    assert restore_final_punct("It is ready 🚀.", "准备好了 🚀.", "zh") == "准备好了 🚀。"


# ---------------------------------------------------------------- 占位符与降级


class FakeModel:
    def __init__(self, table: dict[str, str]) -> None:
        self.table = table
        self.calls: list[list[str]] = []

    def __call__(self, sentences: list[str]) -> list[str]:
        self.calls.append(list(sentences))
        return [self.table.get(sentence, sentence) for sentence in sentences]


SRC = "Please uninstall com.tencent.mm first."


def test_first_pass_used_when_spans_survive() -> None:
    model = FakeModel({SRC: "请先卸载 com.tencent.mm。"})
    stats = TermStats()
    out = translate_with_terms([SRC], "en", "zh", (), model, stats, [find_spans(SRC)])
    assert out == ["请先卸载 com.tencent.mm。"]
    assert len(model.calls) == 1
    assert stats.verbatim_sentences == 1 and stats.verbatim_placeholder == 0


def test_placeholder_retry_when_first_pass_breaks_span() -> None:
    slot = placeholder(0)
    model = FakeModel(
        {SRC: "请先卸载 co.tencent.mm。", f"Please uninstall {slot} first.": f"请先卸载 {slot}。"}
    )
    stats = TermStats()
    out = translate_with_terms([SRC], "en", "zh", (), model, stats, [find_spans(SRC)])
    assert out == ["请先卸载 com.tencent.mm。"]
    assert stats.verbatim_placeholder == 1 and stats.verbatim_chunked == 0


def test_placeholder_only_covers_spans_lost_in_first_pass() -> None:
    source = "Upgrade react-native to 0.74.1, then run cd ios && pod install again."
    slot = placeholder(0)
    model = FakeModel(
        {
            source: "升级到 0.74.1 后，再次运行 cd ios && pod 安装。",
            f"Upgrade react-native to 0.74.1, then run {slot} again.": (
                f"升级 react-native 到 0.74.1 后，再次运行 {slot}。"
            ),
        }
    )
    out = translate_with_terms([source], "en", "zh", (), model, None, [find_spans(source)])
    assert out == ["升级 react-native 到 0.74.1 后，再次运行 cd ios && pod install。"]


def test_chunk_fallback_when_placeholder_is_lost() -> None:
    slot = placeholder(0)
    model = FakeModel(
        {
            SRC: "请先卸载 co.tencent.mm。",
            f"Please uninstall {slot} first.": "请先卸载。",
            "Please uninstall": "请卸载",
            "first.": "先。",
        }
    )
    stats = TermStats()
    out = translate_with_terms([SRC], "en", "zh", (), model, stats, [find_spans(SRC)])
    assert out == ["请卸载 com.tencent.mm 先。"]
    assert stats.verbatim_chunked == 1


def test_too_many_spans_skip_placeholders() -> None:
    names = [f"item_{index}" for index in range(MAX_VERBATIM_SLOTS + 1)]
    source = "Remove " + ", ".join(names) + " now."
    model = FakeModel({source: "删除它们。", "Remove": "删除", "now.": "现在。"})
    stats = TermStats()
    out = translate_with_terms([source], "en", "zh", (), model, stats, [find_spans(source)])
    assert all(name in out[0] for name in names)
    assert not any("ZX" in text for call in model.calls for text in call)
    assert stats.verbatim_chunked == 1 and stats.verbatim_placeholder == 0


def test_sentence_of_only_spans_is_copied() -> None:
    source = "com.tencent.mm / com.eg.android.AlipayGphone"
    model = FakeModel({})
    stats = TermStats()
    out = translate_with_terms([source], "en", "zh", (), model, stats, [find_spans(source)])
    assert out == [source] and model.calls == [] and stats.verbatim_copied == 1


# ---------------------------------------------------------------- Translator 与接口


class CountingBackend:
    created: list[str] = []

    def __init__(self, record: ModelRecord) -> None:
        self.record = record
        CountingBackend.created.append(record.id)

    def translate_batch(self, sentences: list[str]) -> list[str]:
        table = {
            "Please uninstall com.tencent.mm first.": "请先卸载 co.tencent.mm。",
            f"Please uninstall {placeholder(0)} first.": f"请先卸载 {placeholder(0)}。",
            "Add this:": "添加以下内容：",
            "Then restart.": "然后重启。",
        }
        return [table.get(sentence, f"<{sentence}>") for sentence in sentences]


def _translator(root: Path, **kwargs: object) -> Translator:
    directory = root / "opus-mt-en-zh"
    directory.mkdir(parents=True, exist_ok=True)
    payload = {"id": "opus-mt-en-zh", "src": "en", "tgt": "zh", "src_prefix_token": None}
    (directory / "suiyi-model.json").write_text(json.dumps(payload), encoding="utf-8")
    return Translator(root, backend_factory=CountingBackend, **kwargs)  # type: ignore[arg-type]


def test_pure_code_returns_unchanged_without_loading_a_model(tmp_path: Path) -> None:
    CountingBackend.created.clear()
    translator = _translator(tmp_path)
    code = "def main():\n    return run(args)"
    result = translator.translate(code, "en", "zh")
    assert result.text == code and result.route == []
    assert translator.translate("com.tencent.mm", "en", "zh").route == []
    assert CountingBackend.created == []
    off = translator.translate("com.tencent.mm", "en", "zh", verbatim=False)
    assert off.text == "<com.tencent.mm>" and off.route == ["opus-mt-en-zh"]


def test_translator_protects_spans_and_blocks(tmp_path: Path) -> None:
    translator = _translator(tmp_path)
    assert translator.translate(SRC, "en", "zh").text == "请先卸载 com.tencent.mm。"
    assert translator.translate(SRC, "en", "zh", verbatim=False).text == "请先卸载 co.tencent.mm。"
    text = 'Add this:\n\n```json\n{"a": 1}\n```\n\nThen restart.'
    assert translator.translate(text, "en", "zh").text == (
        '添加以下内容：\n\n```json\n{"a": 1}\n```\n\n然后重启。'
    )
    disabled = _translator(tmp_path / "off", verbatim=False)
    assert disabled.translate(SRC, "en", "zh").text == "请先卸载 co.tencent.mm。"
    assert disabled.translate(SRC, "en", "zh", verbatim=True).text == "请先卸载 com.tencent.mm。"


def test_http_verbatim_field_and_health(tmp_path: Path) -> None:
    translator = _translator(tmp_path)
    app = create_app(translator, lambda _t: Detection("en", 0.99, "script"), ApiSettings())
    body = {"text": SRC, "source": "en", "target": "zh"}
    with TestClient(app) as client:
        default = client.post("/translate", json=body).json()
        off = client.post("/translate", json={**body, "verbatim": False}).json()
        bad = client.post("/translate", json={**body, "verbatim": "no"})
        code = client.post("/translate", json={**body, "text": "com.tencent.mm"}).json()
        health = client.get("/health").json()
    assert default["text"] == "请先卸载 com.tencent.mm。"
    assert off["text"] == "请先卸载 co.tencent.mm。"
    assert bad.status_code == 422
    assert code["text"] == "com.tencent.mm" and code["route"] == []
    assert health["verbatim_enabled"] is True


def test_verbatim_switch_cli_over_env_over_default(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("SUIYI_VERBATIM", raising=False)
    assert resolve_verbatim_enabled(None) is True
    monkeypatch.setenv("SUIYI_VERBATIM", "0")
    assert resolve_verbatim_enabled(None) is False
    assert resolve_verbatim_enabled(True) is True
    monkeypatch.setenv("SUIYI_VERBATIM", "maybe")
    assert resolve_verbatim_enabled(None) is True
    assert resolve_verbatim_enabled(False) is False


# ---------------------------------------------------------------- 评测集


def test_eval_set_shape() -> None:
    path = Path(__file__).resolve().parents[1] / "eval" / "verbatim" / "verbatim_v1.jsonl"
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line]
    assert len(rows) >= 150
    assert len({row["id"] for row in rows}) == len(rows)
    directions = {(row["src"], row["tgt"]) for row in rows}
    assert directions == {("en", "zh"), ("zh", "en")}
    for row in rows:
        for keep in row.get("keep", []):
            assert keep in row["text"], row["id"]
        if row["category"] == "punct":
            assert row.get("ref"), row["id"]


# ---------------------------------------------------------------- 真实模型


@pytest.mark.model
def test_issue_101_with_real_models() -> None:
    models_dir = Path(os.environ["SUIYI_MODELS_DIR"])
    if not (models_dir / "opus-mt-zh-en").is_dir():
        pytest.skip("需要 zh→en 模型")
    translator = Translator(models_dir)
    text = "微信的包名是 com.tencent.mm，支付宝的包名是 com.eg.android.AlipayGphone。"
    out = translator.translate(text, "zh", "en").text
    assert "com.tencent.mm" in out and "com.eg.android.AlipayGphone" in out
    assert translator.translate("com.tencent.mm", "zh", "en").route == []
