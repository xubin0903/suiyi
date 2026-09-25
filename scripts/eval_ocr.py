"""OCR 评测与性能基线（#54）：CER、段落切分、P50/P95 耗时、内存，对比检测模型。

用法见 ``docs/engine/OCR评测.md``。Windows 上也是一条命令：``python scripts/eval_ocr.py``。
"""

import sys
from pathlib import Path

try:
    from suiyi_engine.eval.ocr_bench import main
except ImportError:  # 未安装引擎包时从源码树导入
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine" / "src"))
    from suiyi_engine.eval.ocr_bench import main

if __name__ == "__main__":
    raise SystemExit(main())
