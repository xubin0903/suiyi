"""给 scripts/eval_domain.py 的结果 JSON 补 COMET 分（#78）。

COMET 需要 torch，不进运行时，也不进 CI。用装了 ``unbabel-comet`` 的单独 venv 运行：

    pip install unbabel-comet            # 在 convert 用的 venv 里装，不要装进运行环境
    python scripts/score_comet.py reports/domain/*.json

默认模型 Unbabel/wmt22-comet-da（Apache-2.0，约 2.3 GB）。分数写回原 JSON：
每条样例加 ``comet``，``summary.directions.<方向>.comet`` 为该方向的系统分（样例均值 × 100）。
之后重新跑 ``eval_domain.py report`` 即可在报告里看到 COMET。
"""

from __future__ import annotations

import argparse
import json
import os
from collections import defaultdict
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("results", nargs="+", type=Path, help="eval_domain.py run 写出的 JSON")
    parser.add_argument("--model", default="Unbabel/wmt22-comet-da")
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--force", action="store_true", help="已有 COMET 分时也重算")
    args = parser.parse_args()

    import torch
    from comet import download_model, load_from_checkpoint

    torch.set_num_threads(args.threads)
    os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")
    model = load_from_checkpoint(download_model(args.model))

    for path in args.results:
        result = json.loads(path.read_text(encoding="utf-8"))
        samples = result["samples"]
        if not args.force and all("comet" in sample for sample in samples):
            print(f"跳过 {path}（已有 COMET）")
            continue
        data = [
            {"src": s["source"], "mt": s["hypothesis"], "ref": s["reference"]} for s in samples
        ]
        output = model.predict(data, batch_size=args.batch_size, gpus=0, progress_bar=False)
        by_dir: dict[str, list[float]] = defaultdict(list)
        for sample, score in zip(samples, output.scores, strict=True):
            sample["comet"] = round(float(score), 4)
            by_dir[sample["direction"]].append(float(score))
        for direction, scores in by_dir.items():
            entry = result["summary"]["directions"][direction]
            entry["comet"] = round(100.0 * sum(scores) / len(scores), 2)
        result.setdefault("settings", {})["comet_model"] = args.model
        path.write_text(json.dumps(result, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        summary = {d: result["summary"]["directions"][d]["comet"] for d in by_dir}
        print(f"{path.name}: {summary}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
