"""OCR 模型（RapidOCR / PP-OCR ONNX）的清单、下载校验与离线冒烟。

选型与许可证见 ``docs/engine/OCR选型与许可证.md``，目录约定见 ``docs/engine/模型目录约定.md``。

* ``download``：按 ``engine/ocr_model_manifest.json`` 下载到 ``<models_dir>/ocr/``，
  逐个校验 sha256 与字节数，不符即删除半成品并失败。只在开发期联网。
* ``check``：只读本地文件，报告缺失或哈希不符的模型 id，不联网。
* ``smoke``：先 ``check``，再用本地文件跑一次 RapidOCR 识别。进程内禁止任何 socket 连接，
  一旦 RapidOCR 想联网就立即报错，而不是悄悄下载。

本模块导入时不加载 rapidocr / onnxruntime，只装 ``engine[dev]`` 的 CI 也能跑单测。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import socket
import sys
import time
import urllib.request
from collections.abc import Callable, Iterator, Sequence
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import IO

SCHEMA_VERSION = 1
OCR_SUBDIR = "ocr"
METADATA_NAME = "suiyi-ocr.json"
TASKS = ("det", "cls", "rec")
ALLOWED_LICENSES = frozenset({"Apache-2.0", "MIT", "BSD-3-Clause"})
ALLOWED_URL_PREFIX = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/"
_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
_REQUIRED_FIELDS = (
    "id",
    "task",
    "ocr_version",
    "file",
    "bytes",
    "sha256",
    "url",
    "upstream",
    "upstream_url",
    "license",
    "license_url",
    "languages",
    "tier",
    "notes",
)
_CHUNK = 1 << 20
# 截图场景的检测缩放（#51 实测）：RapidOCR 默认 limit_type=min / 736 会把小选区放大到短边 736，
# 400×150 的选区要 547 ms；改为只缩不放后约 90 ms，样例 CER 不变。#52 接入时沿用。
# 注意（#54 实测）：RapidOCR 3.9 在 limit_type=max 时忽略 limit_side_len，
# 按原图长边选 960/1500/2000，所以 ≤2000 px 的截图检测时从不缩小，960 实际不起作用。
# #74 起长边上限由 OcrRuntimeOptions.det_max_side（默认 1024）在交给 RapidOCR 之前缩图实现；
# 这里的 limit_type=max 只保证「不放大」（缩好的图长边 ≤1024，RapidOCR 只做 32 对齐）。
SCREENSHOT_DET_PARAMS: dict[str, object] = {"Det.limit_type": "max", "Det.limit_side_len": 960}
# 运行时依赖里不允许出现的发行包（#51 验收）。
FORBIDDEN_DISTRIBUTIONS = ("torch", "paddlepaddle", "paddlepaddle-gpu", "paddleocr", "transformers")

Opener = Callable[[str], IO[bytes]]


class OcrModelError(Exception):
    """清单、下载或本地文件的问题；``code`` 是命令行退出码。"""

    def __init__(self, message: str, code: int = 1) -> None:
        super().__init__(message)
        self.code = code


class OcrModelsMissingError(OcrModelError):
    """本地缺少 OCR 模型或文件损坏。``missing`` 是缺失/不符的模型 id。"""

    def __init__(self, missing: Sequence[str], ocr_dir: Path) -> None:
        self.missing = tuple(missing)
        self.ocr_dir = ocr_dir
        super().__init__(
            f"缺少 OCR 模型：{'、'.join(self.missing)}（目录 {ocr_dir}）。"
            "请在开发机执行 python scripts/download_ocr_models.py 下载，运行时不会自动联网下载。",
            code=1,
        )


@dataclass(frozen=True)
class OcrModel:
    id: str
    task: str
    ocr_version: str
    file: str
    bytes: int
    sha256: str
    url: str
    license: str
    tier: str


@dataclass(frozen=True)
class OcrManifest:
    rapidocr_requirement: str
    recommended: dict[str, str]
    use_cls: bool
    models: dict[str, OcrModel]

    def recommended_models(self) -> list[OcrModel]:
        return [self.models[self.recommended[task]] for task in TASKS]


def repo_root_from_here() -> Path:
    for parent in Path(__file__).resolve().parents:
        if (parent / "engine" / "pyproject.toml").is_file() and (parent / "scripts").is_dir():
            return parent
    raise OcrModelError("无法定位仓库根目录（未找到 engine/pyproject.toml）", code=2)


def default_manifest_path() -> Path:
    return repo_root_from_here() / "engine" / "ocr_model_manifest.json"


def resolve_models_dir(explicit: Path | None) -> Path:
    if explicit is not None:
        return explicit.expanduser().resolve()
    env = os.environ.get("SUIYI_MODELS_DIR", "").strip()
    if env:
        return Path(env).expanduser().resolve()
    return (repo_root_from_here() / "models").resolve()


def ocr_dir(models_dir: Path) -> Path:
    return models_dir / OCR_SUBDIR


def load_manifest(path: Path) -> OcrManifest:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise OcrModelError(f"找不到 OCR 模型清单：{path}", code=2) from exc
    except json.JSONDecodeError as exc:
        raise OcrModelError(f"无法解析 OCR 模型清单 {path}：{exc}", code=2) from exc
    return parse_manifest(data, str(path))


def parse_manifest(data: object, label: str = "OCR 清单") -> OcrManifest:
    if not isinstance(data, dict):
        raise OcrModelError(f"{label} 必须是对象", code=2)
    if data.get("schema_version") != SCHEMA_VERSION:
        raise OcrModelError(f"{label} 的 schema_version 必须是 {SCHEMA_VERSION}", code=2)
    rapidocr = data.get("rapidocr")
    if not isinstance(rapidocr, dict) or not isinstance(rapidocr.get("requirement"), str):
        raise OcrModelError(f"{label} 缺少 rapidocr.requirement", code=2)
    raw_models = data.get("models")
    if not isinstance(raw_models, list) or not raw_models:
        raise OcrModelError(f"{label} 的 models 必须是非空数组", code=2)
    models: dict[str, OcrModel] = {}
    files: set[str] = set()
    for index, raw in enumerate(raw_models):
        model = _parse_model(raw, f"{label} models[{index}]")
        if model.id in models:
            raise OcrModelError(f"{label} 中模型 id 重复：{model.id}", code=2)
        if model.file in files:
            raise OcrModelError(f"{label} 中文件名重复：{model.file}", code=2)
        models[model.id] = model
        files.add(model.file)
    recommended = data.get("recommended")
    if not isinstance(recommended, dict):
        raise OcrModelError(f"{label} 缺少 recommended", code=2)
    chosen: dict[str, str] = {}
    for task in TASKS:
        model_id = recommended.get(task)
        if not isinstance(model_id, str) or model_id not in models:
            raise OcrModelError(f"{label} recommended.{task} 必须是清单里的模型 id", code=2)
        if models[model_id].task != task:
            raise OcrModelError(f"{label} recommended.{task} 指向的 {model_id} 不是 {task}", code=2)
        chosen[task] = model_id
    use_cls = recommended.get("use_cls")
    if not isinstance(use_cls, bool):
        raise OcrModelError(f"{label} recommended.use_cls 必须是布尔值", code=2)
    return OcrManifest(rapidocr["requirement"], chosen, use_cls, models)


def _parse_model(raw: object, label: str) -> OcrModel:
    if not isinstance(raw, dict):
        raise OcrModelError(f"{label} 必须是对象", code=2)
    for field in _REQUIRED_FIELDS:
        if field not in raw:
            raise OcrModelError(f"{label} 缺少字段 {field}", code=2)
    model_id = raw["id"]
    if not isinstance(model_id, str) or not _ID_RE.match(model_id):
        raise OcrModelError(f"{label} 的 id 不合法：{model_id!r}", code=2)
    if raw["task"] not in TASKS:
        raise OcrModelError(f"{model_id}：task 必须是 {'/'.join(TASKS)}", code=2)
    file = raw["file"]
    if not isinstance(file, str) or not file.endswith(".onnx") or "/" in file or "\\" in file:
        raise OcrModelError(f"{model_id}：file 必须是不含路径的 .onnx 文件名", code=2)
    size = raw["bytes"]
    if isinstance(size, bool) or not isinstance(size, int) or size <= 0:
        raise OcrModelError(f"{model_id}：bytes 必须是正整数", code=2)
    sha = raw["sha256"]
    if not isinstance(sha, str) or not _SHA256_RE.match(sha):
        raise OcrModelError(f"{model_id}：sha256 必须是 64 位小写十六进制", code=2)
    url = raw["url"]
    if not isinstance(url, str) or not url.startswith(ALLOWED_URL_PREFIX) or not url.endswith(file):
        raise OcrModelError(
            f"{model_id}：url 必须在 {ALLOWED_URL_PREFIX} 下并以 {file} 结尾", code=2
        )
    if raw["license"] not in ALLOWED_LICENSES:
        raise OcrModelError(
            f"{model_id}：许可证 {raw['license']!r} 不在允许列表，不可默认分发", code=2
        )
    return OcrModel(
        id=model_id,
        task=raw["task"],
        ocr_version=str(raw["ocr_version"]),
        file=file,
        bytes=size,
        sha256=sha,
        url=url,
        license=raw["license"],
        tier=str(raw["tier"]),
    )


def select_models(
    manifest: OcrManifest, ids: Sequence[str] | None = None, *, everything: bool = False
) -> list[OcrModel]:
    if everything:
        return list(manifest.models.values())
    if not ids:
        return manifest.recommended_models()
    unknown = [model_id for model_id in ids if model_id not in manifest.models]
    if unknown:
        raise OcrModelError(f"清单中没有这些 OCR 模型：{'、'.join(unknown)}", code=2)
    return [manifest.models[model_id] for model_id in dict.fromkeys(ids)]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(_CHUNK), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_file(path: Path, model: OcrModel) -> str | None:
    """文件与清单一致时返回 ``None``，否则返回原因。"""

    if not path.is_file():
        return "文件不存在"
    size = path.stat().st_size
    if size != model.bytes:
        return f"字节数 {size} ≠ 清单 {model.bytes}"
    actual = sha256_file(path)
    if actual != model.sha256:
        return f"sha256 {actual} ≠ 清单 {model.sha256}"
    return None


def find_problems(models_dir: Path, models: Sequence[OcrModel]) -> dict[str, str]:
    """只读本地文件，返回 {模型 id: 原因}；全部正常时为空。"""

    base = ocr_dir(models_dir)
    problems: dict[str, str] = {}
    for model in models:
        reason = verify_file(base / model.file, model)
        if reason is not None:
            problems[model.id] = reason
    return problems


def local_model_paths(models_dir: Path, manifest: OcrManifest) -> dict[str, Path]:
    """推荐组合的本地路径 {det/cls/rec: Path}。缺失或损坏时抛 :class:`OcrModelsMissingError`。"""

    models = manifest.recommended_models()
    problems = find_problems(models_dir, models)
    if problems:
        raise OcrModelsMissingError(list(problems), ocr_dir(models_dir))
    base = ocr_dir(models_dir)
    return {model.task: base / model.file for model in models}


def _default_opener(url: str) -> IO[bytes]:
    request = urllib.request.Request(url, headers={"User-Agent": "suiyi-engine/ocr-models"})
    return urllib.request.urlopen(request, timeout=60)  # noqa: S310 - 清单里只允许固定前缀


def download_model(
    model: OcrModel,
    models_dir: Path,
    *,
    force: bool = False,
    opener: Opener = _default_opener,
    log: Callable[[str], None] = print,
) -> Path:
    base = ocr_dir(models_dir)
    base.mkdir(parents=True, exist_ok=True)
    target = base / model.file
    if not force and verify_file(target, model) is None:
        log(f"已存在且校验通过：{model.id}")
        return target
    partial = base / f".{model.file}.partial"
    partial.unlink(missing_ok=True)
    log(f"下载 {model.id}（{model.bytes / 1e6:.1f} MB）：{model.url}")
    digest = hashlib.sha256()
    written = 0
    try:
        with opener(model.url) as response, partial.open("wb") as handle:
            for chunk in iter(lambda: response.read(_CHUNK), b""):
                handle.write(chunk)
                digest.update(chunk)
                written += len(chunk)
                if written > model.bytes:
                    raise OcrModelError(f"{model.id}：下载内容超过清单字节数 {model.bytes}")
        if written != model.bytes:
            raise OcrModelError(f"{model.id}：下载了 {written} 字节，清单为 {model.bytes}")
        actual = digest.hexdigest()
        if actual != model.sha256:
            raise OcrModelError(f"{model.id}：sha256 不符（实际 {actual}，清单 {model.sha256}）")
        partial.replace(target)
    except OSError as exc:
        partial.unlink(missing_ok=True)
        raise OcrModelError(f"{model.id}：下载失败：{exc}") from exc
    except BaseException:
        partial.unlink(missing_ok=True)
        raise
    return target


def write_metadata(models_dir: Path, manifest: OcrManifest) -> Path:
    """把目录里校验通过的清单模型写进 ``suiyi-ocr.json``。"""

    base = ocr_dir(models_dir)
    files: dict[str, dict[str, object]] = {}
    for model in manifest.models.values():
        if verify_file(base / model.file, model) is None:
            files[model.file] = {
                "id": model.id,
                "task": model.task,
                "ocr_version": model.ocr_version,
                "sha256": model.sha256,
                "bytes": model.bytes,
                "url": model.url,
                "license": model.license,
            }
    meta = {
        "schema_version": SCHEMA_VERSION,
        "rapidocr_requirement": manifest.rapidocr_requirement,
        "recommended": {**manifest.recommended, "use_cls": manifest.use_cls},
        "updated_at": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
        "files": files,
    }
    path = base / METADATA_NAME
    path.write_text(json.dumps(meta, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return path


@contextmanager
def forbid_network() -> Iterator[None]:
    """进程内禁止发起网络连接（``socket.connect`` / ``create_connection`` / DNS 解析）。"""

    def refuse(*_args: object, **_kwargs: object) -> None:
        raise OcrModelError("OCR 冒烟过程中出现了网络连接尝试（应只用本地模型）")

    saved = (socket.socket.connect, socket.socket.connect_ex, socket.create_connection)
    saved_dns = socket.getaddrinfo
    socket.socket.connect = refuse  # type: ignore[assignment,method-assign]
    socket.socket.connect_ex = refuse  # type: ignore[assignment,method-assign]
    socket.create_connection = refuse  # type: ignore[assignment]
    socket.getaddrinfo = refuse  # type: ignore[assignment]
    try:
        yield
    finally:
        socket.socket.connect, socket.socket.connect_ex, socket.create_connection = saved  # type: ignore[method-assign]
        socket.getaddrinfo = saved_dns


def rapidocr_params(paths: dict[str, Path], manifest: OcrManifest) -> dict[str, object]:
    """用本地文件构造 RapidOCR 参数。每个阶段都给 ``model_path``，RapidOCR 就不会走下载分支。"""

    from rapidocr import OCRVersion  # 延迟导入：只有 smoke 需要

    versions = {task: manifest.models[manifest.recommended[task]].ocr_version for task in TASKS}
    return {
        "Global.log_level": "error",
        "Global.use_cls": manifest.use_cls,
        "Det.model_path": str(paths["det"]),
        "Det.ocr_version": OCRVersion(versions["det"]),
        "Cls.model_path": str(paths["cls"]),
        "Cls.ocr_version": OCRVersion(versions["cls"]),
        "Rec.model_path": str(paths["rec"]),
        "Rec.ocr_version": OCRVersion(versions["rec"]),
        **SCREENSHOT_DET_PARAMS,
    }


def smoke(image: Path, models_dir: Path, manifest: OcrManifest) -> int:
    paths = local_model_paths(models_dir, manifest)
    if not image.is_file():
        raise OcrModelError(f"找不到图片：{image}", code=2)
    with forbid_network():
        try:
            from rapidocr import RapidOCR
        except ImportError as exc:
            raise OcrModelError(
                '未安装 rapidocr：请执行 pip install -e "engine[ocr]"', code=2
            ) from exc
        started = time.perf_counter()
        engine = RapidOCR(params=rapidocr_params(paths, manifest))
        loaded = time.perf_counter()
        result = engine(str(image))
        done = time.perf_counter()
    lines = list(result.txts or ())
    scores = list(result.scores or ())
    print(f"模型目录：{ocr_dir(models_dir)}")
    for task in TASKS:
        print(f"  {task}: {paths[task].name}")
    load_ms = 1000 * (loaded - started)
    run_ms = 1000 * (done - loaded)
    print(f"加载 {load_ms:.0f} ms，识别 {run_ms:.0f} ms，{len(lines)} 行")
    for text, score in zip(lines, scores, strict=False):
        print(f"  [{score:.2f}] {text}")
    return 0 if lines else 1


def audit_installed() -> int:
    """列出当前环境的发行包与体积；出现 torch / paddle / transformers 时返回 1。"""

    from importlib import metadata

    rows: list[tuple[str, str, int]] = []
    for dist in metadata.distributions():
        name = dist.metadata["Name"] or "?"
        size = 0
        for file in dist.files or ():
            try:
                size += Path(str(dist.locate_file(file))).stat().st_size
            except OSError:
                continue
        rows.append((name, dist.version, size))
    rows.sort(key=lambda row: row[0].lower())
    for name, version, size in rows:
        print(f"{name:28s} {version:14s} {size / 2**20:8.1f} MB")
    print(f"合计 {len(rows)} 个包，{sum(size for *_rest, size in rows) / 2**20:.1f} MB")
    names = {name.lower().replace("_", "-") for name, _version, _size in rows}
    found = sorted(names & set(FORBIDDEN_DISTRIBUTIONS))
    if found:
        print(f"不允许的运行时依赖：{'、'.join(found)}", file=sys.stderr)
        return 1
    print("未发现 torch / paddlepaddle / paddleocr / transformers")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="download_ocr_models.py",
        description="下载并校验 OCR 模型（RapidOCR / PP-OCR ONNX），或检查本地模型、离线冒烟。",
        epilog="模型写入 <models_dir>/ocr/（默认 SUIYI_MODELS_DIR，否则仓库根 models/）。",
    )
    parser.add_argument("--models-dir", type=Path, default=None, help="模型根目录")
    parser.add_argument("--manifest", type=Path, default=None, help="OCR 清单路径")
    sub = parser.add_subparsers(dest="command")
    down = sub.add_parser("download", help="下载（默认子命令）")
    for target in (parser, down):
        target.add_argument("--model", action="append", default=None, help="只处理这些 id")
        target.add_argument("--all", action="store_true", help="下载清单里的全部候选")
        target.add_argument("--force", action="store_true", help="已存在也重新下载")
    chk = sub.add_parser("check", help="只检查本地文件，不联网")
    chk.add_argument("--model", action="append", default=None)
    chk.add_argument("--all", action="store_true")
    sm = sub.add_parser("smoke", help="禁网状态下用本地推荐模型识别一张图片")
    sm.add_argument("image", type=Path)
    sub.add_parser("audit", help="列出当前环境的包与体积，检查没有 torch / paddle / transformers")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.command == "audit":
        return audit_installed()
    try:
        manifest = load_manifest(args.manifest or default_manifest_path())
        models_dir = resolve_models_dir(args.models_dir)
        command = args.command or "download"
        if command == "smoke":
            return smoke(args.image, models_dir, manifest)
        models = select_models(manifest, args.model, everything=args.all)
        if command == "check":
            problems = find_problems(models_dir, models)
            for model in models:
                status = "缺失/不符" if model.id in problems else "正常"
                print(f"{status}  {model.id}  {problems.get(model.id, '')}".rstrip())
            if problems:
                raise OcrModelsMissingError(list(problems), ocr_dir(models_dir))
            return 0
        for model in models:
            download_model(model, models_dir, force=args.force)
        meta = write_metadata(models_dir, manifest)
        print(f"完成：{len(models)} 个模型，元数据 {meta}")
        return 0
    except OcrModelError as exc:
        print(str(exc), file=sys.stderr)
        return exc.code
