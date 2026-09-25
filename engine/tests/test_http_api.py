"""本机 HTTP API。假翻译器 / 假检测器覆盖端点与错误码，真实模型用 model 标记。"""

from __future__ import annotations

import json
import os
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path
from types import SimpleNamespace

import pytest
import uvicorn
from fastapi.testclient import TestClient

from suiyi_engine import __version__
from suiyi_engine.api import ApiSettings, create_app
from suiyi_engine.langdetect import Detection, detect
from suiyi_engine.registry import ModelRecord
from suiyi_engine.translator import TranslationResult, Translator


class TagBackend:
    def __init__(self, record: ModelRecord) -> None:
        self.record = record

    def translate_batch(self, sentences: list[str]) -> list[str]:
        return [f"<{self.record.id}>{sentence}" for sentence in sentences]


class BoomTranslator:
    def __init__(self) -> None:
        self.registry = SimpleNamespace(
            models_dir=Path("models"),
            resolve=lambda _src, _tgt: (SimpleNamespace(id="unused"),),
        )

    def translate(self, text: str, src: str, tgt: str) -> TranslationResult:
        raise RuntimeError("boom-secret")

    def translate_many(self, texts: list[str], src: str, tgt: str) -> list[TranslationResult]:
        raise RuntimeError("boom-secret")

    def available_pairs(self) -> list[tuple[str, str, str]]:
        return []

    def loaded_model_ids(self) -> list[str]:
        return []


class GateTranslator:
    """把翻译卡住，用来确认 /health 不跟在翻译后面。"""

    def __init__(self) -> None:
        self.entered = threading.Event()
        self.release = threading.Event()
        self.registry = SimpleNamespace(
            models_dir=Path("models"),
            resolve=lambda _src, _tgt: (SimpleNamespace(id="fake-zh-en"),),
        )

    def translate(self, text: str, src: str, tgt: str) -> TranslationResult:
        self.entered.set()
        if not self.release.wait(timeout=5):
            raise TimeoutError("translation gate timed out")
        return TranslationResult(
            text="OK",
            src=src,
            tgt=tgt,
            route=["fake-zh-en"],
            elapsed_ms=10,
        )

    def translate_many(self, texts: list[str], src: str, tgt: str) -> list[TranslationResult]:
        return [self.translate(text, src, tgt) for text in texts]

    def available_pairs(self) -> list[tuple[str, str, str]]:
        return [("zh", "en", "direct")]

    def loaded_model_ids(self) -> list[str]:
        return []


def _install(root: Path, model_id: str, src: str, tgt: str) -> None:
    directory = root / model_id
    directory.mkdir(parents=True, exist_ok=True)
    payload = {
        "id": model_id,
        "src": src,
        "tgt": tgt,
        "src_prefix_token": None,
        "quantization": "int8",
    }
    (directory / "suiyi-model.json").write_text(
        json.dumps(payload, ensure_ascii=False),
        encoding="utf-8",
    )


def _translator(root: Path) -> Translator:
    return Translator(root, backend_factory=TagBackend)


def _detector(lang: str) -> Detection:
    return Detection(lang, 0.99, "script")


def _client(
    translator: object,
    detector: object | None = None,
    settings: ApiSettings | None = None,
) -> TestClient:
    app = create_app(
        translator,  # type: ignore[arg-type]
        detector if detector is not None else (lambda _text: _detector("en")),
        settings or ApiSettings(),
    )
    return TestClient(app)


def test_health_reports_version_models_dir_and_uptime(tmp_path: Path) -> None:
    translator = _translator(tmp_path)
    with _client(translator) as client:
        response = client.get("/health")
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert body["version"] == __version__
    assert Path(body["models_dir"]) == tmp_path
    assert body["loaded_models"] == []
    assert body["uptime_s"] >= 0
    assert set(body) == {"status", "version", "models_dir", "loaded_models", "uptime_s"}
    assert "access-control-allow-origin" not in {name.lower() for name in response.headers}


def test_languages_lists_direct_and_pivot_but_not_uninstalled_direct(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh")
    _install(tmp_path, "opus-mt-ja-en", "ja", "en")
    translator = _translator(tmp_path)
    with _client(translator) as client:
        response = client.get("/languages")
    assert response.status_code == 200
    body = response.json()
    assert body["languages"] == ["en", "ja", "zh"]
    by_pair = {(item["src"], item["tgt"]): item for item in body["pairs"]}
    assert by_pair[("zh", "en")] == {
        "src": "zh",
        "tgt": "en",
        "route": "direct",
        "models": ["opus-mt-zh-en"],
    }
    assert by_pair[("ja", "zh")]["route"] == "pivot"
    assert by_pair[("ja", "zh")]["models"] == ["opus-mt-ja-en", "opus-mt-en-zh"]
    assert ("zh", "ja") not in by_pair
    assert body["pairs"] == sorted(body["pairs"], key=lambda item: (item["src"], item["tgt"]))


def test_languages_empty_when_no_models_installed(tmp_path: Path) -> None:
    with _client(_translator(tmp_path)) as client:
        assert client.get("/languages").json() == {"languages": [], "pairs": []}


def test_translate_direct_and_then_health_shows_loaded_model(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    with _client(_translator(tmp_path)) as client:
        response = client.post(
            "/translate",
            json={"text": "你好。", "source": "zh", "target": "en", "glossary": {"你": "you"}},
        )
        health = client.get("/health")
    assert response.status_code == 200
    body = response.json()
    assert body["text"] == "<opus-mt-zh-en>你好。"
    assert body["source"] == "zh"
    assert body["detected"] is False
    assert body["target"] == "en"
    assert body["route"] == ["opus-mt-zh-en"]
    assert body["elapsed_ms"] >= 0
    assert "results" not in body
    assert health.json()["loaded_models"] == ["opus-mt-zh-en"]


def test_explicit_region_code_is_normalized(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    with _client(_translator(tmp_path)) as client:
        response = client.post(
            "/translate",
            json={"text": "你好。", "source": "ZH-CN", "target": "en_US"},
        )
    body = response.json()
    assert response.status_code == 200
    assert body["source"] == "zh"
    assert body["target"] == "en"
    assert body["detected"] is False


def test_auto_detects_zh_en_ja_with_fake_backend(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")
    _install(tmp_path, "opus-mt-en-zh", "en", "zh")
    _install(tmp_path, "opus-mt-ja-en", "ja", "en")
    samples = [
        ("今天天气很好。", "en", "zh", ["opus-mt-zh-en"]),
        ("Hello.", "zh", "en", ["opus-mt-en-zh"]),
        ("こんにちは。", "en", "ja", ["opus-mt-ja-en"]),
    ]
    with _client(_translator(tmp_path), detect) as client:
        for text, target, source, route in samples:
            response = client.post(
                "/translate",
                json={"text": text, "source": "auto", "target": target},
            )
            assert response.status_code == 200, response.text
            body = response.json()
            assert body["detected"] is True
            assert body["source"] == source
            assert body["target"] == target
            assert body["route"] == route
            assert body["text"].startswith(f"<{route[0]}>")


def test_auto_same_as_target_returns_original(tmp_path: Path) -> None:
    def detector(_text: str) -> Detection:
        return _detector("en")

    with _client(_translator(tmp_path), detector) as client:
        response = client.post(
            "/translate",
            json={"text": "Hello.", "source": "auto", "target": "en"},
        )
    body = response.json()
    assert response.status_code == 200
    assert body["text"] == "Hello."
    assert body["source"] == "en"
    assert body["detected"] is True
    assert body["route"] == []


def test_batch_explicit_and_auto(tmp_path: Path) -> None:
    _install(tmp_path, "opus-mt-zh-en", "zh", "en")

    def detector(text: str) -> Detection:
        if "你" in text:
            return _detector("zh")
        return _detector("en")

    with _client(_translator(tmp_path), detector) as client:
        explicit = client.post(
            "/translate",
            json={"texts": ["你好。", "再见。"], "source": "zh", "target": "en"},
        )
        mixed = client.post(
            "/translate",
            json={"texts": ["你好。", "Hello."], "source": " Auto ", "target": "en"},
        )
    assert explicit.status_code == 200
    rows = explicit.json()["results"]
    assert [row["text"] for row in rows] == [
        "<opus-mt-zh-en>你好。",
        "<opus-mt-zh-en>再见。",
    ]
    assert all(row["detected"] is False for row in rows)
    mixed_rows = mixed.json()["results"]
    assert mixed.status_code == 200
    assert mixed_rows[0]["source"] == "zh"
    assert mixed_rows[0]["route"] == ["opus-mt-zh-en"]
    assert mixed_rows[1]["source"] == "en"
    assert mixed_rows[1]["route"] == []
    assert mixed_rows[1]["text"] == "Hello."
    assert all(row["detected"] is True for row in mixed_rows)


def test_blank_explicit_source_does_not_require_a_model(tmp_path: Path) -> None:
    with _client(_translator(tmp_path)) as client:
        response = client.post(
            "/translate",
            json={"text": " \n\t", "source": "zh", "target": "ja"},
        )
    assert response.status_code == 200
    assert response.json()["text"] == ""
    assert response.json()["route"] == []


@pytest.mark.parametrize(
    "payload",
    [
        {"source": "en", "target": "en"},
        {"text": "hi", "texts": ["hi"], "source": "en", "target": "en"},
        {"texts": [], "source": "en", "target": "en"},
        {"text": "hi", "source": "english", "target": "en"},
        {"text": "hi", "source": "en", "target": "auto"},
        {"texts": [1], "source": "en", "target": "en"},
    ],
)
def test_invalid_request(tmp_path: Path, payload: dict[str, object]) -> None:
    with _client(_translator(tmp_path)) as client:
        response = client.post("/translate", json=payload)
    assert response.status_code == 422
    error = response.json()["error"]
    assert error["code"] == "invalid_request"
    assert error["message"]
    assert isinstance(error["details"], dict)


def test_invalid_json_body(tmp_path: Path) -> None:
    with _client(_translator(tmp_path)) as client:
        response = client.post(
            "/translate",
            content=b"{",
            headers={"Content-Type": "application/json"},
        )
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_request"


def test_text_too_long_single_and_batch(tmp_path: Path) -> None:
    settings = ApiSettings(max_text_chars=4)
    with _client(_translator(tmp_path), settings=settings) as client:
        single = client.post(
            "/translate",
            json={"text": "12345", "source": "en", "target": "en"},
        )
        batch = client.post(
            "/translate",
            json={"texts": ["ok", "123456"], "source": "en", "target": "en"},
        )
        exact = client.post(
            "/translate",
            json={"text": "1234", "source": "en", "target": "en"},
        )
    assert single.status_code == 413
    assert single.json()["error"]["code"] == "text_too_long"
    assert single.json()["error"]["details"] == {"limit": 4, "length": 5}
    assert batch.status_code == 413
    assert batch.json()["error"]["details"]["index"] == 1
    assert "results" not in batch.json()
    assert exact.status_code == 200
    assert exact.json()["text"] == "1234"


def test_default_limit_is_10000_characters(tmp_path: Path) -> None:
    with _client(_translator(tmp_path)) as client:
        over = client.post(
            "/translate",
            json={"text": "x" * 10001, "source": "en", "target": "en"},
        )
        fits = client.post(
            "/translate",
            json={"text": "x" * 10000, "source": "en", "target": "en"},
        )
    assert over.status_code == 413
    assert over.json()["error"]["code"] == "text_too_long"
    assert fits.status_code == 200


def test_unsupported_pair_names_missing_model(tmp_path: Path) -> None:
    with _client(_translator(tmp_path)) as client:
        direct = client.post(
            "/translate",
            json={"text": "Hello.", "source": "en", "target": "zh"},
        )
        pivot = client.post(
            "/translate",
            json={"text": "こんにちは。", "source": "ja", "target": "zh"},
        )
    direct_error = direct.json()["error"]
    assert direct.status_code == 422
    assert direct_error["code"] == "unsupported_pair"
    assert direct_error["details"]["missing_models"] == ["opus-mt-en-zh"]
    assert direct_error["details"]["source"] == "en"
    assert direct_error["details"]["target"] == "zh"
    assert "opus-mt-en-zh" in direct_error["message"]
    assert "index" not in direct_error["details"]
    pivot_error = pivot.json()["error"]
    assert pivot.status_code == 422
    assert pivot_error["code"] == "unsupported_pair"
    assert pivot_error["details"]["missing_models"] == ["opus-mt-ja-en", "opus-mt-en-zh"]


def test_batch_unsupported_pair_includes_index(tmp_path: Path) -> None:
    with _client(_translator(tmp_path)) as client:
        response = client.post(
            "/translate",
            json={"texts": ["안녕", "你好"], "source": "ko", "target": "ja"},
        )
    error = response.json()["error"]
    assert response.status_code == 422
    assert error["code"] == "unsupported_pair"
    assert "opus-mt-ko-en" in error["details"]["missing_models"]
    assert error["details"]["index"] == 0
    assert "results" not in response.json()


def test_detect_failed(tmp_path: Path) -> None:
    def detector(_text: str) -> Detection:
        return Detection("und", 0.0, "fallback")

    with _client(_translator(tmp_path), detector) as client:
        response = client.post(
            "/translate",
            json={"text": "???", "source": "auto", "target": "en"},
        )
    error = response.json()["error"]
    assert response.status_code == 422
    assert error["code"] == "detect_failed"
    assert error["details"]["detected"] == "und"
    assert error["details"]["index"] == 0


def test_internal_error_hides_exception_text(tmp_path: Path) -> None:
    del tmp_path
    with _client(BoomTranslator()) as client:
        response = client.post(
            "/translate",
            json={"text": "你好。", "source": "zh", "target": "en"},
        )
    assert response.status_code == 500
    error = response.json()["error"]
    assert error["code"] == "internal_error"
    assert error["details"] == {}
    assert "boom-secret" not in response.text
    assert "Traceback" not in response.text


def test_docs_disabled_unless_dev(tmp_path: Path) -> None:
    translator = _translator(tmp_path)
    with _client(translator) as client:
        assert client.get("/docs").status_code == 404
        assert client.get("/openapi.json").status_code == 404
    app = create_app(translator, lambda _text: _detector("en"), ApiSettings(dev=True))
    with TestClient(app) as client:
        assert client.get("/docs").status_code == 200
        spec = client.get("/openapi.json")
        assert spec.status_code == 200
        assert {"/health", "/languages", "/translate"} <= set(spec.json()["paths"])


def test_health_stays_under_200ms_while_translation_is_running() -> None:
    translator = GateTranslator()
    app = create_app(translator, lambda _text: _detector("zh"), ApiSettings())
    config = uvicorn.Config(app, host="127.0.0.1", port=0, log_level="warning", access_log=False)
    server = uvicorn.Server(config)
    thread = threading.Thread(target=server.run, name="suiyi-http-test")
    thread.start()
    post: dict[str, object] = {}
    try:
        port = _wait_until_listening(server)
        health_url = f"http://127.0.0.1:{port}/health"
        _read_json(health_url)

        def do_post() -> None:
            try:
                post["body"] = _read_json(
                    f"http://127.0.0.1:{port}/translate",
                    {"text": "你好。", "source": "zh", "target": "en"},
                )
            except Exception as exc:
                post["error"] = exc

        worker = threading.Thread(target=do_post)
        worker.start()
        assert translator.entered.wait(timeout=2), "翻译没有进入线程池"
        started = time.perf_counter()
        try:
            health = _read_json(health_url, timeout=0.2)
        except (TimeoutError, urllib.error.URLError) as exc:
            pytest.fail(f"/health 在翻译期间超过 200ms：{exc}")
        elapsed_ms = (time.perf_counter() - started) * 1000
        assert health["status"] == "ok"
        assert elapsed_ms < 200
    finally:
        translator.release.set()
        if "worker" in locals():
            worker.join(timeout=5)
        server.should_exit = True
        thread.join(timeout=5)
    assert "error" not in post
    assert post["body"]["text"] == "OK"  # type: ignore[index]


def _wait_until_listening(server: uvicorn.Server, timeout: float = 5) -> int:
    deadline = time.time() + timeout
    while time.time() < deadline:
        sockets = getattr(server, "servers", None)
        if server.started and sockets:
            sock = sockets[0].sockets[0]
            port = int(sock.getsockname()[1])
            if port:
                return port
        time.sleep(0.01)
    raise AssertionError("测试服务没有开始监听")


def _read_json(url: str, payload: dict[str, object] | None = None, timeout: float = 2) -> dict:
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {} if data is None else {"Content-Type": "application/json"}
    request = urllib.request.Request(
        url, data=data, headers=headers, method="POST" if data else "GET"
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


@pytest.mark.model
def test_model_languages_and_auto_translate() -> None:
    required = (
        "opus-mt-zh-en",
        "opus-mt-en-zh",
        "opus-mt-zho-jpn-tc-big-2022-07-28",
        "opus-mt-ja-en",
        "opus-mt-eng-jpn-2021-02-18",
    )
    root = Path(os.environ["SUIYI_MODELS_DIR"])
    missing = [
        model_id for model_id in required if not (root / model_id / "suiyi-model.json").is_file()
    ]
    if missing:
        pytest.skip("尚未转换 " + "、".join(missing))
    app = create_app(Translator(root), detect, ApiSettings())
    expected = {
        ("zh", "en"): ("direct", ["opus-mt-zh-en"]),
        ("en", "zh"): ("direct", ["opus-mt-en-zh"]),
        ("zh", "ja"): ("direct", ["opus-mt-zho-jpn-tc-big-2022-07-28"]),
        ("ja", "zh"): ("pivot", ["opus-mt-ja-en", "opus-mt-en-zh"]),
        ("en", "ja"): ("direct", ["opus-mt-eng-jpn-2021-02-18"]),
        ("ja", "en"): ("direct", ["opus-mt-ja-en"]),
    }
    samples = [
        ("今天天气很好。", "en", "zh"),
        ("The weather is nice today.", "zh", "en"),
        ("今日はいい天気です。", "zh", "ja"),
    ]
    with TestClient(app) as client:
        body = client.get("/languages").json()
        by_pair = {(item["src"], item["tgt"]): item for item in body["pairs"]}
        for pair, (kind, models) in expected.items():
            assert by_pair[pair]["route"] == kind
            assert by_pair[pair]["models"] == models
        assert {"en", "ja", "zh"} <= set(body["languages"])
        for text, target, source in samples:
            response = client.post(
                "/translate",
                json={"text": text, "source": "auto", "target": target},
            )
            assert response.status_code == 200, response.text
            payload = response.json()
            assert payload["detected"] is True
            assert payload["source"] == source
            assert payload["target"] == target
            assert payload["text"]
            assert "▁" not in payload["text"]
