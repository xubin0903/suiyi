"""不翻译片段评测（#101）：对真实 ``serve`` 跑 ``engine/eval/verbatim/verbatim_v1.jsonl``。

指标：

- 逐字保留率：每条样例的 ``keep`` 片段在译文里出现的次数不少于原文里的次数才算保留，按类别统计；
- 原样返回：``verbatim: true`` 的样例译文必须与原文逐字相同，且 ``route`` 为空（没经过模型）；
- 标点密集自然句（``category: punct``）：语料级 chrF（sacrebleu 默认参数），按方向统计；
  这些句子不应被当成代码原样返回；
- 延迟：客户端测得的往返毫秒，P50 / P95。

用法::

    python -m suiyi_engine serve --models-dir models --preload zh-en,en-zh &
    python scripts/eval_verbatim.py --url http://127.0.0.1:18796 --out verbatim.json --label "本分支"
    # 两份结果对比（前后对比表、逐条差异）
    python scripts/eval_verbatim.py compare main.json head.json --out compare.md

需要 ``sacrebleu``（``pip install -e "engine[eval]"``）。
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
import urllib.request
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SAMPLES = ROOT / "engine" / "eval" / "verbatim" / "verbatim_v1.jsonl"


def _post(url: str, payload: dict) -> dict:
    request = urllib.request.Request(
        f"{url.rstrip('/')}/translate",
        json.dumps(payload).encode("utf-8"),
        {"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=300) as response:
        return json.load(response)


def _percentile(values: list[float], percent: float) -> float:
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    rank = percent / 100 * (len(ordered) - 1)
    low = int(rank)
    high = min(low + 1, len(ordered) - 1)
    return ordered[low] + (ordered[high] - ordered[low]) * (rank - low)


def load_samples(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text("utf-8").splitlines() if line.strip()]


def score(samples: list[dict], outputs: list[dict]) -> dict:
    import sacrebleu

    keep = defaultdict(lambda: [0, 0])
    verbatim = [0, 0]
    bypassed_punct = 0
    hyps: dict[str, list[str]] = defaultdict(list)
    refs: dict[str, list[str]] = defaultdict(list)
    for sample, out in zip(samples, outputs, strict=True):
        text = out["out"]
        for span in sample["keep"]:
            keep[sample["category"]][1] += 1
            if text.count(span) >= sample["text"].count(span):
                keep[sample["category"]][0] += 1
        if sample.get("verbatim"):
            verbatim[1] += 1
            if text == sample["text"] and not out["route"]:
                verbatim[0] += 1
        if sample.get("ref"):
            direction = f"{sample['src']}-{sample['tgt']}"
            hyps[direction].append(text)
            refs[direction].append(sample["ref"])
            if not out["route"]:
                bypassed_punct += 1
    chrf = {
        direction: round(sacrebleu.metrics.CHRF().corpus_score(hyps[direction], [refs[direction]]).score, 1)
        for direction in sorted(hyps)
    }
    kept = sum(value[0] for value in keep.values())
    total = sum(value[1] for value in keep.values())
    latencies = [out["client_ms"] for out in outputs]
    return {
        "keep": {category: {"kept": a, "total": b} for category, (a, b) in sorted(keep.items())},
        "keep_rate": round(100 * kept / total, 1) if total else None,
        "keep_kept": kept,
        "keep_total": total,
        "verbatim_exact": {"ok": verbatim[0], "total": verbatim[1]},
        "punct_chrf": chrf,
        "punct_bypassed": bypassed_punct,
        "latency": {
            "p50_ms": round(statistics.median(latencies), 1),
            "p95_ms": round(_percentile(latencies, 95), 1),
            "n": len(latencies),
        },
    }


def run(args: argparse.Namespace) -> int:
    samples = load_samples(args.samples)
    outputs = []
    for sample in samples:
        payload = {"text": sample["text"], "source": sample["src"], "target": sample["tgt"]}
        started = time.perf_counter()
        response = _post(args.url, payload)
        client_ms = (time.perf_counter() - started) * 1000
        outputs.append(
            {
                "id": sample["id"],
                "out": response["text"],
                "route": response.get("route", []),
                "client_ms": round(client_ms, 1),
            }
        )
    summary = score(samples, outputs)
    result = {"label": args.label, "url": args.url, "summary": summary, "samples": outputs}
    if args.out:
        args.out.write_text(json.dumps(result, ensure_ascii=False, indent=1), "utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=1))
    return 0


def compare(args: argparse.Namespace) -> int:
    samples = {sample["id"]: sample for sample in load_samples(args.samples)}
    before = json.loads(args.before.read_text("utf-8"))
    after = json.loads(args.after.read_text("utf-8"))
    sb, sa = before["summary"], after["summary"]
    lines = [f"## 不翻译片段评测：{before['label']} → {after['label']}", ""]
    lines += ["| 类别 | 前 | 后 |", "|---|---|---|"]
    for category in sorted(set(sb["keep"]) | set(sa["keep"])):
        b = sb["keep"].get(category, {"kept": 0, "total": 0})
        a = sa["keep"].get(category, {"kept": 0, "total": 0})
        lines.append(f"| {category} | {b['kept']}/{b['total']} | {a['kept']}/{a['total']} |")
    lines.append(
        f"| **合计** | {sb['keep_kept']}/{sb['keep_total']}（{sb['keep_rate']}%） "
        f"| {sa['keep_kept']}/{sa['keep_total']}（{sa['keep_rate']}%） |"
    )
    ve_b, ve_a = sb["verbatim_exact"], sa["verbatim_exact"]
    lines.append(f"| 原样返回 | {ve_b['ok']}/{ve_b['total']} | {ve_a['ok']}/{ve_a['total']} |")
    for direction in sorted(sa["punct_chrf"]):
        lines.append(
            f"| 标点句 chrF {direction} | {sb['punct_chrf'].get(direction)} | {sa['punct_chrf'][direction]} |"
        )
    lines.append(
        f"| 延迟 P50 / P95（ms） | {sb['latency']['p50_ms']} / {sb['latency']['p95_ms']} "
        f"| {sa['latency']['p50_ms']} / {sa['latency']['p95_ms']} |"
    )
    lines += ["", "### 译文有变化的样例", "", "| id | 原文 | 前 | 后 |", "|---|---|---|---|"]
    esc = lambda value: value.replace("|", "\\|").replace("\n", "↵")  # noqa: E731
    after_by_id = {item["id"]: item for item in after["samples"]}
    for item in before["samples"]:
        new = after_by_id.get(item["id"])
        if new is None or new["out"] == item["out"]:
            continue
        text = samples[item["id"]]["text"] if item["id"] in samples else ""
        lines.append(f"| {item['id']} | {esc(text)} | {esc(item['out'])} | {esc(new['out'])} |")
    report = "\n".join(lines) + "\n"
    if args.out:
        args.out.write_text(report, "utf-8")
    print(report)
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command")
    parser.add_argument("--url", default="http://127.0.0.1:18796")
    parser.add_argument("--samples", type=Path, default=DEFAULT_SAMPLES)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--label", default="")
    cmp = sub.add_parser("compare", help="对比两份结果")
    cmp.add_argument("before", type=Path)
    cmp.add_argument("after", type=Path)
    cmp.add_argument("--samples", type=Path, default=DEFAULT_SAMPLES)
    cmp.add_argument("--out", type=Path)
    args = parser.parse_args(argv)
    if args.command == "compare":
        return compare(args)
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
