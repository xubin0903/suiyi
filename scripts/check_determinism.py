"""对真实 serve 反复翻同一批句子，检查译文是否确定（#103）。

带 AMX 的 CPU 上，MKL 的 AMX int8 路径在多线程或 CPU 争用时会给出不确定甚至乱码的译文。
这个脚本把样例翻 ``--rounds`` 轮，统计每条的不同译文个数；有任何一条不止一种译文就以 1 退出。
同时报告全部请求的往返延迟 P50 / P95，并可与基线结果逐字比较。

用法::

    python -m suiyi_engine serve --models-dir models --preload zh-en,en-zh --port 18840 &
    python scripts/check_determinism.py --url http://127.0.0.1:18840 --rounds 10
    # 用 #78 专业领域集（en↔zh 308 条）测延迟，并与基线逐字比较
    python scripts/check_determinism.py --url ... --samples engine/eval/domain/domain_v1.jsonl \\
        --directions en-zh,zh-en --rounds 3 --out head.json --baseline main.json
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
import urllib.request
from collections import Counter
from pathlib import Path

DEFAULT = [
    (
        "en",
        "zh",
        "Kubernetes is an open-source system for automating deployment, scaling, and "
        "management of containerized applications.",
    ),
    ("en", "zh", "The pull request was merged after the CI pipeline passed."),
    ("en", "zh", "Please restart the application to apply the new settings."),
    ("zh", "en", "今天天气很好，我们去公园散步吧。"),
    ("zh", "en", "请先卸载旧版本，然后重新安装。"),
    ("zh", "en", "模型加载完成后，翻译速度会明显提高。"),
]


def _load(path: Path | None, directions: set[str] | None) -> list[tuple[str, str, str]]:
    if path is None:
        return list(DEFAULT)
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        item = json.loads(line)
        src = item.get("src_lang") or item["src"]
        tgt = item.get("tgt_lang") or item["tgt"]
        if directions and f"{src}-{tgt}" not in directions:
            continue
        rows.append((src, tgt, item.get("source") or item["text"]))
    return rows


def _post(url: str, src: str, tgt: str, text: str) -> str:
    body = json.dumps({"text": text, "source": src, "target": tgt}).encode("utf-8")
    request = urllib.request.Request(
        f"{url}/translate", body, {"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)["text"]


def _pct(values: list[float], q: float) -> float:
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round(q * (len(ordered) - 1))))
    return round(ordered[index], 1)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--url", default="http://127.0.0.1:18780")
    parser.add_argument("--samples", type=Path, default=None)
    parser.add_argument("--directions", default="")
    parser.add_argument("--rounds", type=int, default=10)
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--baseline", type=Path, default=None, help="与这份结果逐字比较")
    args = parser.parse_args(argv)

    directions = {d.strip() for d in args.directions.split(",") if d.strip()} or None
    rows = _load(args.samples, directions)
    health = json.load(urllib.request.urlopen(f"{args.url}/health", timeout=10))
    outputs: list[Counter[str]] = [Counter() for _ in rows]
    latencies: list[float] = []
    for _ in range(args.rounds):
        for index, (src, tgt, text) in enumerate(rows):
            started = time.perf_counter()
            outputs[index][_post(args.url, src, tgt, text)] += 1
            latencies.append((time.perf_counter() - started) * 1000.0)
    unstable = [i for i, counter in enumerate(outputs) if len(counter) > 1]
    first = [counter.most_common(1)[0][0] for counter in outputs]
    result = {
        "url": args.url,
        "isa": {key: health.get(key) for key in health if key.startswith(("cpu_", "mkl_"))},
        "samples": len(rows),
        "rounds": args.rounds,
        "unstable": len(unstable),
        "max_distinct": max(len(counter) for counter in outputs),
        "p50_ms": round(statistics.median(latencies), 1),
        "p95_ms": _pct(latencies, 0.95),
        "outputs": [dict(counter) for counter in outputs],
    }
    if args.baseline is not None:
        base = json.loads(args.baseline.read_text(encoding="utf-8"))
        base_first = [max(item, key=item.get) for item in base["outputs"]]
        result["differs_from_baseline"] = sum(
            1 for a, b in zip(first, base_first, strict=True) if a != b
        )
    if args.out is not None:
        args.out.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    summary = {key: value for key, value in result.items() if key != "outputs"}
    print(json.dumps(summary, ensure_ascii=False))
    for index in unstable[:5]:
        print(json.dumps({"text": rows[index][2][:60], "outputs": result["outputs"][index]},
                         ensure_ascii=False))
    return 1 if unstable or result.get("differs_from_baseline") else 0


if __name__ == "__main__":
    sys.exit(main())
