"""运行时术语保护（#83）：用户术语表格式、热加载、合并、保护与回退、句末标点、服务参数与接口。"""

from __future__ import annotations

import json
import logging
import os
import socket
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from suiyi_engine.api import ApiSettings, create_app
from suiyi_engine.glossary import FIXED, KEEP, Term, find_terms, protect, restore
from suiyi_engine.langdetect import Detection
from suiyi_engine.registry import ModelRecord
from suiyi_engine.serve import resolve_glossary_enabled, resolve_user_glossary
from suiyi_engine.terms import (
    MAX_USER_BYTES,
    GlossaryStore,
    TermStats,
    default_user_glossary_path,
    load_builtin,
    parse_bool,
    parse_user_glossary,
    translate_with_terms,
)
from suiyi_engine.translator import Translator, restore_final_punct


def _term(term_id: str, en: str, zh: str, kind: str = FIXED, **kwargs: object) -> Term:
    return Term(term_id, kind, "test", {"en": (en,), "zh": (zh,)}, **kwargs)  # type: ignore[arg-type]


class Clock:
    def __init__(self) -> None:
        self.now = 0.0

    def __call__(self) -> float:
        return self.now


# ---------------------------------------------------------------- 用户术语表格式


def test_parse_user_glossary_columns_comments_and_directions() -> None:
    text = (
        "\ufeff# 注释\n"
        "\n"
        "container orchestration\t容器编排\n"
        "预发布环境\tstaging environment\n"
        "Kubernetes\tKubernetes\n"
        "k8s\tKubernetes\ten-zh\n"
        "   # 前导空白的注释\n"
        "PR\t拉取请求\n"
    )
    terms, warnings, entries = parse_user_glossary(text)
    assert warnings == []
    by_id = {term.id: term for term in terms}
    assert by_id["user:3"].forms == {"en": ("container orchestration",), "zh": ("容器编排",)}
    assert by_id["user:3"].direction is None and by_id["user:3"].kind == FIXED
    # 第 1 列含中文：当作中文一侧
    assert by_id["user:4"].forms == {"en": ("staging environment",), "zh": ("预发布环境",)}
    # 两列都不含中文：原样保留类，双向
    assert by_id["user:5"].kind == KEEP and by_id["user:5"].direction is None
    assert by_id["user:6"].direction == ("en", "zh")
    # 全大写缩写区分大小写，其余不区分
    assert by_id["user:8"].case_sensitive is True
    assert by_id["user:3"].case_sensitive is False
    assert entries == 2 + 2 + 2 + 1 + 2


def test_parse_user_glossary_reports_bad_lines_and_keeps_the_rest() -> None:
    text = (
        "只有一列\n"
        "a\tb\tc\td\n"
        "cache\t缓存\tfr-zh\n"
        "缓存\t高速缓存\n"
        "缓存\tcache\ten-zh\n"
        "\t缓存\n"
        "cache hit rate\t缓存命中率\n"
    )
    terms, warnings, entries = parse_user_glossary(text)
    assert [term.id for term in terms] == ["user:7"]
    assert entries == 2
    assert [warning.split("：")[0] for warning in warnings] == [
        "第 1 行",
        "第 2 行",
        "第 3 行",
        "第 4 行",
        "第 5 行",
        "第 6 行",
    ]


def test_parse_bool_accepts_digits_words_and_rejects_others() -> None:
    assert [parse_bool(v) for v in ("1", "0", "TRUE", "false", " on ", "Off")] == [
        True,
        False,
        True,
        False,
        True,
        False,
    ]
    for raw in ("maybe", "", "yes", "no", None):
        assert parse_bool(raw) is None


# ---------------------------------------------------------------- 匹配：复数、大小写、词边界


def test_matching_handles_plural_case_and_acronyms() -> None:
    container = _term("c", "container", "容器", case_sensitive=False)
    policy = _term("p", "retention policy", "保留策略", case_sensitive=False)
    api = _term("a", "API", "接口", case_sensitive=True)
    found = find_terms(
        "Containers and a Container; retention policies; APIs but not api.",
        "en",
        [container, policy, api],
        every=True,
    )
    assert [m.surface for m in found] == [
        "Containers",
        "Container",
        "retention policies",
        "APIs",
    ]


def test_latin_terms_in_chinese_text_use_word_boundaries() -> None:
    go = Term("go", KEEP, "t", {"en": ("Go",), "zh": ("Go",)})
    assert [m.surface for m in find_terms("用 Go 写服务", "zh", [go])] == ["Go"]
    assert find_terms("用 Google 搜索", "zh", [go]) == []


def test_restore_fixes_article_before_restored_term() -> None:
    api = _term("a", "API", "应用程序接口")
    protected = protect("这是一个应用程序接口。", "zh", "en", [api])
    raw = f"This is a {protected.slots[0].placeholder}."
    text, failed = restore(raw, protected, "en")
    assert failed == [] and text == "This is an API."
    url = _term("u", "URL", "网址")
    protected = protect("一个网址", "zh", "en", [url])
    text, _failed = restore(f"An {protected.slots[0].placeholder}", protected, "en")
    assert text == "A URL"


# ---------------------------------------------------------------- 术语表存储


def test_builtin_glossary_loads_zh_en_entries() -> None:
    terms = load_builtin()
    assert len(terms) >= 200
    assert all(term.id.startswith("builtin:") for term in terms)
    store = GlossaryStore(None)
    status = store.status()
    assert status["glossary_enabled"] is True
    assert status["glossary_builtin_entries"] == 2 * len(terms)
    assert status["glossary_user_path"] is None and status["glossary_error"] is None


def test_missing_user_file_is_not_an_error(tmp_path: Path) -> None:
    store = GlossaryStore(tmp_path / "glossary.tsv", builtin=())
    status = store.status()
    assert status["glossary_user_entries"] == 0
    assert status["glossary_error"] is None
    assert status["glossary_user_path"] == str(tmp_path / "glossary.tsv")


def test_user_entries_override_builtin_and_come_first(tmp_path: Path) -> None:
    path = tmp_path / "glossary.tsv"
    path.write_text("Container\t集装箱\n", encoding="utf-8")
    builtin = [
        _term("builtin:container", "container", "容器"),
        _term("builtin:pod", "Pod", "Pod", kind=KEEP),
    ]
    store = GlossaryStore(path, builtin=builtin)
    ids = [term.id for term in store.terms_for("en", "zh")]
    assert ids == ["user:1", "builtin:pod"]
    assert store.terms_for("en", "ja") == ()


def test_user_file_hot_reloads_after_change(tmp_path: Path) -> None:
    path = tmp_path / "glossary.tsv"
    path.write_text("cache\t缓存\n", encoding="utf-8")
    clock = Clock()
    store = GlossaryStore(path, builtin=(), clock=clock)
    assert store.status()["glossary_user_entries"] == 2
    path.write_text("cache\t缓存\ncache hit rate\t缓存命中率\n", encoding="utf-8")
    os.utime(path, ns=(1, 10**18))
    clock.now = 0.5  # 1 秒内不重复检查
    assert len(store.terms_for("en", "zh")) == 1
    clock.now = 2.0
    assert len(store.terms_for("en", "zh")) == 2
    path.write_text("cache\t缓存\n", encoding="utf-8")
    os.utime(path, ns=(1, 2 * 10**18))
    assert store.reload()["glossary_user_entries"] == 2  # reload 不受间隔限制


def test_file_level_errors_keep_builtin_and_report(tmp_path: Path) -> None:
    path = tmp_path / "glossary.tsv"
    path.write_bytes("cache\t缓存\n".encode("gbk"))
    builtin = [_term("builtin:cache", "cache", "缓存")]
    store = GlossaryStore(path, builtin=builtin)
    status = store.status()
    assert "UTF-8" in str(status["glossary_error"])
    assert status["glossary_user_entries"] == 0
    assert [term.id for term in store.terms_for("en", "zh")] == ["builtin:cache"]
    path.write_bytes(b"#" * (MAX_USER_BYTES + 1))
    assert "KiB" in str(store.reload()["glossary_error"])
    path.write_text("cache\t高速缓存\n", encoding="utf-8")
    status = store.reload()
    assert status["glossary_error"] is None and status["glossary_user_entries"] == 2


def test_line_warnings_are_reported_in_status(tmp_path: Path) -> None:
    path = tmp_path / "glossary.tsv"
    path.write_text("\n".join(["坏行"] * 30 + ["cache\t缓存"]), encoding="utf-8")
    status = GlossaryStore(path, builtin=()).status()
    assert status["glossary_user_entries"] == 2
    assert len(status["glossary_warnings"]) == 20  # type: ignore[arg-type]


def test_builtin_failure_disables_protection(monkeypatch: pytest.MonkeyPatch) -> None:
    def broken() -> tuple[Term, ...]:
        raise ValueError("坏了")

    monkeypatch.setattr("suiyi_engine.terms.load_builtin", broken)
    store = GlossaryStore(None)
    assert store.enabled is False
    assert "内置术语表加载失败" in str(store.status()["glossary_error"])


def test_default_user_path_follows_config_dir(tmp_path: Path) -> None:
    assert default_user_glossary_path({"SUIYI_CONFIG_DIR": str(tmp_path)}) == (
        tmp_path / "glossary.tsv"
    )
    if os.name != "nt":
        env = {"XDG_CONFIG_HOME": str(tmp_path)}
        assert default_user_glossary_path(env) == tmp_path / "suiyi" / "glossary.tsv"


# ---------------------------------------------------------------- 保护流程


class FakeModel:
    """按映射表翻译，占位符原样抄过去，记录每次调用的批。"""

    def __init__(self, table: dict[str, str]) -> None:
        self.table = table
        self.calls: list[list[str]] = []

    def __call__(self, sentences: list[str]) -> list[str]:
        self.calls.append(list(sentences))
        return [self.table.get(sentence, sentence) for sentence in sentences]


def test_first_pass_is_kept_when_terms_are_already_right() -> None:
    engine = _term("e", "container orchestration engine", "容器编排引擎")
    model = FakeModel({"An container orchestration engine.": "一个容器编排引擎。"})
    stats = TermStats()
    out = translate_with_terms(
        ["An container orchestration engine."], "en", "zh", [engine], model, stats
    )
    assert out == ["一个容器编排引擎。"]
    assert len(model.calls) == 1  # 原句与占位符版在同一批
    assert stats.matched == 1 and stats.protected == 0


def test_placeholder_version_is_used_when_first_pass_misses_the_term() -> None:
    engine = _term("e", "container orchestration", "容器编排")
    model = FakeModel(
        {
            "Use container orchestration.": "使用集装箱管弦乐。",
            "Use ZXQ.": "使用 ZXQ。",
        }
    )
    stats = TermStats()
    out = translate_with_terms(["Use container orchestration."], "en", "zh", [engine], model, stats)
    assert out == ["使用容器编排。"]
    assert model.calls == [["Use container orchestration.", "Use ZXQ."]]
    assert stats.protected == 1 and stats.fallbacks == 0


def test_lost_placeholder_falls_back_and_logs_ids_only(caplog: pytest.LogCaptureFixture) -> None:
    secret = "机密项目的死锁问题。"
    deadlock = _term("builtin:deadlock", "deadlock", "死锁")
    model = FakeModel(
        {secret: "The secret project has a lock issue.", "机密项目的ZXQ问题。": "Oops."}
    )
    stats = TermStats()
    with caplog.at_level(logging.INFO, logger="suiyi_engine.terms"):
        out = translate_with_terms([secret], "zh", "en", [deadlock], model, stats)
    assert out == ["The secret project has a lock issue."]
    assert stats.fallbacks == 1
    assert "builtin:deadlock:missing" in caplog.text
    assert "机密" not in caplog.text and "secret" not in caplog.text


def test_spacing_variant_in_first_pass_is_fixed_without_placeholder() -> None:
    source = "这个接口在高并发下会出现死锁，需要重新设计加锁顺序。"
    deadlock = _term("builtin:deadlock", "deadlock", "死锁")
    first = "This interface will have a dead lock under high concurrency."
    model = FakeModel({source: first, source.replace("死锁", "ZXQ"): "It is ZXQ high and high."})
    stats = TermStats()
    out = translate_with_terms([source], "zh", "en", [deadlock], model, stats)
    assert out == ["This interface will have a deadlock under high concurrency."]
    assert stats.variant_fixes == 1 and stats.protected == 0


def test_type_suffix_after_placeholder_is_dropped() -> None:
    """tc-big 把占位符当型号，写出「ZXQ型」（#97）。"""

    pr = _term("pr", "pull request", "拉取请求")
    flaky = _term("flaky", "flaky test", "不稳定测试")
    source = "The pull request was merged and the flaky test was quarantined."
    model = FakeModel(
        {
            source: "Pull 请求被合并并进行片状测试。",
            "The ZXQ was merged and the ZXW was quarantined.": "ZXQ型被合并,ZXW 型被隔离。",
        }
    )
    out = translate_with_terms([source], "en", "zh", [pr, flaky], model)
    assert out == ["拉取请求被合并,不稳定测试被隔离。"]


def test_type_suffix_is_kept_when_the_source_says_type() -> None:
    engine = _term("e", "container orchestration", "容器编排")
    source = "Pick a container orchestration type."
    model = FakeModel({source: "选择集装箱类型。", "Pick a ZXQ type.": "选择一种 ZXQ型。"})
    assert translate_with_terms([source], "en", "zh", [engine], model) == ["选择一种容器编排型。"]


def test_new_repetition_in_placeholder_version_falls_back(
    caplog: pytest.LogCaptureFixture,
) -> None:
    flaky = _term("builtin:flaky", "flaky test", "不稳定测试")
    source = "The flaky test was quarantined."
    model = FakeModel(
        {source: "片状测试被隔离。", "The ZXQ was quarantined.": "ZXQ被隔离了,他们被隔离了。"}
    )
    stats = TermStats()
    with caplog.at_level(logging.INFO, logger="suiyi_engine.terms"):
        out = translate_with_terms([source], "en", "zh", [flaky], model, stats)
    assert out == ["片状测试被隔离。"]
    assert stats.fallbacks == 1
    assert "builtin:flaky repeat" in caplog.text


def test_repeated_term_itself_is_not_a_repetition() -> None:
    co = _term("co", "container orchestration", "容器编排")
    source = "Container orchestration beats manual container orchestration."
    model = FakeModel(
        {
            source: "集装箱管弦乐胜过手工集装箱管弦乐。",
            "ZXQ beats manual ZXW.": "ZXQ胜过手工ZXW。",
        }
    )
    assert translate_with_terms([source], "en", "zh", [co], model) == ["容器编排胜过手工容器编排。"]


def test_builtin_glossary_has_common_engineering_terms() -> None:
    by_id = {term.id: term for term in load_builtin()}
    assert by_id["builtin:flaky-test"].forms["zh"][0] == "不稳定测试"
    assert by_id["builtin:ci-pipeline"].forms["zh"][0] == "CI 流水线"
    assert by_id["builtin:ci-cd-pipeline"].forms["zh"][0] == "CI/CD 流水线"
    found = find_terms("The CI/CD pipeline runs every flaky test.", "en", list(by_id.values()))
    assert [match.term.id for match in found] == ["builtin:ci-cd-pipeline", "builtin:flaky-test"]


# ---------------------------------------------------------------- 句末标点


@pytest.mark.parametrize(
    ("source", "output", "tgt", "expected"),
    [
        ("It is an engine.", "它是一个引擎", "zh", "它是一个引擎。"),
        ("Is it?", "是吗", "zh", "是吗？"),
        ("你好。", "Hello", "en", "Hello."),
        ("Wait...", "等等", "zh", "等等"),
        ("It is an engine.", "它是一个引擎。", "zh", "它是一个引擎。"),
        ("Settings", "设置", "zh", "设置"),
        ("Keep it aligned.", "保持一致.", "zh", "保持一致。"),
        ("Version 2.0 is out.", "Version 2.0.", "en", "Version 2.0."),
        ("Call it (now).", "现在调用（", "zh", "现在调用（"),
    ],
)
def test_restore_final_punct(source: str, output: str, tgt: str, expected: str) -> None:
    assert restore_final_punct(source, output, tgt) == expected


# ---------------------------------------------------------------- Translator 与接口


class ModelBackend:
    def __init__(self, record: ModelRecord) -> None:
        self.record = record

    def translate_batch(self, sentences: list[str]) -> list[str]:
        table = {
            "Use container orchestration.": "使用集装箱管弦乐。",
            "Use ZXQ.": "使用 ZXQ。",
        }
        return [table.get(sentence, f"<{self.record.id}>{sentence}") for sentence in sentences]


def _install(root: Path, model_id: str, src: str, tgt: str) -> None:
    directory = root / model_id
    directory.mkdir(parents=True, exist_ok=True)
    payload = {"id": model_id, "src": src, "tgt": tgt, "src_prefix_token": None}
    (directory / "suiyi-model.json").write_text(json.dumps(payload), encoding="utf-8")


def _translator(root: Path, *, enabled: bool = True, user: Path | None = None) -> Translator:
    _install(root, "opus-mt-eng-zho-tc-big-2022-05-14", "en", "zh")
    _install(root, "opus-mt-zh-en", "zh", "en")
    store = GlossaryStore(
        user, enabled=enabled, builtin=[_term("b:co", "container orchestration", "容器编排")]
    )
    return Translator(root, backend_factory=ModelBackend, glossary=store)


def test_translator_default_and_per_call_override(tmp_path: Path) -> None:
    on = _translator(tmp_path / "a")
    assert on.translate("Use container orchestration.", "en", "zh").text == "使用容器编排。"
    off = on.translate("Use container orchestration.", "en", "zh", glossary=False)
    assert off.text == "使用集装箱管弦乐。"
    disabled = _translator(tmp_path / "b", enabled=False)
    assert (
        disabled.translate("Use container orchestration.", "en", "zh").text == "使用集装箱管弦乐。"
    )
    forced = disabled.translate_many(["Use container orchestration."], "en", "zh", glossary=True)
    assert forced[0].text == "使用容器编排。"


def _client(translator: Translator) -> TestClient:
    app = create_app(translator, lambda _t: Detection("en", 0.99, "script"), ApiSettings())
    return TestClient(app)


def test_http_glossary_field_health_and_reload(tmp_path: Path) -> None:
    user = tmp_path / "glossary.tsv"
    translator = _translator(tmp_path / "m", user=user)
    body = {"text": "Use container orchestration.", "source": "en", "target": "zh"}
    with _client(translator) as client:
        default = client.post("/translate", json=body).json()
        off = client.post("/translate", json={**body, "glossary": False}).json()
        bad = client.post("/translate", json={**body, "glossary": "yes"})
        health = client.get("/health").json()
        user.write_text("container orchestration\t容器编排调度\n坏行\n", encoding="utf-8")
        reloaded = client.post("/glossary/reload").json()
        after = client.post("/translate", json=body).json()
    assert default["text"] == "使用容器编排。"
    assert off["text"] == "使用集装箱管弦乐。"
    assert bad.status_code == 422 and bad.json()["error"]["code"] == "invalid_request"
    assert health["glossary_enabled"] is True
    assert health["glossary_builtin_entries"] == 2
    assert health["glossary_user_path"] == str(user)
    assert health["glossary_user_entries"] == 0 and health["glossary_error"] is None
    assert reloaded["glossary_user_entries"] == 2
    assert reloaded["glossary_warnings"] == ["第 2 行：缺少目标词（源词和目标词之间要用 Tab 分隔）"]
    assert after["text"] == "使用容器编排调度。"


# ---------------------------------------------------------------- 服务参数与环境变量


def test_glossary_switch_cli_over_env_over_default(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.delenv("SUIYI_GLOSSARY", raising=False)
    assert resolve_glossary_enabled(None) is True
    for raw, expected in (("0", False), ("1", True), ("false", False), ("TRUE", True)):
        monkeypatch.setenv("SUIYI_GLOSSARY", raw)
        assert resolve_glossary_enabled(None) is expected
    monkeypatch.setenv("SUIYI_GLOSSARY", "0")
    assert resolve_glossary_enabled(True) is True
    monkeypatch.setenv("SUIYI_GLOSSARY", "1")
    assert resolve_glossary_enabled(False) is False
    monkeypatch.setenv("SUIYI_GLOSSARY", "maybe")
    assert resolve_glossary_enabled(None) is True
    assert "SUIYI_GLOSSARY" in capsys.readouterr().err


def test_user_glossary_path_cli_over_env_over_default(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.delenv("SUIYI_USER_GLOSSARY", raising=False)
    monkeypatch.setenv("SUIYI_CONFIG_DIR", str(tmp_path / "cfg"))
    assert resolve_user_glossary(None) == tmp_path / "cfg" / "glossary.tsv"
    monkeypatch.setenv("SUIYI_USER_GLOSSARY", str(tmp_path / "env.tsv"))
    assert resolve_user_glossary(None) == tmp_path / "env.tsv"
    assert resolve_user_glossary(str(tmp_path / "cli.tsv")) == tmp_path / "cli.tsv"


def _cli_env_setup(monkeypatch: pytest.MonkeyPatch, root: Path) -> None:
    """``translate`` 子命令用真实 Translator / GlossaryStore，只替换推理后端。"""

    import suiyi_engine.translator as translator_module

    _install(root, "opus-mt-eng-zho-tc-big-2022-05-14", "en", "zh")
    real = translator_module.Translator

    def factory(models_dir: object, **kwargs: object) -> Translator:
        return real(models_dir, backend_factory=ModelBackend, **kwargs)  # type: ignore[arg-type]

    monkeypatch.setattr(translator_module, "Translator", factory)
    for name in ("SUIYI_GLOSSARY", "SUIYI_USER_GLOSSARY"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv("SUIYI_CONFIG_DIR", str(root / "no-config"))


def _cli_translate(capsys: pytest.CaptureFixture[str], root: Path, *extra: str) -> str:
    from suiyi_engine.__main__ import main

    argv = ["translate", "--src", "en", "--tgt", "zh", "--models-dir", str(root), *extra]
    assert main([*argv, "Use container orchestration."]) == 0
    return capsys.readouterr().out.splitlines()[0]


def test_cli_translate_reads_glossary_env_vars(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """客户端推荐用环境变量传参：SUIYI_USER_GLOSSARY 指定文件，SUIYI_GLOSSARY 控制开关。"""

    root = tmp_path / "models"
    _cli_env_setup(monkeypatch, root)
    env_file = tmp_path / "env.tsv"
    env_file.write_text("container orchestration\t容器编排（环境变量）\n", encoding="utf-8")
    cli_file = tmp_path / "cli.tsv"
    cli_file.write_text("container orchestration\t容器编排（命令行）\n", encoding="utf-8")

    monkeypatch.setenv("SUIYI_USER_GLOSSARY", str(env_file))
    assert _cli_translate(capsys, root) == "使用容器编排（环境变量）。"
    assert _cli_translate(capsys, root, "--user-glossary", str(cli_file)) == (
        "使用容器编排（命令行）。"
    )
    for off in ("0", "false", "FALSE"):
        monkeypatch.setenv("SUIYI_GLOSSARY", off)
        assert _cli_translate(capsys, root) == "使用集装箱管弦乐。"
    assert _cli_translate(capsys, root, "--glossary") == "使用容器编排（环境变量）。"
    for on in ("1", "true"):
        monkeypatch.setenv("SUIYI_GLOSSARY", on)
        assert _cli_translate(capsys, root) == "使用容器编排（环境变量）。"
    assert _cli_translate(capsys, root, "--no-glossary") == "使用集装箱管弦乐。"


def test_serve_reads_glossary_env_vars(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """老引擎会忽略这两个环境变量；新引擎的 serve 按 命令行 > 环境变量 > 默认 生效。"""

    from suiyi_engine.__main__ import main

    seen: dict[str, GlossaryStore] = {}

    class FakeTranslator:
        def __init__(self, models_dir: object, **kwargs: object) -> None:
            seen["store"] = kwargs["glossary"]  # type: ignore[assignment]
            self.registry = type("Registry", (), {"models_dir": models_dir})()

        def available_pairs(self) -> list[tuple[str, str, str]]:
            return []

        def glossary_status(self) -> dict[str, object]:
            return seen["store"].status()

    monkeypatch.setattr("suiyi_engine.serve.Translator", FakeTranslator)
    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", lambda *_args, **_kwargs: None)
    monkeypatch.setattr("suiyi_engine.serve.langdetect.warmup", lambda: None)
    env_file = tmp_path / "env.tsv"
    env_file.write_text("k8s\tKubernetes\n", encoding="utf-8")
    monkeypatch.setenv("SUIYI_USER_GLOSSARY", str(env_file))
    monkeypatch.setenv("SUIYI_GLOSSARY", "false")

    def serve(*extra: str) -> dict[str, object]:
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        argv = ["serve", "--models-dir", str(tmp_path), "--port", str(port), *extra]
        assert main(argv) == 0
        return seen["store"].status()

    status = serve()
    assert status["glossary_enabled"] is False
    assert status["glossary_user_path"] == str(env_file)
    assert status["glossary_user_entries"] == 2
    assert f"术语保护关闭：内置 608 条，用户 2 条（{env_file}）" in capsys.readouterr().out
    monkeypatch.setenv("SUIYI_GLOSSARY", "1")
    assert serve()["glossary_enabled"] is True
    assert serve("--no-glossary")["glossary_enabled"] is False
    other = tmp_path / "other.tsv"
    assert serve("--user-glossary", str(other))["glossary_user_path"] == str(other)


# ---------------------------------------------------------------- 模型升级（#83）

NEW_EN_ZH = "opus-mt-eng-zho-tc-big-2022-05-14"


def test_manifest_prefers_tc_big_and_keeps_old_en_zh_as_legacy() -> None:
    from suiyi_engine.registry import default_manifest_path, load_manifest

    manifest = load_manifest(default_manifest_path())
    assert manifest.direct[("en", "zh")] == NEW_EN_ZH
    assert manifest.direct[("zh", "en")] == "opus-mt-zh-en"
    assert manifest.known["opus-mt-en-zh"] == ("en", "zh")
    raw = json.loads(default_manifest_path().read_text(encoding="utf-8"))
    tiers = {entry["id"]: entry["tier"] for entry in raw["models"]}
    assert tiers[NEW_EN_ZH] == "mvp" and tiers["opus-mt-en-zh"] == "legacy"
    entry = next(item for item in raw["models"] if item["id"] == NEW_EN_ZH)
    assert entry["license"] == "CC-BY-4.0" and entry["src_prefix_token"] == ">>cmn_Hans<<"


def test_old_en_zh_still_serves_and_is_reported_outdated(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    from suiyi_engine.serve import _warn_outdated

    _install(tmp_path, "opus-mt-en-zh", "en", "zh")
    _install(tmp_path, "opus-mt-ja-en", "ja", "en")
    translator = Translator(tmp_path, backend_factory=ModelBackend)
    assert translator.translate("Hi.", "en", "zh").route == ["opus-mt-en-zh"]
    # ja→zh 中转的第二跳：清单指定新模型，没装时用清单里登记过的旧模型
    assert translator.translate("こんにちは。", "ja", "zh").route == [
        "opus-mt-ja-en",
        "opus-mt-en-zh",
    ]
    assert ("ja", "zh", "pivot") in translator.available_pairs()
    assert translator.registry.outdated() == [("en", "zh", NEW_EN_ZH, "opus-mt-en-zh")]
    _warn_outdated(translator)
    err = capsys.readouterr().err
    assert NEW_EN_ZH in err and f"--ids {NEW_EN_ZH}" in err


def test_new_en_zh_wins_when_both_are_installed(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-en-zh", "en", "zh")
    _install(tmp_path, NEW_EN_ZH, "en", "zh")
    translator = Translator(tmp_path, backend_factory=ModelBackend)
    assert translator.translate("Hi.", "en", "zh").route == [NEW_EN_ZH]
    assert translator.registry.outdated() == []


def test_convert_accepts_legacy_tier_but_mvp_skips_it() -> None:
    from suiyi_engine.registry import default_manifest_path
    from suiyi_engine.tools import convert

    manifest = convert.load_manifest(default_manifest_path())
    mvp = {entry["id"] for entry in convert.select_models(manifest, None, "mvp")}
    assert NEW_EN_ZH in mvp and "opus-mt-en-zh" not in mvp
    assert [e["id"] for e in convert.select_models(manifest, ["opus-mt-en-zh"], None)] == [
        "opus-mt-en-zh"
    ]


# ---------------------------------------------------------------- 真实模型回归（#97）

_ISSUE_97_PR = (
    "The pull request was merged after the CI pipeline passed and the flaky test was quarantined."
)
_ISSUE_97_K8S = (
    "Kubernetes is an open-source system for automating deployment, scaling, and management of "
    "containerized applications. It is an open source project hosted by the CNCF "
    "(Cloud Native Computing Foundation)."
)


@pytest.mark.model
def test_issue_97_regressions_with_real_models() -> None:
    models_dir = Path(os.environ["SUIYI_MODELS_DIR"])
    if not (models_dir / "opus-mt-eng-zho-tc-big-2022-05-14").is_dir():
        pytest.skip("需要 tc-big en→zh 模型")
    translator = Translator(models_dir, glossary=GlossaryStore(None))
    on = translator.translate(_ISSUE_97_PR, "en", "zh", glossary=True).text
    assert "不稳定测试" in on and "隔离" in on and "拉取请求" in on
    assert "型" not in on
    for glossary in (True, False):
        text = translator.translate(_ISSUE_97_K8S, "en", "zh", glossary=glossary).text
        assert not any(mark in text for mark in ",;()"), text
