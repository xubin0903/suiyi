"""经本机 HTTP 服务测量冷启动、延迟与内存。

薄封装：测量逻辑在 ``suiyi_engine.bench``。
需先 ``pip install -e "engine[bench]"``。用法见 ``docs/engine/性能基线.md``。
"""

from suiyi_engine.bench import main

if __name__ == "__main__":
    raise SystemExit(main())
