"""把评测结果写成 Markdown、summary.json 与 results.jsonl。"""

from __future__ import annotations

import json
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path

from suiyi_engine.eval.metrics import GroupMetrics, MustKeepHit, ScoredSample, missed_samples

SCHEMA_VERSION = 1


@dataclass(frozen=True, slots=True)
class DirectionResult:
    """一个方向的路由、加载耗时和分类汇总。"""

    src_lang: str
    tgt_lang: str
    route_label: str
    model_ids: tuple[str, ...]
    load_ms: float
    newly_loaded: tuple[str, ...]
    overall: GroupMetrics
    categories: tuple[GroupMetrics, ...]
    ocr_pairs_missing_clean: int
    bleu_tokenize: str

    @property
    def label(self) -> str:
        return f"{self.src_lang}→{self.tgt_lang}"

    @property
    def pivot(self) -> bool:
        return len(self.model_ids) > 1


@dataclass(frozen=True, slots=True)
class EvalRun:
    """一次评测的分数，不含机器环境。"""

    samples: tuple[ScoredSample, ...]
    directions: tuple[DirectionResult, ...]
    examples: tuple[tuple[str, tuple[ScoredSample, ...]], ...]
    seed: int
    limit: int | None


@dataclass(frozen=True, slots=True)
class EnvironmentInfo:
    """报告开头的机器与软件版本。"""

    cpu_model: str
    cpu_count: int
    memory_bytes: int
    os: str
    python: str
    ctranslate2: str
    sacrebleu: str
    psutil: str
    engine: str


@dataclass(frozen=True, slots=True)
class ModelMeta:
    """参与评测的模型修订。"""

    id: str
    hf_revision: str | None
    quantization: str | None
    ctranslate2_version: str | None
    converted_at: str | None


@dataclass(frozen=True, slots=True)
class EvalReport:
    """写入磁盘的完整报告。"""

    run: EvalRun
    environment: EnvironmentInfo
    models: tuple[ModelMeta, ...]
    peak_rss_bytes: int
    ja_mecab: bool
    ja_mecab_note: str
    samples_path: str
    samples_sha256: str
    models_dir: str
    evaluated_at: str


def render_markdown(report: EvalReport) -> str:
    """生成 ``report.md``。"""

    lines: list[str] = [
        "# 样例评测报告",
        "",
        f"- 评测时间：{report.evaluated_at}",
        f"- 样例集：`{report.samples_path}`",
        f"- 样例 SHA256：`{report.samples_sha256}`",
        f"- 模型目录：`{report.models_dir}`",
        f"- 对照样例种子：{report.run.seed}",
        f"- 每方向条数上限：{_limit_text(report.run.limit)}",
        "",
        "## 环境",
        "",
        "| 项目 | 值 |",
        "| --- | --- |",
        f"| CPU | {_cell(report.environment.cpu_model)} |",
        f"| 核数 | {report.environment.cpu_count} |",
        f"| 内存 | {_cell(format_bytes(report.environment.memory_bytes))} |",
        f"| 操作系统 | {_cell(report.environment.os)} |",
        f"| Python | {_cell(report.environment.python)} |",
        f"| ctranslate2 | {_cell(report.environment.ctranslate2)} |",
        f"| sacrebleu | {_cell(report.environment.sacrebleu)} |",
        f"| psutil | {_cell(report.environment.psutil)} |",
        f"| suiyi-engine | {_cell(report.environment.engine)} |",
        f"| 日文 BLEU | {_cell(report.ja_mecab_note)} |",
        f"| 峰值 RSS | {_cell(format_bytes(report.peak_rss_bytes))} |",
        "",
        "峰值 RSS 是本次进程的高水位（Linux 为 `VmHWM`，Windows 为 `peak_wset`），"
        "含模型加载与指标计算。",
        "",
        "## 模型",
        "",
        "| 模型 id | hf_revision | 量化 | 转换时 ctranslate2 | 转换时间 |",
        "| --- | --- | --- | --- | --- |",
    ]
    if report.models:
        for model in report.models:
            lines.append(
                "| {id} | {rev} | {quant} | {ct2} | {when} |".format(
                    id=_cell(model.id),
                    rev=_cell(model.hf_revision or "—"),
                    quant=_cell(model.quantization or "—"),
                    ct2=_cell(model.ctranslate2_version or "—"),
                    when=_cell(model.converted_at or "—"),
                )
            )
    else:
        lines.append("| — | — | — | — | — |")
    lines.extend(
        [
            "",
            "## 总表",
            "",
            "chrF / BLEU 是该方向全部有参考译文条目的语料级分数（0–100）。"
            "专名保留率是 `must_keep.tgt` 在译文中连续出现的微平均。"
            "OCR chrF 差值是句级 `chrF(噪声译文, 参考) - chrF(干净译文, 参考)` 的平均，"
            "负值表示噪声把译文拉离参考。"
            "延迟是预热之后 `Translator.translate` 的 `elapsed_ms`，P50 / P95 为线性插值。"
            "加载耗时是该方向 `preload` 的墙钟时间；前面已经加载的模型不会再计。",
            "",
            (
                "| 方向 | route | 条数 | chrF | BLEU | 专名保留率 | "
                "OCR chrF 差值 | 加载（ms） | P50（ms） | P95（ms） | max（ms） |"
            ),
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
        ]
    )
    for direction in report.run.directions:
        lines.append(_direction_row(direction))
    lines.extend(["", "## 分方向 × 分类", ""])
    for direction in report.run.directions:
        lines.extend(_category_section(direction))
    lines.extend(["## 专名未保留", ""])
    missed = missed_samples(report.run.samples)
    if not missed:
        lines.append("没有。")
        lines.append("")
    else:
        lines.append(f"共 {len(missed)} 条。`tgt` 未作为连续子串出现在译文中。")
        lines.append("")
        lines.append("| id | 方向 | 未保留 | 译文 |")
        lines.append("| --- | --- | --- | --- |")
        for sample in missed:
            missing = "；".join(hit.tgt or "（空）" for hit in sample.hits if not hit.kept)
            row = (
                f"| {_cell(sample.id)} | {_cell(sample.direction)} | "
                f"{_cell(missing)} | {_cell(sample.hypothesis)} |"
            )
            lines.append(row)
        lines.append("")
    lines.extend(
        [
            "## 对照样例",
            "",
            f"每个方向用种子 {report.run.seed} 抽取最多 5 条，供人工抽查。原文里的换行写成 `⏎`。",
            "",
        ]
    )
    for label, examples in report.run.examples:
        lines.append(f"### {label}")
        lines.append("")
        if not examples:
            lines.append("没有样例。")
            lines.append("")
            continue
        for index, sample in enumerate(examples, 1):
            lines.append(
                f"{index}. `{sample.id}`（{sample.category}，{_fmt_ms(sample.elapsed_ms)} ms，"
                f"route `{sample.route_label}`）"
            )
            lines.append(f"   - 原文：{_flat(sample.source)}")
            lines.append(f"   - 参考：{_flat(sample.reference) if sample.reference else '（空）'}")
            lines.append(f"   - 译文：{_flat(sample.hypothesis)}")
            lines.append("")
    lines.extend(_metric_notes(report))
    return "\n".join(lines).rstrip() + "\n"


def summary_dict(report: EvalReport) -> dict[str, object]:
    """``summary.json`` 的对象，供以后做回归阈值比对。"""

    return {
        "schema_version": SCHEMA_VERSION,
        "evaluated_at": report.evaluated_at,
        "samples_path": report.samples_path,
        "samples_sha256": report.samples_sha256,
        "models_dir": report.models_dir,
        "seed": report.run.seed,
        "limit_per_direction": report.run.limit,
        "peak_rss_bytes": report.peak_rss_bytes,
        "ja_mecab": report.ja_mecab,
        "ja_mecab_note": report.ja_mecab_note,
        "environment": {
            "cpu_model": report.environment.cpu_model,
            "cpu_count": report.environment.cpu_count,
            "memory_bytes": report.environment.memory_bytes,
            "os": report.environment.os,
            "python": report.environment.python,
            "ctranslate2": report.environment.ctranslate2,
            "sacrebleu": report.environment.sacrebleu,
            "psutil": report.environment.psutil,
            "engine": report.environment.engine,
        },
        "models": [
            {
                "id": model.id,
                "hf_revision": model.hf_revision,
                "quantization": model.quantization,
                "ctranslate2_version": model.ctranslate2_version,
                "converted_at": model.converted_at,
            }
            for model in report.models
        ],
        "metrics": {
            "chrf": "sacrebleu.metrics.CHRF 默认（字符 n-gram，word_order=0），语料级，0-100",
            "bleu": (
                "sacrebleu.metrics.BLEU，中文 tokenize=zh，"
                "日文 ja-mecab 或 char，其余 13a，语料级，0-100"
            ),
            "proper_noun_retention": "must_keep.tgt 在译文中精确连续出现的项数 / 总项数",
            "ocr_chrf_delta": "句级 chrF(噪声译文, 参考) - chrF(干净译文, 参考) 的算术平均",
            "latency_ms": "preload 之后 Translator.elapsed_ms；P50/P95 为线性插值",
        },
        "directions": [_direction_dict(direction) for direction in report.run.directions],
        "examples": {label: [sample.id for sample in rows] for label, rows in report.run.examples},
        "proper_noun_misses": [
            {
                "id": sample.id,
                "direction": sample.direction,
                "missing": [hit.tgt for hit in sample.hits if not hit.kept],
            }
            for sample in missed_samples(report.run.samples)
        ],
    }


def result_record(sample: ScoredSample) -> dict[str, object]:
    """``results.jsonl`` 的一行。"""

    kept = sum(1 for hit in sample.hits if hit.kept)
    total = len(sample.hits)
    return {
        "id": sample.id,
        "src_lang": sample.src_lang,
        "tgt_lang": sample.tgt_lang,
        "direction": sample.direction,
        "category": sample.category,
        "source": sample.source,
        "reference": sample.reference,
        "hypothesis": sample.hypothesis,
        "route": list(sample.route),
        "route_label": sample.route_label,
        "elapsed_ms": _round(sample.elapsed_ms, 3),
        "chrf": _round(sample.chrf, 4),
        "bleu": _round(sample.bleu, 4),
        "proper_noun_kept": kept,
        "proper_noun_total": total,
        "proper_noun_retention": _round(kept / total, 4) if total else None,
        "must_keep": [_hit_dict(hit) for hit in sample.hits],
        "clean_id": sample.clean_id,
        "ocr_chrf_delta": _round(sample.ocr_chrf_delta, 4),
        "ocr_status": sample.ocr_status,
        "notes": sample.notes,
    }


def write_outputs(out_dir: Path, report: EvalReport) -> None:
    """写出 ``report.md``、``summary.json``、``results.jsonl``。"""

    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "report.md").write_text(render_markdown(report), encoding="utf-8")
    summary = json.dumps(summary_dict(report), ensure_ascii=False, indent=2) + "\n"
    (out_dir / "summary.json").write_text(summary, encoding="utf-8")
    lines = [json.dumps(result_record(sample), ensure_ascii=False) for sample in report.run.samples]
    (out_dir / "results.jsonl").write_text(
        "\n".join(lines) + ("\n" if lines else ""), encoding="utf-8"
    )


def format_bytes(size: int) -> str:
    """同时给出 GiB/MiB 和原始字节，方便和 `psutil` 对照。"""

    gib = size / (1024**3)
    if gib >= 1:
        return f"{gib:.1f} GiB（{size} 字节）"
    return f"{size / (1024**2):.1f} MiB（{size} 字节）"


def _direction_row(direction: DirectionResult) -> str:
    overall = direction.overall
    template = (
        "| {label} | {route} | {n} | {chrf} | {bleu} | {proper} | "
        "{ocr} | {load} | {p50} | {p95} | {mx} |"
    )
    return template.format(
        label=_cell(direction.label),
        route=_cell(_route_cell(direction)),
        n=overall.n,
        chrf=_fmt_score(overall.chrf),
        bleu=_fmt_score(overall.bleu),
        proper=_fmt_rate(overall),
        ocr=_fmt_ocr(overall.ocr_chrf_delta, overall.ocr_pairs),
        load=_fmt_ms(direction.load_ms),
        p50=_fmt_latency(overall, "p50"),
        p95=_fmt_latency(overall, "p95"),
        mx=_fmt_latency(overall, "max"),
    )


def _category_section(direction: DirectionResult) -> list[str]:
    route = _route_cell(direction)
    heading = f"### {direction.label}（route：{route}）"
    lines = [
        heading,
        "",
        f"模型：{', '.join(direction.model_ids) or '—'}。"
        f"本次新加载：{', '.join(direction.newly_loaded) or '无'}。"
        f"加载 {_fmt_ms(direction.load_ms)} ms。"
        f"BLEU 分词 `{direction.bleu_tokenize}`。",
        "",
        (
            "| 分类 | 条数 | 有参考 | chrF | BLEU | 专名保留率 | "
            "OCR chrF 差值 | P50（ms） | P95（ms） | max（ms） |"
        ),
        "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    for group in direction.categories:
        lines.append(
            (
                "| {key} | {n} | {nref} | {chrf} | {bleu} | "
                "{proper} | {ocr} | {p50} | {p95} | {mx} |"
            ).format(
                key=_cell(group.key),
                n=group.n,
                nref=group.n_with_reference,
                chrf=_fmt_score(group.chrf),
                bleu=_fmt_score(group.bleu),
                proper=_fmt_rate(group),
                ocr=_fmt_ocr(group.ocr_chrf_delta, group.ocr_pairs),
                p50=_fmt_latency(group, "p50"),
                p95=_fmt_latency(group, "p95"),
                mx=_fmt_latency(group, "max"),
            )
        )
    if direction.ocr_pairs_missing_clean:
        lines.append("")
        lines.append(
            f"有 {direction.ocr_pairs_missing_clean} 条 ocr_noise 找不到已评测的 clean_id，"
            "未计入 OCR 差值。"
        )
    lines.append("")
    return lines


def _metric_notes(report: EvalReport) -> list[str]:
    signature = ""
    bleu_signature = ""
    for direction in report.run.directions:
        if direction.overall.chrf_signature and not signature:
            signature = direction.overall.chrf_signature
        if direction.overall.bleu_signature and not bleu_signature:
            bleu_signature = direction.overall.bleu_signature
    lines = [
        "## 指标说明",
        "",
        "- chrF 是主指标，sacrebleu 默认 chrF2（去空白后的字符 n-gram）。"
        + (f" 签名：`{signature}`。" if signature else ""),
        "- BLEU：目标语为中文时 `tokenize=zh`，日文为 `ja-mecab`"
        "（不可用则 `char` 并在环境一节注明），"
        "其余为 `13a`。短句语料可能缺少 4-gram，语料级 BLEU 会偏低，回归以 chrF 为准。",
    ]
    if bleu_signature:
        lines.append(f"- 首个方向的 BLEU 签名：`{bleu_signature}`。")
    lines.append("- 专名保留只检查 `must_keep.tgt` 是否原样子串出现，不检查 `src`。")
    lines.append("- 只对参考译文去空白后非空的条目计算 chrF / BLEU。")
    lines.append("- 中转方向的 route 写成语种路径，例如 `ja→en→zh`，并列出两段模型 id。")
    lines.append("")
    return lines


def _direction_dict(direction: DirectionResult) -> dict[str, object]:
    return {
        "direction": direction.label,
        "src_lang": direction.src_lang,
        "tgt_lang": direction.tgt_lang,
        "route_label": direction.route_label,
        "pivot": direction.pivot,
        "model_ids": list(direction.model_ids),
        "newly_loaded_model_ids": list(direction.newly_loaded),
        "load_ms": _round(direction.load_ms, 3),
        "bleu_tokenize": direction.bleu_tokenize,
        "ocr_pairs_missing_clean": direction.ocr_pairs_missing_clean,
        **_group_dict(direction.overall),
        "categories": [_group_dict(group) for group in direction.categories],
    }


def _group_dict(group: GroupMetrics) -> dict[str, object]:
    latency = group.latency
    return {
        "key": group.key,
        "n": group.n,
        "n_with_reference": group.n_with_reference,
        "chrf": _round(group.chrf, 4),
        "bleu": _round(group.bleu, 4),
        "chrf_signature": group.chrf_signature,
        "bleu_signature": group.bleu_signature,
        "proper_noun_kept": group.proper_kept,
        "proper_noun_total": group.proper_total,
        "proper_noun_retention": _round(group.proper_noun_retention, 4),
        "ocr_chrf_delta": _round(group.ocr_chrf_delta, 4),
        "ocr_pairs": group.ocr_pairs,
        "latency_ms": None
        if latency is None
        else {
            "p50": _round(latency.p50_ms, 3),
            "p95": _round(latency.p95_ms, 3),
            "max": _round(latency.max_ms, 3),
            "n": latency.n,
        },
    }


def _hit_dict(hit: MustKeepHit) -> dict[str, object]:
    return {"src": hit.src, "tgt": hit.tgt, "kept": hit.kept}


def _route_cell(direction: DirectionResult) -> str:
    models = " → ".join(direction.model_ids)
    if models:
        return f"{direction.route_label}（{models}）"
    return direction.route_label


def _fmt_rate(group: GroupMetrics) -> str:
    if group.proper_noun_retention is None:
        return "—"
    percent = group.proper_noun_retention * 100
    return f"{group.proper_kept}/{group.proper_total}（{percent:.1f}%）"


def _fmt_ocr(delta: float | None, pairs: int) -> str:
    if delta is None:
        return "—"
    return f"{delta:+.2f}（n={pairs}）"


def _fmt_score(value: float | None) -> str:
    if value is None:
        return "—"
    return f"{value:.2f}"


def _fmt_ms(value: float) -> str:
    return f"{value:.1f}"


def _fmt_latency(group: GroupMetrics, which: str) -> str:
    if group.latency is None:
        return "—"
    if which == "p50":
        return _fmt_ms(group.latency.p50_ms)
    if which == "p95":
        return _fmt_ms(group.latency.p95_ms)
    return _fmt_ms(group.latency.max_ms)


def _limit_text(limit: int | None) -> str:
    if limit is None:
        return "不限"
    return str(limit)


def _cell(text: str) -> str:
    return text.replace("\r\n", " ").replace("\n", " ").replace("|", "\\|").strip()


def _flat(text: str) -> str:
    return text.replace("\r\n", "⏎").replace("\n", "⏎").replace("\r", "⏎")


def _round(value: float | None, digits: int) -> float | None:
    if value is None:
        return None
    return round(float(value), digits)


def read_model_meta(models_dir: Path, model_ids: Sequence[str]) -> tuple[ModelMeta, ...]:
    """从各模型目录的 ``suiyi-model.json`` 读取修订。缺文件时保留 id，其余为空。"""

    metas: list[ModelMeta] = []
    for model_id in model_ids:
        path = models_dir / model_id / "suiyi-model.json"
        data: Mapping[str, object] = {}
        if path.is_file():
            loaded = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                data = loaded
        metas.append(
            ModelMeta(
                id=model_id,
                hf_revision=_optional_str(data.get("hf_revision")),
                quantization=_optional_str(data.get("quantization")),
                ctranslate2_version=_optional_str(data.get("ctranslate2_version")),
                converted_at=_optional_str(data.get("converted_at")),
            )
        )
    return tuple(metas)


def _optional_str(value: object) -> str | None:
    if isinstance(value, str) and value.strip():
        return value
    return None
