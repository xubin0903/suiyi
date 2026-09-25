"""#46：服务被强杀后立即重启不应被残留连接误判为端口占用；真实监听者仍要判为占用。"""

from __future__ import annotations

import http.client
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

import pytest

from suiyi_engine.serve import ServeError, bind_listen_socket, run_server

IS_WINDOWS = os.name == "nt"


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def _spawn(port: int, models_dir: Path, log_path: Path) -> subprocess.Popen[bytes]:
    with log_path.open("w", encoding="utf-8") as handle:
        return subprocess.Popen(
            [
                sys.executable,
                "-m",
                "suiyi_engine",
                "serve",
                "--port",
                str(port),
                "--models-dir",
                str(models_dir),
            ],
            stdout=handle,
            stderr=subprocess.STDOUT,
            env={**os.environ, "PYTHONUNBUFFERED": "1", "PYTHONIOENCODING": "utf-8"},
        )


def _wait_ready(proc: subprocess.Popen[bytes], port: int, log_path: Path) -> float:
    """等到 ``/health`` 返回 200，返回耗时秒数；进程提前退出或超时则失败并附日志。"""

    started = time.monotonic()
    deadline = started + 30
    while time.monotonic() < deadline:
        code = proc.poll()
        if code is not None:
            log = log_path.read_text(encoding="utf-8", errors="replace")
            raise AssertionError(f"服务提前退出 rc={code}：\n{log}")
        conn = http.client.HTTPConnection("127.0.0.1", port, timeout=0.5)
        try:
            conn.request("GET", "/health")
            if conn.getresponse().status == 200:
                return time.monotonic() - started
        except OSError:
            time.sleep(0.05)
        finally:
            conn.close()
    raise AssertionError("服务 30 秒内未就绪：\n" + log_path.read_text(encoding="utf-8"))


def _stop(proc: subprocess.Popen[bytes]) -> None:
    if proc.poll() is None:
        proc.kill()
    proc.wait(timeout=10)


def test_immediate_restart_after_kill_with_open_keepalive_connection(tmp_path: Path) -> None:
    """复现 #46：客户端连接池里留着 keep-alive 连接时强杀服务，立即重启必须成功。

    服务被杀后内核替它先发 FIN，服务端这一侧的连接停在 FIN-WAIT-2（客户端关闭后变 TIME_WAIT）。
    修复前 Linux 上端口预检的 ``bind`` 因此报 ``EADDRINUSE``，新进程以「端口已被占用」退出。
    """

    port = _free_port()
    first = _spawn(port, tmp_path, tmp_path / "first.log")
    keepalive = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    second: subprocess.Popen[bytes] | None = None
    try:
        _wait_ready(first, port, tmp_path / "first.log")
        keepalive.request("GET", "/health")
        response = keepalive.getresponse()
        response.read()
        assert response.status == 200
        assert response.getheader("connection", "").lower() != "close"

        first.kill()
        first.wait(timeout=10)

        second = _spawn(port, tmp_path, tmp_path / "second.log")
        elapsed = _wait_ready(second, port, tmp_path / "second.log")
        assert elapsed < 20
    finally:
        keepalive.close()
        _stop(first)
        if second is not None:
            _stop(second)


def _leave_lingering_connection(port: int) -> socket.socket:
    """模拟服务被强杀：服务端先关闭已接受的连接和监听套接字，客户端连接仍开着。

    返回客户端连接；此时服务端一侧停在 FIN-WAIT-2，客户端关闭后变为 TIME_WAIT。
    """

    listener = bind_listen_socket("127.0.0.1", port)
    try:
        listener.listen(1)
        client = socket.create_connection(("127.0.0.1", port), timeout=5)
        accepted, _addr = listener.accept()
        accepted.close()
    finally:
        listener.close()
    return client


def test_rebind_succeeds_while_previous_connection_lingers() -> None:
    port = _free_port()
    client = _leave_lingering_connection(port)
    try:
        bind_listen_socket("127.0.0.1", port).close()  # 服务端一侧 FIN-WAIT-2
    finally:
        client.close()
    time.sleep(0.05)
    bind_listen_socket("127.0.0.1", port).close()  # 服务端一侧 TIME_WAIT


@pytest.mark.skipif(not sys.platform.startswith("linux"), reason="根因只在 Linux 上稳定复现")
def test_plain_bind_is_blocked_by_lingering_connection_on_linux() -> None:
    """记录根因：不设 ``SO_REUSEADDR`` 的 ``bind`` 在 Linux 上会被残留连接挡住（修复前的预检）。"""

    port = _free_port()
    client = _leave_lingering_connection(port)
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as plain:
            with pytest.raises(OSError):
                plain.bind(("127.0.0.1", port))
    finally:
        client.close()


def _occupy(kind: str) -> socket.socket:
    if kind == "ours":
        sock = bind_listen_socket("127.0.0.1", 0)
    else:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        if kind == "reuseaddr":
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind(("0.0.0.0" if kind == "wildcard" else "127.0.0.1", 0))
    sock.listen(1)
    return sock


@pytest.mark.parametrize(
    "kind",
    [
        "plain",
        "ours",
        pytest.param(
            "reuseaddr",
            marks=pytest.mark.skipif(
                IS_WINDOWS, reason="Windows 上 SO_REUSEADDR 本身就允许抢占，不作为对手测试"
            ),
        ),
        pytest.param(
            "wildcard",
            marks=pytest.mark.skipif(
                sys.platform == "darwin",
                reason="BSD 语义允许特定地址与通配地址共存，uvicorn 本身也能绑上",
            ),
        ),
    ],
)
def test_live_listener_is_still_reported_in_use(kind: str) -> None:
    occupier = _occupy(kind)
    port = int(occupier.getsockname()[1])
    try:
        with pytest.raises(ServeError) as excinfo:
            bind_listen_socket("127.0.0.1", port)
    finally:
        occupier.close()
    assert excinfo.value.code == 1
    assert "占用" in str(excinfo.value)


def test_run_server_reports_live_listener(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    occupier = _occupy("ours")
    port = int(occupier.getsockname()[1])
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
        occupier.close()
    assert code == 1
    assert "占用" in capsys.readouterr().err


def test_listen_socket_options_follow_platform() -> None:
    sock = bind_listen_socket("127.0.0.1", 0)
    try:
        if IS_WINDOWS:
            assert sock.getsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR) == 0
        else:
            assert sock.getsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR) != 0
        assert not sock.get_inheritable()
    finally:
        sock.close()


def test_run_server_serves_on_prebound_socket_and_closes_it(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """端口在预热前就绑好，交给 uvicorn 的正是这个套接字；服务结束后套接字被关闭。"""

    port = _free_port()
    order: list[str] = []
    seen: dict[str, object] = {}
    real_bind = bind_listen_socket

    def spy_bind(host: str, bind_port: int) -> socket.socket:
        order.append("bind")
        return real_bind(host, bind_port)

    def fake_warmup() -> float:
        order.append("warmup")
        return 1.0

    def fake_serve(_app: object, listen_socket: socket.socket) -> None:
        order.append("serve")
        seen["addr"] = listen_socket.getsockname()[:2]
        seen["socket"] = listen_socket

    monkeypatch.setattr("suiyi_engine.serve.bind_listen_socket", spy_bind)
    monkeypatch.setattr("suiyi_engine.serve.langdetect.warmup", fake_warmup)
    monkeypatch.setattr("suiyi_engine.serve._serve_uvicorn", fake_serve)
    code = run_server(
        host="127.0.0.1",
        port=port,
        models_dir=tmp_path,
        preload_pairs=[],
        max_text_chars=10,
        dev=False,
    )
    assert code == 0
    assert order == ["bind", "warmup", "serve"]
    assert seen["addr"] == ("127.0.0.1", port)
    listen_socket = seen["socket"]
    assert isinstance(listen_socket, socket.socket)
    assert listen_socket.fileno() == -1
    bind_listen_socket("127.0.0.1", port).close()


def test_second_instance_cannot_listen_on_same_port() -> None:
    """两个实例同时启动：即使都绑上了（POSIX 上都未 listen 时允许），后 listen 的一方必然失败。"""

    first = bind_listen_socket("127.0.0.1", 0)
    port = int(first.getsockname()[1])
    try:
        try:
            second = bind_listen_socket("127.0.0.1", port)
        except ServeError:
            return  # Windows：绑定阶段就报占用
        try:
            first.listen(1)
            with pytest.raises(OSError):
                second.listen(1)
        finally:
            second.close()
    finally:
        first.close()
