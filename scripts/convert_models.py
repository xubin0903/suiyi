"""按模型清单把 OPUS-MT 下载并转换为 CTranslate2。

薄封装：参数与转换逻辑在 ``suiyi_engine.tools.convert``。
需先 ``pip install -e "engine[convert]"``。用法见 ``docs/engine/模型目录约定.md``。
"""

from suiyi_engine.tools.convert import main

if __name__ == "__main__":
    raise SystemExit(main())
