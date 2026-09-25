"""``python -m suiyi_engine serve``：只绑回环、端口占用、启动日志。"""

from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

import pytest

from suiyi_engine.__main__ import main
from suiyi_engine.registry import DEFAULT_BEAM_SIZE, DEFAULT_MAX_BATCH_SIZE, default_intra_threads
from suiyi_engine.serve import (
    ServeError,
    parse_preload,
    resolve_decode_options,
    resolve_max_text_chars,
    resolve_port,
    run_server,
    validate_host,
)


def _free_port() -> int:
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    port = int(sock.getsockname()[1])
    sock.close()
    return port


@pytest.mark.parametrize(
    "host",
    ["127.0.0.1", "localhost", "LOCALHOST", "::1", " 127.0.0.1 "],
)
def test_validate_host_accepts_loopback_only(host: str) -> None:
    assert validate_host(host) in {"127.0.0.1", "localhost", "::1"}


@pytest.mark.parametrize(
    "host",
    ["0.0.0.0", "192.168.0.1", "::", "", "127.0.0.2", "localhost.example"],
)
def test_validate_host_rejects_other_addresses(host: str) -> None:
    with pytest.raises(ServeError, match="127.0.0.1"):
        validate_host(host)


def test_resolve_port_prefers_cli_over_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SUIYI_PORT", "19000")
    assert resolve_port(None) == 19000
    assert resolve_port(19001) == 19001
    monkeypatch.delenv("SUIYI_PORT")
    assert resolve_port(None) == 18780


def test_resolve_port_rejects_bad_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SUIYI_PORT", "nope")
    with pytest.raises(ServeError, match="SUIYI_PORT"):
        resolve_port(None)
    with pytest.raises(ServeError):
        resolve_port(0)
    with pytest.raises(ServeError):
        resolve_port(65536)


def test_resolve_max_text_chars(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("SUIYI_MAX_TEXT_CHARS", raising=False)
    assert resolve_max_text_chars(None) == 10_000
    monkeypatch.setenv("SUIYI_MAX_TEXT_CHARS", "32")
    assert resolve_max_text_chars(None) == 32
    assert resolve_max_text_chars(8) == 8
    monkeypatch.setenv("SUIYI_MAX_TEXT_CHARS", "x")
    with pytest.raises(ServeError, match="SUIYI_MAX_TEXT_CHARS"):
        resolve_max_text_chars(None)


def test_parse_preload() -> None:
    assert parse_preload("") == []
    assert parse_preload(" ZH-en, en-zh, ") == [("zh", "en"), ("en", "zh")]
    with pytest.raises(ServeError, match="zh"):
        parse_preload("zh")
    with pytest.raises(ServeError):
        parse_preload("auto-en")


def test_cli_rejects_wildcard_host_without_listening() -> None:
    completed = subprocess.run(
        [sys.executable, "-m", "suiyi_engine", "serve", "--host", "0.0.0.0"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    assert completed.returncode != 0
    assert "0.0.0.0" in completed.stderr
    assert "127.0.0.1" in completed.stderr


def test_rejected_host_does_not_construct_translator(
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    def boom(*_args: object, **_kwargs: object) -> None:
        raise AssertionError("不应构造翻译器")

    monkeypatch.setattr("suiyi_engine.serve.Translator", boom)
    code = run_server(
        host="0.0.0.0",
        port=18780,
        models_dir=None,
        preload_pairs=[],
        max_text_chars=10,
        dev=False,
    )
    assert code == 2
    assert "0.0.0.0" in capsys.readouterr().err


def test_port_in_use_exits_nonzero(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    sock.listen(1)
    port = int(sock.getsockname()[1])
    try:
        code = run_server(
            host="127.0.0.1",
            port=port,
            models_dir=tmp_path,
            preload_pairs=[],
            max_text_chars=10,
            dev=False,
        )
    finally:
        sock.close()
    assert code == 1
    err = capsys.readouterr().err
    assert str(port) in err
    assert "占用" in err


def test_preload_missing_model_does_not_listen(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    def fail(*_args: object, **_kwargs: object) -> None:
        raise AssertionError("不应开始监听")

    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fail)
    code = run_server(
        host="127.0.0.1",
        port=_free_port(),
        models_dir=tmp_path,
        preload_pairs=[("zh", "en")],
        max_text_chars=100,
        dev=False,
    )
    assert code == 1
    assert "opus-mt-zh-en" in capsys.readouterr().err


def test_startup_log_and_loopback_binding(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    seen: dict[str, object] = {}

    def fake(app: object, listen_socket: socket.socket) -> None:
        seen["host"], seen["port"] = listen_socket.getsockname()[:2]
        seen["docs"] = app.docs_url  # type: ignore[attr-defined]

    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fake)
    port = _free_port()
    code = run_server(
        host="127.0.0.1",
        port=port,
        models_dir=tmp_path,
        preload_pairs=[],
        max_text_chars=100,
        dev=False,
    )
    assert code == 0
    out = capsys.readouterr().out
    assert f"127.0.0.1:{port}" in out
    assert str(tmp_path) in out
    assert "可用语向 0" in out
    assert f"intra_threads={default_intra_threads()}" in out
    assert f"beam_size={DEFAULT_BEAM_SIZE}" in out
    assert f"max_batch_size={DEFAULT_MAX_BATCH_SIZE}" in out
    assert seen == {"host": "127.0.0.1", "port": port, "docs": None}


def test_cli_port_env_and_dev_flag(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    seen: dict[str, object] = {}

    def fake(app: object, listen_socket: socket.socket) -> None:
        seen["host"], seen["port"] = listen_socket.getsockname()[:2]
        seen["docs"] = app.docs_url  # type: ignore[attr-defined]
        seen["max"] = app.state.settings.max_text_chars  # type: ignore[attr-defined]

    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fake)
    port = _free_port()
    monkeypatch.setenv("SUIYI_PORT", str(port))
    monkeypatch.setenv("SUIYI_MAX_TEXT_CHARS", "12")
    assert main(["serve", "--models-dir", str(tmp_path)]) == 0
    assert seen["host"] == "127.0.0.1"
    assert seen["port"] == port
    assert seen["docs"] is None
    assert seen["max"] == 12

    override = _free_port()
    assert (
        main(
            [
                "serve",
                "--models-dir",
                str(tmp_path),
                "--port",
                str(override),
                "--max-text-chars",
                "3",
                "--dev",
            ]
        )
        == 0
    )
    assert seen["port"] == override
    assert seen["max"] == 3
    assert seen["docs"] == "/docs"


def test_cli_bad_port_and_preload(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("SUIYI_PORT", "nope")
    assert main(["serve"]) == 2
    assert "SUIYI_PORT" in capsys.readouterr().err
    assert main(["serve", "--preload", "zh", "--port", "18780"]) == 2


def test_resolve_decode_options_defaults_and_overrides() -> None:
    defaults = resolve_decode_options(intra_threads=None, beam_size=None, max_batch_size=None)
    assert defaults == {
        "intra_threads": default_intra_threads(),
        "beam_size": DEFAULT_BEAM_SIZE,
        "max_batch_size": DEFAULT_MAX_BATCH_SIZE,
    }
    assert resolve_decode_options(intra_threads=2, beam_size=1, max_batch_size=8) == {
        "intra_threads": 2,
        "beam_size": 1,
        "max_batch_size": 8,
    }
    with pytest.raises(ServeError, match="beam_size"):
        resolve_decode_options(intra_threads=None, beam_size=0, max_batch_size=None)


def test_cli_passes_decode_flags(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    seen: dict[str, object] = {}

    class FakeTranslator:
        def __init__(self, models_dir: object, **kwargs: int) -> None:
            seen["kwargs"] = kwargs
            self.registry = type("Registry", (), {"models_dir": models_dir})()

        def available_pairs(self) -> list[tuple[str, str, str]]:
            return []

        def preload(self, pairs: object) -> None:
            seen["preload"] = pairs

    monkeypatch.setattr("suiyi_engine.serve.Translator", FakeTranslator)
    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", lambda *_args, **_kwargs: None)
    code = main(
        [
            "serve",
            "--models-dir",
            str(tmp_path),
            "--port",
            str(_free_port()),
            "--intra-threads",
            "2",
            "--beam-size",
            "4",
            "--max-batch-size",
            "8",
            "--preload",
            "zh-en",
        ]
    )
    assert code == 0
    assert seen["kwargs"] == {"intra_threads": 2, "beam_size": 4, "max_batch_size": 8}
    assert seen["preload"] == [("zh", "en")]
    assert "intra_threads=2 beam_size=4 max_batch_size=8" in capsys.readouterr().out


def test_serve_help_mentions_loopback() -> None:
    completed = subprocess.run(
        [sys.executable, "-m", "suiyi_engine", "serve", "--help"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    assert completed.returncode == 0
    assert "127.0.0.1" in completed.stdout
    assert "18780" in completed.stdout


def test_cli_serves_health_on_loopback_only(tmp_path: Path) -> None:
    port = _free_port()
    log_path = tmp_path / "serve.log"
    with log_path.open("w", encoding="utf-8") as handle:
        proc = subprocess.Popen(
            [
                sys.executable,
                "-m",
                "suiyi_engine",
                "serve",
                "--host",
                "127.0.0.1",
                "--port",
                str(port),
                "--models-dir",
                str(tmp_path),
            ],
            stdout=handle,
            stderr=subprocess.STDOUT,
            env={
                **os.environ,
                "PYTHONUNBUFFERED": "1",
                "PYTHONIOENCODING": "utf-8",
            },
        )
    try:
        body = _wait_health(port)
        assert body["status"] == "ok"
        assert body["version"]
        assert Path(body["models_dir"]) == tmp_path
        log = log_path.read_text(encoding="utf-8")
        assert f"127.0.0.1:{port}" in log
        assert "模型目录" in log
        assert "可用语向" in log
        if Path("/proc/net/tcp").is_file():
            listeners = _ipv4_listeners(port)
            assert "127.0.0.1" in listeners
            assert "0.0.0.0" not in listeners
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)


def _wait_health(port: int) -> dict[str, object]:
    deadline = time.time() + 20
    last = ""
    url = f"http://127.0.0.1:{port}/health"
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=0.5) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except OSError as exc:
            last = str(exc)
            time.sleep(0.05)
            continue
        if isinstance(payload, dict):
            return payload
    raise AssertionError(f"服务未就绪：{last}")


def _ipv4_listeners(port: int) -> set[str]:
    found: set[str] = set()
    for line in Path("/proc/net/tcp").read_text(encoding="utf-8").splitlines()[1:]:
        parts = line.split()
        if len(parts) < 4:
            continue
        local, state = parts[1], parts[3]
        ip_hex, port_hex = local.split(":")
        if int(port_hex, 16) != port or state != "0A":
            continue
        found.add(socket.inet_ntoa(bytes.fromhex(ip_hex)[::-1]))
    return found


def test_detector_is_warmed_before_listening(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    order: list[str] = []

    def fake_warmup() -> float:
        order.append("warmup")
        return 123.4

    def fake_uvicorn(*_args: object, **_kwargs: object) -> None:
        order.append("listen")

    monkeypatch.setattr("suiyi_engine.serve.langdetect.warmup", fake_warmup)
    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fake_uvicorn)
    code = run_server(
        host="127.0.0.1",
        port=_free_port(),
        models_dir=tmp_path,
        preload_pairs=[],
        max_text_chars=100,
        dev=False,
    )
    assert code == 0
    assert order == ["warmup", "listen"]
    assert "语种检测已预热 123 ms" in capsys.readouterr().out


def test_detector_warmup_failure_does_not_block_startup(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    listened: list[bool] = []

    def broken() -> float:
        raise RuntimeError("model file missing")

    monkeypatch.setattr("suiyi_engine.serve.langdetect.warmup", broken)
    monkeypatch.setattr(
        "suiyi_engine.serve._serve_uvicorn", lambda *_a, **_k: listened.append(True)
    )
    code = run_server(
        host="127.0.0.1",
        port=_free_port(),
        models_dir=tmp_path,
        preload_pairs=[],
        max_text_chars=100,
        dev=False,
    )
    assert code == 0
    assert listened == [True]
    captured = capsys.readouterr()
    assert "语种检测预热失败" in captured.err
    assert "model file missing" in captured.err
    assert "语种检测已预热" not in captured.out


def test_port_in_use_skips_detector_warmup(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    def boom() -> float:
        raise AssertionError("端口被占用时不应预热")

    monkeypatch.setattr("suiyi_engine.serve.langdetect.warmup", boom)
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    sock.listen(1)
    try:
        code = run_server(
            host="127.0.0.1",
            port=int(sock.getsockname()[1]),
            models_dir=tmp_path,
            preload_pairs=[],
            max_text_chars=10,
            dev=False,
        )
    finally:
        sock.close()
    assert code == 1


def test_max_image_bytes_from_cli_env_and_default(monkeypatch: pytest.MonkeyPatch) -> None:
    from suiyi_engine.serve import ServeError, resolve_max_image_bytes

    monkeypatch.delenv("SUIYI_MAX_IMAGE_BYTES", raising=False)
    assert resolve_max_image_bytes(None) == 8 * 1024 * 1024
    monkeypatch.setenv("SUIYI_MAX_IMAGE_BYTES", "1234")
    assert resolve_max_image_bytes(None) == 1234
    assert resolve_max_image_bytes(99) == 99
    monkeypatch.setenv("SUIYI_MAX_IMAGE_BYTES", "big")
    with pytest.raises(ServeError):
        resolve_max_image_bytes(None)
    with pytest.raises(ServeError):
        resolve_max_image_bytes(0)


def test_preload_ocr_missing_models_exits_nonzero_without_listening(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    def fail(*_args: object, **_kwargs: object) -> None:
        raise AssertionError("不应开始监听")

    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fail)
    monkeypatch.setattr("suiyi_engine.serve.bind_listen_socket", fail)
    code = run_server(
        host="127.0.0.1",
        port=_free_port(),
        models_dir=tmp_path,
        preload_pairs=[],
        max_text_chars=100,
        dev=False,
        preload_ocr=True,
    )
    assert code == 1
    err = capsys.readouterr().err
    assert "--preload-ocr" in err
    assert "PP-OCRv6_det_small" in err and "PP-OCRv6_rec_small" in err  # 报模型 id
    assert "download_ocr_models.py" in err


def test_without_preload_ocr_missing_models_still_serves(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    seen: dict[str, object] = {}

    def fake(app: object, listen_socket: socket.socket) -> None:
        seen["ocr_loaded"] = app.state.ocr.loaded  # type: ignore[attr-defined]
        seen["max_image_bytes"] = app.state.settings.max_image_bytes  # type: ignore[attr-defined]

    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fake)
    code = run_server(
        host="127.0.0.1",
        port=_free_port(),
        models_dir=tmp_path,
        preload_pairs=[],
        max_text_chars=100,
        dev=False,
        max_image_bytes=4096,
    )
    assert code == 0
    assert seen == {"ocr_loaded": False, "max_image_bytes": 4096}


def test_preload_ocr_flag_is_parsed(monkeypatch: pytest.MonkeyPatch) -> None:
    seen: dict[str, object] = {}

    def fake_run(**kwargs: object) -> int:
        seen.update(kwargs)
        return 0

    monkeypatch.setattr("suiyi_engine.serve.run_server", fake_run)
    # 客户端（EngineCommandResolver）的实际参数顺序
    assert main(["serve", "--port", "18780", "--preload", "zh-en,en-zh", "--preload-ocr"]) == 0
    assert seen["preload_ocr"] is True
    assert main(["serve", "--port", "18780", "--max-image-bytes", "1000"]) == 0
    assert seen["preload_ocr"] is False and seen["max_image_bytes"] == 1000


def test_preload_ocr_success_is_logged(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    from suiyi_engine.api_ocr import OcrProvider

    seen: dict[str, object] = {}

    def warmup(self: OcrProvider) -> float:
        seen["warmed"] = True
        return 12.0

    def fake(app: object, listen_socket: socket.socket) -> None:
        seen["same_provider"] = app.state.ocr is provider_holder[0]  # type: ignore[attr-defined]

    provider_holder: list[object] = []
    original_init = OcrProvider.__init__

    def init(self: OcrProvider, *args: object, **kwargs: object) -> None:
        original_init(self, *args, **kwargs)  # type: ignore[arg-type]
        provider_holder.append(self)

    monkeypatch.setattr(OcrProvider, "warmup", warmup)
    monkeypatch.setattr(OcrProvider, "__init__", init)
    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fake)
    code = run_server(
        host="127.0.0.1",
        port=_free_port(),
        models_dir=tmp_path,
        preload_pairs=[],
        max_text_chars=100,
        dev=False,
        preload_ocr=True,
    )
    assert code == 0
    assert "OCR 已预热 12 ms" in capsys.readouterr().out
    assert seen == {"warmed": True, "same_provider": True}  # 预热的就是服务用的那个实例
