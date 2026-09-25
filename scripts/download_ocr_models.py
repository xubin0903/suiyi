"""下载并校验 OCR 模型；也可检查本地模型或离线冒烟。

薄封装：逻辑在 ``suiyi_engine.tools.ocr_models``。下载只需标准库；``smoke`` 需要
``pip install -e "engine[ocr]"``。用法见 ``docs/engine/OCR选型与许可证.md``。
"""

from suiyi_engine.tools.ocr_models import main

if __name__ == "__main__":
    raise SystemExit(main())
