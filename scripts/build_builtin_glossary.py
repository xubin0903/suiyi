"""从评测术语表生成随引擎发布的内置术语表（只保留 en / zh）。

用法：python scripts/build_builtin_glossary.py
输出：engine/src/suiyi_engine/data/glossary_zh_en.json
"""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "engine" / "eval" / "domain" / "glossary.json"
TARGET = ROOT / "engine" / "src" / "suiyi_engine" / "data" / "glossary_zh_en.json"


def build() -> dict[str, object]:
    data = json.loads(SOURCE.read_text(encoding="utf-8"))
    terms = [
        {key: term[key] for key in ("id", "kind", "domain", "en", "zh")}
        for term in data["terms"]
        if term.get("en") and term.get("zh")
    ]
    return {
        "schema_version": 1,
        "license": "MIT",
        "source": (
            "engine/eval/domain/glossary.json（#79）的 en / zh 部分，"
            "由 scripts/build_builtin_glossary.py 生成，不要手改"
        ),
        "terms": terms,
    }


def main() -> int:
    text = json.dumps(build(), ensure_ascii=False, indent=1) + "\n"
    TARGET.write_text(text, encoding="utf-8")
    print(f"{TARGET}：{len(json.loads(text)['terms'])} 条")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
