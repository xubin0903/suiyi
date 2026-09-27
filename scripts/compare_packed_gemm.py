"""在同一台机器上对比几种环境变量配置下真实 serve 的提交内存、延迟和译文（#113）。

每种配置单独起一个 ``serve``（预热 zh↔en），把评测集翻 ``--rounds`` 轮，记录：

- 内存：Windows 为 WS 与 Private Bytes（提交量），Linux 为 RSS 与 VmData（≈ 提交量）；
- 全部请求往返延迟的 P50 / P95；
- 与第一种配置逐字比较，不同的条数（整数 GEMM，打包与否应当逐字相同）；
- ``/health`` 里 ``ct2_packed_gemm`` / ``ct2_packed_gemm_source`` / ``commit_available_mib``。

``--alternate N`` 把全部配置轮流跑 N 遍，延迟取每种配置各遍 P95 的中位数，抵消机器负载波动。

用法（负责人 Intel 实机复测，PowerShell）::

    $env:PYTHONPATH = "$PWD\\engine\\src"
    python scripts\\compare_packed_gemm.py --models-dir models --alternate 2 --out packed.json `
        --config "打包=" --config "不打包=CT2_PACKED_GEMM=0"
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import statistics
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def _load(name: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)  # type: ignore[arg-type]
    assert spec is not None and spec.loader is not None
    spec.loader.exec_module(module)
    return module


MEM = _load("measure_serve_memory")
DET = _load("check_determinism")


def _parse_config(text: str) -> tuple[str, dict[str, str]]:
    name, _, rest = text.partition("=")
    env: dict[str, str] = {}
    for item in filter(None, (part.strip() for part in rest.split(","))):
        key, _, value = item.partition("=")
        env[key.strip()] = value.strip()
    return name.strip(), env


def _run(args: argparse.Namespace, name: str, extra: dict[str, str], port: int) -> dict:
    env = dict(os.environ, PYTHONUTF8="1", PYTHONUNBUFFERED="1")
    env["SUIYI_USER_GLOSSARY"] = str(
        Path(tempfile.gettempdir()) / "suiyi-113-no-glossary.tsv"
    )
    for key in ("CT2_PACKED_GEMM", "CT2_USE_MKL", "SUIYI_PACKED_GEMM_MIN_COMMIT_MIB"):
        env.pop(key, None)  # 只用 --config 给的值
    env.update(extra)
    command = [sys.executable, "-m", "suiyi_engine", "serve", "--port", str(port)]
    command += ["--models-dir", str(args.models_dir), "--preload", "zh-en,en-zh"]
    log_path = Path(tempfile.gettempdir()) / f"suiyi-113-{port}.log"
    log = open(log_path, "w", encoding="utf-8")  # noqa: SIM115 - serve 子进程整个生命周期都要写
    proc = subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT)
    url = f"http://127.0.0.1:{port}"
    try:
        for _ in range(int(args.start_timeout * 5)):
            if proc.poll() is not None:
                raise SystemExit(
                    f"{name}：serve 退出，返回码 {proc.returncode}，日志 {log.name}"
                )
            try:
                with urllib.request.urlopen(f"{url}/health", timeout=1) as resp:
                    health = json.load(resp)
                break
            except OSError:
                time.sleep(0.2)
        else:
            raise SystemExit(f"{name}：serve 没有在限定时间内就绪")
        idle = MEM.memory(MEM.target_pid(proc.pid))
        outputs: list[str] = []
        latencies: list[float] = []
        for round_index in range(args.rounds):
            for src, tgt, text in args.rows:
                started = time.perf_counter()
                out = DET._post(url, src, tgt, text)
                latencies.append((time.perf_counter() - started) * 1000.0)
                if round_index == 0:
                    outputs.append(out)
        time.sleep(2)
        used = MEM.memory(MEM.target_pid(proc.pid))
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            proc.kill()
        log.close()
    keys = ("ws_mib", "private_mib") if MEM.WINDOWS else ("rss_mib", "vmdata_mib")
    return {
        "name": name,
        "env": extra,
        "health": {
            k: health.get(k)
            for k in (
                "ct2_packed_gemm",
                "ct2_packed_gemm_source",
                "commit_available_mib",
            )
        },
        "idle": {k: round(idle.get(k, 0.0), 1) for k in keys},
        "used": {k: round(used.get(k, 0.0), 1) for k in keys},
        "p50_ms": round(statistics.median(latencies), 1),
        "p95_ms": DET._pct(latencies, 0.95),
        "outputs": outputs,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--models-dir", type=Path, required=True)
    parser.add_argument("--config", action="append", required=True,
                        help="名称=ENV=VAL,ENV=VAL；第一种是比较基准")  # fmt: skip
    parser.add_argument("--samples", type=Path, default=MEM.DOMAIN)
    parser.add_argument("--directions", default="en-zh,zh-en")
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--alternate", type=int, default=1)
    parser.add_argument("--port", type=int, default=18830)
    parser.add_argument("--start-timeout", type=float, default=180.0)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--summary", type=Path, help="把 Markdown 表追加到这个文件")
    args = parser.parse_args(argv)
    directions = {d.strip() for d in args.directions.split(",") if d.strip()} or None
    args.rows = DET._load(args.samples, directions)
    configs = [_parse_config(text) for text in args.config]

    runs: dict[str, list[dict]] = {name: [] for name, _ in configs}
    port = args.port
    for _ in range(args.alternate):
        for name, extra in configs:
            run = _run(args, name, extra, port)
            port += 1
            runs[name].append(run)
            print(f"[{name}] 内存 {run['used']} P50 {run['p50_ms']} P95 {run['p95_ms']} "
                  f"{run['health']}", flush=True)  # fmt: skip

    base_outputs = runs[configs[0][0]][0]["outputs"]
    rows = []
    for name, _ in configs:
        items = runs[name]
        differs = max(
            sum(1 for a, b in zip(item["outputs"], base_outputs, strict=True) if a != b)
            for item in items
        )
        rows.append(
            {
                "name": name,
                "env": items[0]["env"],
                "health": items[0]["health"],
                "idle": items[0]["idle"],
                "used": items[-1]["used"],
                "p50_ms": statistics.median(item["p50_ms"] for item in items),
                "p95_ms": statistics.median(item["p95_ms"] for item in items),
                "p95_runs": [item["p95_ms"] for item in items],
                "differs_from_first": differs,
                "samples": len(base_outputs),
            }
        )
    base_p95 = rows[0]["p95_ms"]
    mem_cols = (
        ("WS", "Private Bytes（提交）") if MEM.WINDOWS else ("RSS", "VmData（≈提交）")
    )
    lines = [
        (
            f"### 预打包对比（{sys.platform}，{len(base_outputs)} 条 × {args.rounds} 轮，"
            f"轮流 {args.alternate} 遍）"
        ),
        "",
        (
            f"| 配置 | {mem_cols[0]} | {mem_cols[1]} | P50 ms | P95 ms | P95 变化 | "
            "与首个配置不同 | ct2_packed_gemm（来源） |"
        ),
        "|---|---|---|---|---|---|---|---|",
    ]
    for row in rows:
        used = list(row["used"].values())
        change = (row["p95_ms"] / base_p95 - 1) * 100 if base_p95 else 0.0
        health = row["health"]
        lines.append(
            f"| {row['name']} | {used[0]:.1f} | {used[1]:.1f} | {row['p50_ms']:.1f} | "
            f"{row['p95_ms']:.1f} | {change:+.1f}% | {row['differs_from_first']} / "
            f"{row['samples']} | {health['ct2_packed_gemm']}（{health['ct2_packed_gemm_source']}）|"
        )
    table = "\n".join(lines) + "\n"
    print(table)
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as handle:
            handle.write(table + "\n")
    if args.out:
        args.out.write_text(
            json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8"
        )
    return 1 if any(row["differs_from_first"] for row in rows) else 0


if __name__ == "__main__":
    sys.exit(main())
