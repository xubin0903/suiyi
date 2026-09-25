"""`python -m suiyi_engine --version` 打印版本后退出。"""

import argparse
import sys

from . import __version__


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="suiyi_engine")
    parser.add_argument("--version", action="store_true", help="打印版本后退出")
    args = parser.parse_args(argv)
    if args.version:
        print(__version__)
        return 0
    parser.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main())
