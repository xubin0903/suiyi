#!/usr/bin/env python3
"""校验中文质量固定样例集（JSONL）。仅使用 Python 标准库。

用法:
    python scripts/validate_samples.py tests/samples/zh_core_v1.jsonl

成功时把「方向 × 分类」计数表打到标准输出，退出码为 0。
格式、覆盖或关联不满足时，错误打到标准错误，退出码为 1。
参数错误或文件打不开时，退出码为 2。
"""

from __future__ import annotations

import json
import re
import sys
from collections import defaultdict
from pathlib import Path

REQUIRED_FIELDS = (
    "id",
    "src_lang",
    "tgt_lang",
    "category",
    "source",
    "reference",
    "must_keep",
    "reference_status",
    "origin",
)

CATEGORIES = ("short", "colloquial", "proper_noun", "ocr_noise", "paragraph")
LANGS = ("zh", "en", "ja")
REFERENCE_STATUSES = ("draft", "reviewed")

# 方向顺序即计数表行顺序。值为该方向条数下限。
MIN_BY_DIRECTION = {
    ("zh", "en"): 30,
    ("en", "zh"): 30,
    ("zh", "ja"): 25,
    ("ja", "zh"): 25,
    ("en", "ja"): 10,
    ("ja", "en"): 10,
}

# 含中文的方向：四类难点各至少 5 条，另有至少 3 条段落。
ZH_DIRECTIONS = {("zh", "en"), ("en", "zh"), ("zh", "ja"), ("ja", "zh")}
MIN_CATEGORY_FOR_ZH = {
    "short": 5,
    "colloquial": 5,
    "proper_noun": 5,
    "ocr_noise": 5,
    "paragraph": 3,
}

MIN_TOTAL = 130
SHORT_MAX_CHARS = 30
PARAGRAPH_SENTENCE_RANGE = (2, 5)

SENTENCE_END = re.compile(r"[。！？!?]|[.!?](?=\s|$)")


def direction_label(src_lang: str, tgt_lang: str) -> str:
    return f"{src_lang}→{tgt_lang}"


def sentence_count(text: str) -> int:
    return len(SENTENCE_END.findall(text))


def check_must_keep(item: object, lineno: int, errors: list[str]) -> list[dict[str, str]] | None:
    if not isinstance(item, list):
        errors.append(f"L{lineno}: must_keep 必须是数组")
        return None
    parsed: list[dict[str, str]] = []
    for index, entry in enumerate(item, 1):
        if not isinstance(entry, dict):
            errors.append(
                f"L{lineno}: must_keep[{index}] 必须是对象 "
                '{"src": "...", "tgt": "..."}'
            )
            continue
        extra = set(entry) - {"src", "tgt"}
        if extra:
            errors.append(
                f"L{lineno}: must_keep[{index}] 含未定义字段 {sorted(extra)}"
            )
        src = entry.get("src")
        tgt = entry.get("tgt")
        if not isinstance(src, str) or src.strip() == "" or src != src.strip():
            errors.append(f"L{lineno}: must_keep[{index}].src 必须是非空字符串")
            continue
        if not isinstance(tgt, str) or tgt.strip() == "" or tgt != tgt.strip():
            errors.append(f"L{lineno}: must_keep[{index}].tgt 必须是非空字符串")
            continue
        parsed.append({"src": src, "tgt": tgt})
    return parsed


def validate_record(
    obj: object, lineno: int, seen_ids: dict[str, int], errors: list[str]
) -> dict | None:
    if not isinstance(obj, dict):
        errors.append(f"L{lineno}: 每行必须是 JSON 对象")
        return None

    for field in REQUIRED_FIELDS:
        if field not in obj:
            errors.append(f"L{lineno}: 缺少必填字段 {field}")

    record_id = obj.get("id")
    if "id" in obj:
        if not isinstance(record_id, str) or record_id.strip() == "":
            errors.append(f"L{lineno}: id 必须是非空字符串")
            record_id = None
        elif record_id in seen_ids:
            errors.append(
                f"L{lineno}: id {record_id!r} 重复（已在 L{seen_ids[record_id]} 出现）"
            )
        else:
            seen_ids[record_id] = lineno

    src_lang = obj.get("src_lang")
    tgt_lang = obj.get("tgt_lang")
    if "src_lang" in obj and src_lang not in LANGS:
        errors.append(f"L{lineno}: src_lang 必须是 zh/en/ja 之一")
    if "tgt_lang" in obj and tgt_lang not in LANGS:
        errors.append(f"L{lineno}: tgt_lang 必须是 zh/en/ja 之一")
    if src_lang in LANGS and tgt_lang in LANGS:
        if src_lang == tgt_lang:
            errors.append(f"L{lineno}: src_lang 与 tgt_lang 不能相同")
        elif (src_lang, tgt_lang) not in MIN_BY_DIRECTION:
            errors.append(
                f"L{lineno}: 不支持的方向 {direction_label(src_lang, tgt_lang)}"
            )

    category = obj.get("category")
    if "category" in obj and category not in CATEGORIES:
        errors.append(
            f"L{lineno}: category 必须是 {', '.join(CATEGORIES)} 之一"
        )

    if (
        isinstance(record_id, str)
        and src_lang in LANGS
        and tgt_lang in LANGS
        and category in CATEGORIES
    ):
        prefix = f"{src_lang}-{tgt_lang}-{category}-"
        if not record_id.startswith(prefix) or not record_id[len(prefix) :].isdigit():
            errors.append(
                f"L{lineno}: id {record_id!r} 应为 {prefix} 加数字，例如 {prefix}001"
            )

    source = obj.get("source")
    if "source" in obj and (not isinstance(source, str) or source.strip() == ""):
        errors.append(f"L{lineno}: source 必须是非空字符串")

    reference = obj.get("reference")
    if "reference" in obj and not isinstance(reference, str):
        errors.append(f"L{lineno}: reference 必须是字符串")
    elif isinstance(reference, str) and category in ("short", "proper_noun"):
        if reference.strip() == "":
            errors.append(f"L{lineno}: {category} 的 reference 不能为空")

    if isinstance(source, str) and category == "short" and len(source) > SHORT_MAX_CHARS:
        errors.append(
            f"L{lineno}: short 原文长度为 {len(source)}，超过 {SHORT_MAX_CHARS}"
        )

    if isinstance(source, str) and category == "paragraph":
        count = sentence_count(source)
        low, high = PARAGRAPH_SENTENCE_RANGE
        if not low <= count <= high:
            errors.append(
                f"L{lineno}: paragraph 原文句数为 {count}，应在 {low}–{high} 句"
            )

    must_keep = None
    if "must_keep" in obj:
        must_keep = check_must_keep(obj.get("must_keep"), lineno, errors)
        if category == "proper_noun" and must_keep is not None and len(must_keep) == 0:
            errors.append(f"L{lineno}: proper_noun 的 must_keep 不能为空")

    if (
        must_keep
        and isinstance(reference, str)
        and isinstance(source, str)
        and category != "ocr_noise"
    ):
        for entry in must_keep:
            if entry["src"] not in source:
                errors.append(
                    f"L{lineno}: must_keep.src {entry['src']!r} 不在 source 中"
                )
            if entry["tgt"] not in reference:
                errors.append(
                    f"L{lineno}: must_keep.tgt {entry['tgt']!r} 不在 reference 中"
                )

    status = obj.get("reference_status")
    if "reference_status" in obj and status not in REFERENCE_STATUSES:
        errors.append(
            f"L{lineno}: reference_status 必须是 draft 或 reviewed"
        )

    origin = obj.get("origin")
    if "origin" in obj and (not isinstance(origin, str) or origin.strip() == ""):
        errors.append(f"L{lineno}: origin 必须是非空字符串，用于注明来源")

    if "notes" in obj and not isinstance(obj["notes"], str):
        errors.append(f"L{lineno}: notes 必须是字符串")

    clean_id = obj.get("clean_id")
    if category == "ocr_noise":
        if not isinstance(clean_id, str) or clean_id.strip() == "":
            errors.append(f"L{lineno}: ocr_noise 必须提供非空 clean_id")
    elif "clean_id" in obj:
        errors.append(f"L{lineno}: 只有 ocr_noise 可以带 clean_id")

    if any(field not in obj for field in REQUIRED_FIELDS):
        return None
    if not isinstance(record_id, str) or record_id.strip() == "":
        return None
    if src_lang not in LANGS or tgt_lang not in LANGS:
        return None
    if category not in CATEGORIES:
        return None
    if not isinstance(source, str) or not isinstance(reference, str):
        return None
    return obj


def check_ocr_links(records: list[tuple[int, dict]], errors: list[str]) -> None:
    by_id: dict[str, tuple[int, dict]] = {}
    for lineno, obj in records:
        by_id.setdefault(obj["id"], (lineno, obj))

    for lineno, obj in records:
        if obj.get("category") != "ocr_noise":
            continue
        clean_id = obj.get("clean_id")
        if not isinstance(clean_id, str) or clean_id.strip() == "":
            continue
        if clean_id == obj["id"]:
            errors.append(f"L{lineno}: clean_id 不能指向自身")
            continue
        linked = by_id.get(clean_id)
        if linked is None:
            errors.append(f"L{lineno}: clean_id {clean_id!r} 不存在")
            continue
        clean_line, clean = linked
        if (clean["src_lang"], clean["tgt_lang"]) != (obj["src_lang"], obj["tgt_lang"]):
            errors.append(
                f"L{lineno}: clean_id {clean_id!r} 的方向与本条不一致"
            )
        if clean["category"] == "ocr_noise":
            errors.append(
                f"L{lineno}: clean_id {clean_id!r}（L{clean_line}）不能仍是 ocr_noise"
            )
        if obj.get("source") == clean.get("source"):
            errors.append(f"L{lineno}: OCR 原文与干净版本完全相同")
        if obj.get("reference") != clean.get("reference"):
            errors.append(
                f"L{lineno}: reference 必须与干净版本 L{clean_line} 相同"
            )
        must_keep = obj.get("must_keep")
        if isinstance(must_keep, list) and isinstance(clean.get("source"), str):
            reference = obj.get("reference")
            for entry in must_keep:
                if not isinstance(entry, dict):
                    continue
                src = entry.get("src")
                tgt = entry.get("tgt")
                if isinstance(src, str) and src not in clean["source"]:
                    errors.append(
                        f"L{lineno}: must_keep.src {src!r} 不在干净原文中"
                    )
                if (
                    isinstance(tgt, str)
                    and isinstance(reference, str)
                    and tgt not in reference
                ):
                    errors.append(
                        f"L{lineno}: must_keep.tgt {tgt!r} 不在 reference 中"
                    )


def count_records(
    records: list[tuple[int, dict]],
) -> dict[tuple[str, str], dict[str, int]]:
    counts: dict[tuple[str, str], dict[str, int]] = {
        direction: {category: 0 for category in CATEGORIES}
        for direction in MIN_BY_DIRECTION
    }
    for _lineno, obj in records:
        direction = (obj["src_lang"], obj["tgt_lang"])
        if direction in counts and obj["category"] in counts[direction]:
            counts[direction][obj["category"]] += 1
    return counts


def check_minimums(
    counts: dict[tuple[str, str], dict[str, int]], total: int, errors: list[str]
) -> None:
    if total < MIN_TOTAL:
        errors.append(f"总条数 {total}，低于下限 {MIN_TOTAL}")
    for direction, minimum in MIN_BY_DIRECTION.items():
        actual = sum(counts[direction].values())
        label = direction_label(*direction)
        if actual < minimum:
            errors.append(f"方向 {label} 共 {actual} 条，低于下限 {minimum}")
        if direction in ZH_DIRECTIONS:
            for category, category_min in MIN_CATEGORY_FOR_ZH.items():
                got = counts[direction][category]
                if got < category_min:
                    errors.append(
                        f"方向 {label} 类别 {category} 共 {got} 条，低于下限 {category_min}"
                    )


def format_table(counts: dict[tuple[str, str], dict[str, int]]) -> str:
    header = ("direction",) + CATEGORIES + ("total",)
    rows: list[tuple[str, ...]] = []
    grand = {category: 0 for category in CATEGORIES}
    grand_total = 0
    for direction in MIN_BY_DIRECTION:
        values = [counts[direction][category] for category in CATEGORIES]
        total = sum(values)
        grand_total += total
        for category, value in zip(CATEGORIES, values):
            grand[category] += value
        rows.append(
            (direction_label(*direction),)
            + tuple(str(value) for value in values)
            + (str(total),)
        )
    rows.append(
        ("TOTAL",)
        + tuple(str(grand[category]) for category in CATEGORIES)
        + (str(grand_total),)
    )

    widths = [len(column) for column in header]
    for row in rows:
        for index, cell in enumerate(row):
            widths[index] = max(widths[index], len(cell))

    def render(row: tuple[str, ...]) -> str:
        return "  ".join(cell.ljust(widths[index]) for index, cell in enumerate(row))

    lines = [render(header), render(tuple("-" * width for width in widths))]
    lines.extend(render(row) for row in rows)
    return "\n".join(lines)


def load_records(path: Path) -> tuple[list[tuple[int, dict]], list[str]]:
    errors: list[str] = []
    try:
        raw = path.read_bytes()
    except OSError as exc:
        return [], [f"无法读取文件: {exc}"]

    if raw.startswith(b"\xef\xbb\xbf"):
        errors.append("文件含 UTF-8 BOM，请保存为无 BOM 的 UTF-8")
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError as exc:
        return [], [f"文件不是 UTF-8: {exc}"]

    if text == "":
        return [], ["文件为空"]

    records: list[tuple[int, dict]] = []
    seen_ids: dict[str, int] = {}
    for lineno, line in enumerate(text.splitlines(), 1):
        if line.strip() == "":
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError as exc:
            errors.append(f"L{lineno}: JSON 无法解析: {exc.msg}")
            continue
        record = validate_record(obj, lineno, seen_ids, errors)
        if record is not None:
            records.append((lineno, record))
    return records, errors


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(
            "用法: python scripts/validate_samples.py tests/samples/zh_core_v1.jsonl",
            file=sys.stderr,
        )
        return 2

    path = Path(argv[1])
    if not path.is_file():
        print(f"文件不存在: {path}", file=sys.stderr)
        return 2

    records, errors = load_records(path)
    check_ocr_links(records, errors)
    counts = count_records(records)
    check_minimums(counts, len(records), errors)

    print(format_table(counts))
    if errors:
        print(f"FAILED: {len(errors)} 个问题", file=sys.stderr)
        for message in errors:
            print(message, file=sys.stderr)
        return 1

    print(f"OK: {len(records)} samples")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
