"""命令行：``--version``，以及 ``translate``。"""

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

    args = parser.parse_args(argv)
    if args.version:
        print(__version__)
        return 0
    if args.command == "translate":
        return _cmd_translate(args)
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
