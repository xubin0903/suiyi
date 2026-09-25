"""编排固定样例评测：翻译、打分、写报告。

命令行见 ``scripts/eval_samples.py``。翻译只走 ``Translator``，不经 HTTP。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import random
import sys
import time
from collections.abc import Callable, Mapping, Sequence
from datetime import datetime, timezone
from importlib.metadata import PackageNotFoundError, version
from pathlib import Path

from suiyi_engine import __version__
from suiyi_engine.errors import UnsupportedPairError
from suiyi_engine.eval.metrics import (
    DIRECTION_ORDER,
    OCR_EMPTY_REFERENCE,
    OCR_MISSING_CLEAN,
    OCR_SCORED,
    ScoredSample,
    bleu_tokenize,
    must_keep_hits,
    ocr_chrf_delta,
    probe_ja_mecab,
    sentence_scores,
    summarize_samples,
)
from suiyi_engine.eval.report import (
    DirectionResult,
    EnvironmentInfo,
    EvalReport,
    EvalRun,
    read_model_meta,
    render_markdown,
    summary_dict,
    write_outputs,
)
from suiyi_engine.registry import ModelRecord, normalize_lang
from suiyi_engine.translator import Translator

EXAMPLE_COUNT = 5
_REQUIRED_FIELDS = (
    "id",
    "src_lang",
    "tgt_lang",
    "category",
    "source",
    "reference",
    "must_keep",
)


class EvalError(Exception):
    """评测或参数错误。``code`` 作为进程退出码。"""

    def __init__(self, message: str, code: int = 1) -> None:
        super().__init__(message)
        self.code = code


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="scripts/eval_samples.py",
        description="对固定样例集调用 Translator 跑分，写出 Markdown 与 JSON 报告。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "示例：\n"
            "  python scripts/eval_samples.py \\\n"
            "    --samples tests/samples/zh_core_v1.jsonl \\\n"
            "    --models-dir models \\\n"
            "    --out reports/eval-2026-09-25\n"
            "\n"
            '在仓库根目录执行。需要 pip install -e "engine[eval]"。\n'
            "reports/ 已加入 .gitignore，不要把生成的报告提交进 git。\n"
            "基线摘要写在 docs/engine/评测.md。"
        ),
    )
    parser.add_argument("--samples", type=Path, required=True, help="JSONL 样例集路径")
    parser.add_argument("--models-dir", type=Path, required=True, help="已转换的模型根目录")
    parser.add_argument(
        "--out",
        type=Path,
        required=True,
        help="输出目录，写入 report.md、results.jsonl、summary.json",
    )
    parser.add_argument(
        "--directions",
        help="只跑这些方向，逗号分隔，如 zh-en,en-zh。默认跑样例里出现的全部方向",
    )
    parser.add_argument(
        "--limit",
        type=int,
        help="每个方向最多评测前 N 条（按文件顺序）",
    )
    parser.add_argument("--seed", type=int, default=0, help="对照样例的随机种子，默认 0")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    _configure_stdio()
    parser = build_parser()
    args = parser.parse_args(None if argv is None else list(argv))
    try:
        return run(args)
    except EvalError as exc:
        print(str(exc), file=sys.stderr, flush=True)
        return exc.code


def run(args: argparse.Namespace, *, log: Callable[[str], None] | None = None) -> int:
    samples_path = args.samples.expanduser().resolve()
    models_dir = args.models_dir.expanduser().resolve()
    out_dir = args.out.expanduser().resolve()
    if args.limit is not None and args.limit < 1:
        raise EvalError("--limit 必须是 >= 1 的整数", code=2)
    if not models_dir.is_dir():
        raise EvalError(f"模型目录不存在：{models_dir}", code=2)
    samples = load_samples(samples_path)
    directions = parse_directions(args.directions)
    ja_mecab, ja_note = probe_ja_mecab()
    translator = Translator(models_dir)
    evaluation = evaluate(
        samples,
        translator,
        directions=directions,
        limit=args.limit,
        seed=args.seed,
        ja_mecab=ja_mecab,
        log=log if log is not None else _stderr,
    )
    model_ids: list[str] = []
    for direction in evaluation.directions:
        for model_id in direction.model_ids:
            if model_id not in model_ids:
                model_ids.append(model_id)
    report = EvalReport(
        run=evaluation,
        environment=collect_environment(),
        models=read_model_meta(models_dir, model_ids),
        peak_rss_bytes=peak_rss_bytes(),
        ja_mecab=ja_mecab,
        ja_mecab_note=ja_note,
        samples_path=str(samples_path),
        samples_sha256=file_sha256(samples_path),
        models_dir=str(models_dir),
        evaluated_at=datetime.now(timezone.utc).isoformat(timespec="seconds"),
    )
    write_outputs(out_dir, report)
    _stderr(f"已写入 {out_dir / 'report.md'}")
    _stderr(f"已写入 {out_dir / 'results.jsonl'}")
    _stderr(f"已写入 {out_dir / 'summary.json'}")
    print(render_markdown(report).split("## 分方向", 1)[0].rstrip())
    print()
    summary = summary_dict(report)
    directions_out = summary["directions"]
    if isinstance(directions_out, list):
        for item in directions_out:
            if isinstance(item, dict):
                _stderr(
                    "{direction} route={route} chrF={chrf} BLEU={bleu} 专名={proper}".format(
                        direction=item.get("direction"),
                        route=item.get("route_label"),
                        chrf=item.get("chrf"),
                        bleu=item.get("bleu"),
                        proper=item.get("proper_noun_retention"),
                    )
                )
    return 0


def evaluate(
    samples: Sequence[Mapping[str, object]],
    translator: Translator,
    *,
    directions: Sequence[tuple[str, str]] | None = None,
    limit: int | None = None,
    seed: int = 0,
    ja_mecab: bool = False,
    log: Callable[[str], None] | None = None,
) -> EvalRun:
    """按方向预热并翻译。``ja_mecab`` 为假时日文 BLEU 使用 ``char``。"""

    selected = select_samples(samples, directions, limit)
    scored: list[ScoredSample] = []
    direction_results: list[DirectionResult] = []
    examples: list[tuple[str, tuple[ScoredSample, ...]]] = []
    for src_lang, tgt_lang in selected:
        rows = selected[(src_lang, tgt_lang)]
        label = f"{src_lang}→{tgt_lang}"
        if log is not None:
            log(f"评测 {label}：{len(rows)} 条")
        try:
            records = translator.registry.resolve(src_lang, tgt_lang)
        except UnsupportedPairError as exc:
            raise EvalError(str(exc), code=1) from exc
        route_label = language_route(records, src_lang, tgt_lang)
        model_ids = tuple(record.id for record in records)
        before = set(translator.loaded_model_ids())
        started = time.perf_counter()
        try:
            translator.preload([(src_lang, tgt_lang)])
        except UnsupportedPairError as exc:
            raise EvalError(str(exc), code=1) from exc
        load_ms = (time.perf_counter() - started) * 1000.0
        newly = tuple(record.id for record in records if record.id not in before)
        pending: list[ScoredSample] = []
        for row in rows:
            result = translator.translate(_require_text(row, "source"), src_lang, tgt_lang)
            reference = _require_text(row, "reference", allow_empty=True)
            hits = must_keep_hits(result.text, _must_keep(row))
            chrf, bleu = sentence_scores(
                result.text,
                reference,
                tgt_lang,
                ja_mecab=ja_mecab,
            )
            notes = row.get("notes", "")
            pending.append(
                ScoredSample(
                    id=_require_text(row, "id"),
                    src_lang=src_lang,
                    tgt_lang=tgt_lang,
                    category=_require_text(row, "category"),
                    source=_require_text(row, "source"),
                    reference=reference,
                    hypothesis=result.text,
                    route=tuple(result.route),
                    route_label=route_label,
                    elapsed_ms=result.elapsed_ms,
                    chrf=chrf,
                    bleu=bleu,
                    hits=hits,
                    clean_id=_optional_text(row.get("clean_id")),
                    ocr_chrf_delta=None,
                    ocr_status=None,
                    notes=notes if isinstance(notes, str) else "",
                )
            )
        finished = _attach_ocr_deltas(pending)
        overall, categories = summarize_samples(finished, ja_mecab=ja_mecab)
        missing_clean = sum(1 for sample in finished if sample.ocr_status == OCR_MISSING_CLEAN)
        direction_results.append(
            DirectionResult(
                src_lang=src_lang,
                tgt_lang=tgt_lang,
                route_label=route_label,
                model_ids=model_ids,
                load_ms=load_ms,
                newly_loaded=newly,
                overall=overall,
                categories=categories,
                ocr_pairs_missing_clean=missing_clean,
                bleu_tokenize=bleu_tokenize(tgt_lang, ja_mecab=ja_mecab),
            )
        )
        scored.extend(finished)
        examples.append((label, pick_examples(finished, EXAMPLE_COUNT, seed)))
    return EvalRun(
        samples=tuple(scored),
        directions=tuple(direction_results),
        examples=tuple(examples),
        seed=seed,
        limit=limit,
    )


def load_samples(path: Path) -> list[dict[str, object]]:
    """读取 JSONL。空行跳过。字段不齐或 id 重复时失败。"""

    if not path.is_file():
        raise EvalError(f"找不到样例集：{path}", code=2)
    try:
        text = path.read_text(encoding="utf-8-sig")
    except OSError as exc:
        raise EvalError(f"无法读取样例集 {path}：{exc}", code=2) from exc
    rows: list[dict[str, object]] = []
    seen: set[str] = set()
    for lineno, line in enumerate(text.splitlines(), 1):
        if not line.strip():
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError as exc:
            raise EvalError(f"{path}:{lineno} 不是合法 JSON：{exc}", code=1) from exc
        if not isinstance(obj, dict):
            raise EvalError(f"{path}:{lineno} 必须是 JSON 对象", code=1)
        where = f"{path}:{lineno}"
        for key in _REQUIRED_FIELDS:
            if key not in obj:
                raise EvalError(f"{where} 缺少字段 {key}", code=1)
        sample_id = obj["id"]
        if not isinstance(sample_id, str) or not sample_id.strip():
            raise EvalError(f"{where} 的 id 必须是非空字符串", code=1)
        if sample_id in seen:
            raise EvalError(f"{where} 的 id 重复：{sample_id}", code=1)
        seen.add(sample_id)
        _parse_must_keep(obj["must_keep"], where)
        try:
            normalize_lang(str(obj["src_lang"]))
            normalize_lang(str(obj["tgt_lang"]))
        except ValueError as exc:
            raise EvalError(f"{where}：{exc}", code=1) from exc
        rows.append(obj)
    if not rows:
        raise EvalError(f"样例集是空的：{path}", code=1)
    return rows


def parse_directions(raw: str | None) -> list[tuple[str, str]] | None:
    """解析 ``zh-en,ja-zh``。``None`` 表示按样例内容决定。"""

    if raw is None or not raw.strip():
        return None
    parsed: list[tuple[str, str]] = []
    for token in raw.split(","):
        if not token.strip():
            continue
        pair = _parse_direction_token(token)
        if pair not in parsed:
            parsed.append(pair)
    if not parsed:
        raise EvalError("--directions 为空", code=2)
    return parsed


def select_samples(
    samples: Sequence[Mapping[str, object]],
    directions: Sequence[tuple[str, str]] | None,
    limit: int | None,
) -> dict[tuple[str, str], list[Mapping[str, object]]]:
    """按规范方向排序，并在每个方向上截断到 ``limit``。"""

    grouped: dict[tuple[str, str], list[Mapping[str, object]]] = {}
    for sample in samples:
        pair = (normalize_lang(str(sample["src_lang"])), normalize_lang(str(sample["tgt_lang"])))
        grouped.setdefault(pair, []).append(sample)
    if directions is None:
        ordered = [pair for pair in DIRECTION_ORDER if pair in grouped]
        ordered.extend(pair for pair in grouped if pair not in ordered)
    else:
        ordered = list(directions)
        missing = [f"{src}→{tgt}" for src, tgt in ordered if (src, tgt) not in grouped]
        if missing:
            raise EvalError("样例集中没有这些方向：" + "、".join(missing), code=2)
    selected: dict[tuple[str, str], list[Mapping[str, object]]] = {}
    for pair in ordered:
        rows = grouped[pair]
        if limit is not None:
            rows = rows[:limit]
        if rows:
            selected[pair] = rows
    if not selected:
        raise EvalError("没有可评测的样例", code=1)
    return selected


def language_route(records: Sequence[ModelRecord], src_lang: str, tgt_lang: str) -> str:
    """直连为 ``zh→en``，英文中转为 ``ja→en→zh``。"""

    if not records:
        return f"{src_lang}→{tgt_lang}"
    parts = [records[0].src]
    parts.extend(record.tgt for record in records)
    return "→".join(parts)


def pick_examples(
    samples: Sequence[ScoredSample],
    count: int,
    seed: int,
) -> tuple[ScoredSample, ...]:
    """每个方向单独用 ``(seed, 方向)`` 做种子，抽完再按 id 排序。"""

    if len(samples) <= count:
        return tuple(samples)
    label = samples[0].direction
    chosen = random.Random(f"{seed}:{label}").sample(list(samples), count)
    chosen.sort(key=lambda sample: sample.id)
    return tuple(chosen)


def collect_environment() -> EnvironmentInfo:
    """采集 CPU、内存、OS、Python 与相关包版本。"""

    try:
        import psutil
    except ImportError as exc:
        raise EvalError('记录内存需要评测依赖：pip install -e "engine[eval]"', code=1) from exc
    return EnvironmentInfo(
        cpu_model=_cpu_model(),
        cpu_count=os.cpu_count() or 0,
        memory_bytes=int(psutil.virtual_memory().total),
        os=platform.platform(),
        python=platform.python_version(),
        ctranslate2=_package_version("ctranslate2"),
        sacrebleu=_package_version("sacrebleu"),
        psutil=_package_version("psutil"),
        engine=__version__,
    )


def peak_rss_bytes() -> int:
    """进程启动以来的峰值常驻内存。"""

    if sys.platform == "win32":
        import psutil

        info = psutil.Process().memory_info()
        return int(getattr(info, "peak_wset", info.rss))
    status = Path("/proc/self/status")
    if status.is_file():
        for line in status.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("VmHWM:"):
                return int(line.split()[1]) * 1024
    import resource

    rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    if sys.platform == "darwin":
        return int(rss)
    return int(rss) * 1024


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _attach_ocr_deltas(samples: Sequence[ScoredSample]) -> tuple[ScoredSample, ...]:
    by_id = {sample.id: sample for sample in samples}
    updated: list[ScoredSample] = []
    for sample in samples:
        if sample.category != "ocr_noise":
            updated.append(sample)
            continue
        if not sample.reference.strip():
            updated.append(_replace_ocr(sample, None, OCR_EMPTY_REFERENCE))
            continue
        clean = by_id.get(sample.clean_id or "")
        if clean is None or clean.direction != sample.direction:
            updated.append(_replace_ocr(sample, None, OCR_MISSING_CLEAN))
            continue
        delta = ocr_chrf_delta(sample.hypothesis, clean.hypothesis, sample.reference)
        updated.append(_replace_ocr(sample, delta, OCR_SCORED))
    return tuple(updated)


def _replace_ocr(sample: ScoredSample, delta: float | None, status: str) -> ScoredSample:
    return ScoredSample(
        id=sample.id,
        src_lang=sample.src_lang,
        tgt_lang=sample.tgt_lang,
        category=sample.category,
        source=sample.source,
        reference=sample.reference,
        hypothesis=sample.hypothesis,
        route=sample.route,
        route_label=sample.route_label,
        elapsed_ms=sample.elapsed_ms,
        chrf=sample.chrf,
        bleu=sample.bleu,
        hits=sample.hits,
        clean_id=sample.clean_id,
        ocr_chrf_delta=delta,
        ocr_status=status,
        notes=sample.notes,
    )


def _parse_direction_token(token: str) -> tuple[str, str]:
    raw = token.strip().replace("→", "-").replace(">", "-")
    parts = raw.split("-")
    if len(parts) != 2 or not parts[0] or not parts[1]:
        raise EvalError(f"无法识别的方向：{token}", code=2)
    try:
        return normalize_lang(parts[0]), normalize_lang(parts[1])
    except ValueError as exc:
        raise EvalError(f"无法识别的方向：{token}（{exc}）", code=2) from exc


def _parse_must_keep(value: object, where: str) -> None:
    if not isinstance(value, list):
        raise EvalError(f"{where} 的 must_keep 必须是数组", code=1)
    for index, entry in enumerate(value, 1):
        if not isinstance(entry, dict):
            raise EvalError(
                f'{where} 的 must_keep[{index}] 必须是对象 {{"src", "tgt"}}',
                code=1,
            )
        src = entry.get("src")
        tgt = entry.get("tgt")
        if not isinstance(src, str) or not isinstance(tgt, str):
            raise EvalError(f"{where} 的 must_keep[{index}] 的 src 和 tgt 必须是字符串", code=1)


def _must_keep(row: Mapping[str, object]) -> list[dict[str, str]]:
    value = row["must_keep"]
    if not isinstance(value, list):
        return []
    items: list[dict[str, str]] = []
    for entry in value:
        if (
            isinstance(entry, dict)
            and isinstance(entry.get("src"), str)
            and isinstance(entry.get("tgt"), str)
        ):
            items.append({"src": entry["src"], "tgt": entry["tgt"]})
    return items


def _require_text(row: Mapping[str, object], key: str, *, allow_empty: bool = False) -> str:
    value = row[key]
    if not isinstance(value, str) or (not allow_empty and not value.strip()):
        raise EvalError(f"样例 {row.get('id')} 的 {key} 必须是字符串", code=1)
    return value


def _optional_text(value: object) -> str | None:
    if isinstance(value, str) and value.strip():
        return value
    return None


def _cpu_model() -> str:
    cpuinfo = Path("/proc/cpuinfo")
    if cpuinfo.is_file():
        for line in cpuinfo.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.lower().startswith("model name"):
                name = line.split(":", 1)[1].strip()
                if name:
                    return name
    processor = platform.processor().strip()
    if processor:
        return processor
    return platform.machine()


def _package_version(name: str) -> str:
    try:
        return version(name)
    except PackageNotFoundError:
        return "未安装"


def _stderr(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def _configure_stdio() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        encoding = (getattr(stream, "encoding", None) or "").lower().replace("-", "")
        if encoding == "utf8":
            continue
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except (OSError, ValueError):
            continue
