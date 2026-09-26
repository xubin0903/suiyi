"""经本机 HTTP 服务测量冷启动、延迟和内存。

命令行入口是仓库根目录的 ``scripts/bench_service.py``。
``psutil`` 只在真正采样 RSS 时导入，安装在可选组 ``bench``，不进运行时。
"""

from __future__ import annotations

import argparse
import importlib.metadata
import json
import math
import os
import platform
import re
import signal
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from suiyi_engine import __version__
from suiyi_engine.registry import (
    DEFAULT_BEAM_SIZE,
    DEFAULT_INTER_THREADS,
    DEFAULT_MAX_BATCH_SIZE,
    default_intra_threads,
    repo_root,
)
from suiyi_engine.segment import split_sentences

# 初始目标。内存按 1024 进制：800 MB = 800 MiB，1.5 GB = 1.5 GiB。
TARGET_COLD_START_MS = 5000.0
TARGET_ZH_SHORT_P50_MS = 300.0
TARGET_ZH_SHORT_P95_MS = 800.0
TARGET_ZH_MEDIUM_P95_MS = 1500.0
TARGET_ZH_PARAGRAPH_P95_MS = 2000.0
TARGET_PIVOT_P95_MS = 1500.0
TARGET_RSS_BILINGUAL_BYTES = 800 * 1024 * 1024
TARGET_RSS_MVP_BYTES = int(1.5 * 1024**3)

# 束宽保持 2 时，P95 不比最快档差过这个比例，就取更小的 intra，把核留给客户端。
INTRA_NEAR_RATIO = 1.10
# 段落上 max_batch_size=32 比 8 慢过这个比例，才建议把默认批量降到 8。
BATCH_SLOWER_RATIO = 1.15

PRELOAD_PAIRS = "zh-en,en-zh"
GRID_INTRA_THREADS = (1, 2, 4)
GRID_BEAM_SIZES = (1, 2, 4)
GRID_BATCH_SIZES = (1, 8, 32)
_HEALTH_POLL_S = 0.05
_DECODE_LINE = re.compile(r"intra_threads=(\d+) beam_size=(\d+) max_batch_size=(\d+)")

ZH_SHORT = "今天下午三点我们在公园门口见面。"
ZH_MEDIUM = (
    "今天下午三点，项目组在图书馆一楼大厅集合，请大家带上笔记本和充电器，"
    "我们要把这次翻译演示的流程完整地走一遍。"
)
ZH_PARAGRAPH = (
    "随译想把复制翻译做成一件轻快的事。用户在浏览器里选中一段中文，按下复制，浮层应在两秒内给出英文。"
    "这句话看起来不长，可是模型要先把整段切成句子，再逐句生成译文，最后按原来的顺序拼回去。"
    "如果服务刚启动，权重还在磁盘上，第一次请求会把加载时间也算进去，所以客户端应当在后台预热中英两个方向。"
    "热路径上则只剩下分词、解码和拼回。短句应当明显快于长段落。中转方向要跑两次解码，延迟大约会高一截，超时不能按短句来设。"
    "我们用固定的短句、中句和段落反复请求本机接口，记录客户端看到的耗时，而不是只相信服务内部的计时。"
    "这样做才能判断四核笔记本上，默认线程和束搜索宽度该怎么取，以及内存是否放得下全部模型。"
    "测量时请关掉其他重负载程序，否则冷启动和内存数字都会被旁边的任务抬高。"
)
EN_SHORT = "Please send me the notes before the meeting starts today."
JA_SHORT = "今日は天気がいいので公園に行きます。"

_PROBE_TEXT = {
    "zh": "你好。",
    "en": "Hello.",
    "ja": "こんにちは。",
}


class BenchError(Exception):
    """基准无法完成。消息可以直接打到 stderr。"""


@dataclass(frozen=True, slots=True)
class BenchCase:
    """一条固定输入。``kind`` 为 ``direct`` 或 ``pivot``。"""

    case_id: str
    label: str
    src: str
    tgt: str
    text: str
    kind: str


CASES: tuple[BenchCase, ...] = (
    BenchCase("zh_short", "中文短句", "zh", "en", ZH_SHORT, "direct"),
    BenchCase("zh_medium", "中文中句", "zh", "en", ZH_MEDIUM, "direct"),
    BenchCase("en_short", "英文短句", "en", "zh", EN_SHORT, "direct"),
    BenchCase("zh_paragraph", "中文段落", "zh", "en", ZH_PARAGRAPH, "direct"),
    BenchCase("ja_short", "日文短句", "ja", "en", JA_SHORT, "direct"),
    BenchCase("ja_zh_pivot", "中转短句", "ja", "zh", JA_SHORT, "pivot"),
)


@dataclass(frozen=True, slots=True)
class BenchOptions:
    """一次基准的参数。``python`` 是用来拉起服务子进程的解释器。"""

    models_dir: Path
    manifest_path: Path
    repeats: int
    cold_starts: int
    warmup: int
    grid_repeats: int
    batch_repeats: int
    no_grid: bool
    out_dir: Path
    host: str
    cold_timeout_s: float
    request_timeout_s: float
    python: str


@dataclass
class RunningServer:
    """已经拉起、尚未停止的 ``suiyi_engine serve`` 子进程。"""

    proc: subprocess.Popen[bytes]
    log_path: Path
    log_handle: object
    host: str
    port: int
    started: float
    kill_group: bool

    @property
    def base_url(self) -> str:
        shown = f"[{self.host}]" if ":" in self.host else self.host
        return f"http://{shown}:{self.port}"

    def stop(self) -> None:
        stop_process(self.proc, kill_group=self.kill_group)
        handle = self.log_handle
        close = getattr(handle, "close", None)
        if close is not None:
            close()
        self.log_handle = None


def han_count(text: str) -> int:
    """统计 CJK 统一汉字，用来核对「约 N 字」。"""

    return sum(1 for ch in text if "\u4e00" <= ch <= "\u9fff")


def word_count(text: str) -> int:
    """按空白切分的词数。英文短句用这个核对「约 N 词」。"""

    return len([part for part in text.split() if part])


def case_by_id(case_id: str) -> BenchCase:
    for case in CASES:
        if case.case_id == case_id:
            return case
    raise BenchError(f"未知样例 {case_id}")


def nearest_rank(values: Sequence[float], percent: float) -> float:
    """最近秩百分位。1-based 秩 = ceil(percent/100 * n)，再取排序后的该样本。"""

    if not values:
        raise ValueError("空样本没有百分位")
    if percent <= 0:
        return float(min(values))
    if percent >= 100:
        return float(max(values))
    ordered = sorted(float(value) for value in values)
    rank = math.ceil(percent / 100.0 * len(ordered))
    index = min(len(ordered), max(1, rank)) - 1
    return ordered[index]


def summarize(values: Sequence[float]) -> dict[str, float | int]:
    """P50 用中位数（偶数个样本取中间两点平均），P95 用最近秩，另给 max。"""

    if not values:
        raise ValueError("空样本无法汇总")
    series = [float(value) for value in values]
    return {
        "n": len(series),
        "p50_ms": statistics.median(series),
        "p95_ms": nearest_rank(series, 95),
        "max_ms": max(series),
        "min_ms": min(series),
    }


def service_is_ready(payload: Mapping[str, object], expected_ids: set[str]) -> bool:
    """``/health`` 为 ok，且 ``loaded_models`` 已包含期望的模型 id。"""

    if payload.get("status") != "ok":
        return False
    loaded = payload.get("loaded_models")
    if not isinstance(loaded, list):
        return False
    return expected_ids <= {str(item) for item in loaded}


def build_serve_command(
    *,
    python: str,
    models_dir: Path,
    host: str,
    port: int,
    preload: str,
    intra_threads: int | None,
    beam_size: int | None,
    max_batch_size: int | None,
) -> list[str]:
    """拼出子进程参数列表。不用 shell，Windows 与 Linux 同一形式。"""

    command = [
        python,
        "-m",
        "suiyi_engine",
        "serve",
        "--host",
        host,
        "--port",
        str(port),
        "--models-dir",
        str(models_dir),
    ]
    if preload:
        command.extend(["--preload", preload])
    if intra_threads is not None:
        command.extend(["--intra-threads", str(intra_threads)])
    if beam_size is not None:
        command.extend(["--beam-size", str(beam_size)])
    if max_batch_size is not None:
        command.extend(["--max-batch-size", str(max_batch_size)])
    return command


def suggest_defaults(
    cells: Sequence[Mapping[str, object]],
    batch_cells: Sequence[Mapping[str, object]],
    *,
    cpu_count: int,
    short_p50_target_ms: float = TARGET_ZH_SHORT_P50_MS,
    short_p95_target_ms: float = TARGET_ZH_SHORT_P95_MS,
) -> dict[str, object]:
    """根据网格给出默认 intra / beam / max_batch_size。

    没有网格时保持当前默认。有网格时：

    - 只在 ``beam_size=2`` 里、且 ``intra_threads`` 不超过逻辑 CPU 数的格子中，
      选 P95 不比最快档差过 10% 的最小 intra。
    - 该 intra 上 beam=2 达不到短句 P50 或 P95，而 beam=1 可以时，才建议 beam=1。
    - 段落 P95 上 batch=32 比 batch=8 慢过 15% 时，才建议 batch=8。
    - ``inter_threads`` 保持 1：HTTP 同时只跑一路翻译。
    """

    logical = max(1, cpu_count)
    current_intra = default_intra_threads()
    current_beam = DEFAULT_BEAM_SIZE
    current_batch = DEFAULT_MAX_BATCH_SIZE
    reasons: dict[str, str] = {
        "inter_threads": (
            "HTTP 对翻译加了进程内互斥，同一时刻只有一路解码，inter_threads 保持 1，"
            "避免空转的 worker 占内存。"
        )
    }
    if not cells:
        reasons["intra_threads"] = "未跑线程网格，保持 intra_threads=min(4, CPU 数)。"
        reasons["beam_size"] = "未跑束宽网格，保持 beam_size=2。"
        reasons["max_batch_size"] = "未跑批量网格，保持 max_batch_size=32。"
        return _suggestion(current_intra, current_beam, current_batch, False, reasons)

    beam2 = [
        cell
        for cell in cells
        if _cell_int(cell, "beam_size") == DEFAULT_BEAM_SIZE
        and _cell_int(cell, "intra_threads") <= logical
    ]
    if not beam2:
        reasons["intra_threads"] = "网格里没有可用的 beam_size=2 格子，保持现有 intra 默认。"
        recommended_intra = current_intra
    else:
        best_p95 = min(_cell_float(cell, "p95_ms") for cell in beam2)
        near = [
            cell for cell in beam2 if _cell_float(cell, "p95_ms") <= best_p95 * INTRA_NEAR_RATIO
        ]
        recommended_intra = min(_cell_int(cell, "intra_threads") for cell in near)
        picked = _find_cell(cells, recommended_intra, DEFAULT_BEAM_SIZE)
        picked_p95 = _cell_float(picked, "p95_ms") if picked else best_p95
        reasons["intra_threads"] = (
            f"beam_size=2 且线程数不超过 {logical} 个逻辑 CPU 时，"
            f"intra_threads={recommended_intra} 的短句 P95 为 {picked_p95:.1f} ms，"
            f"与最快档 {best_p95:.1f} ms 相差不超过 {INTRA_NEAR_RATIO - 1:.0%}，"
            "取这一档以把其余核心留给客户端。"
        )

    base = _find_cell(cells, recommended_intra, DEFAULT_BEAM_SIZE)
    greedy = _find_cell(cells, recommended_intra, 1)
    recommended_beam = current_beam
    reasons["beam_size"] = (
        "保持 beam_size=2。束宽影响译文质量，本基准不评质量；"
        "只有 beam=2 达不到短句目标、且 beam=1 可以达到时才建议改为 1。"
    )
    if base is not None and greedy is not None:
        base_p50 = _cell_float(base, "p50_ms")
        base_p95 = _cell_float(base, "p95_ms")
        greedy_p50 = _cell_float(greedy, "p50_ms")
        greedy_p95 = _cell_float(greedy, "p95_ms")
        p50_fixed = base_p50 > short_p50_target_ms and greedy_p50 <= short_p50_target_ms
        p95_fixed = base_p95 > short_p95_target_ms and greedy_p95 <= short_p95_target_ms
        if p50_fixed or p95_fixed:
            recommended_beam = 1
            reasons["beam_size"] = (
                f"intra_threads={recommended_intra} 时 beam_size=2 的短句"
                f" P50 {base_p50:.1f} ms / P95 {base_p95:.1f} ms 未达到目标，"
                f"beam_size=1 为 P50 {greedy_p50:.1f} ms / P95 {greedy_p95:.1f} ms，可以达到。"
                "译文质量会下降，采用前需要另做样例评测。"
            )

    recommended_batch = current_batch
    batch_8 = _find_batch(batch_cells, 8)
    batch_32 = _find_batch(batch_cells, 32)
    if batch_8 is None or batch_32 is None:
        reasons["max_batch_size"] = "未比较 batch=8 与 32，保持 max_batch_size=32。"
    else:
        p95_8 = _cell_float(batch_8, "p95_ms")
        p95_32 = _cell_float(batch_32, "p95_ms")
        if p95_32 > p95_8 * BATCH_SLOWER_RATIO:
            recommended_batch = 8
            reasons["max_batch_size"] = (
                f"中文段落 P95：max_batch_size=32 为 {p95_32:.1f} ms，"
                f"比 8 的 {p95_8:.1f} ms 慢超过 {BATCH_SLOWER_RATIO - 1:.0%}，建议默认改为 8。"
            )
        else:
            reasons["max_batch_size"] = (
                f"中文段落 P95：max_batch_size=32 为 {p95_32:.1f} ms，"
                f"batch=8 为 {p95_8:.1f} ms，没有慢过 {BATCH_SLOWER_RATIO - 1:.0%}，保持 32。"
                "短句只有一句，批量上限几乎不影响复制翻译。"
            )

    changed = (
        recommended_intra != current_intra
        or recommended_beam != current_beam
        or recommended_batch != current_batch
    )
    return _suggestion(recommended_intra, recommended_beam, recommended_batch, changed, reasons)


def parse_args(argv: Sequence[str] | None = None) -> BenchOptions:
    parser = argparse.ArgumentParser(
        description="经本机 HTTP 服务测量冷启动、延迟与内存。测前请关闭其他重负载程序。",
    )
    parser.add_argument("--models-dir", required=True, help="已转换的 CTranslate2 模型目录")
    parser.add_argument("--manifest", default=None, help="默认 engine/model_manifest.json")
    parser.add_argument("--repeats", type=int, default=50, help="每个热路径样例的请求次数")
    parser.add_argument("--cold-starts", type=int, default=3, help="冷启动次数，报告取中位数")
    parser.add_argument("--warmup", type=int, default=3, help="每个样例计入统计前的预热次数")
    parser.add_argument("--grid-repeats", type=int, default=20, help="线程和束宽网格每个格子的次数")
    parser.add_argument("--batch-repeats", type=int, default=10, help="批量网格上每个格子的次数")
    parser.add_argument("--no-grid", action="store_true", help="跳过线程、束宽和批量网格")
    parser.add_argument("--out", default=None, help="输出目录，默认 reports/bench-<日期>")
    parser.add_argument("--host", default="127.0.0.1", help="只接受回环地址")
    parser.add_argument("--cold-timeout", type=float, default=180.0, help="等单次冷启动就绪的秒数")
    parser.add_argument("--request-timeout", type=float, default=120.0, help="单次翻译的 HTTP 超时")
    parser.add_argument("--python", default=sys.executable, help="拉起服务的 Python")
    args = parser.parse_args(list(argv) if argv is not None else None)
    _require_positive_arg("repeats", args.repeats)
    _require_positive_arg("cold-starts", args.cold_starts)
    _require_non_negative_arg("warmup", args.warmup)
    _require_positive_arg("grid-repeats", args.grid_repeats)
    _require_positive_arg("batch-repeats", args.batch_repeats)
    if args.cold_timeout <= 0 or args.request_timeout <= 0:
        raise BenchError("超时必须大于 0")
    if args.host.strip().lower() not in {"127.0.0.1", "::1", "localhost"}:
        raise BenchError("--host 只接受 127.0.0.1、::1、localhost")
    models_dir = Path(args.models_dir).expanduser().resolve()
    if not models_dir.is_dir():
        raise BenchError(f"模型目录不存在：{models_dir}")
    manifest_path = (
        Path(args.manifest).expanduser().resolve()
        if args.manifest
        else repo_root() / "engine" / "model_manifest.json"
    )
    if not manifest_path.is_file():
        raise BenchError(f"找不到模型清单：{manifest_path}")
    if args.out:
        out_dir = Path(args.out).expanduser().resolve()
    else:
        day = datetime.now().astimezone().date().isoformat()
        out_dir = repo_root() / "reports" / f"bench-{day}"
    return BenchOptions(
        models_dir=models_dir,
        manifest_path=manifest_path,
        repeats=args.repeats,
        cold_starts=args.cold_starts,
        warmup=args.warmup,
        grid_repeats=args.grid_repeats,
        batch_repeats=args.batch_repeats,
        no_grid=bool(args.no_grid),
        out_dir=out_dir,
        host=args.host.strip(),
        cold_timeout_s=float(args.cold_timeout),
        request_timeout_s=float(args.request_timeout),
        python=str(args.python),
    )


def main(argv: Sequence[str] | None = None) -> int:
    """跑完全部测量并写出 ``bench.md`` 与 ``bench.json``。未达标仍返回 0。"""

    _configure_stdio()
    try:
        options = parse_args(argv)
        report = run_benchmark(options)
        md_path, json_path = write_report(report, options.out_dir)
    except BenchError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    print(f"已写入 {md_path}")
    print(f"已写入 {json_path}")
    for row in report["verdicts"]:
        if isinstance(row, dict):
            print(f"{row['mark']}\t{row['metric']}\t{row['actual_display']}")
    return 0


def run_benchmark(options: BenchOptions) -> dict[str, object]:
    """按冷启动、RSS、热路径、可选网格的顺序测量。同一时间只有一个服务进程。"""

    mvp = load_mvp_models(options.manifest_path)
    missing = missing_model_ids(options.models_dir, mvp)
    if missing:
        listed = "、".join(missing)
        raise BenchError(
            f"模型目录缺少 mvp 权重：{listed}。请先运行 python scripts/convert_models.py --tier mvp"
        )
    bilingual_ids = model_ids_for_pairs(mvp, (("zh", "en"), ("en", "zh")))
    require_psutil()
    options.out_dir.mkdir(parents=True, exist_ok=True)
    (options.out_dir / "logs").mkdir(parents=True, exist_ok=True)
    print("测前请关闭其他重负载程序。基准会多次启动服务进程。", flush=True)
    environment = collect_environment(options)
    cold_runs = _measure_cold_starts(options, bilingual_ids)
    rss = _measure_rss(options, mvp, bilingual_ids)
    first_translate, latency = _measure_latency(options, bilingual_ids)
    grid_cells: list[dict[str, object]] = []
    batch_cells: list[dict[str, object]] = []
    if not options.no_grid:
        grid_cells = _measure_thread_grid(options, bilingual_ids)
        batch_cells = _measure_batch_grid(options, bilingual_ids)
    suggestion = suggest_defaults(
        grid_cells,
        batch_cells,
        cpu_count=int(environment["cpu_count_logical"] or 1),
    )
    latency_by_id = {str(row["case_id"]): row for row in latency}
    m2 = m2_advice(latency_by_id, statistics.median(cold_runs), first_translate)
    return make_report(
        environment=environment,
        options=options,
        cold_runs_ms=cold_runs,
        first_translate=first_translate,
        latency=latency,
        rss=rss,
        grid_cells=grid_cells,
        batch_cells=batch_cells,
        suggestion=suggestion,
        m2=m2,
        bilingual_ids=sorted(bilingual_ids),
        mvp_ids=[model["id"] for model in mvp],
    )


def make_report(
    *,
    environment: Mapping[str, object],
    options: BenchOptions,
    cold_runs_ms: Sequence[float],
    first_translate: Mapping[str, object],
    latency: Sequence[Mapping[str, object]],
    rss: Mapping[str, object],
    grid_cells: Sequence[Mapping[str, object]],
    batch_cells: Sequence[Mapping[str, object]],
    suggestion: Mapping[str, object],
    m2: Mapping[str, object],
    bilingual_ids: Sequence[str],
    mvp_ids: Sequence[str],
) -> dict[str, object]:
    cold_median = statistics.median([float(value) for value in cold_runs_ms])
    verdicts = evaluate(
        cold_median_ms=cold_median,
        latency=latency,
        rss_bilingual=int(rss["bilingual_bytes"]),
        rss_mvp=int(rss["mvp_bytes"]),
    )
    return {
        "created_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "models_dir": str(options.models_dir),
        "manifest_path": str(options.manifest_path),
        "environment": dict(environment),
        "method": {
            "timing": "time.perf_counter，包住客户端 HTTP 往返",
            "cold_start": (
                "子进程启动 python -m suiyi_engine serve --preload zh-en,en-zh，"
                "直到 /health 为 ok 且 loaded_models 含中英双向模型。重复取中位数。"
            ),
            "first_translate": "预加载完成后的第一次 POST /translate（中文短句），不计入热路径样本",
            "warmup": options.warmup,
            "repeats": options.repeats,
            "health_poll_s": _HEALTH_POLL_S,
            "rss": "psutil RSS，含子进程；每个阶段采样 5 次取最大",
            "percentile": "P50 为统计中位数；P95 为最近秩 ceil(0.95*n)",
            "paragraph_target": "目标表未写分位，对照时用 P95，并同时列出 max",
            "memory_unit": "800 MB = 800×1024² 字节，1.5 GB = 1.5×1024³ 字节",
        },
        "inputs": [_case_payload(case) for case in CASES],
        "cold_start": {
            "preload": PRELOAD_PAIRS,
            "expected_models": list(bilingual_ids),
            "runs_ms": [round(float(value), 3) for value in cold_runs_ms],
            "median_ms": round(cold_median, 3),
        },
        "first_translate": dict(first_translate),
        "latency": [dict(row) for row in latency],
        "rss": dict(rss),
        "mvp_model_ids": list(mvp_ids),
        "grid": {
            "case_id": "zh_short",
            "intra_threads": list(GRID_INTRA_THREADS),
            "beam_size": list(GRID_BEAM_SIZES),
            "repeats": options.grid_repeats,
            "cells": [dict(cell) for cell in grid_cells],
        },
        "batch_grid": {
            "case_id": "zh_paragraph",
            "max_batch_size": list(GRID_BATCH_SIZES),
            "repeats": options.batch_repeats,
            "cells": [dict(cell) for cell in batch_cells],
        },
        "suggestion": dict(suggestion),
        "verdicts": verdicts,
        "m2": dict(m2),
    }


def evaluate(
    *,
    cold_median_ms: float,
    latency: Sequence[Mapping[str, object]],
    rss_bilingual: int,
    rss_mvp: int,
) -> list[dict[str, object]]:
    by_id = {str(row["case_id"]): row for row in latency}
    rows = [
        _verdict("冷启动中位数", cold_median_ms, TARGET_COLD_START_MS, "ms", "cold_start"),
        _verdict(
            "中文短句 P50",
            _latency_stat(by_id, "zh_short", "p50_ms"),
            TARGET_ZH_SHORT_P50_MS,
            "ms",
            "zh_short_p50",
        ),
        _verdict(
            "中文短句 P95",
            _latency_stat(by_id, "zh_short", "p95_ms"),
            TARGET_ZH_SHORT_P95_MS,
            "ms",
            "zh_short_p95",
        ),
        _verdict(
            "中文中句 P95",
            _latency_stat(by_id, "zh_medium", "p95_ms"),
            TARGET_ZH_MEDIUM_P95_MS,
            "ms",
            "zh_medium_p95",
        ),
        _verdict(
            "中文段落 P95",
            _latency_stat(by_id, "zh_paragraph", "p95_ms"),
            TARGET_ZH_PARAGRAPH_P95_MS,
            "ms",
            "zh_paragraph_p95",
        ),
        _verdict(
            "中转短句 P95",
            _latency_stat(by_id, "ja_zh_pivot", "p95_ms"),
            TARGET_PIVOT_P95_MS,
            "ms",
            "pivot_p95",
        ),
        _verdict(
            "RSS 中英双向",
            float(rss_bilingual),
            float(TARGET_RSS_BILINGUAL_BYTES),
            "bytes",
            "rss_bilingual",
        ),
        _verdict(
            "RSS 全部 mvp",
            float(rss_mvp),
            float(TARGET_RSS_MVP_BYTES),
            "bytes",
            "rss_mvp",
        ),
    ]
    return rows


def m2_advice(
    latency_by_id: Mapping[str, Mapping[str, object]],
    cold_median_ms: float,
    first_translate: Mapping[str, object],
) -> dict[str, object]:
    """给 M2 客户端的超时和预热建议。数字来自本次热路径，不含界面绘制。"""

    short_p95 = _latency_stat(latency_by_id, "zh_short", "p95_ms")
    short_p50 = _latency_stat(latency_by_id, "zh_short", "p50_ms")
    para_p95 = _latency_stat(latency_by_id, "zh_paragraph", "p95_ms")
    pivot_p95 = _latency_stat(latency_by_id, "ja_zh_pivot", "p95_ms")
    first_ms = float(first_translate["elapsed_ms"])
    short_timeout_ms = _ceil_to(max(1500.0, short_p95 * 3), 100)
    paragraph_timeout_ms = _ceil_to(max(3000.0, para_p95 * 2), 100)
    pivot_timeout_ms = _ceil_to(max(3000.0, pivot_p95 * 2), 100)
    ui_room_ms = 2000.0 - short_p95
    return {
        "short_http_timeout_ms": short_timeout_ms,
        "paragraph_http_timeout_ms": paragraph_timeout_ms,
        "pivot_http_timeout_ms": pivot_timeout_ms,
        "cold_start_median_ms": round(cold_median_ms, 3),
        "first_translate_ms": round(first_ms, 3),
        "short_p50_ms": round(short_p50, 3),
        "short_p95_ms": round(short_p95, 3),
        "ui_budget_after_short_p95_ms": round(ui_room_ms, 3),
        "notes": [
            (
                "复制翻译的 2 秒是浮层出现的预算，含界面，不含服务冷启动。"
                f"中文短句 HTTP P95 为 {short_p95:.1f} ms，留给界面约 {ui_room_ms:.0f} ms。"
            ),
            (
                f"短句 HTTP 超时建议 {short_timeout_ms} ms（max(1500, P95×3)，向上取整到 100 ms）。"
                "不要把段落和中转也卡在这个值上。"
            ),
            (
                f"中文段落 HTTP 超时建议 {paragraph_timeout_ms} ms，"
                f"日文经英文中转的短句建议 {pivot_timeout_ms} ms（均为 max(3000, P95×2)）。"
            ),
            (
                "托盘启动时拉起服务并 `--preload zh-en,en-zh`。"
                f"本次冷启动中位数 {cold_median_ms:.0f} ms，就绪前界面显示「正在准备」，"
                "不要把这段时间算进用户按下复制后的 2 秒。"
            ),
            (
                f"预加载完成后的首译是 {first_ms:.1f} ms，热路径 P50 是 {short_p50:.1f} ms。"
                "就绪后在后台再发一条短句并丢掉结果，避免用户第一次复制撞上首译。"
            ),
            "服务进程保持常驻。每次复制都新起进程会把冷启动加进热路径。",
        ],
    }


def render_markdown(report: Mapping[str, object]) -> str:
    """把报告写成可读的 Markdown。"""

    environment = _as_dict(report["environment"])
    cold = _as_dict(report["cold_start"])
    first = _as_dict(report["first_translate"])
    rss = _as_dict(report["rss"])
    suggestion = _as_dict(report["suggestion"])
    reasons = _as_dict(suggestion["reasons"])
    m2 = _as_dict(report["m2"])
    lines = [
        "# 性能基准",
        "",
        f"- 生成时间：{report['created_at']}",
        f"- 模型目录：`{report['models_dir']}`",
        "",
        "## 环境",
        "",
        f"- 日期：{environment.get('date')}",
        f"- 系统：{environment.get('system')} / {environment.get('platform')}",
        f"- CPU：{environment.get('cpu_model')}",
        (
            f"- 逻辑 CPU：{environment.get('cpu_count_logical')}，"
            f"物理 CPU：{environment.get('cpu_count_physical')}"
        ),
        f"- 内存：{_format_bytes(int(environment['memory_total_bytes']))}",
        (
            f"- Python {environment.get('python')}，"
            f"suiyi_engine {environment.get('suiyi_engine')}，"
            f"ctranslate2 {environment.get('ctranslate2')}"
        ),
        "",
        "## 测量方法",
        "",
        "- 客户端用 `time.perf_counter()` 包住 HTTP 往返。服务内部的 `elapsed_ms` 只作对照。",
        "- 冷启动测进程启动到 `/health` 为 ok，且中英双向已在 `loaded_models`。取 3 次中位数。",
        "- 热路径先丢弃预热请求，再记录 P50 / P95 / max。P50 是中位数，P95 是最近秩。",
        "- 首译是预加载完成后的第一次中文短句，不进入上面的样本。",
        "- RSS 用 `psutil`，含子进程，每个阶段采样 5 次取最大。",
        "- 段落目标表没有写分位，这里用 P95 对照，并列出 max。",
        "- 内存目标按 1024 进制：800 MB = 800 MiB，1.5 GB = 1.5 GiB。",
        "",
        "## 冷启动",
        "",
        f"- 预加载：`{cold['preload']}`",
        f"- 各次（ms）：{', '.join(str(value) for value in cold['runs_ms'])}",
        f"- 中位数：{float(cold['median_ms']):.1f} ms",
        "",
        "## 首译",
        "",
        (f"- {first['label']} {first['src']}→{first['tgt']}：{float(first['elapsed_ms']):.1f} ms"),
        f"- 样例译文：{first.get('sample_text')}",
        "",
        "## 延迟",
        "",
        "| 样例 | 方向 | 规模 | 句数 | n | P50 ms | P95 ms | max ms |",
        "| --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    for row in _as_list(report["latency"]):
        item = _as_dict(row)
        lines.append(
            f"| {item['label']} | {item['src']}→{item['tgt']} | {item['size_label']} | "
            f"{item['sentences']} | {item['n']} | {float(item['p50_ms']):.1f} | "
            f"{float(item['p95_ms']):.1f} | {float(item['max_ms']):.1f} |"
        )
    lines.extend(
        [
            "",
            "## 内存",
            "",
            "| 阶段 | RSS | 已加载模型 |",
            "| --- | --- | --- |",
            (
                f"| 空载 | {_format_bytes(int(rss['idle_bytes']))} | "
                f"{_join_ids(rss['idle_loaded'])} |"
            ),
            (
                f"| 中英双向 | {_format_bytes(int(rss['bilingual_bytes']))} | "
                f"{_join_ids(rss['bilingual_loaded'])} |"
            ),
            (
                f"| 全部 mvp | {_format_bytes(int(rss['mvp_bytes']))} | "
                f"{_join_ids(rss['mvp_loaded'])} |"
            ),
            "",
            "## 线程与束宽",
            "",
            "样例为中文短句。未跑网格时下表为空。",
            "",
            "| intra_threads | beam_size | n | P50 ms | P95 ms | max ms |",
            "| --- | --- | --- | --- | --- | --- |",
        ]
    )
    for row in _as_list(_as_dict(report["grid"])["cells"]):
        item = _as_dict(row)
        lines.append(
            f"| {item['intra_threads']} | {item['beam_size']} | {item['n']} | "
            f"{float(item['p50_ms']):.1f} | {float(item['p95_ms']):.1f} | "
            f"{float(item['max_ms']):.1f} |"
        )
    lines.extend(
        [
            "",
            "## 批量",
            "",
            "样例为中文段落。比较的是单次请求内部的句批上限。",
            "",
            "| max_batch_size | n | P50 ms | P95 ms | max ms |",
            "| --- | --- | --- | --- | --- |",
        ]
    )
    for row in _as_list(_as_dict(report["batch_grid"])["cells"]):
        item = _as_dict(row)
        lines.append(
            f"| {item['max_batch_size']} | {item['n']} | "
            f"{float(item['p50_ms']):.1f} | {float(item['p95_ms']):.1f} | "
            f"{float(item['max_ms']):.1f} |"
        )
    lines.extend(
        [
            "",
            "## 目标对照",
            "",
            "| 指标 | 目标 | 实测 | 结果 |",
            "| --- | --- | --- | --- |",
        ]
    )
    for row in _as_list(report["verdicts"]):
        item = _as_dict(row)
        lines.append(
            f"| {item['metric']} | {item['target_display']} | "
            f"{item['actual_display']} | {item['mark']} |"
        )
    lines.extend(["", "## 默认参数建议", ""])
    lines.append(f"- 是否建议改默认：{'是' if suggestion['changed'] else '否'}")
    lines.append(
        f"- intra_threads={suggestion['intra_threads']}，"
        f"beam_size={suggestion['beam_size']}，"
        f"max_batch_size={suggestion['max_batch_size']}，"
        f"inter_threads={suggestion['inter_threads']}"
    )
    for key in ("intra_threads", "beam_size", "max_batch_size", "inter_threads"):
        lines.append(f"- {key}：{reasons.get(key)}")
    lines.extend(["", "## 给 M2 客户端", ""])
    for note in _as_list(m2["notes"]):
        lines.append(f"- {note}")
    lines.extend(
        [
            "",
            "## 已知限制",
            "",
            "- 只测 CPU，不测 GPU。计时含本机 HTTP，不含 WPF 界面。",
            "- 冷启动含解释器启动。预加载在监听前完成，第一次 `/health` 成功时模型已在内存里。",
            "- RSS 是服务进程及其子进程，不是整机占用。采样取最大值，避免读到刚加载完的偏低值。",
            "- 全部 mvp 只包括清单里 `tier=mvp` 的模型，不含 optional。",
            "- 网格的重复次数可以少于热路径的 50 次，用来选默认参数，不替代热路径数字。",
            "- Linux 与 Windows 都用参数列表启动子进程。报告里的数字只代表跑脚本的那台机器。",
            "",
        ]
    )
    return "\n".join(lines)


def write_report(report: Mapping[str, object], out_dir: Path) -> tuple[Path, Path]:
    out_dir.mkdir(parents=True, exist_ok=True)
    json_path = out_dir / "bench.json"
    md_path = out_dir / "bench.md"
    json_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    md_path.write_text(render_markdown(report), encoding="utf-8")
    return md_path, json_path


def collect_environment(options: BenchOptions) -> dict[str, object]:
    psutil = require_psutil()
    memory = psutil.virtual_memory()
    now = datetime.now().astimezone()
    return {
        "date": now.date().isoformat(),
        "created_at": now.isoformat(timespec="seconds"),
        "system": platform.system(),
        "release": platform.release(),
        "platform": platform.platform(),
        "machine": platform.machine(),
        "processor": platform.processor(),
        "cpu_model": cpu_model(),
        "cpu_count_logical": os.cpu_count() or 1,
        "cpu_count_physical": psutil.cpu_count(logical=False),
        "memory_total_bytes": int(memory.total),
        "python": platform.python_version(),
        "suiyi_engine": __version__,
        "ctranslate2": _package_version("ctranslate2"),
        "sentencepiece": _package_version("sentencepiece"),
        "psutil": _package_version("psutil"),
        "host": options.host,
        "python_executable": options.python,
    }


def cpu_model() -> str:
    """尽量读到 CPU 型号。Linux 读 ``/proc/cpuinfo``，Windows 用 ``PROCESSOR_IDENTIFIER``。"""

    if sys.platform == "linux":
        try:
            text = Path("/proc/cpuinfo").read_text(encoding="utf-8", errors="replace")
        except OSError:
            return platform.processor() or "unknown"
        for line in text.splitlines():
            if line.lower().startswith("model name"):
                return line.split(":", 1)[1].strip() or "unknown"
        return platform.processor() or "unknown"
    if sys.platform == "win32":
        identified = os.environ.get("PROCESSOR_IDENTIFIER", "").strip()
        return identified or platform.processor() or "unknown"
    if sys.platform == "darwin":
        try:
            output = subprocess.check_output(
                ["sysctl", "-n", "machdep.cpu.brand_string"],
                text=True,
                timeout=2,
            )
        except (OSError, subprocess.SubprocessError):
            return platform.processor() or "unknown"
        return output.strip() or "unknown"
    return platform.processor() or "unknown"


def require_psutil() -> object:
    try:
        import psutil
    except ImportError as exc:
        raise BenchError('缺少 psutil。请安装基准依赖：pip install -e "engine[bench]"') from exc
    return psutil


def load_mvp_models(manifest_path: Path) -> list[dict[str, str]]:
    try:
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise BenchError(f"无法读取模型清单：{exc}") from exc
    models = payload.get("models") if isinstance(payload, dict) else None
    if not isinstance(models, list):
        raise BenchError("模型清单缺少 models 数组")
    mvp: list[dict[str, str]] = []
    for item in models:
        if not isinstance(item, dict) or item.get("tier") != "mvp":
            continue
        model_id = item.get("id")
        src = item.get("src")
        tgt = item.get("tgt")
        if not all(isinstance(value, str) and value for value in (model_id, src, tgt)):
            raise BenchError(f"mvp 模型条目不完整：{item!r}")
        mvp.append({"id": str(model_id), "src": str(src), "tgt": str(tgt)})
    if not mvp:
        raise BenchError("清单里没有 tier=mvp 的模型")
    return mvp


def missing_model_ids(models_dir: Path, mvp: Sequence[Mapping[str, str]]) -> list[str]:
    missing: list[str] = []
    for model in mvp:
        if not (models_dir / model["id"] / "model.bin").is_file():
            missing.append(model["id"])
    return missing


def model_ids_for_pairs(
    mvp: Sequence[Mapping[str, str]],
    pairs: Sequence[tuple[str, str]],
) -> set[str]:
    found: set[str] = set()
    for src, tgt in pairs:
        matches = [model["id"] for model in mvp if model["src"] == src and model["tgt"] == tgt]
        if len(matches) != 1:
            raise BenchError(f"清单中 {src}→{tgt} 的 mvp 模型不是唯一的：{matches}")
        found.add(matches[0])
    return found


def start_server(
    options: BenchOptions,
    *,
    log_path: Path,
    preload: str,
    intra_threads: int | None = None,
    beam_size: int | None = None,
    max_batch_size: int | None = None,
) -> RunningServer:
    port = _free_port()
    command = build_serve_command(
        python=options.python,
        models_dir=options.models_dir,
        host=options.host,
        port=port,
        preload=preload,
        intra_threads=intra_threads,
        beam_size=beam_size,
        max_batch_size=max_batch_size,
    )
    log_path.parent.mkdir(parents=True, exist_ok=True)
    handle = log_path.open("wb")
    env = os.environ.copy()
    env["PYTHONUNBUFFERED"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    popen_kwargs: dict[str, object] = {
        "stdin": subprocess.DEVNULL,
        "stdout": handle,
        "stderr": subprocess.STDOUT,
        "env": env,
        "cwd": str(repo_root()),
    }
    kill_group = False
    if sys.platform == "win32":
        popen_kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        popen_kwargs["start_new_session"] = True
        kill_group = True
    started = time.perf_counter()
    try:
        proc = subprocess.Popen(command, **popen_kwargs)
    except OSError as exc:
        handle.close()
        raise BenchError(f"无法启动服务进程：{exc}") from exc
    return RunningServer(
        proc=proc,
        log_path=log_path,
        log_handle=handle,
        host=options.host,
        port=port,
        started=started,
        kill_group=kill_group,
    )


def stop_process(proc: subprocess.Popen[bytes], *, kill_group: bool = False) -> None:
    """结束子进程。POSIX 上若以新会话启动，则向整个进程组发信号。"""

    if proc.poll() is not None:
        return
    _signal_process(proc, kill_group=kill_group, terminate=True)
    try:
        proc.wait(timeout=10)
        return
    except subprocess.TimeoutExpired:
        _signal_process(proc, kill_group=kill_group, terminate=False)
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        return


def wait_until_ready(
    server: RunningServer,
    expected_ids: set[str],
    timeout_s: float,
) -> float:
    """返回从进程启动到服务就绪的毫秒数。"""

    deadline = server.started + timeout_s
    health = f"{server.base_url}/health"
    while time.perf_counter() < deadline:
        if server.proc.poll() is not None:
            tail = read_tail(server.log_path)
            raise BenchError(f"服务进程提前退出，code={server.proc.returncode}\n{tail}")
        payload = try_get_json(health, timeout=0.5)
        if payload is not None and service_is_ready(payload, expected_ids):
            return (time.perf_counter() - server.started) * 1000.0
        time.sleep(_HEALTH_POLL_S)
    tail = read_tail(server.log_path)
    expected = ", ".join(sorted(expected_ids))
    raise BenchError(f"等待 /health 超时（{timeout_s:.0f}s），期望模型 {expected}\n{tail}")


def try_get_json(url: str, timeout: float) -> dict[str, object] | None:
    request = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, OSError):
        return None
    if not isinstance(payload, dict):
        return None
    return payload


def post_translate(
    url: str,
    text: str,
    source: str,
    target: str,
    timeout: float,
) -> tuple[float, dict[str, object]]:
    """客户端计时的一次 ``POST /translate``。失败时抛 :class:`BenchError`。"""

    raw = json.dumps(
        {"text": text, "source": source, "target": target},
        ensure_ascii=False,
    ).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=raw,
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload_bytes = response.read()
            status = int(response.status)
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:500]
        raise BenchError(f"POST /translate HTTP {exc.code}：{detail}") from exc
    except urllib.error.URLError as exc:
        raise BenchError(f"POST /translate 失败：{exc.reason}") from exc
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    if status != 200:
        raise BenchError(f"POST /translate HTTP {status}")
    try:
        payload = json.loads(payload_bytes.decode("utf-8"))
    except json.JSONDecodeError as exc:
        raise BenchError("POST /translate 的响应不是 JSON") from exc
    if not isinstance(payload, dict) or not isinstance(payload.get("text"), str):
        raise BenchError("POST /translate 的响应缺少 text")
    if payload["text"] == "" and text.strip():
        raise BenchError(f"{source}→{target} 的译文为空")
    return elapsed_ms, payload


def measure_case(
    base_url: str,
    case: BenchCase,
    *,
    warmup: int,
    repeats: int,
    timeout: float,
) -> dict[str, object]:
    translate_url = f"{base_url}/translate"
    sample_body: dict[str, object] = {}
    for _ in range(warmup):
        _, sample_body = post_translate(translate_url, case.text, case.src, case.tgt, timeout)
    samples: list[float] = []
    for _ in range(repeats):
        elapsed_ms, sample_body = post_translate(
            translate_url,
            case.text,
            case.src,
            case.tgt,
            timeout,
        )
        samples.append(elapsed_ms)
    _check_route(case, sample_body)
    stats = summarize(samples)
    sample_text = str(sample_body.get("text", ""))
    if len(sample_text) > 180:
        sample_text = sample_text[:180] + "…"
    return {
        "case_id": case.case_id,
        "label": case.label,
        "src": case.src,
        "tgt": case.tgt,
        "kind": case.kind,
        "size_label": _size_label(case),
        "han_chars": han_count(case.text),
        "words": word_count(case.text),
        "chars": len(case.text),
        "sentences": len(split_sentences(case.text, lang=case.src)),
        "warmup": warmup,
        "n": stats["n"],
        "p50_ms": round(float(stats["p50_ms"]), 3),
        "p95_ms": round(float(stats["p95_ms"]), 3),
        "max_ms": round(float(stats["max_ms"]), 3),
        "min_ms": round(float(stats["min_ms"]), 3),
        "samples_ms": [round(value, 3) for value in samples],
        "route": list(sample_body.get("route", []))
        if isinstance(sample_body.get("route"), list)
        else [],
        "sample_text": sample_text,
    }


def sample_rss(pid: int, *, samples: int = 5, interval_s: float = 0.1) -> int:
    if samples < 1:
        raise BenchError("RSS 采样次数必须 >= 1")
    values: list[int] = []
    for index in range(samples):
        values.append(rss_bytes(pid))
        if index + 1 < samples:
            time.sleep(interval_s)
    return max(values)


def rss_bytes(pid: int) -> int:
    psutil = require_psutil()
    try:
        proc = psutil.Process(pid)
        total = int(proc.memory_info().rss)
        for child in proc.children(recursive=True):
            try:
                total += int(child.memory_info().rss)
            except psutil.NoSuchProcess:
                continue
    except psutil.Error as exc:
        raise BenchError(f"读取 RSS 失败：{exc}") from exc
    return total


def read_tail(path: Path, limit: int = 2000) -> str:
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""
    if len(text) <= limit:
        return text
    return text[-limit:]


def _measure_cold_starts(options: BenchOptions, bilingual_ids: set[str]) -> list[float]:
    runs: list[float] = []
    for index in range(options.cold_starts):
        print(f"冷启动 {index + 1}/{options.cold_starts}", flush=True)
        log_path = options.out_dir / "logs" / f"cold-{index + 1}.log"
        server = start_server(options, log_path=log_path, preload=PRELOAD_PAIRS)
        try:
            elapsed_ms = wait_until_ready(server, bilingual_ids, options.cold_timeout_s)
            _expect_decode_line(
                server.log_path,
                intra_threads=default_intra_threads(),
                beam_size=DEFAULT_BEAM_SIZE,
                max_batch_size=DEFAULT_MAX_BATCH_SIZE,
            )
        finally:
            server.stop()
        runs.append(elapsed_ms)
        print(f"  {elapsed_ms:.0f} ms", flush=True)
    print(f"冷启动中位数 {statistics.median(runs):.0f} ms", flush=True)
    return runs


def _measure_rss(
    options: BenchOptions,
    mvp: Sequence[Mapping[str, str]],
    bilingual_ids: set[str],
) -> dict[str, object]:
    print("测量 RSS：空载 → 中英双向 → 全部 mvp", flush=True)
    log_path = options.out_dir / "logs" / "rss.log"
    server = start_server(options, log_path=log_path, preload="")
    try:
        wait_until_ready(server, set(), options.cold_timeout_s)
        idle_health = try_get_json(f"{server.base_url}/health", timeout=2)
        if idle_health is None:
            raise BenchError("空载阶段无法读取 /health")
        idle_loaded = _loaded_ids(idle_health)
        if idle_loaded:
            raise BenchError(f"空载时已经加载了模型：{idle_loaded}")
        idle_bytes = sample_rss(server.proc.pid)
        for model in mvp:
            if model["id"] not in bilingual_ids:
                continue
            post_translate(
                f"{server.base_url}/translate",
                _PROBE_TEXT.get(model["src"], "Hello."),
                model["src"],
                model["tgt"],
                options.request_timeout_s,
            )
        bilingual_health = _wait_loaded(server, bilingual_ids, options.request_timeout_s)
        bilingual_bytes = sample_rss(server.proc.pid)
        for model in mvp:
            if model["id"] in bilingual_ids:
                continue
            post_translate(
                f"{server.base_url}/translate",
                _PROBE_TEXT.get(model["src"], "Hello."),
                model["src"],
                model["tgt"],
                options.request_timeout_s,
            )
        mvp_ids = {model["id"] for model in mvp}
        mvp_health = _wait_loaded(server, mvp_ids, options.request_timeout_s)
        mvp_bytes = sample_rss(server.proc.pid)
    finally:
        server.stop()
    idle = _format_bytes(idle_bytes)
    bilingual = _format_bytes(bilingual_bytes)
    mvp_rss = _format_bytes(mvp_bytes)
    print(f"  空载 {idle}，中英 {bilingual}，全部 mvp {mvp_rss}", flush=True)
    return {
        "idle_bytes": idle_bytes,
        "bilingual_bytes": bilingual_bytes,
        "mvp_bytes": mvp_bytes,
        "idle_loaded": _loaded_ids(idle_health),
        "bilingual_loaded": _loaded_ids(bilingual_health),
        "mvp_loaded": _loaded_ids(mvp_health),
    }


def _measure_latency(
    options: BenchOptions,
    bilingual_ids: set[str],
) -> tuple[dict[str, object], list[dict[str, object]]]:
    print("测量首译与热路径", flush=True)
    log_path = options.out_dir / "logs" / "latency.log"
    server = start_server(options, log_path=log_path, preload=PRELOAD_PAIRS)
    try:
        wait_until_ready(server, bilingual_ids, options.cold_timeout_s)
        short = case_by_id("zh_short")
        elapsed_ms, body = post_translate(
            f"{server.base_url}/translate",
            short.text,
            short.src,
            short.tgt,
            options.request_timeout_s,
        )
        sample_text = str(body.get("text", ""))
        first = {
            "case_id": short.case_id,
            "label": short.label,
            "src": short.src,
            "tgt": short.tgt,
            "elapsed_ms": round(elapsed_ms, 3),
            "sample_text": sample_text[:180],
            "note": "预加载完成后的第一次翻译，不计入热路径",
        }
        print(f"  首译 {elapsed_ms:.0f} ms", flush=True)
        rows: list[dict[str, object]] = []
        for case in CASES:
            print(f"  {case.label} {case.src}→{case.tgt} × {options.repeats}", flush=True)
            row = measure_case(
                server.base_url,
                case,
                warmup=options.warmup,
                repeats=options.repeats,
                timeout=options.request_timeout_s,
            )
            print(
                f"    P50 {float(row['p50_ms']):.0f} ms  P95 {float(row['p95_ms']):.0f} ms",
                flush=True,
            )
            rows.append(row)
    finally:
        server.stop()
    return first, rows


def _measure_thread_grid(options: BenchOptions, bilingual_ids: set[str]) -> list[dict[str, object]]:
    cells: list[dict[str, object]] = []
    short = case_by_id("zh_short")
    for intra in GRID_INTRA_THREADS:
        for beam in GRID_BEAM_SIZES:
            print(f"网格 intra={intra} beam={beam}", flush=True)
            log_path = options.out_dir / "logs" / f"grid-intra{intra}-beam{beam}.log"
            server = start_server(
                options,
                log_path=log_path,
                preload=PRELOAD_PAIRS,
                intra_threads=intra,
                beam_size=beam,
            )
            try:
                wait_until_ready(server, bilingual_ids, options.cold_timeout_s)
                _expect_decode_line(
                    server.log_path,
                    intra_threads=intra,
                    beam_size=beam,
                    max_batch_size=DEFAULT_MAX_BATCH_SIZE,
                )
                measured = measure_case(
                    server.base_url,
                    short,
                    warmup=options.warmup,
                    repeats=options.grid_repeats,
                    timeout=options.request_timeout_s,
                )
            finally:
                server.stop()
            cells.append(
                {
                    "intra_threads": intra,
                    "beam_size": beam,
                    "n": measured["n"],
                    "p50_ms": measured["p50_ms"],
                    "p95_ms": measured["p95_ms"],
                    "max_ms": measured["max_ms"],
                }
            )
            print(f"  P50 {float(measured['p50_ms']):.0f} ms", flush=True)
    return cells


def _measure_batch_grid(options: BenchOptions, bilingual_ids: set[str]) -> list[dict[str, object]]:
    cells: list[dict[str, object]] = []
    paragraph = case_by_id("zh_paragraph")
    for batch_size in GRID_BATCH_SIZES:
        print(f"批量 max_batch_size={batch_size}", flush=True)
        log_path = options.out_dir / "logs" / f"batch-{batch_size}.log"
        server = start_server(
            options,
            log_path=log_path,
            preload=PRELOAD_PAIRS,
            max_batch_size=batch_size,
        )
        try:
            wait_until_ready(server, bilingual_ids, options.cold_timeout_s)
            measured = measure_case(
                server.base_url,
                paragraph,
                warmup=min(options.warmup, 2),
                repeats=options.batch_repeats,
                timeout=options.request_timeout_s,
            )
        finally:
            server.stop()
        cells.append(
            {
                "max_batch_size": batch_size,
                "n": measured["n"],
                "p50_ms": measured["p50_ms"],
                "p95_ms": measured["p95_ms"],
                "max_ms": measured["max_ms"],
            }
        )
    return cells


def _wait_loaded(
    server: RunningServer,
    expected_ids: set[str],
    timeout_s: float,
) -> dict[str, object]:
    deadline = time.perf_counter() + timeout_s
    health = f"{server.base_url}/health"
    while time.perf_counter() < deadline:
        payload = try_get_json(health, timeout=2)
        if payload is not None and service_is_ready(payload, expected_ids):
            return payload
        time.sleep(_HEALTH_POLL_S)
    raise BenchError(f"翻译后模型仍未出现在 /health：{sorted(expected_ids)}")


def _expect_decode_line(
    log_path: Path,
    *,
    intra_threads: int,
    beam_size: int,
    max_batch_size: int,
) -> None:
    expected = (
        f"intra_threads={intra_threads} beam_size={beam_size} max_batch_size={max_batch_size}"
    )
    for _ in range(20):
        if expected in read_tail(log_path, limit=8000):
            return
        time.sleep(0.05)
    raise BenchError(f"启动日志里没有「{expected}」\n{read_tail(log_path)}")


def _loaded_ids(payload: Mapping[str, object]) -> list[str]:
    loaded = payload.get("loaded_models")
    if not isinstance(loaded, list):
        return []
    return sorted(str(item) for item in loaded)


def _check_route(case: BenchCase, body: Mapping[str, object]) -> None:
    route = body.get("route")
    if not isinstance(route, list) or not route:
        raise BenchError(f"{case.label} 的 route 为空：{body!r}"[:500])
    if case.kind == "pivot" and len(route) != 2:
        raise BenchError(f"{case.label} 应走两段中转，实际 route={route}")


def _case_payload(case: BenchCase) -> dict[str, object]:
    return {
        "case_id": case.case_id,
        "label": case.label,
        "src": case.src,
        "tgt": case.tgt,
        "kind": case.kind,
        "text": case.text,
        "han_chars": han_count(case.text),
        "words": word_count(case.text),
        "chars": len(case.text),
        "sentences": len(split_sentences(case.text, lang=case.src)),
    }


def _size_label(case: BenchCase) -> str:
    if case.case_id == "en_short":
        return f"{word_count(case.text)} 词"
    if case.src == "ja":
        return f"{len(case.text)} 字符"
    return f"{han_count(case.text)} 字"


def _verdict(
    metric: str,
    actual: float,
    target: float,
    unit: str,
    metric_id: str,
) -> dict[str, object]:
    passed = actual <= target
    if unit == "bytes":
        actual_display = _format_bytes(int(actual))
        target_display = _format_bytes(int(target))
    else:
        actual_display = f"{actual:.1f} ms"
        target_display = f"{target:.0f} ms"
    return {
        "id": metric_id,
        "metric": metric,
        "actual": actual,
        "target": target,
        "unit": unit,
        "passed": passed,
        "mark": "达标" if passed else "未达标",
        "actual_display": actual_display,
        "target_display": f"≤ {target_display}",
    }


def _latency_stat(
    rows: Mapping[str, Mapping[str, object]],
    case_id: str,
    key: str,
) -> float:
    row = rows.get(case_id)
    if row is None or key not in row:
        raise BenchError(f"延迟结果缺少 {case_id}.{key}")
    return float(row[key])


def _suggestion(
    intra_threads: int,
    beam_size: int,
    max_batch_size: int,
    changed: bool,
    reasons: dict[str, str],
) -> dict[str, object]:
    return {
        "intra_threads": intra_threads,
        "beam_size": beam_size,
        "max_batch_size": max_batch_size,
        "inter_threads": DEFAULT_INTER_THREADS,
        "changed": changed,
        "reasons": reasons,
    }


def _find_cell(
    cells: Sequence[Mapping[str, object]],
    intra_threads: int,
    beam_size: int,
) -> Mapping[str, object] | None:
    for cell in cells:
        if (
            _cell_int(cell, "intra_threads") == intra_threads
            and _cell_int(cell, "beam_size") == beam_size
        ):
            return cell
    return None


def _find_batch(
    cells: Sequence[Mapping[str, object]],
    max_batch_size: int,
) -> Mapping[str, object] | None:
    for cell in cells:
        if _cell_int(cell, "max_batch_size") == max_batch_size:
            return cell
    return None


def _cell_int(cell: Mapping[str, object], key: str) -> int:
    value = cell[key]
    if isinstance(value, bool) or not isinstance(value, int):
        raise BenchError(f"网格字段 {key} 不是整数")
    return value


def _cell_float(cell: Mapping[str, object], key: str) -> float:
    value = cell[key]
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise BenchError(f"网格字段 {key} 不是数字")
    return float(value)


def _as_dict(value: object) -> dict[str, object]:
    if not isinstance(value, dict):
        raise BenchError("报告结构损坏")
    return value


def _as_list(value: object) -> list[object]:
    if not isinstance(value, list):
        raise BenchError("报告结构损坏")
    return value


def _join_ids(value: object) -> str:
    if not isinstance(value, list) or not value:
        return "（无）"
    return ", ".join(str(item) for item in value)


def _format_bytes(value: int) -> str:
    mib = value / (1024 * 1024)
    return f"{mib:.1f} MiB（{value} 字节）"


def _ceil_to(value: float, step: int) -> int:
    return int(math.ceil(value / step) * step)


def _package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "未安装"


def _free_port() -> int:
    import socket

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def _signal_process(proc: subprocess.Popen[bytes], *, kill_group: bool, terminate: bool) -> None:
    # Windows 的 signal 模块没有 SIGKILL。进程组信号只在 POSIX 上使用。
    if kill_group and sys.platform != "win32":
        sig = signal.SIGTERM if terminate else signal.SIGKILL
        try:
            os.killpg(proc.pid, sig)
            return
        except OSError:
            pass
    if terminate:
        proc.terminate()
    else:
        proc.kill()


def _require_positive_arg(name: str, value: int) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        raise BenchError(f"--{name} 必须是 >= 1 的整数")


def _require_non_negative_arg(name: str, value: int) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise BenchError(f"--{name} 必须是 >= 0 的整数")


def _configure_stdio() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except (OSError, ValueError):
            continue
