"""扫描中文译文里的乱码与繁体（#106），并报 chrF 与逐条延迟。

对 ``engine/eval/zh_script`` 下的短句集（en→zh 与 ja→zh，经英文中转）和专业领域评测集
（``engine/eval/domain/domain_v1.jsonl`` 里目标为中文的条目）逐条调用 ``Translator.translate``，
统计含乱码字 / 繁体字的译文条数（判定见 ``suiyi_engine.zh_script``），领域集再算 chrF。
需要 ``sacrebleu``（``pip install -e "engine[eval]"``）。

用法::

    python scripts/scan_zh_script.py --models-dir models --out scan.json
    # 对比另一份源码（例如 main 的 worktree）：... --src <worktree>/engine/src
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SETS = {
    "short_en": (ROOT / "engine" / "eval" / "zh_script" / "short_en_v1.txt", "en"),
    "short_ja": (ROOT / "engine" / "eval" / "zh_script" / "short_ja_v1.txt", "ja"),
}
DOMAIN = ROOT / "engine" / "eval" / "domain" / "domain_v1.jsonl"


def _percentile(values: list[float], q: float) -> float:
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, max(0, round(q * (len(ordered) - 1))))]


def _load() -> dict[str, list[tuple[str, str, str | None]]]:
    data: dict[str, list[tuple[str, str, str | None]]] = {}
    for name, (path, src) in SETS.items():
        lines = [line.strip() for line in path.read_text(encoding="utf-8").splitlines()]
        data[name] = [(src, line, None) for line in lines if line]
    rows = [json.loads(line) for line in DOMAIN.read_text(encoding="utf-8").splitlines() if line]
    for src in ("en", "ja"):
        data[f"domain_{src}_zh"] = [
            (src, row["source"], row["reference"])
            for row in rows
            if row["src_lang"] == src and row["tgt_lang"] == "zh"
        ]
    return data


def translate_all(models_dir: Path) -> dict[str, dict[str, list]]:
    """逐条翻译所有集合，返回每个集合的译文与逐条耗时（毫秒）。只依赖 ``Translator``。"""

    from suiyi_engine.translator import Translator

    translator = Translator(models_dir, glossary=None)
    translator.translate("Warm up.", "en", "zh")
    translator.translate("ウォームアップ。", "ja", "zh")
    out: dict[str, dict[str, list]] = {}
    for name, items in _load().items():
        outputs: list[str] = []
        latencies: list[float] = []
        for src, text, _ in items:
            started = time.perf_counter()
            outputs.append(translator.translate(text, src, "zh").text)
            latencies.append(1000 * (time.perf_counter() - started))
        out[name] = {"outputs": outputs, "latencies": latencies}
        print(f"{name}: 已翻译 {len(items)} 条", file=sys.stderr, flush=True)
    stats = getattr(translator, "zh_guard_stats", None)
    if stats is not None:
        out["_zh_guard"] = {"stats": [vars(stats)]}
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--models-dir", type=Path, required=True)
    parser.add_argument("--out", type=Path, help="写出 JSON（含每条译文）")
    parser.add_argument("--label", default="")
    parser.add_argument(
        "--src",
        type=Path,
        help="用另一份 engine/src 翻译（例如 main 的 worktree）；判定与 chrF 仍用本仓库的代码",
    )
    parser.add_argument("--translate-only", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    if args.translate_only:
        result = translate_all(args.models_dir)
        args.translate_only.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
        return 0

    sys.path.insert(0, str(ROOT / "engine" / "src"))
    from sacrebleu.metrics import CHRF
    from suiyi_engine.zh_script import find_issues

    if args.src:
        import os
        import subprocess
        import tempfile

        dump = Path(tempfile.mkstemp(suffix=".json")[1])
        env = dict(os.environ, PYTHONPATH=str(args.src.resolve()))
        command = [sys.executable, __file__, "--models-dir", str(args.models_dir)]
        subprocess.run([*command, "--translate-only", str(dump)], env=env, check=True)
        translated = json.loads(dump.read_text(encoding="utf-8"))
        dump.unlink()
    else:
        translated = translate_all(args.models_dir)

    report: dict[str, object] = {"label": args.label, "src": str(args.src or ""), "sets": {}}
    for name, items in _load().items():
        outputs = translated[name]["outputs"]
        latencies = translated[name]["latencies"]
        mojibake: list[dict[str, str]] = []
        traditional: list[dict[str, str]] = []
        for (_, text, _), output in zip(items, outputs, strict=True):
            issues = find_issues(output, text)
            example = {"source": text, "output": output}
            if issues.mojibake:
                mojibake.append({**example, "chars": "".join(issues.mojibake)})
            if issues.traditional:
                traditional.append({**example, "chars": "".join(issues.traditional)})
        entry: dict[str, object] = {
            "n": len(items),
            "mojibake": len(mojibake),
            "traditional": len(traditional),
            "p50_ms": round(statistics.median(latencies), 1),
            "p95_ms": round(_percentile(latencies, 0.95), 1),
            "mojibake_examples": mojibake,
            "traditional_examples": traditional,
            "outputs": outputs,
        }
        refs = [ref for _, _, ref in items]
        if all(ref is not None for ref in refs):
            entry["chrf"] = round(CHRF().corpus_score(outputs, [refs]).score, 2)  # type: ignore[list-item]
        report["sets"][name] = entry  # type: ignore[index]
        print(
            f"{name}: n={entry['n']} 乱码 {entry['mojibake']} 繁体 {entry['traditional']} "
            f"chrF {entry.get('chrf', '-')} P50 {entry['p50_ms']} P95 {entry['p95_ms']} ms",
            flush=True,
        )
        for example in (mojibake + traditional)[:8]:
            print(f"    {example['source'][:60]} => {example['output'][:60]}", flush=True)
    if "_zh_guard" in translated:
        report["zh_guard"] = translated["_zh_guard"]["stats"][0]
        print("中文译文检查:", report["zh_guard"])
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
