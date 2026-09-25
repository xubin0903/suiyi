"""对固定样例集跑分并输出报告。

指标与编排在 ``suiyi_engine.eval``。用法见 ``docs/engine/评测.md``。
"""

from suiyi_engine.eval.runner import main

if __name__ == "__main__":
    raise SystemExit(main())
