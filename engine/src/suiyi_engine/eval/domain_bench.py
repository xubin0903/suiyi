"""专业领域翻译评测（#78）：同一测试集上比较多个候选模型。

命令行见 ``scripts/eval_domain.py``，方法与结果见 ``docs/engine/专业领域评测.md``。

- 测试集 ``engine/eval/domain/domain_v1.jsonl``、术语表 ``glossary.json``；
- 候选清单 ``engine/eval/domain/candidates.json``（名称、后端、模型路径、体积与许可证）；
- 每个候选单独一个进程跑（``run``），这样内存高水位只属于该候选；
  再用 ``report`` 把多份结果合成对比表。

后端（都只在评测里用，默认翻译流程不变）：

- ``suiyi``：现有 ``Translator``（基线，含 ja→en→zh 中转）；
- ``ct2``：任意 CTranslate2 seq2seq 模型（Marian / NLLB / M2M100），逐方向指定路由；
- ``llama``：启动 llama.cpp 的 ``llama-server`` 子进程，走本机 HTTP（只用标准库）。

torch / transformers 不在这里导入。COMET 另见 ``scripts/score_comet.py``。
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from collections.abc import Callable, Iterable, Mapping, Sequence
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Protocol

from suiyi_engine.eval.metrics import corpus_scores, latency_stats
from suiyi_engine.glossary import Term, load_glossary, term_present

SCHEMA_VERSION = 1
REPO_ROOT = Path(__file__).resolve().parents[4]
DOMAIN_DIR = REPO_ROOT / "engine" / "eval" / "domain"
DEFAULT_SAMPLES = DOMAIN_DIR / "domain_v1.jsonl"
DEFAULT_GLOSSARY = DOMAIN_DIR / "glossary.json"
DEFAULT_CANDIDATES = DOMAIN_DIR / "candidates.json"

DOMAINS = ("code", "ai", "hw", "med", "legal", "fin", "ui")
DOMAIN_LABELS = {
    "code": "编程/云原生",
    "ai": "AI/ML",
    "hw": "硬件/电子",
    "med": "医学",
    "legal": "法律",
    "fin": "金融",
    "ui": "UI 文案",
}
CATEGORIES = ("sentence", "paragraph")
DIRECTIONS = (
    ("en", "zh"),
    ("zh", "en"),
    ("ja", "zh"),
    ("ja", "en"),
    ("zh", "ja"),
    ("en", "ja"),
)
LANG_NAMES = {
    "en": ("English", "英语"),
    "zh": ("Chinese", "中文"),
    "ja": ("Japanese", "日语"),
}
# 快速子集：每个方向 × 领域取前 N 句单句和第一个段落；日文方向每个最多 QUICK_JA 条。
QUICK_SENTENCES = 2
QUICK_JA = 8
# 报告里的典型对比；第一条是用户反馈的 Kubernetes / CNCF 句。
TYPICAL_IDS = (
    "en-zh-code-009",
    "en-zh-code-001",
    "en-zh-code-010",
    "en-zh-ai-004",
    "en-zh-hw-007",
    "en-zh-med-001",
    "en-zh-legal-003",
    "en-zh-fin-001",
    "zh-en-code-010",
    "zh-en-ai-001",
    "zh-en-legal-002",
    "ja-zh-code-003",
)


class BenchError(Exception):
    """评测或参数错误。``code`` 是进程退出码。"""

    def __init__(self, message: str, code: int = 1) -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True, slots=True)
class DomainSample:
    id: str
    src_lang: str
    tgt_lang: str
    domain: str
    category: str
    source: str
    reference: str
    terms: tuple[str, ...]
    license: str

    @property
    def direction(self) -> tuple[str, str]:
        return (self.src_lang, self.tgt_lang)


_REQUIRED = (
    "id",
    "src_lang",
    "tgt_lang",
    "domain",
    "category",
    "source",
    "reference",
    "must_keep",
    "terms",
    "reference_status",
    "origin",
    "license",
)


def load_samples(path: Path) -> list[DomainSample]:
    if not path.is_file():
        raise BenchError(f"找不到测试集：{path}", code=2)
    samples: list[DomainSample] = []
    seen: set[str] = set()
    for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        where = f"{path.name}:{lineno}"
        try:
            row = json.loads(line)
        except json.JSONDecodeError as exc:
            raise BenchError(f"{where} 不是合法 JSON：{exc}") from exc
        missing = [key for key in _REQUIRED if key not in row]
        if missing:
            raise BenchError(f"{where} 缺少字段：{', '.join(missing)}")
        if row["id"] in seen:
            raise BenchError(f"{where} id 重复：{row['id']}")
        seen.add(row["id"])
        direction = (row["src_lang"], row["tgt_lang"])
        if direction not in DIRECTIONS:
            raise BenchError(f"{where} 不支持的方向：{direction}")
        if row["domain"] not in DOMAINS:
            raise BenchError(f"{where} 未知领域：{row['domain']}")
        if row["category"] not in CATEGORIES:
            raise BenchError(f"{where} 未知类别：{row['category']}")
        samples.append(
            DomainSample(
                id=row["id"],
                src_lang=row["src_lang"],
                tgt_lang=row["tgt_lang"],
                domain=row["domain"],
                category=row["category"],
                source=row["source"],
                reference=row["reference"],
                terms=tuple(row["terms"]),
                license=row["license"],
            )
        )
    if not samples:
        raise BenchError(f"测试集是空的：{path}")
    return samples


def select_samples(
    samples: Sequence[DomainSample],
    *,
    directions: Sequence[tuple[str, str]] | None = None,
    quick: bool = False,
    limit: int | None = None,
) -> list[DomainSample]:
    """按方向过滤；``quick`` 取固定的快速子集（见 ``QUICK_*``，并总是包含典型对比句）。"""

    wanted = set(directions) if directions else set(DIRECTIONS)
    chosen = [sample for sample in samples if sample.direction in wanted]
    if quick:
        picked: list[DomainSample] = []
        per_key: dict[tuple[str, str, str, str], int] = {}
        per_dir: dict[tuple[str, str], int] = {}
        for sample in chosen:
            is_ja = "ja" in sample.direction
            key = (sample.src_lang, sample.tgt_lang, sample.domain, sample.category)
            cap = 1 if sample.category == "paragraph" else QUICK_SENTENCES
            if is_ja and per_dir.get(sample.direction, 0) >= QUICK_JA:
                keep = False
            else:
                keep = per_key.get(key, 0) < cap
            if keep or sample.id in TYPICAL_IDS:
                picked.append(sample)
                per_key[key] = per_key.get(key, 0) + 1
                per_dir[sample.direction] = per_dir.get(sample.direction, 0) + 1
        chosen = picked
    if limit is not None:
        counts: dict[tuple[str, str], int] = {}
        limited: list[DomainSample] = []
        for sample in chosen:
            if counts.get(sample.direction, 0) < limit:
                limited.append(sample)
                counts[sample.direction] = counts.get(sample.direction, 0) + 1
        chosen = limited
    if not chosen:
        raise BenchError("没有可评测的样例", code=2)
    return chosen


# ---------------------------------------------------------------- 打分


@dataclass(frozen=True, slots=True)
class SampleResult:
    sample: DomainSample
    hypothesis: str
    elapsed_ms: float
    term_hits: tuple[str, ...]
    term_misses: tuple[str, ...]


def score_terms(
    hypothesis: str, sample: DomainSample, glossary: Mapping[str, Term]
) -> tuple[tuple[str, ...], tuple[str, ...]]:
    hits: list[str] = []
    misses: list[str] = []
    for term_id in sample.terms:
        term = glossary.get(term_id)
        if term is None:
            raise BenchError(f"{sample.id} 引用了术语表里没有的术语：{term_id}")
        if term_present(hypothesis, term, sample.tgt_lang):
            hits.append(term_id)
        else:
            misses.append(term_id)
    return tuple(hits), tuple(misses)


def _group_scores(results: Sequence[SampleResult], tgt_lang: str) -> dict[str, object]:
    hyps = [result.hypothesis for result in results]
    refs = [result.sample.reference for result in results]
    scores = corpus_scores(hyps, refs, tgt_lang, ja_mecab=False)
    hits = sum(len(result.term_hits) for result in results)
    total = hits + sum(len(result.term_misses) for result in results)
    return {
        "n": len(results),
        "chrf": _round(scores.chrf),
        "bleu": _round(scores.bleu),
        "term_hits": hits,
        "term_total": total,
        "term_acc": _round(100.0 * hits / total) if total else None,
    }


def summarize(results: Sequence[SampleResult]) -> dict[str, object]:
    """按方向汇总：整体、分领域、分类别的 chrF / BLEU / 术语准确率，以及单句 / 段落延迟。"""

    by_dir: dict[str, object] = {}
    for src, tgt in DIRECTIONS:
        rows = [result for result in results if result.sample.direction == (src, tgt)]
        if not rows:
            continue
        entry = _group_scores(rows, tgt)
        entry["domains"] = {
            domain: _group_scores(subset, tgt)
            for domain in DOMAINS
            if (subset := [row for row in rows if row.sample.domain == domain])
        }
        for category in CATEGORIES:
            latencies = [row.elapsed_ms for row in rows if row.sample.category == category]
            stats = latency_stats(latencies)
            entry[f"latency_{category}"] = (
                None
                if stats is None
                else {"p50_ms": _round(stats.p50_ms), "p95_ms": _round(stats.p95_ms), "n": stats.n}
            )
        by_dir[f"{src}-{tgt}"] = entry
    sentence = [row.elapsed_ms for row in results if row.sample.category == "sentence"]
    paragraph = [row.elapsed_ms for row in results if row.sample.category == "paragraph"]
    hits = sum(len(row.term_hits) for row in results)
    total = hits + sum(len(row.term_misses) for row in results)
    return {
        "directions": by_dir,
        "term_acc": _round(100.0 * hits / total) if total else None,
        "term_hits": hits,
        "term_total": total,
        "latency_sentence": _latency_dict(sentence),
        "latency_paragraph": _latency_dict(paragraph),
    }


def _latency_dict(values: Sequence[float]) -> dict[str, object] | None:
    stats = latency_stats(values)
    if stats is None:
        return None
    return {"p50_ms": _round(stats.p50_ms), "p95_ms": _round(stats.p95_ms), "n": stats.n}


def _round(value: float | None, digits: int = 1) -> float | None:
    return None if value is None else round(float(value), digits)


# ---------------------------------------------------------------- 候选清单


@dataclass(frozen=True)
class Candidate:
    name: str
    label: str
    kind: str
    spec: Mapping[str, object]
    license: str = ""
    redistributable: str = ""
    commercial: str = ""
    notes: str = ""

    def model_paths(self, models_dir: Path) -> list[Path]:
        """该候选用到的模型文件 / 目录（算磁盘体积）。"""

        paths: list[Path] = []
        if self.kind == "llama":
            paths.append(models_dir / str(self.spec["gguf"]))
        elif self.kind == "ct2":
            for hops in _routes(self.spec).values():
                for hop in hops:
                    path = models_dir / str(hop["model"])
                    if path not in paths:
                        paths.append(path)
        elif self.kind == "suiyi":
            for name in self.spec.get("models", []):  # type: ignore[union-attr]
                paths.append(models_dir / str(name))
        return paths


def load_candidates(path: Path) -> dict[str, Candidate]:
    if not path.is_file():
        raise BenchError(f"找不到候选清单：{path}", code=2)
    data = json.loads(path.read_text(encoding="utf-8"))
    found: dict[str, Candidate] = {}
    for item in data.get("candidates", []):
        name = str(item["name"])
        kind = str(item["kind"])
        if kind not in ("suiyi", "ct2", "llama"):
            raise BenchError(f"候选 {name} 的 kind 未知：{kind}")
        if name in found:
            raise BenchError(f"候选名称重复：{name}")
        found[name] = Candidate(
            name=name,
            label=str(item.get("label", name)),
            kind=kind,
            spec=item,
            license=str(item.get("license", "")),
            redistributable=str(item.get("redistributable", "")),
            commercial=str(item.get("commercial", "")),
            notes=str(item.get("notes", "")),
        )
    return found


def _routes(spec: Mapping[str, object]) -> dict[str, list[Mapping[str, object]]]:
    """``routes`` 逐方向列出各跳；只有 ``model`` 时表示一个多语模型直连全部方向。"""

    routes = spec.get("routes")
    if isinstance(routes, Mapping):
        return {str(key): list(value) for key, value in routes.items()}  # type: ignore[arg-type]
    hop = {"model": spec["model"], "family": spec.get("family", "marian")}
    return {f"{src}-{tgt}": [hop] for src, tgt in DIRECTIONS}


# ---------------------------------------------------------------- 后端


class Backend(Protocol):
    def supports(self, src: str, tgt: str) -> bool: ...

    def load(self, directions: Sequence[tuple[str, str]]) -> None: ...

    def translate(self, text: str, src: str, tgt: str) -> str: ...

    def close(self) -> None: ...

    def process_pid(self) -> int: ...


class SuiyiBackend:
    """现有 ``Translator``：与产品同一条路径（分句、批量、中转、拼回）。"""

    def __init__(self, models_dir: Path, threads: int) -> None:
        from suiyi_engine.translator import Translator

        self._translator = Translator(models_dir, intra_threads=threads)

    def supports(self, src: str, tgt: str) -> bool:
        return any(pair[0] == src and pair[1] == tgt for pair in self._translator.available_pairs())

    def load(self, directions: Sequence[tuple[str, str]]) -> None:
        self._translator.preload(list(directions))

    def translate(self, text: str, src: str, tgt: str) -> str:
        return self._translator.translate(text, src, tgt).text

    def close(self) -> None:
        return None

    def process_pid(self) -> int:
        return os.getpid()


NLLB_CODES = {"en": "eng_Latn", "zh": "zho_Hans", "ja": "jpn_Jpan"}
M2M_CODES = {"en": "__en__", "zh": "__zh__", "ja": "__ja__"}


class Ct2Model:
    """一个 CTranslate2 seq2seq 模型。``family``：marian / nllb / m2m100。"""

    def __init__(
        self,
        path: Path,
        family: str,
        *,
        prefix: str | None,
        threads: int,
        beam_size: int,
        max_decoding_length: int,
    ) -> None:
        import ctranslate2
        import sentencepiece as spm

        if family not in ("marian", "nllb", "m2m100"):
            raise BenchError(f"未知的 ct2 模型族：{family}")
        self.family = family
        self.prefix = prefix if prefix is not None else _suiyi_prefix(path)
        self._beam = beam_size
        self._max_len = max_decoding_length
        self._translator = ctranslate2.Translator(
            str(path), device="cpu", compute_type="int8", intra_threads=threads
        )
        if family == "marian":
            self._src_sp = spm.SentencePieceProcessor(model_file=str(path / "source.spm"))
            self._tgt_sp = spm.SentencePieceProcessor(model_file=str(path / "target.spm"))
        else:
            shared = spm.SentencePieceProcessor(model_file=str(path / "sentencepiece.bpe.model"))
            self._src_sp = self._tgt_sp = shared

    def translate_batch(self, sentences: Sequence[str], src: str, tgt: str) -> list[str]:
        from suiyi_engine.backends.ct2_opus import decode_hypothesis, prepare_source_tokens

        if not sentences:
            return []
        batch: list[list[str]] = []
        for sentence in sentences:
            pieces = self._src_sp.encode(sentence, out_type=str)
            if self.family == "marian":
                batch.append(prepare_source_tokens(pieces, self.prefix))
            else:
                code = (NLLB_CODES if self.family == "nllb" else M2M_CODES)[src]
                batch.append([code, *pieces, "</s>"])
        prefix = None
        if self.family != "marian":
            code = (NLLB_CODES if self.family == "nllb" else M2M_CODES)[tgt]
            prefix = [[code] for _ in batch]
        results = self._translator.translate_batch(
            batch,
            target_prefix=prefix,
            beam_size=self._beam,
            max_decoding_length=self._max_len,
            max_batch_size=32,
        )
        out: list[str] = []
        for result in results:
            tokens = list(result.hypotheses[0]) if result.hypotheses else []
            if self.family != "marian" and tokens:
                tokens = tokens[1:]
            out.append(decode_hypothesis(self._tgt_sp, tokens, tgt))
        return out


def _suiyi_prefix(path: Path) -> str | None:
    meta = path / "suiyi-model.json"
    if meta.is_file():
        value = json.loads(meta.read_text(encoding="utf-8")).get("src_prefix_token")
        return str(value) if value else None
    return None


class Ct2Backend:
    """逐方向路由的 CTranslate2 候选。长文按 ``split_sentences`` 分句，与产品一致。"""

    def __init__(self, candidate: Candidate, models_dir: Path, threads: int) -> None:
        self._routes = _routes(candidate.spec)
        self._models_dir = models_dir
        self._threads = threads
        self._beam = int(candidate.spec.get("beam_size", 2))  # type: ignore[arg-type]
        self._models: dict[str, Ct2Model] = {}

    def supports(self, src: str, tgt: str) -> bool:
        return f"{src}-{tgt}" in self._routes

    def load(self, directions: Sequence[tuple[str, str]]) -> None:
        for src, tgt in directions:
            for hop in self._routes[f"{src}-{tgt}"]:
                self._model(hop)

    def _model(self, hop: Mapping[str, object]) -> Ct2Model:
        key = str(hop["model"])
        if key not in self._models:
            prefix = hop.get("prefix")
            self._models[key] = Ct2Model(
                self._models_dir / key,
                str(hop.get("family", "marian")),
                prefix=None if prefix is None else str(prefix),
                threads=self._threads,
                beam_size=self._beam,
                max_decoding_length=512,
            )
        return self._models[key]

    def translate(self, text: str, src: str, tgt: str) -> str:
        from suiyi_engine.segment import join_segments, split_sentences

        segments = split_sentences(text, lang=src)
        if not segments:
            return ""
        current = [segment.text for segment in segments]
        hops = self._routes[f"{src}-{tgt}"]
        lang = src
        for index, hop in enumerate(hops):
            nxt = tgt if index == len(hops) - 1 else str(hop.get("tgt", "en"))
            current = self._model(hop).translate_batch(current, lang, nxt)
            lang = nxt
        return join_segments(current, segments, tgt)

    def close(self) -> None:
        self._models.clear()

    def process_pid(self) -> int:
        return os.getpid()


# llama.cpp：提示词模板。{src} / {tgt} 是语种名，{text} 是原文。
HY_ZH_PROMPT = "将以下文本翻译为{tgt_zh}，注意只需要输出翻译后的结果，不要额外解释：\n\n{text}"
HY_XX_PROMPT = (
    "Translate the following segment into {tgt}, without additional explanation.\n\n{text}"
)
GENERIC_PROMPT = (
    "Translate the following {src} text into {tgt}. Output only the translation, "
    "with no explanations or notes.\n\n{text}"
)
GEMMA_TRANSLATE_RAW = (
    "<start_of_turn>user\n"
    "You are a professional {src} ({src_code}) to {tgt} ({tgt_code}) translator. "
    "Your goal is to accurately convey the meaning and nuances of the original {src} text "
    "while adhering to {tgt} grammar, vocabulary, and cultural sensitivities.\n"
    "Produce only the {tgt} translation, without any additional explanations or commentary. "
    "Please translate the following {src} text into {tgt}:\n\n\n{text}<end_of_turn>\n"
    "<start_of_turn>model\n"
)
GEMMA_LANG_CODES = {"en": "en", "zh": "zh-Hans", "ja": "ja"}


def build_prompt(style: str, text: str, src: str, tgt: str) -> str:
    """按候选的提示词风格拼出用户消息（``gemma-translate`` 返回完整原始提示）。"""

    names = {
        "src": LANG_NAMES[src][0],
        "tgt": LANG_NAMES[tgt][0],
        "tgt_zh": LANG_NAMES[tgt][1],
        "text": text,
    }
    if style == "hy-mt":
        template = HY_ZH_PROMPT if "zh" in (src, tgt) else HY_XX_PROMPT
        return template.format(**names)
    if style == "generic":
        return GENERIC_PROMPT.format(**names)
    if style == "gemma-translate":
        return GEMMA_TRANSLATE_RAW.format(
            src_code=GEMMA_LANG_CODES[src], tgt_code=GEMMA_LANG_CODES[tgt], **names
        )
    raise BenchError(f"未知的提示词风格：{style}")


_THINK = re.compile(r"<think>.*?</think>", re.DOTALL)


def clean_llm_output(text: str) -> str:
    """去掉思考块、首尾空白和整段包裹的引号 / 代码块。"""

    text = _THINK.sub("", text).strip()
    if text.startswith("```") and text.endswith("```"):
        text = text.strip("`").strip()
    for left, right in (('"', '"'), ("“", "”"), ("「", "」")):
        if len(text) > 1 and text.startswith(left) and text.endswith(right):
            inner = text[1:-1]
            if left not in inner and right not in inner:
                text = inner.strip()
    return text


class LlamaServerBackend:
    """启动 ``llama-server`` 子进程，通过本机 HTTP 翻译。只用标准库。

    ``api=chat`` 走 ``/v1/chat/completions``（用 GGUF 自带的聊天模板，``--jinja``）；
    ``api=completion`` 走 ``/completion``，提示由 :func:`build_prompt` 原样给出。
    解码一律贪心（temperature 0），便于复现。
    """

    def __init__(
        self,
        candidate: Candidate,
        models_dir: Path,
        threads: int,
        server_bin: Path,
        *,
        ctx: int = 4096,
        log_path: Path | None = None,
    ) -> None:
        spec = candidate.spec
        self._gguf = models_dir / str(spec["gguf"])
        self._style = str(spec.get("prompt", "generic"))
        self._api = str(spec.get("api", "chat"))
        self._extra = dict(spec.get("request", {}))  # type: ignore[arg-type]
        self._threads = threads
        self._ctx = ctx
        self._bin = server_bin
        self._log_path = log_path
        self._proc: subprocess.Popen[bytes] | None = None
        self._port = 0
        self.load_ms: float | None = None

    def supports(self, src: str, tgt: str) -> bool:
        return True

    def load(self, directions: Sequence[tuple[str, str]]) -> None:
        if self._proc is not None:
            return
        if not self._gguf.is_file():
            raise BenchError(f"找不到 GGUF：{self._gguf}", code=2)
        if not self._bin.is_file():
            raise BenchError(f"找不到 llama-server：{self._bin}", code=2)
        self._port = _free_port()
        args = [
            str(self._bin),
            "-m",
            str(self._gguf),
            "--host",
            "127.0.0.1",
            "--port",
            str(self._port),
            "-t",
            str(self._threads),
            "-c",
            str(self._ctx),
            "-np",
            "1",
            "--no-webui",
        ]
        # 新版 llama-server 默认启用 jinja 模板；completion 模式自己拼提示，关掉模板解析，
        # 以免 TranslateGemma 这类要求结构化 content 的模板在启动时报错。
        args.append("--jinja" if self._api == "chat" else "--no-jinja")
        log = open(self._log_path, "wb") if self._log_path else subprocess.DEVNULL  # noqa: SIM115
        started = time.perf_counter()
        self._proc = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT)
        deadline = started + 600
        while time.perf_counter() < deadline:
            if self._proc.poll() is not None:
                raise BenchError(f"llama-server 提前退出（{self._proc.returncode}）")
            try:
                with urllib.request.urlopen(self._url("/health"), timeout=2) as response:
                    if response.status == 200:
                        break
            except (urllib.error.URLError, ConnectionError, TimeoutError):
                pass
            time.sleep(0.2)
        else:
            raise BenchError("llama-server 10 分钟内没有就绪")
        self.load_ms = (time.perf_counter() - started) * 1000.0

    def _url(self, path: str) -> str:
        return f"http://127.0.0.1:{self._port}{path}"

    def translate(self, text: str, src: str, tgt: str) -> str:
        prompt = build_prompt(self._style, text, src, tgt)
        budget = max(128, min(2048, len(text) * 4))
        if self._api == "chat":
            payload: dict[str, object] = {
                "messages": [{"role": "user", "content": prompt}],
                "temperature": 0,
                "max_tokens": budget,
                **self._extra,
            }
            data = _post_json(self._url("/v1/chat/completions"), payload)
            content = data["choices"][0]["message"]["content"]  # type: ignore[index]
        else:
            payload = {
                "prompt": prompt,
                "temperature": 0,
                "n_predict": budget,
                "cache_prompt": False,
                **self._extra,
            }
            content = _post_json(self._url("/completion"), payload)["content"]
        return clean_llm_output(str(content or ""))

    def close(self) -> None:
        if self._proc is not None and self._proc.poll() is None:
            self._proc.terminate()
            try:
                self._proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self._proc.kill()
        self._proc = None

    def process_pid(self) -> int:
        if self._proc is None:
            raise BenchError("llama-server 尚未启动")
        return self._proc.pid


def _post_json(url: str, payload: Mapping[str, object]) -> dict[str, object]:
    body = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url, data=body, headers={"Content-Type": "application/json"}, method="POST"
    )
    with urllib.request.urlopen(request, timeout=600) as response:
        return json.loads(response.read().decode("utf-8"))


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


# ---------------------------------------------------------------- 内存


def process_rss_mb(pid: int) -> float | None:
    try:
        import psutil
    except ImportError:
        return None
    try:
        return psutil.Process(pid).memory_info().rss / 1024 / 1024
    except psutil.Error:
        return None


def process_peak_mb(pid: int) -> float | None:
    """进程峰值常驻内存：Linux 读 ``VmHWM``，Windows 用 ``peak_wset``，其余返回 ``None``。"""

    status = Path(f"/proc/{pid}/status")
    if status.is_file():
        for line in status.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("VmHWM:"):
                return int(line.split()[1]) / 1024
    if sys.platform == "win32":
        try:
            import psutil

            info = psutil.Process(pid).memory_info()
            return getattr(info, "peak_wset", info.rss) / 1024 / 1024
        except (ImportError, OSError):
            return None
    return None


def disk_mb(paths: Iterable[Path]) -> float:
    total = 0
    for path in paths:
        if path.is_file():
            total += path.stat().st_size
        elif path.is_dir():
            total += sum(item.stat().st_size for item in path.rglob("*") if item.is_file())
    return total / 1024 / 1024


# ---------------------------------------------------------------- 运行


@dataclass
class RunConfig:
    candidate: Candidate
    models_dir: Path
    samples: list[DomainSample]
    glossary: Mapping[str, Term]
    threads: int = 4
    server_bin: Path | None = None
    label: str = ""
    extra: dict[str, object] = field(default_factory=dict)


def make_backend(config: RunConfig, log_dir: Path | None) -> Backend:
    candidate = config.candidate
    if candidate.kind == "suiyi":
        return SuiyiBackend(config.models_dir, config.threads)
    if candidate.kind == "ct2":
        return Ct2Backend(candidate, config.models_dir, config.threads)
    if config.server_bin is None:
        raise BenchError("llama 候选需要 --llama-server 指向 llama-server 可执行文件", code=2)
    log_path = None if log_dir is None else log_dir / f"{candidate.name}.server.log"
    return LlamaServerBackend(
        candidate, config.models_dir, config.threads, config.server_bin, log_path=log_path
    )


def run_candidate(
    config: RunConfig,
    *,
    backend: Backend | None = None,
    log: Callable[[str], None] | None = None,
    log_dir: Path | None = None,
    translate: Callable[[Backend, str, str, str], str] | None = None,
) -> dict[str, object]:
    """跑一个候选，返回可写成 JSON 的结果（含逐条译文）。``backend`` 仅供测试注入。"""

    emit = log or (lambda _message: None)
    call = translate or (lambda b, text, src, tgt: b.translate(text, src, tgt))
    base_mb = process_rss_mb(os.getpid()) if config.candidate.kind != "llama" else 0.0
    if backend is None:
        backend = make_backend(config, log_dir)
    directions = [d for d in DIRECTIONS if any(s.direction == d for s in config.samples)]
    unsupported = [d for d in directions if not backend.supports(*d)]
    if unsupported:
        names = "、".join(f"{src}→{tgt}" for src, tgt in unsupported)
        emit(f"{config.candidate.name} 不支持：{names}，跳过这些方向")
    directions = [d for d in directions if d not in unsupported]
    try:
        started = time.perf_counter()
        backend.load(directions)
        load_ms = getattr(backend, "load_ms", None) or (time.perf_counter() - started) * 1000.0
        first_ms: dict[str, float] = {}
        for src, tgt in directions:
            warm = next(s for s in config.samples if s.direction == (src, tgt))
            t0 = time.perf_counter()
            call(backend, warm.source, src, tgt)
            first_ms[f"{src}-{tgt}"] = (time.perf_counter() - t0) * 1000.0
        pid = backend.process_pid()
        loaded_mb = process_rss_mb(pid)
        results: list[SampleResult] = []
        todo = [s for s in config.samples if s.direction in directions]
        for index, sample in enumerate(todo, 1):
            t0 = time.perf_counter()
            hypothesis = call(backend, sample.source, sample.src_lang, sample.tgt_lang)
            elapsed = (time.perf_counter() - t0) * 1000.0
            hits, misses = score_terms(hypothesis, sample, config.glossary)
            results.append(SampleResult(sample, hypothesis, elapsed, hits, misses))
            if index % 25 == 0 or index == len(todo):
                emit(f"  [{index}/{len(todo)}] {sample.id} {elapsed:.0f} ms")
        end_mb = process_rss_mb(pid)
        peak_mb = process_peak_mb(pid)
    finally:
        backend.close()
    summary = summarize(results)
    candidate = config.candidate
    return {
        "schema_version": SCHEMA_VERSION,
        "candidate": {
            "name": candidate.name,
            "label": candidate.label,
            "kind": candidate.kind,
            "license": candidate.license,
            "redistributable": candidate.redistributable,
            "commercial": candidate.commercial,
            "notes": candidate.notes,
        },
        "label": config.label,
        "settings": {
            "threads": config.threads,
            "samples": len(results),
            "directions": [f"{src}-{tgt}" for src, tgt in directions],
            "skipped_directions": [f"{src}-{tgt}" for src, tgt in unsupported],
            **config.extra,
        },
        "environment": _environment(),
        "evaluated_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "load_ms": _round(load_ms),
        "first_ms": {key: _round(value) for key, value in first_ms.items()},
        "memory_mb": {
            "base": _round(base_mb),
            "loaded": _round(loaded_mb),
            "end": _round(end_mb),
            "peak": _round(peak_mb),
            "resident": _round(_minus(end_mb, base_mb)),
            "peak_over_base": _round(_minus(peak_mb, base_mb)),
        },
        "disk_mb": _round(disk_mb(candidate.model_paths(config.models_dir))),
        "summary": summary,
        "samples": [
            {
                "id": row.sample.id,
                "direction": f"{row.sample.src_lang}-{row.sample.tgt_lang}",
                "domain": row.sample.domain,
                "category": row.sample.category,
                "source": row.sample.source,
                "reference": row.sample.reference,
                "hypothesis": row.hypothesis,
                "elapsed_ms": _round(row.elapsed_ms),
                "term_hits": list(row.term_hits),
                "term_misses": list(row.term_misses),
            }
            for row in results
        ],
    }


def _minus(value: float | None, base: float | None) -> float | None:
    if value is None or base is None:
        return None
    return value - base


def _environment() -> dict[str, object]:
    cpu = platform.processor() or platform.machine()
    cpuinfo = Path("/proc/cpuinfo")
    if cpuinfo.is_file():
        for line in cpuinfo.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.lower().startswith("model name"):
                cpu = line.split(":", 1)[1].strip()
                break
    affinity = None
    if hasattr(os, "sched_getaffinity"):
        affinity = len(os.sched_getaffinity(0))
    return {
        "os": platform.platform(),
        "python": platform.python_version(),
        "cpu": cpu,
        "cpu_count": os.cpu_count(),
        "cpu_affinity": affinity,
        "ctranslate2": _version("ctranslate2"),
        "sacrebleu": _version("sacrebleu"),
        # KVM 虚拟机上 MKL 的 AMX int8 路径在多进程争用时会算出乱码，
        # 评测机用 MKL_ENABLE_INSTRUCTIONS=AVX512_E1 关掉 AMX，这里记下实际取值。
        "mkl_enable_instructions": os.environ.get("MKL_ENABLE_INSTRUCTIONS"),
    }


def _version(name: str) -> str:
    from importlib.metadata import PackageNotFoundError, version

    try:
        return version(name)
    except PackageNotFoundError:
        return "未安装"


# ---------------------------------------------------------------- 报告


def _fmt(value: object, suffix: str = "") -> str:
    if value is None:
        return "—"
    if isinstance(value, float):
        return f"{value:.1f}{suffix}"
    return f"{value}{suffix}"


def _lat(entry: object) -> str:
    if not isinstance(entry, Mapping):
        return "—"
    return f"{entry['p50_ms']:.0f} / {entry['p95_ms']:.0f}"


def _dir_metric(result: Mapping[str, object], direction: str, key: str) -> object:
    dirs = result["summary"]["directions"]  # type: ignore[index]
    entry = dirs.get(direction) if isinstance(dirs, Mapping) else None
    return None if not isinstance(entry, Mapping) else entry.get(key)


def render_report(results: Sequence[Mapping[str, object]], *, title: str = "") -> str:
    """把多个候选的结果合成 Markdown 对比表。"""

    lines = [f"# {title or '专业领域翻译评测'}", ""]
    if results:
        first = results[0]
        env = first["environment"]
        lines += [
            f"- 环境：{env['os']}，CPU {env['cpu']}（可用核 {env['cpu_affinity']}），"  # type: ignore[index]
            f"Python {env['python']}，ctranslate2 {env['ctranslate2']}，"  # type: ignore[index]
            f"sacrebleu {env['sacrebleu']}"  # type: ignore[index]
            + (
                f"，MKL_ENABLE_INSTRUCTIONS={env['mkl_enable_instructions']}"  # type: ignore[index]
                if env.get("mkl_enable_instructions")  # type: ignore[union-attr]
                else ""
            ),
            f"- 线程：{first['settings']['threads']}；样例数：{first['settings']['samples']}"  # type: ignore[index]
            f"{'（快速子集）' if first['settings'].get('quick') else ''}",  # type: ignore[union-attr]
            "- chrF / BLEU 为 sacrebleu 语料级 0–100；术语准确率 = 译文里按术语表写法出现的术语 / "
            "原文里出现的术语；延迟为单条 P50 / P95（ms）；内存为进程 RSS（MB）。",
            "",
        ]
    dirs = [f"{src}-{tgt}" for src, tgt in DIRECTIONS]
    head = ["候选"] + [d.replace("-", "→") for d in dirs] + ["术语准确率"]
    has_comet = any(_dir_metric(result, d, "comet") is not None for result in results for d in dirs)
    lines += ["## chrF（分方向）与术语准确率", ""]
    if has_comet:
        lines.append("括号内为 COMET（wmt22-comet-da，×100）。")
        lines.append("")
    lines += ["| " + " | ".join(head) + " |", "|" + "---|" * len(head)]
    for result in results:
        cells = [str(result["candidate"]["label"])]  # type: ignore[index]
        for d in dirs:
            chrf = _dir_metric(result, d, "chrf")
            comet = _dir_metric(result, d, "comet")
            cell = _fmt(chrf)
            if comet is not None:
                cell += f"（{comet:.1f}）"
            cells.append(cell)
        summary = result["summary"]
        cells.append(
            f"{_fmt(summary['term_acc'], '%')}（{summary['term_hits']}/{summary['term_total']}）"  # type: ignore[index]
        )
        lines.append("| " + " | ".join(cells) + " |")
    lines += [
        "",
        "## 速度、资源与许可证",
        "",
        "| 候选 | 单句 P50 / P95 | 段落 P50 / P95 | 加载 | 常驻 / 峰值 | 体积 | 许可证 "
        "| 可随项目分发 | 商用 |",
        "|---|---|---|---|---|---|---|---|---|",
    ]
    for result in results:
        summary = result["summary"]
        memory = result["memory_mb"]
        cand = result["candidate"]
        lines.append(
            (
                "| {label} | {s} | {p} | {load} | {res} / {peak} | {disk} | {lic} "
                "| {dist} | {com} |"
            ).format(
                label=cand["label"],  # type: ignore[index]
                s=_lat(summary["latency_sentence"]),  # type: ignore[index]
                p=_lat(summary["latency_paragraph"]),  # type: ignore[index]
                load=_fmt(result["load_ms"], " ms"),
                res=_fmt(memory["resident"]),  # type: ignore[index]
                peak=_fmt(memory["peak_over_base"]),  # type: ignore[index]
                disk=_fmt(result["disk_mb"], " MB"),
                lic=cand["license"] or "—",  # type: ignore[index]
                dist=cand["redistributable"] or "—",  # type: ignore[index]
                com=cand["commercial"] or "—",  # type: ignore[index]
            )
        )
    for d in ("en-zh", "zh-en"):
        present = [r for r in results if _dir_metric(r, d, "domains")]
        if not present:
            continue
        lines += [
            "",
            f"## {d.replace('-', '→')} 分领域 chrF / 术语准确率",
            "",
            "| 候选 | " + " | ".join(DOMAIN_LABELS[x] for x in DOMAINS) + " |",
            "|" + "---|" * (len(DOMAINS) + 1),
        ]
        for result in present:
            domains = _dir_metric(result, d, "domains")
            cells = [str(result["candidate"]["label"])]  # type: ignore[index]
            for domain in DOMAINS:
                entry = domains.get(domain) if isinstance(domains, Mapping) else None
                if not isinstance(entry, Mapping):
                    cells.append("—")
                    continue
                cells.append(f"{_fmt(entry['chrf'])} / {_fmt(entry['term_acc'], '%')}")
            lines.append("| " + " | ".join(cells) + " |")
    lines += ["", "## 典型译例", ""]
    by_id: dict[str, dict[str, str]] = {}
    meta: dict[str, tuple[str, str]] = {}
    for result in results:
        for row in result["samples"]:  # type: ignore[union-attr]
            if row["id"] in TYPICAL_IDS:
                by_id.setdefault(row["id"], {})[str(result["candidate"]["label"])] = row[  # type: ignore[index]
                    "hypothesis"
                ]
                meta[row["id"]] = (row["source"], row["reference"])
    for sample_id in TYPICAL_IDS:
        if sample_id not in by_id:
            continue
        source, reference = meta[sample_id]
        lines += [f"**{sample_id}**：{source}", "", f"- 参考：{reference}"]
        for label, hypothesis in by_id[sample_id].items():
            lines.append(f"- {label}：{hypothesis}")
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


# ---------------------------------------------------------------- 命令行


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="scripts/eval_domain.py",
        description="专业领域翻译评测（#78）：跑候选模型、合成对比报告。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "示例：\n"
            "  python scripts/eval_domain.py run --candidate opus-mt --models-dir models "
            "--out reports/domain --quick\n"
            "  python scripts/eval_domain.py report reports/domain/*.json "
            "--out reports/domain/report.md\n"
            "详见 docs/engine/专业领域评测.md。"
        ),
    )
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run", help="跑一个候选，写出 <out>/<name>.json")
    run.add_argument("--candidate", required=True, help="候选名称（见 candidates.json）")
    run.add_argument("--models-dir", type=Path, required=True, help="模型根目录")
    run.add_argument("--out", type=Path, required=True, help="输出目录")
    run.add_argument("--samples", type=Path, default=DEFAULT_SAMPLES)
    run.add_argument("--glossary", type=Path, default=DEFAULT_GLOSSARY)
    run.add_argument("--candidates", type=Path, default=DEFAULT_CANDIDATES)
    run.add_argument("--directions", help="只跑这些方向，逗号分隔，如 en-zh,zh-en")
    run.add_argument("--quick", action="store_true", help="只跑快速子集（初筛与 CI 用）")
    run.add_argument("--limit", type=int, help="每个方向最多 N 条")
    run.add_argument("--threads", type=int, default=4, help="推理线程数，默认 4")
    run.add_argument("--llama-server", type=Path, help="llama-server 可执行文件（llama 候选需要）")
    run.add_argument("--label", default="", help="写进报告的环境说明")
    run.add_argument("--name", help="输出文件名（默认同候选名）")
    report = sub.add_parser("report", help="把多个 run 结果合成 Markdown")
    report.add_argument("results", nargs="+", type=Path)
    report.add_argument("--out", type=Path, required=True)
    report.add_argument("--title", default="")
    return parser


def parse_directions(raw: str | None) -> list[tuple[str, str]] | None:
    if not raw:
        return None
    parsed: list[tuple[str, str]] = []
    for token in raw.split(","):
        token = token.strip().replace("→", "-")
        if not token:
            continue
        pair = tuple(token.split("-"))
        if pair not in DIRECTIONS:
            raise BenchError(f"不支持的方向：{token}", code=2)
        parsed.append(pair)  # type: ignore[arg-type]
    return parsed


def main(argv: Sequence[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (OSError, ValueError):
                pass
    args = build_parser().parse_args(None if argv is None else list(argv))
    try:
        if args.command == "report":
            loaded = [json.loads(path.read_text(encoding="utf-8")) for path in args.results]
            args.out.parent.mkdir(parents=True, exist_ok=True)
            args.out.write_text(render_report(loaded, title=args.title), encoding="utf-8")
            print(f"已写入 {args.out}", file=sys.stderr)
            return 0
        return _run(args)
    except BenchError as exc:
        print(str(exc), file=sys.stderr)
        return exc.code


def _run(args: argparse.Namespace) -> int:
    candidates = load_candidates(args.candidates)
    if args.candidate not in candidates:
        raise BenchError(
            f"候选清单里没有 {args.candidate}；可选：{', '.join(sorted(candidates))}", code=2
        )
    glossary = {term.id: term for term in load_glossary(args.glossary)}
    samples = select_samples(
        load_samples(args.samples),
        directions=parse_directions(args.directions),
        quick=args.quick,
        limit=args.limit,
    )
    out_dir: Path = args.out
    out_dir.mkdir(parents=True, exist_ok=True)
    config = RunConfig(
        candidate=candidates[args.candidate],
        models_dir=args.models_dir.expanduser().resolve(),
        samples=samples,
        glossary=glossary,
        threads=args.threads,
        server_bin=args.llama_server,
        label=args.label,
        extra={"quick": bool(args.quick), "limit": args.limit},
    )
    log = lambda message: print(message, file=sys.stderr, flush=True)  # noqa: E731
    log(f"{args.candidate}：{len(samples)} 条，线程 {args.threads}")
    result = run_candidate(config, log=log, log_dir=out_dir)
    name = args.name or args.candidate
    path = out_dir / f"{name}.json"
    path.write_text(json.dumps(result, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    summary = result["summary"]
    for direction, entry in summary["directions"].items():  # type: ignore[union-attr]
        log(f"  {direction}: chrF {entry['chrf']} 术语 {entry['term_acc']}%")
    log(f"已写入 {path}")
    return 0
