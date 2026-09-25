"""基准脚本的纯函数、报告和进程控制。真实模型用 model 标记，CI 不跑。"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

from suiyi_engine.bench import (
    CASES,
    EN_SHORT,
    ZH_MEDIUM,
    ZH_PARAGRAPH,
    ZH_SHORT,
    BenchError,
    BenchOptions,
    build_serve_command,
    cpu_model,
    han_count,
    main,
    make_report,
    nearest_rank,
    parse_args,
    post_translate,
    render_markdown,
    require_psutil,
    service_is_ready,
    stop_process,
    suggest_defaults,
    summarize,
    word_count,
    write_report,
)
from suiyi_engine.registry import default_intra_threads


def test_sample_lengths_match_the_brief() -> None:
    assert han_count(ZH_SHORT) == 15
    assert han_count(ZH_MEDIUM) == 50
    assert han_count(ZH_PARAGRAPH) == 300
    assert ZH_PARAGRAPH.count("。") >= 3
    assert word_count(EN_SHORT) == 10
    ids = [case.case_id for case in CASES]
    assert "ja_zh_pivot" in ids
    assert "zh_paragraph" in ids


def test_percentiles_use_median_and_nearest_rank() -> None:
    values = list(range(1, 51))
    assert nearest_rank(values, 95) == 48
    stats = summarize(values)
    assert stats["p50_ms"] == 25.5
    assert stats["p95_ms"] == 48
    assert stats["max_ms"] == 50
    assert stats["n"] == 50


def test_service_is_ready_requires_expected_models() -> None:
    assert service_is_ready({"status": "ok", "loaded_models": ["opus-mt-zh-en"]}, set())
    assert service_is_ready(
        {"status": "ok", "loaded_models": ["opus-mt-en-zh", "opus-mt-zh-en"]},
        {"opus-mt-zh-en"},
    )
    assert not service_is_ready({"status": "ok", "loaded_models": []}, {"opus-mt-zh-en"})
    assert not service_is_ready({"status": "ok"}, set())
    assert not service_is_ready({"status": "starting", "loaded_models": []}, set())


def test_serve_command_is_an_argument_list() -> None:
    python = "C:/Program Files/Python311/python.exe"
    models = Path("C:/Program Files/models")
    command = build_serve_command(
        python=python,
        models_dir=models,
        host="127.0.0.1",
        port=18780,
        preload="zh-en,en-zh",
        intra_threads=None,
        beam_size=1,
        max_batch_size=None,
    )
    assert command[0] == python
    assert command[1:4] == ["-m", "suiyi_engine", "serve"]
    assert "--preload" in command
    assert "zh-en,en-zh" in command
    assert "--intra-threads" not in command
    assert command[command.index("--beam-size") + 1] == "1"
    assert str(models) in command
    assert all(not part.startswith('"') for part in command)


def test_suggest_keeps_defaults_without_a_grid(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("suiyi_engine.bench.default_intra_threads", lambda: 4)
    suggestion = suggest_defaults([], [], cpu_count=4)
    assert suggestion["intra_threads"] == 4
    assert suggestion["beam_size"] == 2
    assert suggestion["max_batch_size"] == 32
    assert suggestion["inter_threads"] == 1
    assert suggestion["changed"] is False


def test_suggest_smaller_intra_when_it_is_close(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("suiyi_engine.bench.default_intra_threads", lambda: 4)
    cells = [
        _grid(1, 2, 80, 120),
        _grid(2, 2, 70, 105),
        _grid(4, 2, 60, 100),
        _grid(2, 1, 40, 70),
        _grid(4, 1, 30, 50),
    ]
    batch = [_batch(8, 1000), _batch(32, 1100)]
    suggestion = suggest_defaults(cells, batch, cpu_count=4)
    assert suggestion["intra_threads"] == 2
    assert suggestion["beam_size"] == 2
    assert suggestion["max_batch_size"] == 32
    assert suggestion["changed"] is True


def test_suggest_beam_one_only_if_it_meets_the_target(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("suiyi_engine.bench.default_intra_threads", lambda: 4)
    cells = [
        _grid(4, 2, 400, 900),
        _grid(4, 1, 200, 500),
        _grid(2, 2, 500, 1100),
        _grid(1, 2, 800, 1400),
    ]
    suggestion = suggest_defaults(cells, [], cpu_count=4)
    assert suggestion["intra_threads"] == 4
    assert suggestion["beam_size"] == 1
    assert suggestion["changed"] is True


def test_suggest_does_not_use_more_threads_than_cpus(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("suiyi_engine.bench.default_intra_threads", lambda: 2)
    cells = [
        _grid(4, 2, 40, 80),
        _grid(2, 2, 90, 120),
        _grid(1, 2, 200, 300),
    ]
    suggestion = suggest_defaults(cells, [], cpu_count=2)
    assert suggestion["intra_threads"] == 2
    assert suggestion["beam_size"] == 2
    assert suggestion["changed"] is False


def test_suggest_smaller_batch_when_thirty_two_is_slower(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr("suiyi_engine.bench.default_intra_threads", lambda: 4)
    cells = [_grid(4, 2, 80, 100), _grid(2, 2, 90, 140), _grid(1, 2, 200, 300)]
    suggestion = suggest_defaults(cells, [_batch(8, 1000), _batch(32, 1300)], cpu_count=4)
    assert suggestion["max_batch_size"] == 8


def test_parse_args_defaults(tmp_path: Path) -> None:
    options = parse_args(["--models-dir", str(tmp_path), "--out", str(tmp_path / "out")])
    assert options.repeats == 50
    assert options.cold_starts == 3
    assert options.warmup == 3
    assert options.no_grid is False
    assert options.models_dir == tmp_path.resolve()


def test_parse_args_rejects_public_host(tmp_path: Path) -> None:
    with pytest.raises(BenchError, match="127.0.0.1"):
        parse_args(["--models-dir", str(tmp_path), "--host", "0.0.0.0"])


def test_report_marks_pass_and_fail(tmp_path: Path) -> None:
    report = _sample_report(cold_ms=1000, short_p50=100, short_p95=200, rss_bytes=1000)
    text = render_markdown(report)
    assert "## 冷启动" in text
    assert "## 内存" in text
    assert "达标" in text
    assert "未达标" not in text
    slow = _sample_report(cold_ms=9000, short_p50=500, short_p95=1200, rss_bytes=2 * 1024**3)
    slow_text = render_markdown(slow)
    assert "未达标" in slow_text
    md_path, json_path = write_report(slow, tmp_path / "bench")
    assert md_path.is_file()
    assert json_path.is_file()
    payload = json.loads(json_path.read_text(encoding="utf-8"))
    assert payload["cold_start"]["median_ms"] == 9000
    assert any(row["mark"] == "未达标" for row in payload["verdicts"])


def test_cpu_model_is_non_empty() -> None:
    assert cpu_model().strip()


def test_missing_psutil_explains_the_extra(monkeypatch: pytest.MonkeyPatch) -> None:
    real_import = __import__

    def guarded(name: str, *args: object, **kwargs: object) -> object:
        if name == "psutil":
            raise ImportError("blocked")
        return real_import(name, *args, **kwargs)

    monkeypatch.delitem(sys.modules, "psutil", raising=False)
    monkeypatch.setattr("builtins.__import__", guarded)
    with pytest.raises(BenchError, match="engine\\[bench\\]"):
        require_psutil()


def test_stop_process_terminates_child() -> None:
    proc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
    stop_process(proc, kill_group=False)
    assert proc.poll() is not None


def test_post_translate_times_a_local_response() -> None:
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self) -> None:  # noqa: N802
            length = int(self.headers.get("Content-Length", "0"))
            self.rfile.read(length)
            body = json.dumps({"text": "Hello", "route": ["opus-mt-zh-en"]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, format: str, *args: object) -> None:
            return

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    port = int(server.server_address[1])
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        elapsed_ms, payload = post_translate(
            f"http://127.0.0.1:{port}/translate",
            "你好",
            "zh",
            "en",
            5,
        )
    finally:
        server.shutdown()
        server.server_close()
    assert payload["text"] == "Hello"
    assert elapsed_ms >= 0


def test_main_returns_nonzero_when_models_are_missing(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    code = main(
        [
            "--models-dir",
            str(tmp_path),
            "--out",
            str(tmp_path / "out"),
            "--no-grid",
        ]
    )
    assert code == 1
    assert "mvp" in capsys.readouterr().err


@pytest.mark.model
def test_bench_smoke_with_real_models(tmp_path: Path) -> None:
    pytest.importorskip("psutil")
    models = os.environ["SUIYI_MODELS_DIR"]
    out = tmp_path / "out"
    code = main(
        [
            "--models-dir",
            models,
            "--out",
            str(out),
            "--repeats",
            "2",
            "--cold-starts",
            "1",
            "--warmup",
            "1",
            "--no-grid",
        ]
    )
    assert code == 0
    payload = json.loads((out / "bench.json").read_text(encoding="utf-8"))
    assert len(payload["cold_start"]["runs_ms"]) == 1
    assert [row["case_id"] for row in payload["latency"]] == [case.case_id for case in CASES]
    assert payload["rss"]["idle_bytes"] > 0
    assert payload["rss"]["mvp_bytes"] >= payload["rss"]["bilingual_bytes"]
    assert (out / "bench.md").is_file()


def _grid(intra: int, beam: int, p50: float, p95: float) -> dict[str, float | int]:
    return {
        "intra_threads": intra,
        "beam_size": beam,
        "p50_ms": p50,
        "p95_ms": p95,
        "max_ms": p95,
        "n": 20,
    }


def _batch(size: int, p95: float) -> dict[str, float | int]:
    return {"max_batch_size": size, "p50_ms": p95 * 0.8, "p95_ms": p95, "max_ms": p95, "n": 10}


def _sample_report(
    *,
    cold_ms: float,
    short_p50: float,
    short_p95: float,
    rss_bytes: int,
) -> dict[str, object]:
    latency = []
    for case in CASES:
        p50 = short_p50
        p95 = short_p95
        if case.case_id == "zh_medium":
            p95 = 400
        if case.case_id == "zh_paragraph":
            p95 = 900
        if case.case_id == "ja_zh_pivot":
            p95 = 700
        latency.append(
            {
                "case_id": case.case_id,
                "label": case.label,
                "src": case.src,
                "tgt": case.tgt,
                "kind": case.kind,
                "size_label": "n",
                "sentences": 2,
                "n": 50,
                "p50_ms": p50,
                "p95_ms": p95,
                "max_ms": p95,
            }
        )
    options = BenchOptions(
        models_dir=Path("/models"),
        manifest_path=Path("/manifest.json"),
        repeats=50,
        cold_starts=1,
        warmup=3,
        grid_repeats=20,
        batch_repeats=10,
        no_grid=True,
        out_dir=Path("/tmp/bench"),
        host="127.0.0.1",
        cold_timeout_s=180,
        request_timeout_s=120,
        python=sys.executable,
    )
    return make_report(
        environment={
            "date": "2026-09-25",
            "system": "Linux",
            "platform": "Linux-test",
            "cpu_model": "test-cpu",
            "cpu_count_logical": default_intra_threads(),
            "cpu_count_physical": 2,
            "memory_total_bytes": 8 * 1024**3,
            "python": "3.11.0",
            "suiyi_engine": "0.0.1",
            "ctranslate2": "4.8.2",
        },
        options=options,
        cold_runs_ms=[cold_ms],
        first_translate={
            "label": "中文短句",
            "src": "zh",
            "tgt": "en",
            "elapsed_ms": short_p50,
            "sample_text": "Hello",
        },
        latency=latency,
        rss={
            "idle_bytes": rss_bytes // 4,
            "bilingual_bytes": rss_bytes // 2,
            "mvp_bytes": rss_bytes,
            "idle_loaded": [],
            "bilingual_loaded": ["opus-mt-en-zh", "opus-mt-zh-en"],
            "mvp_loaded": ["opus-mt-zh-en"],
        },
        grid_cells=[],
        batch_cells=[],
        suggestion=suggest_defaults([], [], cpu_count=4),
        m2={
            "notes": ["短句超时单独设置。", "启动时预加载中英双向。"],
        },
        bilingual_ids=["opus-mt-en-zh", "opus-mt-zh-en"],
        mvp_ids=["opus-mt-zh-en"],
    )
