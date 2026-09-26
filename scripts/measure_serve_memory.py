"""测量真实 ``serve`` 进程的内存与延迟（#96）。Windows 与 Linux 都能跑。

Windows 报 Working Set（WS）、Private Bytes（提交的私有内存）、私有工作集（USS）和峰值 WS，
并用 ``VirtualQueryEx`` 统计地址空间：已提交私有 / 只保留 / 映像 / 映射文件，以及最大的几块私有区域。
Linux 报 RSS、USS、峰值 RSS（VmHWM）和 VmData。

需要 ``psutil``（``pip install -e "engine[eval]"``），不进运行时依赖。

用法::

    # 真实 serve 进程，按 Issue #96 的场景依次加载：空闲 → en→zh → zh→en → 段落 P95 → en→ja → OCR
    python scripts/measure_serve_memory.py serve --models-dir models --out serve-mem.json
    # 对比另一份源码（例如 main 的 worktree）：--src <engine/src 目录>
    # 额外 serve 参数放在 -- 之后：... serve --models-dir models -- --model-idle-unload 30

    # 进程内逐项加载，看每个模型、OCR、Python 基线各占多少
    python scripts/measure_serve_memory.py breakdown --models-dir models

PowerShell 同样适用（``python scripts\\measure_serve_memory.py serve --models-dir models``）。
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import statistics
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOMAIN = ROOT / "engine" / "eval" / "domain" / "domain_v1.jsonl"
OCR_IMAGE = ROOT / "engine" / "eval" / "ocr_samples" / "images" / "zh_ui_1080_01.png"
MIB = 1024 * 1024
WINDOWS = sys.platform == "win32"
FIRST = {
    ("en", "zh"): "Kubernetes is an open-source container orchestration engine hosted by the CNCF.",
    ("zh", "en"): "容器编排平台负责调度、扩缩容和故障恢复。",
    ("en", "ja"): "The service restarts automatically after a crash.",
}


# ---------------------------------------------------------------- 内存读数


def target_pid(pid: int) -> int:
    """真正跑 serve 的进程。Windows 的 venv ``python.exe`` 只是个启动器，会再起一个子进程。"""

    import psutil

    try:
        family = [psutil.Process(pid), *psutil.Process(pid).children(recursive=True)]
    except psutil.NoSuchProcess:
        return pid
    return max(family, key=lambda proc: proc.memory_info().rss).pid


def memory(pid: int) -> dict[str, float]:
    """进程内存（MiB）。键名在两个平台上不同，见模块说明。"""

    import psutil

    proc = psutil.Process(pid)
    info = proc.memory_info()
    out: dict[str, float] = {"threads": float(proc.num_threads())}
    try:
        out["uss_mib"] = proc.memory_full_info().uss / MIB
    except (psutil.AccessDenied, AttributeError):
        pass
    if WINDOWS:
        out["ws_mib"] = info.rss / MIB
        out["peak_ws_mib"] = info.peak_wset / MIB
        out["private_mib"] = info.private / MIB
        out.update(_vm_summary(pid))
    else:
        out["rss_mib"] = info.rss / MIB
        status = Path(f"/proc/{pid}/status")
        if status.is_file():
            for line in status.read_text().splitlines():
                key = line.split(":")[0]
                if key in ("VmHWM", "VmData"):
                    out[f"{key.lower()}_mib"] = int(line.split()[1]) / 1024
    return out


def _vm_summary(pid: int) -> dict[str, float]:
    """Windows：遍历地址空间，按类型统计（MiB）。"""

    import ctypes
    from ctypes import wintypes

    class MBI(ctypes.Structure):
        _fields_ = [
            ("BaseAddress", ctypes.c_void_p),
            ("AllocationBase", ctypes.c_void_p),
            ("AllocationProtect", wintypes.DWORD),
            ("PartitionId", wintypes.WORD),
            ("RegionSize", ctypes.c_size_t),
            ("State", wintypes.DWORD),
            ("Protect", wintypes.DWORD),
            ("Type", wintypes.DWORD),
        ]

    mem_commit, mem_reserve = 0x1000, 0x2000
    mem_private, mem_mapped, mem_image = 0x20000, 0x40000, 0x1000000
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.VirtualQueryEx.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.POINTER(MBI),
        ctypes.c_size_t,
    ]
    kernel32.VirtualQueryEx.restype = ctypes.c_size_t
    handle = kernel32.OpenProcess(0x0400 | 0x0010, False, pid)  # QUERY_INFORMATION | VM_READ
    if not handle:
        return {}
    totals = {"commit_private": 0, "reserve_private": 0, "commit_image": 0, "commit_mapped": 0}
    by_base: dict[int, int] = {}
    address = 0
    mbi = MBI()
    try:
        while kernel32.VirtualQueryEx(handle, ctypes.c_void_p(address), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            size = int(mbi.RegionSize)
            if mbi.State == mem_commit:
                if mbi.Type == mem_private:
                    totals["commit_private"] += size
                    base = int(mbi.AllocationBase or 0)
                    by_base[base] = by_base.get(base, 0) + size
                elif mbi.Type == mem_image:
                    totals["commit_image"] += size
                elif mbi.Type == mem_mapped:
                    totals["commit_mapped"] += size
            elif mbi.State == mem_reserve and mbi.Type == mem_private:
                totals["reserve_private"] += size
            address = int(mbi.BaseAddress or 0) + size
            if address >= 1 << 47:
                break
    finally:
        kernel32.CloseHandle(handle)
    out = {f"vm_{key}_mib": value / MIB for key, value in totals.items()}
    largest = sorted(by_base.values(), reverse=True)
    out["vm_private_blocks"] = float(len(largest))
    out["vm_private_top5_mib"] = sum(largest[:5]) / MIB
    for index, size in enumerate(largest[:5]):
        out[f"vm_private_top{index + 1}_mib"] = size / MIB
    buckets = {"ge64": 0, "ge8": 0, "ge1": 0, "lt1": 0}
    for size in largest:
        key = "ge64" if size >= 64 * MIB else "ge8" if size >= 8 * MIB else "ge1" if size >= MIB else "lt1"
        buckets[key] += size
    out.update({f"vm_private_{key}_mib": value / MIB for key, value in buckets.items()})
    return out


def _headline(mem: dict[str, float]) -> str:
    if WINDOWS:
        return (
            f"WS {mem.get('ws_mib', 0):.1f} / Private {mem.get('private_mib', 0):.1f} / "
            f"USS {mem.get('uss_mib', 0):.1f} / 峰值 WS {mem.get('peak_ws_mib', 0):.1f} MiB"
        )
    return (
        f"RSS {mem.get('rss_mib', 0):.1f} / USS {mem.get('uss_mib', 0):.1f} / "
        f"峰值 {mem.get('vmhwm_mib', 0):.1f} MiB"
    )


# ---------------------------------------------------------------- serve 场景


def _post(port: int, path: str, body: bytes, content_type: str, timeout: float = 300) -> tuple[int, dict]:
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}", body, {"Content-Type": content_type}
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as exc:
        return exc.code, json.loads(exc.read() or b"{}")


def _translate(port: int, src: str, tgt: str, text: str) -> tuple[int, float]:
    body = json.dumps({"text": text, "source": src, "target": tgt}).encode()
    started = time.perf_counter()
    status, _ = _post(port, "/translate", body, "application/json")
    return status, (time.perf_counter() - started) * 1000


def _paragraphs() -> list[tuple[str, str, str]]:
    rows = [json.loads(line) for line in DOMAIN.read_text(encoding="utf-8").splitlines() if line.strip()]
    return [
        (row["src_lang"], row["tgt_lang"], row["source"])
        for row in rows
        if row["category"] == "paragraph" and {row["src_lang"], row["tgt_lang"]} == {"zh", "en"}
    ]


def _percentile(values: list[float], q: float) -> float:
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, max(0, round(q * (len(ordered) - 1))))]


def run_serve(args: argparse.Namespace) -> dict:
    env = dict(os.environ, PYTHONUTF8="1", PYTHONUNBUFFERED="1")
    env["SUIYI_USER_GLOSSARY"] = str(Path(tempfile.gettempdir()) / "suiyi-96-no-glossary.tsv")
    if args.src:
        env["PYTHONPATH"] = str(Path(args.src).resolve())
    else:
        env.pop("PYTHONPATH", None)
    port = args.port
    command = [args.python, "-m", "suiyi_engine", "serve", "--port", str(port)]
    command += ["--models-dir", str(args.models_dir), *args.serve_args]
    log = open(Path(tempfile.gettempdir()) / f"suiyi-96-serve-{port}.log", "w", encoding="utf-8")
    proc = subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT)
    steps: list[dict] = []
    result: dict = {
        "label": args.label,
        "platform": platform.platform(),
        "cpu_count": os.cpu_count(),
        "python": args.python,
        "src": args.src,
        "serve_args": args.serve_args,
        "steps": steps,
    }
    try:
        started = time.perf_counter()
        for _ in range(int(args.start_timeout * 5)):
            if proc.poll() is not None:
                raise SystemExit(f"serve 退出了，返回码 {proc.returncode}，日志 {log.name}")
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1) as resp:
                    health = json.load(resp)
                break
            except OSError:
                time.sleep(0.2)
        else:
            raise SystemExit("serve 没有在限定时间内就绪")
        result["startup_s"] = round(time.perf_counter() - started, 2)
        result["health"] = health

        def record(name: str, **extra: object) -> None:
            time.sleep(args.settle)
            mem = memory(target_pid(proc.pid))
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=5) as resp:
                loaded = json.load(resp)
            entry = {
                "step": name,
                "loaded_models": loaded.get("loaded_models"),
                "ocr_loaded": loaded.get("ocr_loaded"),
                **extra,
                **{key: round(value, 1) for key, value in mem.items()},
            }
            steps.append(entry)
            print(f"[{name}] {_headline(mem)}  模型 {entry['loaded_models']} OCR {entry['ocr_loaded']}", flush=True)

        record("空闲")
        for pair, name in (((("en", "zh")), "en→zh"), ((("zh", "en")), "zh→en")):
            status, cold = _translate(port, *pair, FIRST[pair])
            _, warm = _translate(port, *pair, FIRST[pair])
            record(f"用过 {name}", status=status, cold_ms=round(cold), warm_ms=round(warm))
        latencies: list[float] = []
        for _ in range(args.rounds):
            for src, tgt, text in _paragraphs():
                status, elapsed = _translate(port, src, tgt, text)
                if status != 200:
                    raise SystemExit(f"段落翻译失败：HTTP {status}")
                latencies.append(elapsed)
        record(
            "段落 zh↔en 之后",
            p50_ms=round(statistics.median(latencies)),
            p95_ms=round(_percentile(latencies, 0.95)),
            n=len(latencies),
        )
        status, cold = _translate(port, "en", "ja", FIRST[("en", "ja")])
        record("用过 en→ja", status=status, cold_ms=round(cold))
        image = Path(args.ocr_image).read_bytes()
        begun = time.perf_counter()
        status, _ = _post(port, "/ocr", image, "image/png")
        cold = (time.perf_counter() - begun) * 1000
        record("用过 OCR（全部加载）", status=status, cold_ms=round(cold))
        if args.idle_wait > 0:
            time.sleep(args.idle_wait)
            record(f"空闲 {args.idle_wait:.0f} 秒后")
        result["serve_pid"] = target_pid(proc.pid)
        result["peak"] = {key: round(value, 1) for key, value in memory(result["serve_pid"]).items()}
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()
        log.close()
    return result


# ---------------------------------------------------------------- 进程内分解


def run_breakdown(args: argparse.Namespace) -> dict:
    pid = os.getpid()
    steps: list[dict] = []
    previous: dict[str, float] = {}

    trim = None

    def record(name: str) -> None:
        nonlocal previous
        if trim is not None:
            trim()  # 与 serve 一致：读数前把空闲堆内存还给系统
        time.sleep(args.settle)
        mem = memory(pid)
        key = "ws_mib" if WINDOWS else "rss_mib"
        delta = {
            f"delta_{name_}": round(mem[name_] - previous.get(name_, 0), 1)
            for name_ in (key, "private_mib", "uss_mib")
            if name_ in mem
        }
        steps.append({"step": name, **{k: round(v, 1) for k, v in mem.items()}, **delta})
        print(f"[{name}] {_headline(mem)}  增量 {delta}", flush=True)
        previous = mem

    record("Python 基线")
    from suiyi_engine import memory as engine_memory
    from suiyi_engine.translator import Translator

    if hasattr(engine_memory, "configure_allocator"):
        engine_memory.configure_allocator()
    trim = getattr(engine_memory, "trim", None)
    import ctranslate2  # noqa: F401

    record("导入 suiyi_engine + ctranslate2")
    translator = Translator(args.models_dir)
    pairs = [(src, tgt) for src, tgt, kind in translator.available_pairs() if kind == "direct"]
    for src, tgt in pairs:
        records = translator.registry.resolve(src, tgt)
        translator.registry.get(records[0].id)
        record(f"加载 {records[0].id}")
        translator.translate(FIRST.get((src, tgt), "Hello world."), src, tgt)
        record(f"用 {src}→{tgt} 翻译一句")
    try:
        from suiyi_engine.ocr import OcrEngine

        engine = OcrEngine(Path(args.models_dir))
        engine.load()
        record("加载 OCR")
        engine.recognize(Path(args.ocr_image))
        record("OCR 识别一张 1080p")
    except Exception as exc:  # noqa: BLE001 - 只是测量，OCR 不可用时跳过
        print(f"OCR 跳过：{exc}", flush=True)
    return {"label": args.label, "platform": platform.platform(), "steps": steps}


# ---------------------------------------------------------------- 入口


def _markdown(result: dict) -> str:
    steps = result["steps"]
    if WINDOWS:
        cols = ["ws_mib", "private_mib", "uss_mib", "peak_ws_mib", "vm_commit_private_mib"]
        heads = ["WS", "Private Bytes", "私有 WS", "峰值 WS", "已提交私有（VirtualQuery）"]
    else:
        cols = ["rss_mib", "uss_mib", "vmhwm_mib", "vmdata_mib"]
        heads = ["RSS", "USS", "峰值 RSS", "VmData"]
    lines = [
        f"### {result.get('label') or '内存测量'}（{result['platform']}）",
        "",
        "| 步骤 | " + " | ".join(heads) + " | 其他 |",
        "|---" * (len(heads) + 2) + "|",
    ]
    for step in steps:
        extra = {k: v for k, v in step.items() if k.endswith("_ms") or k in ("n", "status") or k.startswith("delta_")}
        cells = [f"{step.get(col, float('nan')):.1f}" for col in cols]
        lines.append(f"| {step['step']} | " + " | ".join(cells) + f" | {extra} |")
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="mode", required=True)
    for name in ("serve", "breakdown"):
        p = sub.add_parser(name)
        p.add_argument("--models-dir", type=Path, required=True)
        p.add_argument("--out", type=Path, help="写出 JSON")
        p.add_argument("--label", default="")
        p.add_argument("--settle", type=float, default=4.0, help="每步读数前等待秒数（默认 4）")
        p.add_argument("--ocr-image", default=str(OCR_IMAGE))
        p.add_argument("--summary", type=Path, help="把 Markdown 表追加到这个文件（CI 用 GITHUB_STEP_SUMMARY）")
        if name == "serve":
            p.add_argument("--python", default=sys.executable, help="运行 serve 的解释器")
            p.add_argument("--src", help="PYTHONPATH，指向要测的 engine/src（对比 main 时用）")
            p.add_argument("--port", type=int, default=18796)
            p.add_argument("--rounds", type=int, default=3, help="段落轮数（默认 3）")
            p.add_argument("--idle-wait", type=float, default=0.0, help="最后空闲多少秒再读一次")
            p.add_argument("--start-timeout", type=float, default=300.0)
            p.add_argument("serve_args", nargs="*", help="额外 serve 参数，写在 -- 之后")
    args = parser.parse_args(argv)
    result = run_serve(args) if args.mode == "serve" else run_breakdown(args)
    table = _markdown(result)
    print(table)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as handle:
            handle.write(table + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
