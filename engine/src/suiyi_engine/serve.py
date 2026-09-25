"""``python -m suiyi_engine serve`` 的启动逻辑。

只接受回环地址。端口被占用或参数不合法时打印原因并以非零状态退出，
不把服务暴露到局域网。
"""

from __future__ import annotations

import argparse
import os
import socket
import sys
from pathlib import Path

from suiyi_engine import langdetect
from suiyi_engine.api import ApiSettings, create_app
from suiyi_engine.errors import UnsupportedPairError
from suiyi_engine.registry import (
    DEFAULT_BEAM_SIZE,
    DEFAULT_MAX_BATCH_SIZE,
    default_intra_threads,
    normalize_lang,
)
from suiyi_engine.translator import Translator

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 18780
DEFAULT_MAX_TEXT_CHARS = 10_000
_ALLOWED_HOSTS = frozenset({"127.0.0.1", "::1", "localhost"})


class ServeError(Exception):
    """启动参数或端口检查失败。``code`` 作为进程退出码。"""

    def __init__(self, message: str, code: int = 2) -> None:
        super().__init__(message)
        self.code = code


def validate_host(host: str) -> str:
    """只允许 ``127.0.0.1``、``::1`` 和 ``localhost``。"""

    if not isinstance(host, str):
        raise ServeError("--host 必须是字符串")
    cleaned = host.strip()
    lowered = cleaned.lower()
    if lowered == "localhost":
        return "localhost"
    if cleaned in _ALLOWED_HOSTS:
        return cleaned
    raise ServeError(
        f"--host 仅接受 127.0.0.1、::1、localhost，拒绝 {host!r}。"
        "服务只监听本机回环，不能暴露到局域网。"
    )


def resolve_port(cli_port: int | None) -> int:
    """命令行优先，其次环境变量 ``SUIYI_PORT``，默认 18780。"""

    if cli_port is not None:
        return _require_port(cli_port, "端口")
    raw = os.environ.get("SUIYI_PORT", "").strip()
    if not raw:
        return DEFAULT_PORT
    try:
        value = int(raw)
    except ValueError as exc:
        raise ServeError(f"SUIYI_PORT 不是整数：{raw}") from exc
    return _require_port(value, "SUIYI_PORT")


def resolve_max_text_chars(cli_value: int | None) -> int:
    """命令行优先，其次 ``SUIYI_MAX_TEXT_CHARS``，默认 10000。"""

    if cli_value is not None:
        return _require_limit(cli_value, "--max-text-chars")
    raw = os.environ.get("SUIYI_MAX_TEXT_CHARS", "").strip()
    if not raw:
        return DEFAULT_MAX_TEXT_CHARS
    try:
        value = int(raw)
    except ValueError as exc:
        raise ServeError(f"SUIYI_MAX_TEXT_CHARS 不是整数：{raw}") from exc
    return _require_limit(value, "SUIYI_MAX_TEXT_CHARS")


def parse_preload(raw: str | None) -> list[tuple[str, str]]:
    """把 ``zh-en,en-zh`` 解析成语种对。空字符串表示不预热。"""

    text = (raw or "").strip()
    if not text:
        return []
    pairs: list[tuple[str, str]] = []
    for part in text.split(","):
        item = part.strip()
        if not item:
            continue
        pieces = item.split("-")
        if len(pieces) != 2 or not pieces[0] or not pieces[1]:
            raise ServeError(f"无法解析预热语向 {item!r}，应为 zh-en 这种形式")
        try:
            src = normalize_lang(pieces[0])
            tgt = normalize_lang(pieces[1])
        except ValueError as exc:
            raise ServeError(str(exc)) from exc
        pairs.append((src, tgt))
    return pairs


def serve_from_args(args: argparse.Namespace) -> int:
    """从 argparse 结果启动。供 ``python -m suiyi_engine serve`` 调用。"""

    try:
        host = validate_host(args.host)
        port = resolve_port(args.port)
        max_text_chars = resolve_max_text_chars(args.max_text_chars)
        preload_pairs = parse_preload(args.preload)
    except ServeError as exc:
        print(str(exc), file=sys.stderr)
        return exc.code
    return run_server(
        host=host,
        port=port,
        models_dir=args.models_dir,
        preload_pairs=preload_pairs,
        max_text_chars=max_text_chars,
        dev=bool(args.dev),
        intra_threads=args.intra_threads,
        beam_size=args.beam_size,
        max_batch_size=args.max_batch_size,
    )


def run_server(
    *,
    host: str,
    port: int,
    models_dir: Path | str | None,
    preload_pairs: list[tuple[str, str]],
    max_text_chars: int,
    dev: bool,
    intra_threads: int | None = None,
    beam_size: int | None = None,
    max_batch_size: int | None = None,
) -> int:
    """构建翻译器并阻塞运行，直到进程收到停止信号。

    ``intra_threads``、``beam_size``、``max_batch_size`` 为 ``None`` 时用翻译核心的默认值。
    """

    try:
        host = validate_host(host)
        port = _require_port(port, "端口")
        max_text_chars = _require_limit(max_text_chars, "max_text_chars")
        decode = resolve_decode_options(
            intra_threads=intra_threads,
            beam_size=beam_size,
            max_batch_size=max_batch_size,
        )
    except ServeError as exc:
        print(str(exc), file=sys.stderr)
        return exc.code

    try:
        translator = Translator(models_dir, **decode)
        if preload_pairs:
            translator.preload(preload_pairs)
    except UnsupportedPairError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    except (OSError, ValueError) as exc:
        print(str(exc), file=sys.stderr)
        return 1

    # 先占住端口再预热：端口被占用时尽快退出，也不会在预热期间被别的进程抢走（#46）。
    try:
        listen_socket = bind_listen_socket(host, port)
    except ServeError as exc:
        print(str(exc), file=sys.stderr)
        return exc.code

    try:
        detector_ms = warmup_detector()

        app = create_app(
            translator,
            None,
            ApiSettings(max_text_chars=max_text_chars, dev=dev),
        )
        _print_startup(host, port, translator, decode, detector_ms)
        try:
            _serve_uvicorn(app, listen_socket)
        except OSError as exc:
            print(f"无法在 {host}:{port} 启动服务：{exc}", file=sys.stderr)
            return 1
        return 0
    finally:
        listen_socket.close()


def warmup_detector() -> float | None:
    """开始监听前加载语种检测的统计模型，返回耗时毫秒；失败时返回 ``None``。

    不预热的话，第一次需要统计模型的 ``source=auto`` 请求要多等约 0.5 秒，
    而这段时间不在 ``elapsed_ms`` 里（见 #40）。预热失败不阻止启动，检测会在第一次使用时再加载。
    """

    try:
        return langdetect.warmup()
    except Exception as exc:  # 预热失败只告警，不影响翻译
        print(f"语种检测预热失败，将在首次使用时加载：{exc}", file=sys.stderr, flush=True)
        return None


def _serve_uvicorn(app: object, listen_socket: socket.socket) -> None:
    """在 :func:`bind_listen_socket` 绑好的套接字上开始监听并服务，直到进程收到退出信号。"""

    import uvicorn

    config = uvicorn.Config(app, log_level="info", access_log=True)  # type: ignore[arg-type]
    uvicorn.Server(config).run(sockets=[listen_socket])


def resolve_decode_options(
    *,
    intra_threads: int | None,
    beam_size: int | None,
    max_batch_size: int | None,
) -> dict[str, int]:
    """把可选的解码参数收成传给 ``Translator`` 的关键字。

    ``None`` 表示沿用默认：``intra_threads`` 为 ``min(2, CPU 数)``，
    ``beam_size`` 为 2，``max_batch_size`` 为 32。传入的整数必须 >= 1。
    """

    resolved = {
        "intra_threads": default_intra_threads() if intra_threads is None else intra_threads,
        "beam_size": DEFAULT_BEAM_SIZE if beam_size is None else beam_size,
        "max_batch_size": DEFAULT_MAX_BATCH_SIZE if max_batch_size is None else max_batch_size,
    }
    for name, value in resolved.items():
        if isinstance(value, bool) or not isinstance(value, int) or value < 1:
            raise ServeError(f"{name} 必须是 >= 1 的整数，收到 {value!r}")
    return resolved


def _print_startup(
    host: str,
    port: int,
    translator: Translator,
    decode: dict[str, int],
    detector_ms: float | None = None,
) -> None:
    print(f"监听 {_listen_url(host, port)}", flush=True)
    print(f"模型目录 {translator.registry.models_dir}", flush=True)
    print(f"可用语向 {len(translator.available_pairs())}", flush=True)
    print(
        "解码 "
        f"intra_threads={decode['intra_threads']} "
        f"beam_size={decode['beam_size']} "
        f"max_batch_size={decode['max_batch_size']}",
        flush=True,
    )
    if detector_ms is not None:
        print(f"语种检测已预热 {detector_ms:.0f} ms", flush=True)


def _listen_url(host: str, port: int) -> str:
    shown = f"[{host}]" if ":" in host else host
    return f"http://{shown}:{port}"


def bind_listen_socket(host: str, port: int) -> socket.socket:
    """绑定服务的监听套接字（还不 ``listen``）交给 uvicorn；端口被占用时抛 :class:`ServeError`。

    套接字选项按平台区分（#46）：

    - Linux / macOS：设 ``SO_REUSEADDR``。服务被强杀后，客户端连接池里的 keep-alive 连接会让
      服务端一侧停在 FIN-WAIT / TIME_WAIT（Linux 上最长约 60 秒）；不设这个选项时 ``bind``
      报 ``EADDRINUSE``，服务无法立即重启。这些平台上 ``SO_REUSEADDR`` 不允许与正在监听的
      套接字共用同一地址，真实占用仍然报错。uvicorn 自己建监听套接字时也设这个选项。
    - Windows：不设 ``SO_REUSEADDR``，它在 Windows 上允许抢占别人正在使用的端口。
      也不设 ``SO_EXCLUSIVEADDRUSE``：按微软文档，设了之后它接受过的连接在完全结束前会挡住
      下一次独占绑定，崩溃后同样无法立即重启。默认绑定不受残留连接影响，同一地址上已有
      监听者时仍报 ``WSAEADDRINUSE``。同一用户下「别人监听 ``0.0.0.0``、我们绑 ``127.0.0.1``」
      是 Windows 允许的（独占探测也发现不了，CI 实测），与修复前相同，由客户端的 ``/health``
      身份检查区分是不是随译。
    """

    try:
        infos = socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)
    except socket.gaierror as exc:
        raise ServeError(f"无法解析监听地址 {host}：{exc}") from exc
    if not infos:
        raise ServeError(f"无法解析监听地址 {host}")
    family, socktype, proto, _canon, sockaddr = infos[0]
    sock = socket.socket(family, socktype, proto)
    try:
        if os.name != "nt":
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        if family == socket.AF_INET6:
            try:
                sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
            except OSError:
                pass
        sock.bind(sockaddr)
    except OSError as exc:
        sock.close()
        raise ServeError(
            f"端口 {port} 已被占用或无法在 {host} 上监听：{exc}",
            code=1,
        ) from exc
    return sock


def _require_port(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1 or value > 65535:
        raise ServeError(f"{label} 必须在 1 到 65535 之间，收到 {value!r}")
    return value


def _require_limit(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        raise ServeError(f"{label} 必须是 >= 1 的整数，收到 {value!r}")
    return value
