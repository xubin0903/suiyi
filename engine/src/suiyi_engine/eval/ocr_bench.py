"""OCR 评测与性能基线的编排（#54）。命令行入口：``scripts/eval_ocr.py``。

- 每个检测模型（det）变体在**独立子进程**里跑，内存互不干扰：冷加载耗时、首次识别、
  每张样例 ``warmup`` 次不计时 + ``repeats`` 次计时；内存按 #74 口径：**单次请求峰值增量**
  （相对 OCR 已预热、空闲时的进程）与**常驻**（空闲 RSS、跑完全部样例后的增长）。
- 主进程拿子进程返回的文本行，在本进程里重新做段落合并并打分：CER（按类别/语种/尺寸）、
  段落切分（误合并 / 误拆分），以及 ``line_gap`` 扫描（只重跑合并，不重跑模型）。
- 输出 ``report.md`` 与 ``report.json``（默认 ``reports/ocr-eval-<时间>/``，已被 .gitignore）。

计时范围是 ``OcrEngine.recognize``：解码 PNG + 检测 + 识别 + 段落合并，不含读文件和翻译。
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import subprocess
import sys
import tempfile
import threading
import time
from collections.abc import Callable, Iterable, Mapping, Sequence
from dataclasses import asdict, replace
from datetime import datetime
from importlib.metadata import PackageNotFoundError, version
from pathlib import Path
from typing import Any

from suiyi_engine import __version__
from suiyi_engine.eval.metrics import percentile
from suiyi_engine.eval.ocr_metrics import (
    CerScore,
    SegmentationScore,
    cer,
    merge_cer,
    merge_segmentation,
    segmentation,
)
from suiyi_engine.tools.ocr_models import repo_root_from_here

__all__ = ["main"]

SIZE_CLASSES = ("small", "720p", "1080p")
SIZE_LABELS = {"small": "400×150", "720p": "1280×720", "1080p": "1920×1080"}
TARGET_P95_MS = {"small": 800.0, "720p": 800.0, "1080p": 1500.0}
TARGET_CER = {"中文网页正文": 0.05, "英文文档": 0.05, "日文网页横排": 0.10}
TARGET_REQUEST_PEAK_MB = 400.0
"""单次 OCR 请求峰值内存增量上限（相对预热后空闲进程，#74 负责人拍板，取代 #54 的 300 MB）。"""
TARGET_DENSE_1080_P95_MS = 1500.0
DENSE_CATEGORY = "密集文字"
BASELINE_EXACT = 23
"""段落完全正确数下限（原 32 张 = dev 集）：#74 调优后的 23/32，#75 要求不低于它。"""
TARGET_SPLITS = 1
"""误拆分上限（dev 集，#75 新增验收）。"""
FULL_SAMPLE_COUNT = 32
"""dev 集（原 32 张）样例数；只有跑完整 dev 集时才对照上面两个目标。"""
SPLITS = ("dev", "holdout")
"""样例划分：dev 调参可看；holdout 是 #75 的留出集，调参时不看，报告里单独汇总。"""
DET_ALIASES = {
    "small": "PP-OCRv6_det_small",
    "tiny": "PP-OCRv6_det_tiny",
    "medium": "PP-OCRv6_det_medium",
}
DEFAULT_DETS = "small,tiny"
DEFAULT_LINE_GAPS = (0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.2)
QUICK_IDS = (
    "zh_ui_small_01",
    "en_doc_small_01",
    "ja_ui_small_01",
    "zh_web_720_01",
    "ja_vertical_720_01",
    "zh_ui_1080_01",
)
"""``--quick``：6 张，覆盖三种尺寸、中英日与竖排，给 CI 冒烟用。"""

UI_MARK = "UI 小字"
BODY_CATEGORIES = ("中文网页正文", "英文文档", "日文网页横排", "双栏", "密集文字", "中英混排")

Log = Callable[[str], None]


class OcrEvalError(Exception):
    def __init__(self, message: str, code: int = 1) -> None:
        super().__init__(message)
        self.code = code


# ---------------------------------------------------------------- 命令行


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="eval_ocr.py",
        description="OCR 评测与性能基线：CER、段落切分、P50/P95 耗时、内存，对比检测模型。",
        epilog="说明与 Windows 补测步骤见 docs/engine/OCR评测.md。",
    )
    parser.add_argument(
        "--models-dir",
        type=Path,
        default=None,
        help="模型根目录（OCR 模型在其下 ocr/）；默认 SUIYI_MODELS_DIR，否则仓库根 models/",
    )
    parser.add_argument("--manifest", type=Path, default=None, help="OCR 模型清单")
    parser.add_argument("--samples", type=Path, default=None, help="样例清单 samples.json")
    parser.add_argument(
        "--det",
        default=DEFAULT_DETS,
        help=(
            "逗号分隔的检测模型（small/tiny/medium 或清单 id），第一个视为当前默认；"
            f"默认 {DEFAULT_DETS}"
        ),
    )
    parser.add_argument("--repeats", type=int, default=5, help="每张样例计时次数（默认 5）")
    parser.add_argument("--warmup", type=int, default=1, help="每张样例不计时的预热次数（默认 1）")
    parser.add_argument(
        "--threads", type=int, default=None, help="onnxruntime 线程数（默认 min(4, CPU 数)）"
    )
    parser.add_argument("--only", default=None, help="只跑这些样例 id（逗号分隔）")
    parser.add_argument(
        "--split",
        choices=("all", *SPLITS),
        default="all",
        help="只跑某个划分：dev（原 32 张）/ holdout（留出集）；默认 all",
    )
    parser.add_argument(
        "--quick",
        action="store_true",
        help=f"快速子集：{len(QUICK_IDS)} 张样例、repeats=2、warmup=1（CI 冒烟用）",
    )
    parser.add_argument(
        "--line-gaps",
        default=",".join(str(g) for g in DEFAULT_LINE_GAPS),
        help="段落合并 line_gap 扫描值（逗号分隔）",
    )
    parser.add_argument(
        "--label", default="", help="机器说明，写进报告（例如「Windows 笔记本 i5-1135G7」）"
    )
    parser.add_argument(
        "--out", type=Path, default=None, help="输出目录（默认 reports/ocr-eval-<时间>）"
    )
    parser.add_argument("--worker", type=Path, default=None, help=argparse.SUPPRESS)
    parser.add_argument("--worker-out", type=Path, default=None, help=argparse.SUPPRESS)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    _configure_stdio()
    args = build_parser().parse_args(argv)
    if args.worker is not None:
        return run_worker(args.worker, args.worker_out)
    try:
        return run(args, log=_stderr)
    except OcrEvalError as exc:
        _stderr(f"错误：{exc}")
        return exc.code


def run(args: argparse.Namespace, *, log: Log) -> int:
    from suiyi_engine.tools.ocr_models import (
        default_manifest_path,
        load_manifest,
        resolve_models_dir,
    )

    if args.repeats < 1 or args.warmup < 0:
        raise OcrEvalError("--repeats 至少为 1，--warmup 不能为负", code=2)
    repeats, warmup = (
        (min(args.repeats, 2), min(args.warmup, 1)) if args.quick else (args.repeats, args.warmup)
    )
    samples_path = (args.samples or default_samples_path()).resolve()
    samples = load_samples(samples_path)
    only = _split(args.only) or (list(QUICK_IDS) if args.quick else None)
    samples = select_samples(samples, only)
    if args.split != "all":
        samples = [s for s in samples if s["split"] == args.split]
        if not samples:
            raise OcrEvalError(f"划分 {args.split} 里没有选中的样例", code=2)
    line_gaps = parse_floats(args.line_gaps)
    manifest_path = (args.manifest or default_manifest_path()).resolve()
    manifest = load_manifest(manifest_path)
    models_dir = resolve_models_dir(args.models_dir)
    dets = [resolve_det(token, manifest.models) for token in _split(args.det) or [DEFAULT_DETS]]
    if not dets:
        raise OcrEvalError("--det 为空", code=2)

    started = datetime.now().astimezone()
    out_dir = (
        args.out or repo_root_from_here() / "reports" / f"ocr-eval-{started:%Y%m%d-%H%M%S}"
    ).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    log(f"样例 {len(samples)} 张，检测模型 {', '.join(dets)}，repeats={repeats} warmup={warmup}")

    variants: list[dict[str, Any]] = []
    for det in dets:
        log(f"— 运行 {det} …")
        raw = run_variant_subprocess(
            det=det,
            samples=samples,
            samples_dir=samples_path.parent,
            models_dir=models_dir,
            manifest_path=manifest_path,
            repeats=repeats,
            warmup=warmup,
            threads=args.threads,
            log=log,
        )
        variants.append(score_variant(raw, samples, line_gaps))
        if raw.get("ok"):
            log(
                f"  完成：加载 {raw['load_ms']:.0f} ms，"
                f"单请求峰值增量最大 {_mb(_max_peak(raw['samples'])):.0f} MB"
            )
        else:
            log(f"  失败：{raw.get('error')}")

    report = {
        "schema_version": 1,
        "generated_at": started.isoformat(timespec="seconds"),
        "label": args.label,
        "quick": bool(args.quick),
        "settings": {
            "repeats": repeats,
            "warmup": warmup,
            "threads": args.threads,
            "line_gaps": line_gaps,
            "samples": str(samples_path),
            "sample_count": len(samples),
            "split": args.split,
            "dev_count": sum(1 for s in samples if s["split"] == "dev"),
            "holdout_count": sum(1 for s in samples if s["split"] == "holdout"),
            "models_dir": str(models_dir),
            "manifest": str(manifest_path),
            "recommended": dict(manifest.recommended),
        },
        "environment": collect_environment(),
        "targets": {
            "p95_ms": TARGET_P95_MS,
            "dense_1080p_p95_ms": TARGET_DENSE_1080_P95_MS,
            "cer": TARGET_CER,
            "request_peak_delta_mb": TARGET_REQUEST_PEAK_MB,
            "baseline_exact": BASELINE_EXACT,
            "max_splits": TARGET_SPLITS,
        },
        "variants": variants,
    }
    json_path = out_dir / "report.json"
    md_path = out_dir / "report.md"
    json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    md_path.write_text(render_markdown(report), encoding="utf-8")
    print(f"报告：{md_path}")
    print(f"JSON：{json_path}")
    if not any(v["ok"] for v in variants):
        return 1
    return 0 if variants[0]["ok"] else 1


# ---------------------------------------------------------------- 样例


def default_samples_path() -> Path:
    return repo_root_from_here() / "engine" / "eval" / "ocr_samples" / "samples.json"


def load_samples(path: Path) -> list[dict[str, Any]]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise OcrEvalError(f"找不到样例清单：{path}", code=2) from exc
    except json.JSONDecodeError as exc:
        raise OcrEvalError(f"无法解析样例清单 {path}：{exc}", code=2) from exc
    rows = data.get("samples") if isinstance(data, dict) else None
    if not isinstance(rows, list) or not rows:
        raise OcrEvalError(f"样例清单里没有 samples：{path}", code=2)
    seen: set[str] = set()
    for row in rows:
        for key in ("id", "file", "lang", "category", "size_class"):
            if not isinstance(row.get(key), str) or not row[key]:
                raise OcrEvalError(f"样例缺少字段 {key}：{row.get('id', row)}", code=2)
        if row["id"] in seen:
            raise OcrEvalError(f"样例 id 重复：{row['id']}", code=2)
        seen.add(row["id"])
        paragraphs = row.get("paragraphs")
        if (
            not isinstance(paragraphs, list)
            or not paragraphs
            or not all(isinstance(p, str) and p for p in paragraphs)
        ):
            raise OcrEvalError(f"样例 {row['id']} 的 paragraphs 必须是非空字符串列表", code=2)
        row.setdefault("split", "dev")
        if row["split"] not in SPLITS:
            raise OcrEvalError(f"样例 {row['id']} 的 split 必须是 {SPLITS} 之一", code=2)
        if row["size_class"] not in SIZE_CLASSES:
            raise OcrEvalError(f"样例 {row['id']} 的 size_class 不在 {SIZE_CLASSES}", code=2)
        if not (path.parent / row["file"]).is_file():
            raise OcrEvalError(f"样例图片不存在：{path.parent / row['file']}", code=2)
    return rows


def select_samples(
    samples: Sequence[dict[str, Any]], only: Sequence[str] | None
) -> list[dict[str, Any]]:
    if not only:
        return list(samples)
    by_id = {s["id"]: s for s in samples}
    missing = [i for i in only if i not in by_id]
    if missing:
        raise OcrEvalError(f"未知样例 id：{', '.join(missing)}", code=2)
    return [by_id[i] for i in only]


VARIANT_FLAGS = "legacy、rb<N>、mp、nomp、dil、nodil、sh<X>、pad<X>"


def resolve_det(token: str, models: Mapping[str, Any]) -> str:
    """变体写法：``检测模型[@长边上限][+开关...]``，返回规范化的变体名。

    不带任何开关时使用引擎默认运行参数（:data:`suiyi_engine.ocr.engine.DEFAULT_RUNTIME`）。

    - 检测模型：别名 small/tiny/medium 或清单 id，例如 ``small`` → ``PP-OCRv6_det_small``；
    - ``@N``：检测长边上限 N 像素（``@0`` = 不缩放，RapidOCR 3.9 原行为）；
    - ``+legacy``：#74 之前的全部默认（不缩放、识别批 6、memory pattern 开、膨胀开），
      其余开关叠加其上；
    - ``+rb<N>``：识别批大小；``+mp`` / ``+nomp``：开/关 onnxruntime memory pattern；
    - ``+dil`` / ``+nodil``：检测后处理膨胀开/关（默认：缩放时关）；
    - ``+sh<X>``：缩放补偿 ``box_shrink``（检测图像素，``+sh0`` = 不补偿）；
    - ``+pad<X>``：识别裁切外扩 ``crop_pad``（检测图像素，``+pad0`` = 不外扩）。
    """

    head, *flags = token.split("+")
    name, _sep, limit = head.partition("@")
    det = DET_ALIASES.get(name, name)
    model = models.get(det)
    if model is None or model.task != "det":
        known = ", ".join(sorted(k for k, m in models.items() if m.task == "det"))
        raise OcrEvalError(
            f"未知检测模型 {token!r}，可选别名 small/tiny/medium 或：{known}", code=2
        )
    variant = det
    if limit:
        if not limit.isdigit() or (int(limit) != 0 and int(limit) < 320):
            raise OcrEvalError(f"检测长边上限必须是 0 或 ≥320 的整数：{token!r}", code=2)
        variant += f"@{int(limit)}"
    for flag in flags:
        rb = flag.startswith("rb") and flag[2:].isdigit() and int(flag[2:]) >= 1
        sh = flag.startswith("sh") and _is_nonneg_float(flag[2:])
        pad = flag.startswith("pad") and _is_nonneg_float(flag[3:])
        if flag in ("legacy", "mp", "nomp", "dil", "nodil") or rb or sh or pad:
            variant += f"+{flag}"
        else:
            raise OcrEvalError(f"未知变体开关 {flag!r}（可用 {VARIANT_FLAGS}）：{token!r}", code=2)
    return variant


def _is_nonneg_float(raw: str) -> bool:
    try:
        return float(raw) >= 0
    except ValueError:
        return False


def split_variant(variant: str) -> tuple[str, dict[str, Any]]:
    """``PP-OCRv6_det_small@960+rb1+nomp`` → ``("PP-OCRv6_det_small", {...})``。

    返回的字典是相对基准的覆盖项；``legacy`` 键为真时基准是 #74 之前的行为。
    """

    head, *flags = variant.split("+")
    det, _sep, limit = head.partition("@")
    options: dict[str, Any] = {}
    if limit:
        options["det_max_side"] = int(limit) or None
    for flag in flags:
        if flag == "legacy":
            options["legacy"] = True
        elif flag in ("mp", "nomp"):
            options["mem_pattern"] = flag == "mp"
        elif flag in ("dil", "nodil"):
            options["det_dilation"] = flag == "dil"
        elif flag.startswith("rb") and flag[2:].isdigit():
            options["rec_batch"] = int(flag[2:])
        elif flag.startswith("sh") and _is_nonneg_float(flag[2:]):
            options["box_shrink"] = float(flag[2:])
        elif flag.startswith("pad") and _is_nonneg_float(flag[3:]):
            options["crop_pad"] = float(flag[3:])
        else:
            raise OcrEvalError(f"变体 {variant!r} 含未知标记 {flag!r}", code=2)
    return det, options


def runtime_for(options: Mapping[str, Any]) -> Any:
    """覆盖项 → :class:`~suiyi_engine.ocr.engine.OcrRuntimeOptions`。"""

    from suiyi_engine.ocr.engine import DEFAULT_RUNTIME, LEGACY_RUNTIME

    overrides = {k: v for k, v in options.items() if k != "legacy"}
    base = LEGACY_RUNTIME if options.get("legacy") else DEFAULT_RUNTIME
    return replace(base, **overrides)


def parse_floats(raw: str) -> list[float]:
    try:
        values = [float(x) for x in _split(raw)]
    except ValueError as exc:
        raise OcrEvalError(f"无法解析数值列表：{raw!r}", code=2) from exc
    if not values or any(v <= 0 for v in values):
        raise OcrEvalError(f"line_gap 必须为正数：{raw!r}", code=2)
    return values


# ---------------------------------------------------------------- 子进程


def run_variant_subprocess(
    *,
    det: str,
    samples: Sequence[dict[str, Any]],
    samples_dir: Path,
    models_dir: Path,
    manifest_path: Path,
    repeats: int,
    warmup: int,
    threads: int | None,
    log: Log,
) -> dict[str, Any]:
    config = {
        "det": det,
        "models_dir": str(models_dir),
        "manifest": str(manifest_path),
        "repeats": repeats,
        "warmup": warmup,
        "threads": threads,
        "samples": [
            {"id": s["id"], "path": str(samples_dir / s["file"]), "size_class": s["size_class"]}
            for s in samples
        ],
    }
    with tempfile.TemporaryDirectory(prefix="suiyi-ocr-eval-") as tmp:
        config_path = Path(tmp) / "config.json"
        out_path = Path(tmp) / "out.json"
        config_path.write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
        env = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
        cmd = [
            sys.executable,
            "-m",
            "suiyi_engine.eval.ocr_bench",
            "--worker",
            str(config_path),
            "--worker-out",
            str(out_path),
        ]
        proc = subprocess.run(cmd, env=env, check=False)
        if out_path.is_file():
            return json.loads(out_path.read_text(encoding="utf-8"))
        return {"det": det, "ok": False, "error": f"子进程退出码 {proc.returncode}，没有输出"}


def run_worker(config_path: Path, out_path: Path | None) -> int:
    """子进程：加载一个 det 变体，逐张计时，把原始结果写成 JSON。"""

    config = json.loads(Path(config_path).read_text(encoding="utf-8"))
    result: dict[str, Any] = {"det": config["det"], "ok": False}
    try:
        result.update(_worker_body(config))
        result["ok"] = True
    except Exception as exc:  # noqa: BLE001 - 子进程把任何失败带回主进程写进报告
        result["error"] = f"{type(exc).__name__}: {exc}"
    if out_path is not None:
        Path(out_path).write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    return 0 if result["ok"] else 1


def _worker_body(config: Mapping[str, Any]) -> dict[str, Any]:
    import gc

    import numpy  # noqa: F401 - 计入基线：解码图片本来就需要
    from PIL import Image  # noqa: F401

    from suiyi_engine.ocr.engine import OcrEngine
    from suiyi_engine.tools.ocr_models import load_manifest

    det, options = split_variant(config["det"])
    runtime = runtime_for(options)
    manifest = load_manifest(Path(config["manifest"]))
    recommended = dict(manifest.recommended)
    recommended["det"] = det
    manifest = replace(manifest, recommended=recommended)
    images = [(s, Path(s["path"]).read_bytes()) for s in config["samples"]]

    process_base = current_rss_bytes()
    engine = OcrEngine(
        Path(config["models_dir"]),
        manifest=manifest,
        threads=config.get("threads"),
        runtime=runtime,
    )
    engine.check()
    t0 = time.perf_counter()
    engine.load()
    load_ms = 1000 * (time.perf_counter() - t0)
    after_load = current_rss_bytes()
    t0 = time.perf_counter()
    engine.warmup()
    first_ms = 1000 * (time.perf_counter() - t0)
    gc.collect()
    idle = current_rss_bytes()  # 预热后空闲：单请求峰值增量的基准
    meter = PeakMeter()

    rows: list[dict[str, Any]] = []
    total = len(images)
    for index, (sample, data) in enumerate(images, start=1):
        for _ in range(int(config["warmup"])):
            engine.recognize(data)
        times: list[float] = []
        peaks: list[int] = []
        stages: dict[str, float] = {}
        result = None
        for _ in range(int(config["repeats"])):
            meter.start()
            result = engine.recognize(data)
            peak = meter.stop()
            times.append(result.elapsed_ms)
            if peak is not None and idle is not None:
                peaks.append(peak - idle)
            for key, value in result.stats.items():
                stages[key] = stages.get(key, 0.0) + value
        assert result is not None
        n = len(times)
        rows.append(
            {
                "id": sample["id"],
                "size_class": sample["size_class"],
                "width": result.width,
                "height": result.height,
                "times_ms": [round(t, 2) for t in times],
                "request_peak_delta": peaks,
                "stages_mean_ms": {k: round(v / n, 2) for k, v in stages.items()},
                "lines": [line.to_dict() for line in result.lines],
                "rss_after": current_rss_bytes(),
            }
        )
        worst = f"，单请求峰值增量 {_mb(max(peaks)):.0f} MB" if peaks else ""
        print(
            f"  [{index}/{total}] {sample['id']}: "
            f"中位 {percentile(times, 50):.0f} ms，{len(result.lines)} 行{worst}",
            file=sys.stderr,
            flush=True,
        )
    gc.collect()
    final = current_rss_bytes()
    return {
        "threads": engine.threads,
        "runtime": asdict(runtime),
        "load_ms": round(load_ms, 1),
        "first_ms": round(first_ms, 1),
        "peak_method": meter.method,
        "rss": {
            "process_base": process_base,
            "after_load": after_load,
            "idle": idle,
            "final": final,
            "lifetime_peak": peak_rss_bytes(),
            "model_delta": _delta(idle, process_base),
            "resident_delta": _delta(final, idle),
        },
        "samples": rows,
    }


class PeakMeter:
    """单次请求期间的峰值 RSS。

    - Linux：请求前向 ``/proc/self/clear_refs`` 写 ``5`` 把 ``VmHWM`` 重置为当前 RSS，
      请求后读 ``VmHWM``（精确）；
    - 其他平台：后台线程每 2 ms 用 psutil 采样 RSS 取最大值（近似，可能漏掉极短的尖峰；
      Windows 的计时精度约 15 ms）。Windows 另读进程峰值工作集 ``peak_wset``：请求期间它涨了，
      说明请求峰值就是新的进程峰值，取这个精确值；``peak_wset`` 不能重置，没涨时只能靠采样；
    - 两者都不可用时返回 ``None``。
    """

    def __init__(self) -> None:
        self.method = "none"
        self._clear_refs = Path("/proc/self/clear_refs")
        self._status = Path("/proc/self/status")
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()
        self._peak = 0
        self._process: Any = None
        self._lifetime_peak: int | None = None
        if sys.platform.startswith("linux") and self._reset_hwm():
            self.method = "linux-vmhwm"
            return
        try:
            import psutil

            self._process = psutil.Process()
            self.method = "psutil-sampling-2ms"
        except ImportError:
            pass

    def start(self) -> None:
        if self.method == "linux-vmhwm":
            self._reset_hwm()
        elif self._process is not None:
            self._stop.clear()
            info = self._process.memory_info()
            self._peak = int(info.rss)
            self._lifetime_peak = getattr(info, "peak_wset", None)
            self._thread = threading.Thread(target=self._sample, daemon=True)
            self._thread.start()

    def stop(self) -> int | None:
        if self.method == "linux-vmhwm":
            return self._read_hwm()
        if self._process is None or self._thread is None:
            return None
        self._stop.set()
        self._thread.join()
        self._thread = None
        info = self._process.memory_info()
        peak = max(self._peak, int(info.rss))
        lifetime = getattr(info, "peak_wset", None)
        if (
            lifetime is not None
            and self._lifetime_peak is not None
            and lifetime > self._lifetime_peak
        ):
            peak = max(peak, int(lifetime))  # 请求期间刷新了进程峰值：这就是请求峰值
        return peak

    def _sample(self) -> None:
        while not self._stop.wait(0.002):
            rss = int(self._process.memory_info().rss)
            if rss > self._peak:
                self._peak = rss

    def _reset_hwm(self) -> bool:
        try:
            self._clear_refs.write_text("5", encoding="ascii")
        except OSError:
            return False
        return self._read_hwm() is not None

    def _read_hwm(self) -> int | None:
        try:
            text = self._status.read_text(encoding="utf-8", errors="replace")
        except OSError:
            return None
        for line in text.splitlines():
            if line.startswith("VmHWM:"):
                return int(line.split()[1]) * 1024
        return None


# ---------------------------------------------------------------- 打分


def score_variant(
    raw: Mapping[str, Any], samples: Sequence[dict[str, Any]], line_gaps: Sequence[float]
) -> dict[str, Any]:
    from suiyi_engine.ocr.layout import DEFAULT_OPTIONS, LayoutOptions

    variant: dict[str, Any] = {"det": raw.get("det"), "ok": bool(raw.get("ok"))}
    if not raw.get("ok"):
        variant["error"] = raw.get("error", "未知错误")
        return variant
    by_id = {s["id"]: s for s in samples}
    per_sample: list[dict[str, Any]] = []
    cer_scores: dict[str, CerScore] = {}
    seg_scores: dict[str, SegmentationScore] = {}
    sweep: dict[float, dict[str, SegmentationScore]] = {g: {} for g in line_gaps}
    for row in raw["samples"]:
        sample = by_id[row["id"]]
        lines = _lines_from_dicts(row["lines"])
        predicted = _paragraph_texts(lines, DEFAULT_OPTIONS)
        c = cer(sample["paragraphs"], predicted)
        s = segmentation(sample["paragraphs"], predicted)
        cer_scores[row["id"]] = c
        seg_scores[row["id"]] = s
        for gap in line_gaps:
            options = LayoutOptions(**{**_options_dict(DEFAULT_OPTIONS), "line_gap": gap})
            sweep[gap][row["id"]] = segmentation(
                sample["paragraphs"], _paragraph_texts(lines, options)
            )
        times = row["times_ms"]
        per_sample.append(
            {
                "id": row["id"],
                "lang": sample["lang"],
                "category": sample["category"],
                "split": sample.get("split", "dev"),
                "size_class": row["size_class"],
                "cer": round(c.cer, 4),
                "edits": c.edits,
                "ref_chars": c.ref_chars,
                "expected_paragraphs": s.expected_paragraphs,
                "predicted_paragraphs": s.predicted_paragraphs,
                "merges": s.merges,
                "splits": s.splits,
                "lines": len(lines),
                "low_confidence": sum(1 for line in lines if line.low_confidence),
                "p50_ms": round(percentile(times, 50), 1),
                "request_peak_max": max(row.get("request_peak_delta") or [0]) or None,
                "stages_mean_ms": row["stages_mean_ms"],
                "predicted": predicted,
                "ocr_lines": row["lines"],
            }
        )

    # 主汇总只算 dev（原 32 张），保证与 #54 / #74 的基线可比；留出集单独汇总。
    # 只跑了留出集（--split holdout）时，主汇总退回到全部样例。
    main_ids = {r["id"] for r in per_sample if by_id[r["id"]].get("split", "dev") == "dev"}
    if not main_ids:
        main_ids = {r["id"] for r in per_sample}
    holdout_ids = [r["id"] for r in per_sample if by_id[r["id"]].get("split", "dev") == "holdout"]
    main_rows = [r for r in per_sample if r["id"] in main_ids]
    main_raw = [r for r in raw["samples"] if r["id"] in main_ids]

    def group(key: str) -> dict[str, list[str]]:
        groups: dict[str, list[str]] = {}
        for sample_row in main_rows:
            groups.setdefault(sample_row[key], []).append(sample_row["id"])
        return groups

    def seg_block(ids: Iterable[str], scores: Mapping[str, SegmentationScore]) -> dict[str, Any]:
        ids = list(ids)
        return {
            "overall": _seg_dict(merge_segmentation(scores[i] for i in ids)),
            "ui": _seg_dict(
                merge_segmentation(scores[i] for i in ids if UI_MARK in by_id[i]["category"])
            ),
            "body": _seg_dict(
                merge_segmentation(
                    scores[i] for i in ids if by_id[i]["category"] in BODY_CATEGORIES
                )
            ),
        }

    main_seg = {k: v for k, v in seg_scores.items() if k in main_ids}
    variant.update(
        threads=raw["threads"],
        load_ms=raw["load_ms"],
        first_ms=raw["first_ms"],
        rss=raw["rss"],
        runtime=raw.get("runtime"),
        peak_method=raw.get("peak_method"),
        memory={
            **{size: _memory(main_raw, size) for size in SIZE_CLASSES},
            "overall": _memory(main_raw, None),
        },
        cer={
            "overall": _cer_dict(merge_cer(cer_scores[i] for i in main_ids)),
            "by_category": {
                k: _cer_dict(merge_cer(cer_scores[i] for i in ids))
                for k, ids in group("category").items()
            },
            "by_lang": {
                k: _cer_dict(merge_cer(cer_scores[i] for i in ids))
                for k, ids in group("lang").items()
            },
            "by_size": {
                k: _cer_dict(merge_cer(cer_scores[i] for i in ids))
                for k, ids in group("size_class").items()
            },
        },
        segmentation={
            **seg_block(main_ids, main_seg),
            "by_category": {
                k: _seg_dict(merge_segmentation(seg_scores[i] for i in ids))
                for k, ids in group("category").items()
            },
        },
        holdout=(
            {
                "samples": len(holdout_ids),
                "cer": _cer_dict(merge_cer(cer_scores[i] for i in holdout_ids)),
                "segmentation": seg_block(holdout_ids, seg_scores),
            }
            if holdout_ids and len(holdout_ids) < len(per_sample)
            else None
        ),
        line_gap_sweep=[
            {
                "line_gap": gap,
                "default": gap == DEFAULT_OPTIONS.line_gap,
                **seg_block(main_ids, scores),
            }
            for gap, scores in sweep.items()
        ],
        latency={size: _latency(main_raw, size) for size in SIZE_CLASSES},
        dense_1080p=_latency(
            [r for r in main_raw if by_id[r["id"]]["category"] == DENSE_CATEGORY],
            "1080p",
        ),
        samples=per_sample,
    )
    return variant


def _lines_from_dicts(rows: Iterable[Mapping[str, Any]]) -> list[Any]:
    from suiyi_engine.ocr.types import OcrLine

    lines = []
    for row in rows:
        box = tuple((float(p[0]), float(p[1])) for p in row["box"])
        lines.append(
            OcrLine(
                text=row["text"],
                box=(box[0], box[1], box[2], box[3]),
                score=float(row["score"]),
                low_confidence=bool(row["low_confidence"]),
            )
        )
    return lines


def _paragraph_texts(lines: Sequence[Any], options: Any) -> list[str]:
    from suiyi_engine.ocr.layout import merge_paragraphs

    return [p.text for p in merge_paragraphs(lines, options)]


def _options_dict(options: Any) -> dict[str, Any]:
    from dataclasses import asdict

    return asdict(options)


def _max_peak(rows: Sequence[Mapping[str, Any]]) -> int | None:
    peaks = [p for row in rows for p in row.get("request_peak_delta") or []]
    return max(peaks) if peaks else None


def _memory(rows: Sequence[Mapping[str, Any]], size: str | None) -> dict[str, Any] | None:
    peaks = [
        p
        for row in rows
        if size is None or row["size_class"] == size
        for p in row.get("request_peak_delta") or []
    ]
    if not peaks:
        return None
    return {
        "n": len(peaks),
        "p50": round(percentile(peaks, 50)),
        "p95": round(percentile(peaks, 95)),
        "max": max(peaks),
    }


def _latency(rows: Sequence[Mapping[str, Any]], size: str) -> dict[str, Any] | None:
    times = [t for row in rows if row["size_class"] == size for t in row["times_ms"]]
    if not times:
        return None
    ocr = [row["stages_mean_ms"].get("ocr_ms", 0.0) for row in rows if row["size_class"] == size]
    return {
        "n": len(times),
        "samples": sum(1 for row in rows if row["size_class"] == size),
        "p50_ms": round(percentile(times, 50), 1),
        "p95_ms": round(percentile(times, 95), 1),
        "max_ms": round(max(times), 1),
        "ocr_mean_ms": round(sum(ocr) / len(ocr), 1),
        "target_p95_ms": TARGET_P95_MS[size],
    }


def _cer_dict(score: CerScore) -> dict[str, Any]:
    return {"cer": round(score.cer, 4), "edits": score.edits, "ref_chars": score.ref_chars}


def _seg_dict(score: SegmentationScore) -> dict[str, Any]:
    return {
        "samples": score.samples,
        "exact_samples": score.exact_samples,
        "exact_rate": round(score.exact_rate, 4),
        "expected_boundaries": score.expected_boundaries,
        "matched": score.matched,
        "merges": score.merges,
        "splits": score.splits,
        "precision": round(score.precision, 4),
        "recall": round(score.recall, 4),
        "f1": round(score.f1, 4),
    }


# ---------------------------------------------------------------- 报告


def render_markdown(report: Mapping[str, Any]) -> str:
    env = report["environment"]
    settings = report["settings"]
    variants = [v for v in report["variants"]]
    ok = [v for v in variants if v["ok"]]
    out: list[str] = []
    add = out.append
    add("# OCR 评测报告")
    add("")
    add(f"- 生成时间：{report['generated_at']}")
    if report.get("label"):
        add(f"- 机器说明：{report['label']}")
    if report.get("quick"):
        add("- **快速子集（--quick）**：样例少、重复次数少，只用于验证脚本可用，耗时仅供参考")
    if settings.get("holdout_count"):
        add(
            f"- 其中 dev（原 32 张）{settings.get('dev_count', 0)} 张、"
            f"留出集 {settings['holdout_count']} 张；除「留出集」一节外，所有汇总只算 dev"
        )
    add(
        f"- 样例 {settings['sample_count']} 张；"
        f"每张预热 {settings['warmup']} 次、计时 {settings['repeats']} 次；"
        "计时范围 = 解码 PNG + 检测 + 识别 + 段落合并（不含翻译）"
    )
    add(f"- 当前清单推荐组合：`{json.dumps(settings['recommended'], ensure_ascii=False)}`")
    add("")
    add("## 环境")
    add("")
    add("| 项 | 值 |")
    add("|---|---|")
    for key, label in (
        ("os", "系统"),
        ("cpu_model", "CPU"),
        ("cpu_count", "逻辑核数"),
        ("affinity", "可用核数（亲和性）"),
        ("memory_gb", "内存 (GB)"),
        ("python", "Python"),
        ("rapidocr", "rapidocr"),
        ("onnxruntime", "onnxruntime"),
        ("engine", "suiyi-engine"),
    ):
        add(f"| {label} | {env.get(key, '')} |")
    for v in ok:
        add(f"| onnxruntime 线程数（{_short(v['det'])}） | {v['threads']} |")
    add("")
    for v in variants:
        if not v["ok"]:
            add(f"> **{v['det']} 未运行**：{v['error']}")
            add("")
    if not ok:
        return "\n".join(out) + "\n"

    names = [_short(v["det"]) for v in ok]
    add("## 目标对照")
    add("")
    add("| 指标 | 目标 | " + " | ".join(names) + " |")
    add("|---|---|" + "---|" * len(ok))
    for size in SIZE_CLASSES:
        if all(v["latency"].get(size) is None for v in ok):
            continue
        cells = [_p95_cell(v["latency"].get(size)) for v in ok]
        add(
            f"| P95 {SIZE_LABELS[size]} | ≤ {TARGET_P95_MS[size]:.0f} ms | "
            + " | ".join(cells)
            + " |"
        )
    for category, target in TARGET_CER.items():
        cells = [_cer_cell(v["cer"]["by_category"].get(category), target) for v in ok]
        if all(c == "—" for c in cells):
            continue
        add(f"| CER {category} | ≤ {target:.0%} | " + " | ".join(cells) + " |")
    if any(v.get("dense_1080p") for v in ok):
        cells = [_p95_cell(v.get("dense_1080p"), TARGET_DENSE_1080_P95_MS) for v in ok]
        add(
            f"| P95 1080p 密集文字 | ≤ {TARGET_DENSE_1080_P95_MS:.0f} ms | "
            + " | ".join(cells)
            + " |"
        )
    cells = [_peak_cell(v["memory"]["overall"]) for v in ok]
    add(
        f"| 单请求峰值增量（最大） | ≤ {TARGET_REQUEST_PEAK_MB:.0f} MB | "
        + " | ".join(cells)
        + " |"
    )
    cells = [_mbs(v["rss"]["resident_delta"]) + " MB" for v in ok]
    add("| 常驻增长（跑完全部样例后） | — | " + " | ".join(cells) + " |")
    full = settings.get("dev_count", settings["sample_count"]) == FULL_SAMPLE_COUNT
    cells = [_exact_cell(v["segmentation"]["overall"], full) for v in ok]
    target = f"≥ {BASELINE_EXACT}/{FULL_SAMPLE_COUNT}" if full else "—（非完整样例集）"
    add(f"| 段落完全正确 | {target} | " + " | ".join(cells) + " |")
    cells = [_splits_cell(v["segmentation"]["overall"], full) for v in ok]
    add(f"| 误拆分 | {f'≤ {TARGET_SPLITS}' if full else '—'} | " + " | ".join(cells) + " |")
    cells = [str(v["segmentation"]["overall"]["merges"]) for v in ok]
    add("| 误合并 | — | " + " | ".join(cells) + " |")
    cells = [str(v["segmentation"]["ui"]["merges"]) for v in ok]
    add("| UI 误合并 | — | " + " | ".join(cells) + " |")
    if any(v.get("holdout") for v in ok):
        cells = [_holdout_cell(v.get("holdout")) for v in ok]
        add("| 留出集 完全正确 / 误合并 / 误拆分 | — | " + " | ".join(cells) + " |")
    add("")
    add(
        "CER 另要求不劣于 #74 基线（det small：全部 0.5%，日文竖排 8.9%），"
        "请对照下方 CER 表与 legacy 列。"
    )
    add("")

    add("## 字符错误率（CER）")
    add("")
    add("按字符数加权；NFKC 归一、去空白；段落按阅读顺序拼接（顺序错误计入）。")
    add("")
    for title, key in (("按类别", "by_category"), ("按语种", "by_lang"), ("按尺寸", "by_size")):
        add(f"### {title}")
        add("")
        keys = sorted({k for v in ok for k in v["cer"][key]}, key=_sort_key)
        add("| " + title[1:] + " | 字符数 | " + " | ".join(names) + " |")
        add("|---|---:|" + "---:|" * len(ok))
        for k in keys:
            ref = next((v["cer"][key][k]["ref_chars"] for v in ok if k in v["cer"][key]), 0)
            cells = [_pct(v["cer"][key].get(k)) for v in ok]
            add(f"| {SIZE_LABELS.get(k, k)} | {ref} | " + " | ".join(cells) + " |")
        overall = [_pct(v["cer"]["overall"]) for v in ok]
        add(f"| **全部** | {ok[0]['cer']['overall']['ref_chars']} | " + " | ".join(overall) + " |")
        add("")

    add("## 段落切分")
    add("")
    add(
        "边界容差 ±2 字。误合并 = 漏掉的期望段落边界（两段被并成一段）；误拆分 = 多出来的边界。"
        "完全正确 = 该样例没有任何误合并/误拆分。"
    )
    add("")
    for v in ok:
        add(f"### {_short(v['det'])}（line_gap 默认值）")
        add("")
        add("| 类别 | 样例 | 完全正确 | 期望边界 | 误合并 | 误拆分 | F1 |")
        add("|---|---:|---:|---:|---:|---:|---:|")
        for k in sorted(v["segmentation"]["by_category"], key=_sort_key):
            add(_seg_row(k, v["segmentation"]["by_category"][k]))
        add(_seg_row("**全部**", v["segmentation"]["overall"]))
        add("")
    held = [v for v in ok if v.get("holdout")]
    if held:
        add("### 留出集（#75，调参时不看）")
        add("")
        add(
            _row(
                "检测模型",
                "样例",
                "完全正确",
                "误合并",
                "误拆分",
                "F1",
                "UI 误合并",
                "UI 误拆分",
                "CER",
            )
        )
        add("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
        for v in held:
            ho = v["holdout"]
            o, u = ho["segmentation"]["overall"], ho["segmentation"]["ui"]
            add(
                _row(
                    _short(v["det"]),
                    ho["samples"],
                    f"{o['exact_samples']}/{o['samples']}",
                    o["merges"],
                    o["splits"],
                    f"{o['f1']:.2f}",
                    u["merges"],
                    u["splits"],
                    _pct(ho["cer"]),
                )
            )
        add("")
    add("### line_gap 扫描")
    add("")
    add(
        "只重跑段落合并（同一批识别行），其他阈值不变。"
        "UI = 各语种「UI 小字」类；正文 = 网页正文/文档/双栏/密集/混排。"
    )
    add("")
    for v in ok:
        add(f"**{_short(v['det'])}**")
        add("")
        add(
            _row("line_gap", "全部 完全正确", "全部 误合并", "全部 误拆分")
            + _row("UI 误合并", "UI 误拆分", "正文 误合并", "正文 误拆分")[1:]
        )
        add("|---|---:|---:|---:|---:|---:|---:|---:|")
        for row in v["line_gap_sweep"]:
            gap = f"{row['line_gap']:g}" + ("（默认）" if row["default"] else "")
            o, u, b = row["overall"], row["ui"], row["body"]
            add(
                f"| {gap} | {o['exact_samples']}/{o['samples']} | {o['merges']} | {o['splits']} | "
                f"{u['merges']} | {u['splits']} | {b['merges']} | {b['splits']} |"
            )
        add("")

    add("## 耗时")
    add("")
    add(
        _row("尺寸", "检测模型", "样例", "次数", "P50 (ms)", "P95 (ms)")
        + _row("最大 (ms)", "其中 OCR 均值 (ms)", "目标 P95")[1:]
    )
    add("|---|---|---:|---:|---:|---:|---:|---:|---:|")
    for size in SIZE_CLASSES:
        for v in ok:
            lat = v["latency"].get(size)
            if lat is None:
                continue
            add(
                _row(
                    SIZE_LABELS[size],
                    _short(v["det"]),
                    lat["samples"],
                    lat["n"],
                    f"{lat['p50_ms']:.0f}",
                    f"{lat['p95_ms']:.0f}",
                    f"{lat['max_ms']:.0f}",
                    f"{lat['ocr_mean_ms']:.0f}",
                    f"≤ {lat['target_p95_ms']:.0f}",
                )
            )
    add("")
    add("## 加载与内存")
    add("")
    add(
        "口径（#74）：**单请求峰值增量** = 单次 `recognize` 期间的峰值 RSS"
        " − OCR 已预热、空闲时的 RSS；"
        "**常驻** = 空闲 RSS（绝对值），以及跑完全部样例后空闲 RSS 的增长。"
    )
    add("")
    add(
        _row("检测模型", "冷加载 (ms)", "首次识别 (ms)", "进程基线 (MB)", "模型+预热 (MB)")
        + _row("空闲 RSS (MB)", "跑完后空闲 (MB)", "常驻增长 (MB)", "峰值测法")[1:]
    )
    add("|---|---:|---:|---:|---:|---:|---:|---:|---|")
    for v in ok:
        rss = v["rss"]
        add(
            _row(
                _short(v["det"]),
                f"{v['load_ms']:.0f}",
                f"{v['first_ms']:.0f}",
                _mbs(rss["process_base"]),
                _mbs(rss["model_delta"]),
                _mbs(rss["idle"]),
                _mbs(rss["final"]),
                _mbs(rss["resident_delta"]),
                v.get("peak_method", ""),
            )
        )
    add("")
    add("单请求峰值增量（MB，按尺寸）：")
    add("")
    add(_row("尺寸", "检测模型", "次数", "P50", "P95", "最大", "目标"))
    add("|---|---|---:|---:|---:|---:|---:|")
    for size in (*SIZE_CLASSES, "overall"):
        for v in ok:
            mem = v["memory"].get(size)
            if mem is None:
                continue
            add(
                _row(
                    SIZE_LABELS.get(size, "全部"),
                    _short(v["det"]),
                    mem["n"],
                    _mbs(mem["p50"]),
                    _mbs(mem["p95"]),
                    _mbs(mem["max"]),
                    f"≤ {TARGET_REQUEST_PEAK_MB:.0f}",
                )
            )
    add("")
    add(
        "峰值测法：Linux 每次请求前经 `/proc/self/clear_refs` 重置 `VmHWM`（精确）；"
        "其他平台用 psutil 每 2 ms 采样（近似，可能漏掉极短尖峰）。"
    )
    add("")
    add("## 逐样例")
    add("")
    add(
        "| 样例 | 类别 | 尺寸 | "
        + " | ".join(f"CER {n}" for n in names)
        + " | 期望段 | "
        + " | ".join(f"段数/误合并/误拆分 {n}" for n in names)
        + " | "
        + " | ".join(f"P50 {n}" for n in names)
        + " |"
    )
    add("|---|---|---|" + "---:|" * len(ok) + "---:|" + "---:|" * len(ok) * 2)
    for index, row in enumerate(ok[0]["samples"]):
        rows = [v["samples"][index] for v in ok]
        mark = "（留出）" if row.get("split") == "holdout" else ""
        add(
            f"| {row['id']}{mark} | {row['category']} | {SIZE_LABELS[row['size_class']]} | "
            + " | ".join(f"{r['cer']:.1%}" for r in rows)
            + f" | {row['expected_paragraphs']} | "
            + " | ".join(f"{r['predicted_paragraphs']}/{r['merges']}/{r['splits']}" for r in rows)
            + " | "
            + " | ".join(f"{r['p50_ms']:.0f}" for r in rows)
            + " |"
        )
    add("")
    return "\n".join(out) + "\n"


def _splits_cell(seg: Mapping[str, Any], full: bool) -> str:
    text = str(seg["splits"])
    if not full:
        return text
    return text + (" ✅" if seg["splits"] <= TARGET_SPLITS else " ❌")


def _holdout_cell(holdout: Mapping[str, Any] | None) -> str:
    if not holdout:
        return "—"
    o = holdout["segmentation"]["overall"]
    return f"{o['exact_samples']}/{o['samples']} / {o['merges']} / {o['splits']}"


def _row(*cells: object) -> str:
    return "| " + " | ".join(str(c) for c in cells) + " |"


def _seg_row(label: str, s: Mapping[str, Any]) -> str:
    return (
        f"| {label} | {s['samples']} | {s['exact_samples']} | {s['expected_boundaries']} | "
        f"{s['merges']} | {s['splits']} | {s['f1']:.2f} |"
    )


def _p95_cell(lat: Mapping[str, Any] | None, target: float | None = None) -> str:
    if lat is None:
        return "—"
    limit = lat["target_p95_ms"] if target is None else target
    mark = "✅" if lat["p95_ms"] <= limit else "❌"
    return f"{lat['p95_ms']:.0f} ms {mark}"


def _cer_cell(score: Mapping[str, Any] | None, target: float) -> str:
    if score is None:
        return "—"
    mark = "✅" if score["cer"] <= target else "❌"
    return f"{score['cer']:.1%} {mark}"


def _peak_cell(mem: Mapping[str, Any] | None) -> str:
    if mem is None:
        return "未测（缺 psutil）"
    mark = "✅" if _mb(mem["max"]) <= TARGET_REQUEST_PEAK_MB else "❌"
    return f"{_mb(mem['max']):.0f} MB {mark}"


def _exact_cell(seg: Mapping[str, Any], full: bool) -> str:
    text = f"{seg['exact_samples']}/{seg['samples']}"
    if not full:
        return text
    return text + (" ✅" if seg["exact_samples"] >= BASELINE_EXACT else " ❌")


def _pct(score: Mapping[str, Any] | None) -> str:
    return "—" if score is None else f"{score['cer']:.1%}"


def _short(variant: str) -> str:
    det, _sep, rest = variant.partition("@")
    if not rest and "+" in det:
        det, _plus, flags = det.partition("+")
        rest = "+" + flags
    else:
        rest = "@" + rest if rest else ""
    for alias, full in DET_ALIASES.items():
        if full == det:
            return f"det {alias}{rest}"
    return variant


def _sort_key(key: str) -> tuple[int, str]:
    order = {k: i for i, k in enumerate(SIZE_CLASSES)}
    return (order.get(key, len(order)), key)


def _mb(value: int | None) -> float:
    return 0.0 if value is None else value / 2**20


def _mbs(value: int | None) -> str:
    return "—" if value is None else f"{_mb(value):.0f}"


def _delta(a: int | None, b: int | None) -> int | None:
    return None if a is None or b is None else a - b


# ---------------------------------------------------------------- 环境与内存


def collect_environment() -> dict[str, Any]:
    memory = None
    try:
        import psutil

        memory = round(psutil.virtual_memory().total / 2**30, 1)
    except ImportError:
        pass
    affinity = None
    if hasattr(os, "sched_getaffinity"):
        affinity = len(os.sched_getaffinity(0))
    return {
        "os": platform.platform(),
        "cpu_model": _cpu_model(),
        "cpu_count": os.cpu_count() or 0,
        "affinity": affinity if affinity is not None else "—",
        "memory_gb": memory if memory is not None else "未知（缺 psutil）",
        "python": platform.python_version(),
        "rapidocr": _package_version("rapidocr"),
        "onnxruntime": _package_version("onnxruntime"),
        "engine": __version__,
    }


def current_rss_bytes() -> int | None:
    try:
        import psutil

        return int(psutil.Process().memory_info().rss)
    except ImportError:
        pass
    statm = Path("/proc/self/statm")
    if statm.is_file():
        pages = int(statm.read_text(encoding="ascii").split()[1])
        return pages * os.sysconf("SC_PAGE_SIZE")
    return None


def peak_rss_bytes() -> int | None:
    """进程启动以来的峰值常驻内存。"""

    if sys.platform == "win32":
        try:
            import psutil
        except ImportError:
            return None
        info = psutil.Process().memory_info()
        return int(getattr(info, "peak_wset", info.rss))
    status = Path("/proc/self/status")
    if status.is_file():
        for line in status.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("VmHWM:"):
                return int(line.split()[1]) * 1024
    import resource

    rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    return int(rss) if sys.platform == "darwin" else int(rss) * 1024


def _cpu_model() -> str:
    cpuinfo = Path("/proc/cpuinfo")
    if cpuinfo.is_file():
        for line in cpuinfo.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.lower().startswith("model name"):
                name = line.split(":", 1)[1].strip()
                if name:
                    return name
    if sys.platform == "win32":
        try:
            import winreg

            key = winreg.OpenKey(
                winreg.HKEY_LOCAL_MACHINE, r"HARDWARE\DESCRIPTION\System\CentralProcessor\0"
            )
            return str(winreg.QueryValueEx(key, "ProcessorNameString")[0]).strip()
        except OSError:
            pass
    return platform.processor().strip() or platform.machine()


def _package_version(name: str) -> str:
    try:
        return version(name)
    except PackageNotFoundError:
        return "未安装"


def _split(raw: str | None) -> list[str]:
    return [x.strip() for x in (raw or "").split(",") if x.strip()]


def _stderr(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def _configure_stdio() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except (OSError, ValueError):
            continue


if __name__ == "__main__":
    raise SystemExit(main())
