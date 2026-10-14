"""PyInstaller 入口：等价于 ``python -m suiyi_engine``（#130）。"""

from __future__ import annotations

from suiyi_engine.__main__ import main

if __name__ == "__main__":
    raise SystemExit(main())
