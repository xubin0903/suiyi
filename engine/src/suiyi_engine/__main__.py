"""命令行：``--version``、``translate`` 与 ``serve``。"""

from __future__ import annotations

import argparse
import sys

from suiyi_engine import __version__


def main(argv: list[str] | None = None) -> int:
    """解析参数并执行子命令。``--version`` 只打印版本。"""

    _configure_stdio()
    parser = argparse.ArgumentParser(prog="suiyi_engine")
    parser.add_argument("--version", action="store_true", help="打印版本后退出")
    subparsers = parser.add_subparsers(dest="command")

    translate = subparsers.add_parser("translate", help="翻译一段文本")
    translate.add_argument("--src", required=True, help="源语种，ISO 639-1，例如 zh")
    translate.add_argument("--tgt", required=True, help="目标语种，ISO 639-1，例如 en")
    translate.add_argument(
        "--models-dir",
        default=None,
        help="模型目录，默认 SUIYI_MODELS_DIR 或仓库 models/",
    )
    translate.add_argument("text", help="待翻译文本")

    serve = subparsers.add_parser("serve", help="启动只监听本机回环的 HTTP 翻译服务")
    serve.add_argument(
        "--host",
        default="127.0.0.1",
        help="只接受 127.0.0.1、::1、localhost，默认 127.0.0.1",
    )
    serve.add_argument(
        "--port",
        type=int,
        default=None,
        help="端口，默认环境变量 SUIYI_PORT 或 18780",
    )
    serve.add_argument(
        "--models-dir",
        default=None,
        help="模型目录，默认 SUIYI_MODELS_DIR 或仓库 models/",
    )
    serve.add_argument(
        "--preload",
        default="",
        help="启动前预热的语向，逗号分隔，例如 zh-en,en-zh",
    )
    serve.add_argument(
        "--max-text-chars",
        type=int,
        default=None,
        help="单条文本字符上限，默认环境变量 SUIYI_MAX_TEXT_CHARS 或 10000",
    )
    serve.add_argument(
        "--max-image-bytes",
        type=int,
        default=None,
        help="OCR 请求体字节上限，默认环境变量 SUIYI_MAX_IMAGE_BYTES 或 8388608（8 MiB）",
    )
    serve.add_argument(
        "--preload-ocr",
        action="store_true",
        help="开始监听前加载并预热 OCR 模型；OCR 依赖或模型缺失时非零退出（同 --preload）",
    )
    serve.add_argument("--dev", action="store_true", help="开启 /docs 与 /openapi.json")
    serve.add_argument(
        "--intra-threads",
        type=int,
        default=None,
        help="单模型 intra 线程数，默认 min(2, CPU 数)",
    )
    serve.add_argument(
        "--beam-size",
        type=int,
        default=None,
        help="束搜索宽度，默认 2",
    )
    serve.add_argument(
        "--max-batch-size",
        type=int,
        default=None,
        help="单次解码的句批上限，默认 32",
    )

    args = parser.parse_args(argv)
    if args.version:
        print(__version__)
        return 0
    if args.command == "translate":
        return _cmd_translate(args)
    if args.command == "serve":
        from suiyi_engine.serve import serve_from_args

        return serve_from_args(args)
    parser.print_help()
    return 0


def _cmd_translate(args: argparse.Namespace) -> int:
    from suiyi_engine.translator import Translator, UnsupportedPairError

    try:
        result = Translator(args.models_dir).translate(args.text, args.src, args.tgt)
    except UnsupportedPairError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    except (OSError, ValueError) as exc:
        print(str(exc), file=sys.stderr)
        return 2
    route = ", ".join(result.route) if result.route else "-"
    print(result.text)
    print(f"route: {route}")
    print(f"elapsed_ms: {result.elapsed_ms:.1f}")
    return 0


def _configure_stdio() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except (OSError, ValueError):
            continue


if __name__ == "__main__":
    sys.exit(main())
