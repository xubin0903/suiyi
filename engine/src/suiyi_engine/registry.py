"""模型目录扫描、清单路由与懒加载缓存。

权重在第一次用到某个模型 id 时才加载。扫描阶段只读 ``suiyi-model.json``。
"""

from __future__ import annotations

import json
import os
import threading
import time
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from pathlib import Path

from suiyi_engine.backends.base import TranslationBackend
from suiyi_engine.errors import UnsupportedPairError

__all__ = [
    "Manifest",
    "ModelRecord",
    "ModelRegistry",
    "Pivot",
    "default_manifest_path",
    "default_models_dir",
    "normalize_lang",
    "repo_root",
]

_METADATA_NAME = "suiyi-model.json"
_LANG_CODES = frozenset("abcdefghijklmnopqrstuvwxyz")


def repo_root() -> Path:
    """源码布局下的仓库根目录。

    ``engine/src/suiyi_engine/registry.py`` 向上四级是仓库根。
    安装成 wheel 且不在该布局里时，请设置 ``SUIYI_MODELS_DIR``。
    """

    return Path(__file__).resolve().parents[3]


def default_manifest_path() -> Path:
    """``engine/model_manifest.json``。"""

    path = Path(__file__).resolve().parents[2] / "model_manifest.json"
    if not path.is_file():
        raise FileNotFoundError(f"找不到模型清单：{path}")
    return path


def default_models_dir() -> Path:
    """``SUIYI_MODELS_DIR``，否则仓库根目录下的 ``models/``。"""

    raw = os.environ.get("SUIYI_MODELS_DIR", "").strip()
    if raw:
        return Path(raw)
    return repo_root() / "models"


def normalize_lang(code: str) -> str:
    """把语种代码收成 ISO 639-1 小写二字母。

    ``zh-CN`` / ``zh_Hans`` 取主标签 ``zh``。``auto`` 不在翻译核心里检测，
    调用方应先跑语种检测再传入具体代码。
    """

    if not isinstance(code, str):
        raise ValueError(f"语种代码必须是字符串，收到 {type(code).__name__}")
    raw = code.strip()
    if raw.lower() == "auto":
        raise ValueError("语种代码不接受 auto。请先做语种检测，再传入 ISO 639-1 代码。")
    primary = raw.lower().replace("_", "-").split("-", 1)[0]
    if len(primary) != 2 or any(ch not in _LANG_CODES for ch in primary):
        raise ValueError(f"无法识别的语种代码：{code!r}")
    return primary


# 与 Translator / CTranslate2 后端共用，避免基准脚本和运行时各写一套默认值。
DEFAULT_INTER_THREADS = 1
DEFAULT_BEAM_SIZE = 2
DEFAULT_MAX_BATCH_SIZE = 32
DEFAULT_MAX_DECODING_LENGTH = 512


DEFAULT_MAX_INTRA_THREADS = 4
FALLBACK_INTRA_THREADS = 2


def default_intra_threads() -> int:
    """单模型 intra 线程数：``min(4, os.cpu_count())``，拿不到 CPU 数时为 2。

    #87 起从 ``min(2, CPU 数)`` 改为 4：短句上 2 线程略快，但 #83 换成 tc-big 并开启术语保护后，
    2 线程的段落 P95 超过 200 ms。数据见 ``docs/engine/术语保护.md`` 与 ``性能基线.md``。
    """

    count = os.cpu_count()
    return min(DEFAULT_MAX_INTRA_THREADS, count) if count else FALLBACK_INTRA_THREADS


@dataclass(frozen=True, slots=True)
class ModelRecord:
    """一个已安装模型目录的元数据。权重尚未加载。"""

    id: str
    src: str
    tgt: str
    model_dir: Path
    src_prefix_token: str | None
    quantization: str


@dataclass(frozen=True, slots=True)
class Pivot:
    """清单中的一条英文中转。``legs`` 是按顺序的两段模型 id。"""

    src: str
    tgt: str
    via: str
    legs: tuple[str, str]


@dataclass(frozen=True, slots=True)
class Manifest:
    """路由用到的清单子集：直连模型 id，以及声明过的中转。"""

    direct: Mapping[tuple[str, str], str]
    pivots: Mapping[tuple[str, str], Pivot]
    # 清单里列出的全部模型 id → 方向（含 tier=legacy 的旧模型）。中转的某一跳没装清单指定的
    # 模型、但装了清单里同方向的另一个模型（升级前的旧 en→zh）时，用已安装的那个（#83）。
    known: Mapping[str, tuple[str, str]] = field(default_factory=dict)


BackendFactory = Callable[[ModelRecord], TranslationBackend]


class ModelRegistry:
    """扫描 ``models_dir``，按需构造后端并缓存。

    同一个模型 id 并发第一次加载时只构造一次。锁盖住构造过程，
    构造完成后的 ``translate_batch`` 不再持有这把锁。
    """

    def __init__(
        self,
        models_dir: Path | str | None = None,
        *,
        manifest_path: Path | str | None = None,
        backend_factory: BackendFactory | None = None,
        backend_options: Mapping[str, object] | None = None,
    ) -> None:
        self.models_dir = Path(models_dir) if models_dir is not None else default_models_dir()
        self.manifest_path = (
            Path(manifest_path) if manifest_path is not None else default_manifest_path()
        )
        self.manifest = load_manifest(self.manifest_path)
        self._by_id, self._by_direction = scan_models(self.models_dir, self.manifest)
        options = dict(backend_options or {})
        self._factory = backend_factory or _default_factory(options)
        self._backends: dict[str, TranslationBackend] = {}
        self._last_used: dict[str, float] = {}
        self._lock = threading.Lock()

    def available_pairs(self) -> list[tuple[str, str, str]]:
        """当前目录能翻译的语向。每项是 ``(src, tgt, "direct"|"pivot")``。

        清单把某语向定为直连、但该模型还没安装时，不会改列成英文中转。
        """

        found: dict[tuple[str, str], str] = {}
        for direction in self._by_direction:
            found[direction] = "direct"
        for direction, pivot in self.manifest.pivots.items():
            if direction in found or direction in self.manifest.direct:
                continue
            hops = ((pivot.src, pivot.via), (pivot.via, pivot.tgt))
            if all(
                leg in self._by_id or self._listed_substitute(hop) is not None
                for leg, hop in zip(pivot.legs, hops, strict=True)
            ):
                found[direction] = "pivot"
        sources = [src for src, tgt in self._by_direction if tgt == "en" and src != "en"]
        targets = [tgt for src, tgt in self._by_direction if src == "en" and tgt != "en"]
        for src in sources:
            for tgt in targets:
                if src == tgt:
                    continue
                direction = (src, tgt)
                if (
                    direction in found
                    or direction in self.manifest.direct
                    or direction in self.manifest.pivots
                ):
                    continue
                found[direction] = "pivot"
        return sorted((src, tgt, kind) for (src, tgt), kind in found.items())

    def resolve(self, src: str, tgt: str) -> tuple[ModelRecord, ...]:
        """决定这条语向要用哪些已安装模型。不加载权重。

        顺序：已安装的直连；清单指定了直连但未安装则报该 id；
        清单中的中转（必须是声明的 leg id）；否则若 ``src→en`` 与 ``en→tgt``
        都已安装则英文中转。都没有则抛 :class:`UnsupportedPairError`。

        ``src == tgt`` 时返回空元组，表示不需要模型。
        """

        src_code = normalize_lang(src)
        tgt_code = normalize_lang(tgt)
        if src_code == tgt_code:
            return ()
        installed = self._by_direction.get((src_code, tgt_code))
        if installed is not None:
            return (installed,)
        direct_id = self.manifest.direct.get((src_code, tgt_code))
        if direct_id is not None:
            raise UnsupportedPairError(src_code, tgt_code, (direct_id,))
        pivot = self.manifest.pivots.get((src_code, tgt_code))
        if pivot is not None:
            return self._pivot_legs(src_code, tgt_code, pivot)
        if src_code != "en" and tgt_code != "en":
            left = self._by_direction.get((src_code, "en"))
            right = self._by_direction.get(("en", tgt_code))
            if left is not None and right is not None:
                return (left, right)
            missing: list[str] = []
            if left is None:
                left_id = self.manifest.direct.get((src_code, "en"))
                if left_id:
                    missing.append(left_id)
            if right is None:
                right_id = self.manifest.direct.get(("en", tgt_code))
                if right_id:
                    missing.append(right_id)
            if missing:
                raise UnsupportedPairError(src_code, tgt_code, missing)
        raise UnsupportedPairError(src_code, tgt_code, ())

    def get(self, model_id: str) -> TranslationBackend:
        """返回已缓存的后端；没有则加载并缓存。并发第一次调用只加载一次。"""

        cached = self._backends.get(model_id)
        if cached is not None:
            self._last_used[model_id] = time.monotonic()
            return cached
        with self._lock:
            cached = self._backends.get(model_id)
            if cached is None:
                record = self._by_id.get(model_id)
                if record is None:
                    raise KeyError(f"未安装模型 {model_id}")
                cached = self._factory(record)
                self._backends[model_id] = cached
            self._last_used[model_id] = time.monotonic()
            return cached

    def unload_idle(self, idle_s: float, *, now: float | None = None) -> list[str]:
        """卸载超过 ``idle_s`` 秒没用过的模型，返回卸载的 id（#92）。

        调用方要保证此刻没有翻译在用这些模型（``serve`` 里由翻译锁保证）。即使有，
        正在用的调用仍持有后端引用，翻译照常完成；下一次请求会重新加载。
        """

        current = time.monotonic() if now is None else now
        with self._lock:
            stale = [
                model_id
                for model_id in self._backends
                if current - self._last_used.get(model_id, current) >= idle_s
            ]
            for model_id in stale:
                self._unload_locked(model_id)
        return sorted(stale)

    def last_activity(self) -> float | None:
        """最近一次取用任一模型的 ``time.monotonic()`` 时刻；从没用过时为 ``None``。"""

        return max(self._last_used.values(), default=None)

    def unload(self, model_id: str) -> bool:
        """卸载一个已加载的模型。没加载时返回 ``False``。"""

        with self._lock:
            if model_id not in self._backends:
                return False
            self._unload_locked(model_id)
            return True

    def _unload_locked(self, model_id: str) -> None:
        backend = self._backends.pop(model_id)
        self._last_used.pop(model_id, None)
        close = getattr(backend, "close", None)
        if callable(close):
            close()

    def preload(self, pairs: Sequence[tuple[str, str]]) -> None:
        """按语种对预热。中转会加载两段模型。``src == tgt`` 不加载。"""

        for src, tgt in pairs:
            for record in self.resolve(src, tgt):
                self.get(record.id)

    def _listed_substitute(self, direction: tuple[str, str]) -> ModelRecord | None:
        """该方向已安装、且清单里登记为同方向的模型（清单外的自定义模型不能顶替中转的一跳）。"""

        record = self._by_direction.get(direction)
        if record is not None and self.manifest.known.get(record.id) == direction:
            return record
        return None

    def outdated(self) -> list[tuple[str, str, str, str]]:
        """清单推荐的直连模型没装、正在用同方向别的模型的语向。

        每项是 ``(src, tgt, 推荐 id, 实际 id)``。服务启动时据此告警并给出补装命令（#83）。
        """

        found: list[tuple[str, str, str, str]] = []
        for (src, tgt), record in sorted(self._by_direction.items()):
            preferred = self.manifest.direct.get((src, tgt))
            if preferred is not None and preferred != record.id and preferred not in self._by_id:
                found.append((src, tgt, preferred, record.id))
        return found

    def loaded_model_ids(self) -> list[str]:
        """已经加载进内存的模型 id，按字典序。"""

        with self._lock:
            return sorted(self._backends)

    def _pivot_legs(self, src: str, tgt: str, pivot: Pivot) -> tuple[ModelRecord, ...]:
        expected = ((src, pivot.via), (pivot.via, tgt))
        missing: list[str] = []
        records: list[ModelRecord] = []
        for leg, direction in zip(pivot.legs, expected, strict=True):
            record = self._by_id.get(leg) or self._listed_substitute(direction)
            if record is None:
                missing.append(leg)
                continue
            actual = (record.src, record.tgt)
            if actual != direction:
                raise ValueError(
                    f"{leg} 的方向是 {record.src}→{record.tgt}，"
                    f"与中转 {direction[0]}→{direction[1]} 不一致"
                )
            records.append(record)
        if missing:
            raise UnsupportedPairError(src, tgt, missing)
        return (records[0], records[1])


def load_manifest(path: Path) -> Manifest:
    """读取清单里的 ``models`` 与 ``pivots``。多余字段忽略。"""

    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"模型清单不是合法 JSON：{path}") from exc
    if not isinstance(document, dict):
        raise ValueError(f"模型清单必须是对象：{path}")
    models = document.get("models")
    pivots = document.get("pivots")
    if not isinstance(models, list) or not isinstance(pivots, list):
        raise ValueError(f"模型清单缺少 models 或 pivots 数组：{path}")

    direct: dict[tuple[str, str], str] = {}
    known: dict[str, tuple[str, str]] = {}
    for index, entry in enumerate(models):
        if not isinstance(entry, dict):
            raise ValueError(f"models[{index}] 必须是对象")
        model_id = _require_str(entry, "id", f"models[{index}]")
        src = normalize_lang(_require_str(entry, "src", model_id))
        tgt = normalize_lang(_require_str(entry, "tgt", model_id))
        direct.setdefault((src, tgt), model_id)
        known[model_id] = (src, tgt)

    parsed: dict[tuple[str, str], Pivot] = {}
    for index, entry in enumerate(pivots):
        if not isinstance(entry, dict):
            raise ValueError(f"pivots[{index}] 必须是对象")
        src = normalize_lang(_require_str(entry, "src", f"pivots[{index}]"))
        tgt = normalize_lang(_require_str(entry, "tgt", f"pivots[{index}]"))
        via = normalize_lang(str(entry.get("via", "en")))
        if via != "en":
            raise ValueError(f"pivots[{index}] 目前只支持经 en 中转，收到 {via}")
        legs = entry.get("legs")
        if (
            not isinstance(legs, list)
            or len(legs) != 2
            or not all(isinstance(leg, str) and leg for leg in legs)
        ):
            raise ValueError(f"pivots[{index}] 的 legs 必须是两个模型 id")
        parsed[(src, tgt)] = Pivot(src, tgt, via, (legs[0], legs[1]))
    return Manifest(direct=direct, pivots=parsed, known=known)


def scan_models(
    models_dir: Path,
    manifest: Manifest,
) -> tuple[dict[str, ModelRecord], dict[tuple[str, str], ModelRecord]]:
    """读取各子目录的 ``suiyi-model.json``。

    以 ``.`` 开头的目录（转换半成品）跳过。同一方向有多个目录时，
    优先清单里的 id，否则取字典序较小的 id。
    """

    by_id: dict[str, ModelRecord] = {}
    by_direction: dict[tuple[str, str], ModelRecord] = {}
    if not models_dir.is_dir():
        return by_id, by_direction
    for child in sorted(models_dir.iterdir(), key=lambda path: path.name):
        if not child.is_dir() or child.name.startswith("."):
            continue
        meta_path = child / _METADATA_NAME
        if not meta_path.is_file():
            continue
        record = _read_record(meta_path, child)
        if record.id in by_id:
            raise ValueError(f"模型 id 重复：{record.id}（{child}）")
        by_id[record.id] = record
        current = by_direction.get((record.src, record.tgt))
        if current is None or _prefer(record, current, manifest):
            by_direction[(record.src, record.tgt)] = record
    return by_id, by_direction


def _prefer(candidate: ModelRecord, current: ModelRecord, manifest: Manifest) -> bool:
    preferred = manifest.direct.get((candidate.src, candidate.tgt))
    if preferred == candidate.id:
        return True
    if preferred == current.id:
        return False
    return candidate.id < current.id


def _read_record(meta_path: Path, model_dir: Path) -> ModelRecord:
    try:
        data = json.loads(meta_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"无法解析 {meta_path}") from exc
    if not isinstance(data, dict):
        raise ValueError(f"{meta_path} 必须是对象")
    model_id = _require_str(data, "id", str(meta_path))
    src = normalize_lang(_require_str(data, "src", model_id))
    tgt = normalize_lang(_require_str(data, "tgt", model_id))
    token = data.get("src_prefix_token")
    if token is None or token == "":
        prefix = None
    elif isinstance(token, str):
        prefix = token
    else:
        raise ValueError(f"{model_id} 的 src_prefix_token 必须是字符串或 null")
    quantization = data.get("quantization", "int8")
    if not isinstance(quantization, str) or not quantization:
        raise ValueError(f"{model_id} 的 quantization 必须是非空字符串")
    return ModelRecord(
        id=model_id,
        src=src,
        tgt=tgt,
        model_dir=model_dir,
        src_prefix_token=prefix,
        quantization=quantization,
    )


def _require_str(entry: Mapping[str, object], key: str, where: str) -> str:
    value = entry.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{where} 的 {key} 必须是非空字符串")
    return value


def _default_factory(options: Mapping[str, object]) -> BackendFactory:
    def factory(record: ModelRecord) -> TranslationBackend:
        from suiyi_engine.backends.ct2_opus import Ct2OpusBackend

        return Ct2OpusBackend(record, **options)

    return factory
