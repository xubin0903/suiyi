"""OCR HTTP 接口 /ocr 与 /ocr_translate（#53）。

大部分用例用假 OCR 引擎（不需要 rapidocr / PIL / 模型）；用真实解码器的用例需要 PIL，
没有就跳过（CI 的 OCR 步骤装了 engine[ocr]，会跑全）。
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import struct
import threading
import time
import zlib
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from suiyi_engine.api import ApiSettings, create_app
from suiyi_engine.api_ocr import OcrProvider, png_size
from suiyi_engine.langdetect import Detection
from suiyi_engine.ocr import OcrEngine, OcrError, OcrLine, OcrResult, merge_paragraphs
from suiyi_engine.registry import ModelRecord
from suiyi_engine.tools.ocr_models import parse_manifest
from suiyi_engine.translator import Translator

# ---- 构造 PNG（不依赖 PIL）-----------------------------------------------------


def _chunk(kind: bytes, data: bytes) -> bytes:
    body = kind + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def make_png(width: int = 64, height: int = 32, *, idat: bytes | None = None) -> bytes:
    """8 位灰度、全白的合法 PNG。``idat`` 给定时替换像素数据（用来造损坏的图）。"""

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0)
    raw = b"".join(b"\x00" + b"\xff" * width for _ in range(height))
    pixels = zlib.compress(raw) if idat is None else idat
    return (
        b"\x89PNG\r\n\x1a\n"
        + _chunk(b"IHDR", ihdr)
        + _chunk(b"IDAT", pixels)
        + _chunk(b"IEND", b"")
    )


def header_only_png(width: int, height: int) -> bytes:
    return b"\x89PNG\r\n\x1a\n" + _chunk(
        b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0)
    )


# ---- 假翻译 / 假检测 / 假 OCR ----------------------------------------------------


class TagBackend:
    def __init__(self, record: ModelRecord) -> None:
        self.record = record

    def translate_batch(self, sentences: list[str]) -> list[str]:
        return [f"<{self.record.id}>{sentence}" for sentence in sentences]


def _install(root: Path, model_id: str, src: str, tgt: str) -> None:
    directory = root / model_id
    directory.mkdir(parents=True, exist_ok=True)
    payload = {"id": model_id, "src": src, "tgt": tgt, "src_prefix_token": None}
    (directory / "suiyi-model.json").write_text(json.dumps(payload), encoding="utf-8")
    (directory / "model.bin").write_bytes(b"fake")


def _translator(root: Path) -> Translator:
    _install(root, "opus-mt-zh-en", "zh", "en")
    _install(root, "opus-mt-en-zh", "en", "zh")
    return Translator(root, backend_factory=TagBackend)


def script_detector(text: str) -> Detection:
    """有汉字判 zh，有拉丁字母判 en，否则 und。"""

    if any("\u4e00" <= ch <= "\u9fff" for ch in text):
        return Detection("zh", 0.99, "script")
    if any(ch.isascii() and ch.isalpha() for ch in text):
        return Detection("en", 0.99, "script")
    return Detection("und", 0.0, "script")


def _line(text: str, x0: float, y0: float, x1: float, y1: float, score: float = 0.95) -> OcrLine:
    return OcrLine(text=text, box=((x0, y0), (x1, y0), (x1, y1), (x0, y1)), score=score)


ZH_LINES = [
    _line("本地翻译", 10, 10, 200, 44),
    _line("随译把模型放在本机，复制、翻译、显示都在同", 10, 60, 600, 84),
    _line("一台机器上完成。", 10, 90, 250, 114),
    _line("看不清的字", 10, 150, 200, 174, score=0.2),
]


class FakeOcrEngine:
    """形状与 OcrEngine 相同：load / loaded / warmup / recognize。"""

    def __init__(self, lines: list[OcrLine] | None = None, delay: float = 0.0) -> None:
        self.lines = list(ZH_LINES if lines is None else lines)
        self.delay = delay
        self.loaded = False
        self.calls: list[tuple[int, str]] = []
        self.started = threading.Event()

    def load(self) -> None:
        self.loaded = True

    def warmup(self) -> None:
        self.load()

    def recognize(self, image: bytes, *, lang: str = "auto") -> OcrResult:
        self.started.set()
        self.calls.append((len(image), lang))
        time.sleep(self.delay)
        width, height = png_size(image)
        lines = tuple(
            OcrLine(line.text, line.box, line.score, line.score < 0.5) for line in self.lines
        )
        return OcrResult(lines, tuple(merge_paragraphs(lines)), width, height, 1.0)


def _app(
    tmp_path: Path,
    engine: object | None = None,
    *,
    settings: ApiSettings | None = None,
    detector=script_detector,
    provider: OcrProvider | None = None,
):
    fake = FakeOcrEngine() if engine is None else engine
    if provider is None:
        provider = OcrProvider(tmp_path, engine_factory=lambda: fake)  # type: ignore[arg-type,return-value]
    return create_app(_translator(tmp_path), detector, settings or ApiSettings(), provider)


def _post(client: TestClient, path: str, data: bytes, **params: str):
    return client.post(path, params=params, content=data, headers={"Content-Type": "image/png"})


# ---- 正常路径 -------------------------------------------------------------------


def test_ocr_translate_returns_ocr_fields_translation_and_timings(tmp_path: Path) -> None:
    engine = FakeOcrEngine()
    with TestClient(_app(tmp_path, engine)) as client:
        response = _post(client, "/ocr_translate", make_png(640, 200), source="auto", target="en")
    assert response.status_code == 200, response.text
    body = response.json()
    assert body["image"] == {"width": 640, "height": 200}
    assert [p["text"] for p in body["paragraphs"]] == [
        "本地翻译",
        "随译把模型放在本机，复制、翻译、显示都在同一台机器上完成。",
    ]
    assert body["text"] == "本地翻译\n随译把模型放在本机，复制、翻译、显示都在同一台机器上完成。"
    assert len(body["lines"]) == 4
    assert body["lines"][3]["low_confidence"] is True
    assert set(body["lines"][0]) >= {"text", "box", "score"}
    assert len(body["lines"][0]["box"]) == 4
    assert len(body["paragraphs"][0]["box"]) == 4
    results = body["translation"]["results"]
    assert [r["text"] for r in results] == [
        "<opus-mt-zh-en>本地翻译",
        "<opus-mt-zh-en>随译把模型放在本机，复制、翻译、显示都在同一台机器上完成。",
    ]
    assert all(r["source"] == "zh" and r["target"] == "en" and r["detected"] for r in results)
    assert results[0]["route"] == ["opus-mt-zh-en"]
    elapsed = body["elapsed_ms"]
    assert set(elapsed) == {"ocr", "translate", "total"}
    assert elapsed["total"] >= elapsed["ocr"] >= 0
    assert engine.calls == [(len(make_png(640, 200)), "auto")]


def test_ocr_only_endpoint(tmp_path: Path) -> None:
    engine = FakeOcrEngine()
    with TestClient(_app(tmp_path, engine)) as client:
        response = _post(client, "/ocr", make_png(), lang="ja")
        bad = _post(client, "/ocr", make_png(), lang="fr")
    assert response.status_code == 200
    body = response.json()
    assert set(body) == {"lines", "paragraphs", "text", "image", "elapsed_ms"}
    assert isinstance(body["elapsed_ms"], float)
    assert body["paragraphs"][0]["text"] == "本地翻译"
    assert engine.calls[0][1] == "ja"
    assert bad.status_code == 422
    assert bad.json()["error"]["code"] == "invalid_request"
    assert bad.json()["error"]["details"] == {"field": "lang"}


def test_empty_result_is_200(tmp_path: Path) -> None:
    with TestClient(_app(tmp_path, FakeOcrEngine(lines=[]))) as client:
        response = _post(client, "/ocr_translate", make_png(), target="en")
    assert response.status_code == 200
    body = response.json()
    assert body["paragraphs"] == [] and body["lines"] == [] and body["text"] == ""
    assert body["translation"] == {"results": []}


def test_content_type_is_not_required_png_magic_decides(tmp_path: Path) -> None:
    with TestClient(_app(tmp_path)) as client:
        response = client.post(
            "/ocr_translate",
            params={"target": "en"},
            content=make_png(),
            headers={"Content-Type": "application/octet-stream"},
        )
    assert response.status_code == 200


# ---- 目标语与次目标 ---------------------------------------------------------------


def test_fallback_target_applies_when_source_equals_target(tmp_path: Path) -> None:
    with TestClient(_app(tmp_path)) as client:
        response = _post(client, "/ocr_translate", make_png(), target="zh", fallback_target="en")
    results = response.json()["translation"]["results"]
    assert [r["target"] for r in results] == ["en", "en"]
    assert results[0]["text"].startswith("<opus-mt-zh-en>")


def test_fallback_target_ignored_when_source_differs(tmp_path: Path) -> None:
    lines = [_line("Offline translation keeps text local.", 10, 10, 500, 34)]
    with TestClient(_app(tmp_path, FakeOcrEngine(lines))) as client:
        response = _post(client, "/ocr_translate", make_png(), target="zh", fallback_target="en")
    results = response.json()["translation"]["results"]
    assert [(r["source"], r["target"]) for r in results] == [("en", "zh")]


def test_explicit_source_and_fallback(tmp_path: Path) -> None:
    with TestClient(_app(tmp_path)) as client:
        response = _post(
            client, "/ocr_translate", make_png(), source="zh", target="zh", fallback_target="en"
        )
    results = response.json()["translation"]["results"]
    assert all(r["source"] == "zh" and r["target"] == "en" and not r["detected"] for r in results)


def test_mixed_paragraphs_detected_separately_and_und_uses_image_language(tmp_path: Path) -> None:
    lines = [
        _line("本地翻译的说明文字", 10, 10, 300, 34),
        _line("Settings", 10, 80, 120, 104),
        _line("3:42", 10, 150, 60, 174),
    ]
    with TestClient(_app(tmp_path, FakeOcrEngine(lines))) as client:
        response = _post(client, "/ocr_translate", make_png(), target="en")
    results = response.json()["translation"]["results"]
    assert [(r["source"], r["target"]) for r in results] == [
        ("zh", "en"),
        ("en", "en"),  # 与目标相同：原样返回
        ("zh", "en"),  # 检测不出，用整张图的语种
    ]
    assert results[1]["text"] == "Settings"


def test_detect_failed_when_nothing_detectable(tmp_path: Path) -> None:
    lines = [_line("1,284", 10, 10, 100, 34)]
    with TestClient(_app(tmp_path, FakeOcrEngine(lines))) as client:
        response = _post(client, "/ocr_translate", make_png(), target="en")
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "detect_failed"


def test_unsupported_pair(tmp_path: Path) -> None:
    lines = [_line("本地翻译", 10, 10, 200, 34)]
    with TestClient(_app(tmp_path, FakeOcrEngine(lines))) as client:
        response = _post(client, "/ocr_translate", make_png(), target="ja")
    assert response.status_code == 422
    error = response.json()["error"]
    assert error["code"] == "unsupported_pair"
    assert error["details"]["index"] == 0
    assert error["details"]["missing_models"]


def test_paragraph_too_long(tmp_path: Path) -> None:
    settings = ApiSettings(max_text_chars=5)
    with TestClient(_app(tmp_path, settings=settings)) as client:
        response = _post(client, "/ocr_translate", make_png(), target="en")
    assert response.status_code == 413
    error = response.json()["error"]
    assert error["code"] == "text_too_long"
    assert error["details"] == {"limit": 5, "length": 29, "index": 1}


@pytest.mark.parametrize(
    ("params", "field"),
    [
        ({}, "target"),
        ({"target": "auto"}, "target"),
        ({"target": "en", "fallback_target": "123"}, "fallback_target"),
        ({"target": "en", "source": "??"}, "source"),
    ],
)
def test_invalid_query(tmp_path: Path, params: dict[str, str], field: str) -> None:
    engine = FakeOcrEngine()
    with TestClient(_app(tmp_path, engine)) as client:
        response = _post(client, "/ocr_translate", make_png(), **params)
    assert response.status_code == 422
    error = response.json()["error"]
    assert error["code"] == "invalid_request"
    assert field in json.dumps(error["details"])
    assert engine.calls == []  # 参数错误不做 OCR


# ---- 图片限制 -------------------------------------------------------------------


def test_too_many_bytes(tmp_path: Path) -> None:
    png = make_png()
    settings = ApiSettings(max_image_bytes=len(png) - 1)
    engine = FakeOcrEngine()
    with TestClient(_app(tmp_path, engine, settings=settings)) as client:
        response = _post(client, "/ocr_translate", png, target="en")
    assert response.status_code == 413
    error = response.json()["error"]
    assert error["code"] == "image_too_large"
    assert error["details"] == {"kind": "bytes", "limit": len(png) - 1, "actual": len(png)}
    assert engine.calls == []


def test_too_many_pixels_is_judged_from_header(tmp_path: Path) -> None:
    engine = FakeOcrEngine()
    with TestClient(_app(tmp_path, engine)) as client:
        response = _post(client, "/ocr_translate", header_only_png(5000, 4000), target="en")
        slim = _post(client, "/ocr", make_png(8000, 2))  # 细长图按总像素判断，不限单边
    assert response.status_code == 413
    error = response.json()["error"]
    assert error["code"] == "image_too_large"
    assert error["details"] == {
        "kind": "pixels",
        "limit": 4096 * 4096,
        "actual": 20_000_000,
        "width": 5000,
        "height": 4000,
    }
    assert slim.status_code == 200
    assert len(engine.calls) == 1


@pytest.mark.parametrize(
    "data",
    [b"\xff\xd8\xff\xe0JFIF-not-png", b"GIF89a....", b"", b"<svg/>"],
    ids=["jpeg", "gif", "empty", "svg"],
)
def test_not_png_is_415(tmp_path: Path, data: bytes) -> None:
    with TestClient(_app(tmp_path)) as client:
        response = _post(client, "/ocr_translate", data, target="en")
    assert response.status_code == 415
    assert response.json()["error"]["code"] == "unsupported_media_type"


@pytest.mark.parametrize(
    "data",
    [b"\x89PNG\r\n\x1a\n", b"\x89PNG\r\n\x1a\n" + b"\x00" * 30, header_only_png(0, 10)],
    ids=["signature-only", "no-ihdr", "zero-width"],
)
def test_broken_png_header_is_422(tmp_path: Path, data: bytes) -> None:
    with TestClient(_app(tmp_path)) as client:
        response = _post(client, "/ocr_translate", data, target="en")
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_image"


def _asgi_post(app, headers: list[tuple[bytes, bytes]], chunks: list[bytes]):
    """直接走 ASGI，记录应用实际读了几次请求体。"""

    received: list[int] = []
    sent: list[dict] = []

    async def receive():
        index = len(received)
        received.append(index)
        if index < len(chunks):
            return {
                "type": "http.request",
                "body": chunks[index],
                "more_body": index + 1 < len(chunks),
            }
        return {"type": "http.disconnect"}

    async def send(message):
        sent.append(message)

    scope = {
        "type": "http",
        "asgi": {"version": "3.0"},
        "http_version": "1.1",
        "method": "POST",
        "scheme": "http",
        "path": "/ocr_translate",
        "raw_path": b"/ocr_translate",
        "query_string": b"target=en",
        "headers": [(b"host", b"127.0.0.1"), *headers],
        "client": ("127.0.0.1", 50000),
        "server": ("127.0.0.1", 18780),
    }
    asyncio.run(app(scope, receive, send))
    status = next(m["status"] for m in sent if m["type"] == "http.response.start")
    body = b"".join(m.get("body", b"") for m in sent if m["type"] == "http.response.body")
    return status, json.loads(body), received


def test_oversized_content_length_is_rejected_without_reading_body(tmp_path: Path) -> None:
    app = _app(tmp_path, settings=ApiSettings(max_image_bytes=1000))
    chunks = [b"\x89PNG\r\n\x1a\n" + b"x" * 992] + [b"x" * 1000] * 50
    status, body, received = _asgi_post(
        app, [(b"content-length", str(8 + 992 + 50_000).encode())], chunks
    )
    assert status == 413
    assert body["error"]["details"]["actual"] == 51_000
    assert received == []  # 一个字节都没读


def test_chunked_upload_stops_reading_once_over_limit(tmp_path: Path) -> None:
    app = _app(tmp_path, settings=ApiSettings(max_image_bytes=1000))
    chunks = [b"\x89PNG\r\n\x1a\n" + b"x" * 592] + [b"x" * 600] * 50
    status, body, received = _asgi_post(app, [(b"transfer-encoding", b"chunked")], chunks)
    assert status == 413
    assert body["error"]["details"]["kind"] == "bytes"
    assert len(received) == 2  # 读到第二块就超限，剩下 49 块不读


# ---- OCR 不可用 ------------------------------------------------------------------


def _fake_manifest(tmp_path: Path):
    ocr = tmp_path / "ocr-models" / "ocr"
    ocr.mkdir(parents=True)
    models, files = [], {}
    for task in ("det", "cls", "rec"):
        data = f"fake {task}".encode()
        files[ocr / f"{task}.onnx"] = data
        models.append(
            {
                "id": f"fake_{task}",
                "task": task,
                "ocr_version": "PP-OCRv6",
                "file": f"{task}.onnx",
                "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
                "url": f"https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/{task}.onnx",
                "upstream": "fake",
                "upstream_url": "https://example.invalid/",
                "license": "Apache-2.0",
                "license_url": "https://example.invalid/",
                "languages": "测试",
                "tier": "recommended",
                "notes": "",
            }
        )
    raw = json.loads((Path(__file__).parents[1] / "ocr_model_manifest.json").read_text("utf-8"))
    raw["models"] = models
    raw["recommended"] = {"det": "fake_det", "cls": "fake_cls", "rec": "fake_rec", "use_cls": False}
    return parse_manifest(raw), tmp_path / "ocr-models", files


def _raw_backend(paths, manifest, threads):
    def run(_image):
        return [([[10, 10], [200, 10], [200, 34], [10, 34]], "本地翻译", 0.99)]

    return run


def test_missing_models_503_translation_still_works_and_recovers(tmp_path: Path) -> None:
    manifest, models, files = _fake_manifest(tmp_path)
    provider = OcrProvider(
        models,
        engine_factory=lambda: OcrEngine(models, manifest=manifest, backend_factory=_raw_backend),
    )
    with TestClient(_app(tmp_path, provider=provider)) as client:
        missing = _post(client, "/ocr_translate", make_png(), target="en")
        health = client.get("/health").json()
        text = client.post("/translate", json={"text": "你好", "source": "zh", "target": "en"})
        assert missing.status_code == 503
        error = missing.json()["error"]
        assert error["code"] == "ocr_unavailable"
        assert error["details"]["reason"] == "models_missing"
        assert error["details"]["missing_models"] == ["fake_det", "fake_cls", "fake_rec"]
        assert "download_ocr_models.py" in error["message"]
        assert health["ocr_loaded"] is False
        assert health["ocr_error"]["reason"] == "models_missing"
        assert health["ocr_error"]["missing_models"] == ["fake_det", "fake_cls", "fake_rec"]
        assert text.status_code == 200

        pytest.importorskip("PIL", reason="恢复后的识别需要 PIL 解码")
        for path, data in files.items():
            path.write_bytes(data)
        recovered = _post(client, "/ocr_translate", make_png(), target="en")
        assert recovered.status_code == 200, recovered.text  # 补齐模型后不用重启
        assert recovered.json()["translation"]["results"][0]["text"] == "<opus-mt-zh-en>本地翻译"
        health_after = client.get("/health").json()
        assert health_after["ocr_loaded"] is True
        assert health_after["ocr_error"] is None  # 恢复后清空


def test_default_provider_with_empty_models_dir_is_503(tmp_path: Path) -> None:
    app = create_app(_translator(tmp_path), script_detector, ApiSettings())
    with TestClient(app) as client:
        response = _post(client, "/ocr", make_png())
    assert response.status_code == 503
    details = response.json()["error"]["details"]
    assert details["reason"] == "models_missing"
    assert len(details["missing_models"]) == 3


def test_missing_dependency_is_503(tmp_path: Path) -> None:
    manifest, models, files = _fake_manifest(tmp_path)
    for path, data in files.items():
        path.write_bytes(data)

    def no_rapidocr(paths, manifest, threads):
        raise OcrError('未安装 OCR 依赖：请执行 pip install -e "engine[ocr]"')

    provider = OcrProvider(
        models,
        engine_factory=lambda: OcrEngine(models, manifest=manifest, backend_factory=no_rapidocr),
    )
    with TestClient(_app(tmp_path, provider=provider)) as client:
        response = _post(client, "/ocr_translate", make_png(), target="en")
    assert response.status_code == 503
    error = response.json()["error"]
    assert error["details"] == {"reason": "dependency_missing", "missing_models": []}
    assert "engine[ocr]" in error["message"]


# ---- 解码（需要 PIL）--------------------------------------------------------------


def test_corrupt_png_data_is_422(tmp_path: Path) -> None:
    pytest.importorskip("PIL")
    manifest, models, files = _fake_manifest(tmp_path)
    for path, data in files.items():
        path.write_bytes(data)
    provider = OcrProvider(
        models,
        engine_factory=lambda: OcrEngine(models, manifest=manifest, backend_factory=_raw_backend),
    )
    with TestClient(_app(tmp_path, provider=provider)) as client:
        broken = _post(client, "/ocr_translate", make_png(idat=b"not zlib data"), target="en")
        truncated = _post(client, "/ocr_translate", make_png()[:40], target="en")
        good = _post(client, "/ocr_translate", make_png(), target="en")
    assert broken.status_code == 422, broken.text
    assert broken.json()["error"]["code"] == "invalid_image"
    assert truncated.status_code == 422
    assert good.status_code == 200


# ---- 并发、健康检查、日志 ----------------------------------------------------------


def test_health_stays_fast_during_long_ocr(tmp_path: Path) -> None:
    engine = FakeOcrEngine(delay=1.5)
    with TestClient(_app(tmp_path, engine)) as client:
        box: dict[str, object] = {}
        worker = threading.Thread(
            target=lambda: box.setdefault(
                "r", _post(client, "/ocr_translate", make_png(), target="en")
            )
        )
        worker.start()
        assert engine.started.wait(5)
        timings = []
        for _ in range(5):
            started = time.perf_counter()
            health = client.get("/health")
            timings.append(time.perf_counter() - started)
            assert health.status_code == 200
        text = client.post("/translate", json={"text": "你好", "source": "zh", "target": "en"})
        text_ms = time.perf_counter() - started
        worker.join(10)
        assert box["r"].status_code == 200  # type: ignore[union-attr]
        health_after = client.get("/health").json()
        assert health_after["ocr_loaded"] is True
        assert health_after["ocr_error"] is None  # 恢复后清空
    assert max(timings) < 0.2, timings
    assert text.status_code == 200
    assert text_ms < 1.0  # 翻译不等 OCR


def test_logs_have_sizes_and_timings_but_no_text(tmp_path: Path, caplog) -> None:
    caplog.set_level(logging.INFO, logger="suiyi_engine.api_ocr")
    with TestClient(_app(tmp_path)) as client:
        _post(client, "/ocr_translate", make_png(640, 200), target="en")
    messages = [r.getMessage() for r in caplog.records if r.name == "suiyi_engine.api_ocr"]
    assert len(messages) == 1
    assert "size=640x200" in messages[0] and "paragraphs=2" in messages[0]
    assert "本地" not in messages[0] and "opus" not in messages[0]


def test_no_cors_headers(tmp_path: Path) -> None:
    with TestClient(_app(tmp_path)) as client:
        response = client.options(
            "/ocr_translate",
            headers={"Origin": "https://evil.example", "Access-Control-Request-Method": "POST"},
        )
    assert "access-control-allow-origin" not in {k.lower() for k in response.headers}


def test_settings_validate_image_limits() -> None:
    with pytest.raises(ValueError):
        ApiSettings(max_image_bytes=0)
    with pytest.raises(ValueError):
        ApiSettings(max_image_pixels=True)  # type: ignore[arg-type]
    assert ApiSettings().max_image_bytes == 8 * 1024 * 1024


# ---- 真实模型冒烟 ----------------------------------------------------------------


@pytest.mark.model
def test_real_models_chinese_screenshot_to_english() -> None:
    """中文截图 → /ocr_translate?target=en 返回非空英文译文。需要 zh-en 翻译模型与 OCR 模型。"""

    import os

    from suiyi_engine.ocr import OcrModelsMissingError
    from suiyi_engine.tools.ocr_models import (
        default_manifest_path,
        load_manifest,
        local_model_paths,
    )

    models_dir = Path(os.environ["SUIYI_MODELS_DIR"])
    pytest.importorskip("rapidocr")
    try:
        local_model_paths(models_dir, load_manifest(default_manifest_path()))
    except OcrModelsMissingError as exc:
        pytest.skip(f"OCR 模型不全：{exc}")
    translator = Translator(models_dir)
    if ("zh", "en", "direct") not in translator.available_pairs():
        pytest.skip("缺少 zh-en 翻译模型")
    png = (Path(__file__).parent / "fixtures" / "ocr" / "zh_web_01.png").read_bytes()
    app = create_app(translator, None, ApiSettings(), OcrProvider(models_dir))
    with TestClient(app) as client:
        response = _post(client, "/ocr_translate", png, target="en", fallback_target="zh")
    assert response.status_code == 200, response.text
    body = response.json()
    results = body["translation"]["results"]
    assert len(results) == len(body["paragraphs"]) == 3
    assert all(r["source"] == "zh" and r["target"] == "en" for r in results)
    english = " ".join(r["text"] for r in results)
    assert sum(ch.isascii() and ch.isalpha() for ch in english) > 100, english
