"""表外极短句兜底（#122）的真实 serve 测量：内存峰值 / 常驻、冷加载、延迟与译文。

每种配置单独起一个 ``serve``（预热 zh↔en，常驻上限默认 2，与桌面端一致），按顺序：

1. 常规句：专业集 en↔zh（``--rounds`` 轮），记 P50 / P95 与译文；
2. 极短句：短句集 en→zh 一轮，第一条（兜底模型冷加载）单独计时；
3. 切回 zh→en 一条（被挤掉的 zh→en 重新加载）单独计时；
4. 交替 ``--cycles`` 次「表外极短句 en→zh → zh→en」，记每次往返；
5. 每个阶段后记内存；全程每 0.1 秒采样一次取峰值。

内存：Windows 为 WS 与 Private Bytes（另记 psutil 的 peak_pagefile 为提交峰值），Linux 为 RSS 与
VmData。用法::

    python scripts/measure_short_fallback.py --models-dir models --out sf.json \\
        --config "关闭=CT2_PACKED_GEMM=0,SUIYI_SHORT_FALLBACK=0" \\
        --config "开启=CT2_PACKED_GEMM=0,SUIYI_SHORT_FALLBACK_EVICT_IDLE_S=0"
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
import threading
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SHORT = ROOT / "engine" / "eval" / "short" / "short_v1.jsonl"
SHORT_MISS = "Take a seat."  # 不在极短句表里
ZH_PROBE = "请把窗户关上。"


def _load(name: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)  # type: ignore[arg-type]
    assert spec is not None and spec.loader is not None
    spec.loader.exec_module(module)
    return module


MEM = _load("measure_serve_memory")
DET = _load("check_determinism")
KEYS = ("ws_mib", "private_mib") if MEM.WINDOWS else ("rss_mib", "vmdata_mib")


def _parse_config(text: str) -> tuple[str, dict[str, str]]:
    name, _, rest = text.partition("=")
    env: dict[str, str] = {}
    for item in filter(None, (part.strip() for part in rest.split(","))):
        key, _, value = item.partition("=")
        env[key.strip()] = value.strip()
    return name.strip(), env


class Sampler(threading.Thread):
    def __init__(self, pid: int) -> None:
        super().__init__(daemon=True)
        self.pid = pid
        self.peak: dict[str, float] = {}
        self._halt = threading.Event()

    def run(self) -> None:
        while not self._halt.is_set():
            self.sample()
            self._halt.wait(0.1)

    def sample(self) -> dict[str, float]:
        try:
            mem = _mem(self.pid)
        except Exception:  # noqa: BLE001 - 进程刚退出
            return {}
        for key, value in mem.items():
            self.peak[key] = max(self.peak.get(key, 0.0), value)
        return mem

    def stop(self) -> None:
        self._halt.set()
        self.join(timeout=2)


def _mem(pid: int) -> dict[str, float]:
    mem = MEM.memory(pid)
    out = {key: mem[key] for key in KEYS if key in mem}
    if MEM.WINDOWS:
        import psutil

        out["peak_private_mib"] = (
            psutil.Process(pid).memory_info().peak_pagefile / MEM.MIB
        )
    elif "vmhwm_mib" in mem:
        out["vmhwm_mib"] = mem["vmhwm_mib"]  # 内核记的 RSS 峰值，不怕采样漏掉
    return out


def _timed(url: str, src: str, tgt: str, text: str) -> tuple[str, float]:
    started = time.perf_counter()
    out = DET._post(url, src, tgt, text)
    return out, (time.perf_counter() - started) * 1000.0


def _health(url: str) -> dict:
    with urllib.request.urlopen(f"{url}/health", timeout=5) as resp:
        return json.load(resp)


def _pct(values: list[float], q: float) -> float:
    return round(DET._pct(values, q), 1) if values else 0.0


def _run(args: argparse.Namespace, name: str, extra: dict[str, str], port: int) -> dict:
    env = dict(os.environ, PYTHONUTF8="1", PYTHONUNBUFFERED="1")
    env["SUIYI_USER_GLOSSARY"] = str(
        Path(tempfile.gettempdir()) / "suiyi-122-no-glossary.tsv"
    )
    for key in list(env):
        if key.startswith(("CT2_PACKED", "SUIYI_SHORT_FALLBACK", "SUIYI_PACKED")):
            env.pop(key)
    env.update(extra)
    command = [sys.executable, "-m", "suiyi_engine", "serve", "--port", str(port)]
    command += ["--models-dir", str(args.models_dir), "--preload", "zh-en,en-zh"]
    log_path = Path(tempfile.gettempdir()) / f"suiyi-122-{port}.log"
    log = open(log_path, "w", encoding="utf-8")  # noqa: SIM115 - serve 子进程整个生命周期都要写
    proc = subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT)
    url = f"http://127.0.0.1:{port}"
    result: dict = {"name": name, "env": extra}
    sampler: Sampler | None = None
    try:
        for _ in range(int(args.start_timeout * 5)):
            if proc.poll() is not None:
                raise SystemExit(
                    f"{name}：serve 退出，返回码 {proc.returncode}，日志 {log.name}"
                )
            try:
                _health(url)
                break
            except OSError:
                time.sleep(0.2)
        else:
            raise SystemExit(f"{name}：serve 没有在限定时间内就绪")
        pid = MEM.target_pid(proc.pid)
        sampler = Sampler(pid)
        sampler.start()
        stages: dict[str, dict[str, float]] = {"idle": sampler.sample()}

        latencies: list[float] = []
        regular: list[str] = []
        for round_index in range(args.rounds):
            for src, tgt, text in args.rows:
                out, ms = _timed(url, src, tgt, text)
                latencies.append(ms)
                if round_index == 0:
                    regular.append(out)
        time.sleep(args.settle)  # 等 janitor 做完 trim（安静 2 秒后）
        stages["regular"] = sampler.sample()

        short_out: list[str] = []
        short_ms: list[float] = []
        for text in args.short:
            out, ms = _timed(url, "en", "zh", text)
            short_out.append(out)
            short_ms.append(ms)
        time.sleep(args.settle)  # 等 janitor 做完 trim（安静 2 秒后）
        stages["short"] = sampler.sample()
        loaded_after_short = _health(url)["loaded_models"]

        _, back_ms = _timed(url, "zh", "en", ZH_PROBE)
        stages["back_to_zh_en"] = sampler.sample()

        cycles: list[tuple[float, float]] = []
        for _ in range(args.cycles):
            _, a = _timed(url, "en", "zh", SHORT_MISS)
            _, b = _timed(url, "zh", "en", ZH_PROBE)
            cycles.append((a, b))
        time.sleep(args.settle)
        stages["end"] = sampler.sample()
        health = _health(url)
        sampler.stop()
        result.update(
            {
                "stages": {
                    k: {kk: round(v, 1) for kk, v in s.items()}
                    for k, s in stages.items()
                },
                "peak": {k: round(v, 1) for k, v in sampler.peak.items()},
                "regular_p50_ms": _pct(latencies, 0.5),
                "regular_p95_ms": _pct(latencies, 0.95),
                "regular_outputs": regular,
                "short_first_ms": round(short_ms[0], 1) if short_ms else 0.0,
                "short_p50_ms": _pct(short_ms, 0.5),
                "short_p95_ms": _pct(short_ms, 0.95),
                "short_outputs": short_out,
                "loaded_after_short": loaded_after_short,
                "back_to_zh_en_ms": round(back_ms, 1),
                "cycle_en_zh_ms": [round(a, 1) for a, _ in cycles],
                "cycle_zh_en_ms": [round(b, 1) for _, b in cycles],
                "short_fallback_stats": health.get("short_fallback_stats"),
                "ct2_packed_gemm": health.get("ct2_packed_gemm"),
            }
        )
    finally:
        if sampler is not None and sampler.is_alive():
            sampler.stop()
        proc.terminate()
        try:
            proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            proc.kill()
        log.close()
    return result


def _markdown(rows: list[dict], args: argparse.Namespace) -> str:
    a, b = KEYS
    lines = [
        (
            f"### 表外极短句兜底（{sys.platform}，常规句 {len(args.rows)} 条 × {args.rounds} 轮，"
            f"短句 {len(args.short)} 条，交替 {args.cycles} 次）"
        ),
        "",
        (
            f"| 配置 | 常规后 {a} / {b} | 短句后 {a} / {b} | 结束 {a} / {b} | 峰值 {a} / {b} | "
            "常规 P50 / P95 ms | 首个短句 ms | 切回 zh→en ms | 交替 en→zh / zh→en 中位 ms | "
            "兜底计数 |"
        ),
        "|---|---|---|---|---|---|---|---|---|---|",
    ]
    for row in rows:
        s = row["stages"]
        peak = row["peak"]
        if "peak_private_mib" in peak:
            extra = f"（提交峰值 {peak['peak_private_mib']:.0f}）"
        elif "vmhwm_mib" in peak:
            extra = f"（VmHWM {peak['vmhwm_mib']:.0f}）"
        else:
            extra = ""
        lines.append(
            f"| {row['name']} | {s['regular'].get(a, 0):.0f} / {s['regular'].get(b, 0):.0f} | "
            f"{s['short'].get(a, 0):.0f} / {s['short'].get(b, 0):.0f} | "
            f"{s['end'].get(a, 0):.0f} / {s['end'].get(b, 0):.0f} | "
            f"{peak.get(a, 0):.0f} / {peak.get(b, 0):.0f}{extra} | "
            f"{row['regular_p50_ms']} / {row['regular_p95_ms']} | {row['short_first_ms']} | "
            f"{row['back_to_zh_en_ms']} | {statistics.median(row['cycle_en_zh_ms']):.1f} / "
            f"{statistics.median(row['cycle_zh_en_ms']):.1f} | "
            f"{json.dumps(row['short_fallback_stats'], ensure_ascii=False)} |"
        )
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--models-dir", type=Path, required=True)
    parser.add_argument(
        "--config", action="append", required=True, help="名称=ENV=VAL,ENV=VAL"
    )
    parser.add_argument("--rounds", type=int, default=1)
    parser.add_argument(
        "--settle", type=float, default=4.0, help="取样前静置秒数，让 janitor 先 trim"
    )
    parser.add_argument("--cycles", type=int, default=5)
    parser.add_argument("--port", type=int, default=18990)
    parser.add_argument("--start-timeout", type=float, default=180)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--summary", type=Path, help="把 Markdown 表追加到这个文件")
    args = parser.parse_args(argv)
    args.rows = DET._load(MEM.DOMAIN, {"en-zh", "zh-en"})
    args.short = [
        json.loads(line)["source"]
        for line in SHORT.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    rows = []
    for index, text in enumerate(args.config):
        name, extra = _parse_config(text)
        row = _run(args, name, extra, args.port + index)
        print(
            f"[{name}] 峰值 {row['peak']} 常规 P95 {row['regular_p95_ms']}", flush=True
        )
        rows.append(row)
    table = _markdown(rows, args)
    print(table)
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as handle:
            handle.write(table + "\n")
    if args.out:
        args.out.write_text(
            json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
