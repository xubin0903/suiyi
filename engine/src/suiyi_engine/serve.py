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

    try:
        _ensure_port_free(host, port)
    except ServeError as exc:
        print(str(exc), file=sys.stderr)
        return exc.code

    detector_ms = warmup_detector()

    app = create_app(
        translator,
        None,
        ApiSettings(max_text_chars=max_text_chars, dev=dev),
    )
    _print_startup(host, port, translator, decode, detector_ms)
    try:
        _serve_uvicorn(app, host, port)
    except OSError as exc:
        print(f"无法在 {host}:{port} 启动服务：{exc}", file=sys.stderr)
        return 1
    return 0


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


def _serve_uvicorn(app: object, host: str, port: int) -> None:
    import uvicorn

    uvicorn.run(app, host=host, port=port, log_level="info", access_log=True)


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


def _ensure_port_free(host: str, port: int) -> None:
    try:
        infos = socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)
    except socket.gaierror as exc:
        raise ServeError(f"无法解析监听地址 {host}：{exc}") from exc
    if not infos:
        raise ServeError(f"无法解析监听地址 {host}")
    family, socktype, proto, _canon, sockaddr = infos[0]
    sock = socket.socket(family, socktype, proto)
    try:
        if family == socket.AF_INET6:
            try:
                sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
            except OSError:
                pass
        sock.bind(sockaddr)
    except OSError as exc:
        raise ServeError(
            f"端口 {port} 已被占用或无法在 {host} 上监听：{exc}",
            code=1,
        ) from exc
    finally:
        sock.close()


def _require_port(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1 or value > 65535:
        raise ServeError(f"{label} 必须在 1 到 65535 之间，收到 {value!r}")
    return value


def _require_limit(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        raise ServeError(f"{label} 必须是 >= 1 的整数，收到 {value!r}")
    return value
