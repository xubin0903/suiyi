"""把清单中的 OPUS-MT 模型下载并转换为 CTranslate2。

输出目录约定见 ``docs/engine/模型目录约定.md``。本模块在导入时不加载
torch、transformers 或 ctranslate2，方便只安装 ``engine[dev]`` 的单元测试与 CI。

权重来源有两种：

* 默认：Hugging Face 仓库的固定 ``hf_revision``，用 Transformers 转换器。
* ``weights_source.type == "opus-mt-zip"``：Helsinki-NLP 在 object.pouta.csc.fi
  发布的原始 Marian zip，按 ``sha256`` 校验后用 CTranslate2 的 Marian 转换器。
  用于 Hugging Face 转换版有缺陷、或 HF 上没有该次发布的模型（见 Issue #25）。
"""

import argparse
import hashlib
import importlib
import json
import os
import re
import shutil
import sys
import urllib.request
import zipfile
from collections.abc import Callable, Mapping, Sequence
from datetime import datetime, timezone
from pathlib import Path

SCHEMA_VERSION = 1
METADATA_NAME = "suiyi-model.json"
DEFAULT_QUANTIZATION = "int8"

QUANTIZATIONS = (
    "int8",
    "int8_float32",
    "int8_float16",
    "int8_bfloat16",
    "int16",
    "float16",
    "bfloat16",
    "float32",
)

# 转换只需要 PyTorch 权重和分词文件。同一 HF 仓库里的 tf/flax/rust 副本体积大且用不到。
SNAPSHOT_ALLOW_PATTERNS = (
    "config.json",
    "generation_config.json",
    "pytorch_model.bin",
    "model.safetensors",
    "tokenizer_config.json",
    "special_tokens_map.json",
    "vocab.json",
    "source.spm",
    "target.spm",
    "source.vocab",
    "target.vocab",
)

_REQUIRED_TOP_LEVEL = ("schema_version", "models", "pivots")
_REQUIRED_MODEL_FIELDS = (
    "id",
    "src",
    "tgt",
    "hf_repo",
    "hf_revision",
    "license",
    "license_url",
    "attribution",
    "src_prefix_token",
    "tier",
    "notes",
)
_MODEL_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
_REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
_LANG_RE = re.compile(r"^[a-z]{2}$")
_REPO_RE = re.compile(r"^[^/\s]+/[^/\s]+$")
_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
_ZIP_SOURCE_TYPE = "opus-mt-zip"
_ZIP_URL_PREFIX = "https://object.pouta.csc.fi/"
_SPM_FILES = ("source.spm", "target.spm")
_VOCAB_FILES = ("vocab.json", "source.vocab", "target.vocab")
_REQUIRED_OUTPUTS = ("model.bin", "config.json", "source.spm", "target.spm")


class ConvertError(Exception):
    """转换或清单错误。``code`` 作为进程退出码。"""

    def __init__(self, message: str, code: int = 1) -> None:
        super().__init__(message)
        self.code = code


def repo_root_from_here() -> Path:
    """从本文件向上找到仓库根（同时含 ``engine/pyproject.toml`` 与 ``scripts/``）。"""
    for parent in Path(__file__).resolve().parents:
        if (parent / "engine" / "pyproject.toml").is_file() and (parent / "scripts").is_dir():
            return parent
    raise ConvertError("无法定位仓库根目录（未找到 engine/pyproject.toml）", code=2)


def resolve_manifest(explicit: Path | None, repo_root: Path) -> Path:
    if explicit is not None:
        return explicit.expanduser().resolve()
    return (repo_root / "engine" / "model_manifest.json").resolve()


def resolve_out_dir(explicit: Path | None, repo_root: Path) -> Path:
    if explicit is not None:
        return explicit.expanduser().resolve()
    env = os.environ.get("SUIYI_MODELS_DIR", "").strip()
    if env:
        return Path(env).expanduser().resolve()
    return (repo_root / "models").resolve()


def missing_manifest_message(path: Path) -> str:
    message = f"找不到清单：{path}"
    example = path.with_name("model_manifest.example.json")
    if path.name == "model_manifest.json" and example.is_file():
        message += f"\n正式清单尚未就绪。可先用示例清单：--manifest {example}"
    return message


def load_manifest(path: Path) -> dict:
    """读取并校验清单。额外字段会保留，供元数据原样写回。"""
    if not path.is_file():
        raise ConvertError(missing_manifest_message(path), code=2)
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ConvertError(f"清单不是合法 JSON：{path}（{exc}）", code=2) from exc
    if not isinstance(document, dict):
        raise ConvertError("清单顶层必须是 JSON 对象", code=2)
    for key in _REQUIRED_TOP_LEVEL:
        if key not in document:
            raise ConvertError(f"清单缺少字段：{key}", code=2)
    version = document["schema_version"]
    if not _is_int(version) or version != SCHEMA_VERSION:
        raise ConvertError(
            f"不支持的 schema_version：{version!r}（当前只接受 {SCHEMA_VERSION}）",
            code=2,
        )
    models = document["models"]
    if not isinstance(models, list) or not models:
        raise ConvertError("清单字段 models 必须是非空数组", code=2)
    if not isinstance(document["pivots"], list):
        raise ConvertError("清单字段 pivots 必须是数组", code=2)
    seen: set[str] = set()
    for index, entry in enumerate(models):
        _validate_model_entry(entry, index)
        model_id = entry["id"]
        if model_id in seen:
            raise ConvertError(f"清单中的模型 id 重复：{model_id}", code=2)
        seen.add(model_id)
    return document


def select_models(
    manifest: Mapping[str, object],
    ids: Sequence[str] | None,
    tier: str | None,
) -> list[dict]:
    models = manifest["models"]
    if not isinstance(models, list):
        raise ConvertError("清单字段 models 必须是数组", code=2)
    by_id = {entry["id"]: entry for entry in models}
    if ids is not None:
        unknown = [model_id for model_id in ids if model_id not in by_id]
        if unknown:
            known = ", ".join(entry["id"] for entry in models)
            raise ConvertError(
                f"清单中没有这些 id：{', '.join(unknown)}。可选：{known}",
                code=2,
            )
        if len(ids) != len(set(ids)):
            raise ConvertError("--ids 中有重复的模型 id", code=2)
        return [by_id[model_id] for model_id in ids]
    if tier is None:
        raise ConvertError("请指定 --ids 或 --tier", code=2)
    selected = [entry for entry in models if entry["tier"] == tier]
    if not selected:
        raise ConvertError(f"清单中没有 tier={tier} 的模型", code=2)
    return selected


def download_snapshot(repo_id: str, revision: str) -> Path:
    """用清单里的固定 revision 下载模型快照。"""
    try:
        huggingface_hub = importlib.import_module("huggingface_hub")
    except ImportError as exc:
        raise ConvertError(_missing_convert_extra(), code=1) from exc
    local_dir = huggingface_hub.snapshot_download(
        repo_id=repo_id,
        revision=revision,
        repo_type="model",
        allow_patterns=list(SNAPSHOT_ALLOW_PATTERNS),
    )
    return Path(local_dir)


def convert_snapshot(
    snapshot: Path,
    output_dir: Path,
    quantization: str,
    copy_files: Sequence[str],
) -> None:
    """调用 CTranslate2 的 Transformers 转换器，并确保 SentencePiece / 词表被拷入。"""
    try:
        ctranslate2 = importlib.import_module("ctranslate2")
    except ImportError as exc:
        raise ConvertError(_missing_convert_extra(), code=1) from exc
    converter = ctranslate2.converters.TransformersConverter(
        str(snapshot),
        copy_files=list(copy_files),
        low_cpu_mem_usage=True,
    )
    converter.convert(str(output_dir), quantization=quantization, force=True)
    for name in copy_files:
        destination = output_dir / name
        if not destination.is_file():
            shutil.copyfile(snapshot / name, destination)


def fetch_opus_zip(source: Mapping[str, object], cache_dir: Path, work_dir: Path) -> Path:
    """下载（或复用缓存的）上游 Marian zip，校验 sha256，只解出需要的文件。

    缓存文件名是 ``<sha256>.zip``，放在 ``cache_dir``。返回解压目录。
    """
    url = str(source["url"])
    expected = str(source["sha256"])
    cache_dir.mkdir(parents=True, exist_ok=True)
    archive = cache_dir / f"{expected}.zip"
    if not archive.is_file() or _sha256_file(archive) != expected:
        partial = archive.with_suffix(".zip.partial")
        _emit(f"下载 {url}")
        with urllib.request.urlopen(url, timeout=60) as response, partial.open("wb") as handle:
            shutil.copyfileobj(response, handle, length=1024 * 1024)
        actual = _sha256_file(partial)
        if actual != expected:
            partial.unlink(missing_ok=True)
            raise ConvertError(
                f"zip 的 sha256 不符：期望 {expected}，实际 {actual}（{url}）",
                code=1,
            )
        partial.replace(archive)
    else:
        _emit(f"复用缓存 {archive}")
    wanted = zip_member_names(source)
    with zipfile.ZipFile(archive) as bundle:
        names = set(bundle.namelist())
        missing = [name for name in wanted if name not in names]
        if missing:
            raise ConvertError(f"zip 中缺少文件：{', '.join(missing)}（{url}）", code=1)
        for name in wanted:
            bundle.extract(name, work_dir)
    return work_dir


def zip_member_names(source: Mapping[str, object]) -> list[str]:
    """zip 里需要解出的文件：权重、词表和要拷贝的文件，去重保序。"""
    names = [str(source["model_file"])]
    names.extend(str(name) for name in source["vocab_files"])  # type: ignore[union-attr]
    names.extend(str(name) for name in source["copy_files"])  # type: ignore[union-attr]
    return list(dict.fromkeys(names))


def marian_vocab_to_yaml(path: Path, destination: Path) -> Path:
    """把「一行一个 token」的 Marian 词表改写成 CTranslate2 能读的 YAML。

    已是 ``.yml`` / ``.yaml`` 时原样返回。键用 JSON 字符串写，
    CTranslate2 的 Marian 读取器会去掉引号并反转义。
    """
    if path.suffix in (".yml", ".yaml"):
        return path
    # 按字节读并只按 \n 切分：词表里可能有 \r、U+0085 之类的 token，不能走通用换行。
    text = path.read_bytes().decode("utf-8")
    tokens = text.split("\n")
    if tokens and tokens[-1] == "":
        tokens.pop()
    if len(tokens) != len(set(tokens)):
        raise ConvertError(f"词表中有重复 token：{path}", code=1)
    lines = [f"{_yaml_vocab_key(token, path)}: {index}" for index, token in enumerate(tokens)]
    destination.write_bytes(("\n".join(lines) + "\n").encode("utf-8"))
    return destination


def _yaml_vocab_key(token: str, path: Path) -> str:
    """按 CTranslate2 Marian 读取器能还原的方式给 token 加引号。

    读取器去掉双引号后把 ``\\c`` 还原成 ``c``；整个 token 形如 ``\\xNN`` 时还原成该字符。
    单个控制字符用 ``\\xNN``；多字符 token 里夹控制字符无法表达，直接报错。
    """
    if len(token) == 1 and ord(token) < 0x20:
        return f'"\\x{ord(token):02x}"'
    if any(ord(char) < 0x20 for char in token):
        raise ConvertError(f"词表 token 含控制字符，无法转换：{token!r}（{path}）", code=1)
    escaped = token.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def convert_opus_zip(
    extracted: Path,
    output_dir: Path,
    quantization: str,
    source: Mapping[str, object],
) -> None:
    """用 CTranslate2 Marian 转换器转换解压后的上游模型，再拷入分词与许可证文件。"""
    try:
        ctranslate2 = importlib.import_module("ctranslate2")
    except ImportError as exc:
        raise ConvertError(_missing_convert_extra(), code=1) from exc
    vocab_paths: list[str] = []
    for index, name in enumerate(source["vocab_files"]):  # type: ignore[union-attr]
        original = extracted / str(name)
        converted = extracted / f"vocab{index}.suiyi.yml"
        vocab_paths.append(str(marian_vocab_to_yaml(original, converted)))
    if len(vocab_paths) == 1:
        vocab_paths.append(vocab_paths[0])
    converter = ctranslate2.converters.MarianConverter(
        str(extracted / str(source["model_file"])),
        vocab_paths,
    )
    converter.convert(str(output_dir), quantization=quantization, force=True)
    for name in source["copy_files"]:  # type: ignore[union-attr]
        shutil.copyfile(extracted / str(name), output_dir / Path(str(name)).name)


def source_key(entry: Mapping[str, object]) -> str:
    """判断「已存在」用的来源标识：zip 用 ``sha256:<hex>``，否则用 ``hf_revision``。"""
    source = entry.get("weights_source")
    if isinstance(source, Mapping):
        return f"sha256:{source['sha256']}"
    return str(entry["hf_revision"])


def current_ctranslate2_version() -> str:
    try:
        ctranslate2 = importlib.import_module("ctranslate2")
    except ImportError as exc:
        raise ConvertError(_missing_convert_extra(), code=1) from exc
    return str(ctranslate2.__version__)


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def spm_and_vocab_files(snapshot: Path) -> list[str]:
    missing = [name for name in _SPM_FILES if not (snapshot / name).is_file()]
    if missing:
        raise ConvertError(
            f"快照缺少 SentencePiece 文件：{', '.join(missing)}（{snapshot}）",
            code=1,
        )
    names = list(_SPM_FILES)
    names.extend(name for name in _VOCAB_FILES if (snapshot / name).is_file())
    return names


def convert_one(
    entry: Mapping[str, object],
    out_dir: Path,
    quantization: str,
    force: bool,
    *,
    download: Callable[[str, str], Path] | None = None,
    convert: Callable[[Path, Path, str, Sequence[str]], None] | None = None,
    version: Callable[[], str] | None = None,
    now: Callable[[], str] | None = None,
    fetch_zip: Callable[[Mapping[str, object], Path, Path], Path] | None = None,
    convert_zip: Callable[[Path, Path, str, Mapping[str, object]], None] | None = None,
) -> str:
    """转换单个模型。返回 ``converted`` 或 ``skipped``。

    下载、转换、版本和时钟在调用时解析，便于测试替换模块级函数。
    """
    if download is None:
        download = download_snapshot
    if convert is None:
        convert = convert_snapshot
    if version is None:
        version = current_ctranslate2_version
    if now is None:
        now = utc_now_iso
    if fetch_zip is None:
        fetch_zip = fetch_opus_zip
    if convert_zip is None:
        convert_zip = convert_opus_zip
    model_id = str(entry["id"])
    key = source_key(entry)
    final_dir = _model_dir(out_dir, model_id)
    if _is_current(final_dir, key, quantization) and not force:
        _emit(f"{model_id} 已存在，跳过")
        return "skipped"
    if final_dir.exists() and not force:
        raise ConvertError(
            f"{model_id} 的目录已存在，但来源修订或量化与清单不一致。加上 --force 可覆盖。",
            code=2,
        )
    if force and final_dir.exists():
        _emit(f"强制重新转换 {model_id}")

    partial = out_dir / f".{model_id}.partial"
    if partial.exists():
        shutil.rmtree(partial)
    partial.mkdir(parents=True)
    work = out_dir / f".{model_id}.work"
    try:
        source = entry.get("weights_source")
        if isinstance(source, Mapping):
            if work.exists():
                shutil.rmtree(work)
            work.mkdir(parents=True)
            extracted = fetch_zip(source, out_dir / ".cache", work)
            _emit(f"转换 {model_id} → {final_dir} （{quantization}，上游 Marian zip）")
            convert_zip(extracted, partial, quantization, source)
        else:
            repo_id = str(entry["hf_repo"])
            revision = str(entry["hf_revision"])
            _emit(f"下载 {repo_id}@{revision}")
            snapshot = download(repo_id, revision)
            copy_files = spm_and_vocab_files(snapshot)
            _emit(f"转换 {model_id} → {final_dir} （{quantization}）")
            convert(snapshot, partial, quantization, copy_files)
        _require_outputs(partial)
        file_info = hash_files(partial)
        metadata = build_metadata(entry, quantization, version(), now(), file_info)
        _write_json(partial / METADATA_NAME, metadata)
        if final_dir.exists():
            shutil.rmtree(final_dir)
        partial.rename(final_dir)
    except BaseException:
        if partial.exists():
            shutil.rmtree(partial, ignore_errors=True)
        raise
    finally:
        if work.exists():
            shutil.rmtree(work, ignore_errors=True)
    total = sum(item["bytes"] for item in file_info.values())
    _emit(f"完成 {model_id}，权重大小 {total} 字节（不含 {METADATA_NAME}）")
    return "converted"


def build_metadata(
    entry: Mapping[str, object],
    quantization: str,
    ctranslate2_version: str,
    converted_at: str,
    files: Mapping[str, Mapping[str, object]],
) -> dict:
    metadata = dict(entry)
    metadata["quantization"] = quantization
    metadata["ctranslate2_version"] = ctranslate2_version
    metadata["converted_at"] = converted_at
    metadata["files"] = {name: dict(info) for name, info in files.items()}
    return metadata


def hash_files(directory: Path) -> dict[str, dict[str, object]]:
    """对目录内已有文件计算 sha256 与字节数。不包含尚未写入的元数据文件。"""
    result: dict[str, dict[str, object]] = {}
    paths = sorted(path for path in directory.rglob("*") if path.is_file())
    for path in paths:
        relative = path.relative_to(directory).as_posix()
        if relative == METADATA_NAME:
            continue
        digest = hashlib.sha256()
        size = 0
        with path.open("rb") as handle:
            for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(chunk)
                size += len(chunk)
        result[relative] = {"sha256": digest.hexdigest(), "bytes": size}
    return result


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="scripts/convert_models.py",
        description="按模型清单下载 OPUS-MT，并转换为 CTranslate2 int8。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "示例：\n"
            "  python scripts/convert_models.py "
            "--manifest engine/model_manifest.example.json --ids opus-mt-zh-en\n"
            "  python scripts/convert_models.py "
            "--manifest engine/model_manifest.example.json --tier mvp\n"
            "  python scripts/convert_models.py "
            "--manifest engine/model_manifest.example.json --list\n"
            "\n"
            "默认读取 engine/model_manifest.json。"
            "该文件由模型选型清单提供；合并前请显式传入 --manifest。\n"
            "模型权重写入仓库根 models/（或 SUIYI_MODELS_DIR），不进 git。"
        ),
    )
    parser.add_argument("--ids", nargs="+", metavar="ID", help="只转换这些模型 id")
    parser.add_argument("--tier", choices=("mvp", "optional"), help="转换该 tier 的全部模型")
    parser.add_argument(
        "--manifest",
        type=Path,
        help="清单路径（默认 engine/model_manifest.json）",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        help="输出根目录（默认 SUIYI_MODELS_DIR，否则为仓库根 models/）",
    )
    parser.add_argument(
        "--quantization",
        default=DEFAULT_QUANTIZATION,
        help=f"CTranslate2 量化方式（默认 {DEFAULT_QUANTIZATION}）",
    )
    parser.add_argument("--force", action="store_true", help="即使 revision 与量化一致也重新转换")
    parser.add_argument("--list", action="store_true", dest="list_only", help="只列出清单中的模型")
    return parser


def main(argv: list[str] | None = None) -> int:
    _configure_stdio()
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return run(args)
    except ConvertError as exc:
        print(str(exc), file=sys.stderr, flush=True)
        return exc.code


def run(args: argparse.Namespace) -> int:
    repo_root = repo_root_from_here()
    manifest_path = resolve_manifest(args.manifest, repo_root)
    manifest = load_manifest(manifest_path)
    if args.list_only:
        if args.ids or args.tier or args.force:
            raise ConvertError("--list 只列出清单，不能同时使用 --ids、--tier 或 --force", code=2)
        _print_list(manifest)
        return 0
    if args.ids and args.tier:
        raise ConvertError("请只指定 --ids 或 --tier 之一", code=2)
    if not args.ids and not args.tier:
        raise ConvertError("请指定 --ids 或 --tier，或用 --list 查看清单", code=2)
    if args.quantization not in QUANTIZATIONS:
        allowed = ", ".join(QUANTIZATIONS)
        raise ConvertError(f"不支持的量化方式：{args.quantization}。可选：{allowed}", code=2)

    selected = select_models(manifest, args.ids, args.tier)
    out_dir = resolve_out_dir(args.out_dir, repo_root)
    try:
        out_dir.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise ConvertError(f"无法创建模型目录 {out_dir}：{exc}", code=1) from exc

    converted = 0
    skipped = 0
    for entry in selected:
        try:
            action = convert_one(entry, out_dir, args.quantization, args.force)
        except ConvertError:
            raise
        except Exception as exc:
            raise ConvertError(f"转换 {entry['id']} 失败：{exc}", code=1) from exc
        if action == "skipped":
            skipped += 1
        else:
            converted += 1
    _emit(f"完成：转换 {converted}，跳过 {skipped}")
    return 0


def _validate_model_entry(entry: object, index: int) -> None:
    if not isinstance(entry, dict):
        raise ConvertError(f"models[{index}] 必须是对象", code=2)
    missing = [key for key in _REQUIRED_MODEL_FIELDS if key not in entry]
    if missing:
        raise ConvertError(f"models[{index}] 缺少字段：{', '.join(missing)}", code=2)
    model_id = entry["id"]
    if not isinstance(model_id, str) or _MODEL_ID_RE.fullmatch(model_id) is None:
        raise ConvertError(f"models[{index}] 的 id 非法：{model_id!r}", code=2)
    for key in ("src", "tgt"):
        value = entry[key]
        if not isinstance(value, str) or _LANG_RE.fullmatch(value) is None:
            raise ConvertError(f"{model_id} 的 {key} 必须是 ISO 639-1 小写二字母代码", code=2)
    has_zip = "weights_source" in entry and entry["weights_source"] is not None
    if has_zip:
        _validate_weights_source(model_id, entry["weights_source"])
    repo = entry["hf_repo"]
    revision = entry["hf_revision"]
    if has_zip and repo is None and revision is None:
        pass
    else:
        if not isinstance(repo, str) or _REPO_RE.fullmatch(repo) is None:
            raise ConvertError(f"{model_id} 的 hf_repo 必须是 owner/name：{repo!r}", code=2)
        if not isinstance(revision, str) or _REVISION_RE.fullmatch(revision) is None:
            raise ConvertError(
                f"{model_id} 的 hf_revision 必须是 40 位小写 commit sha，"
                f"不能是分支名或标签：{revision!r}",
                code=2,
            )
    for key in ("license", "license_url", "attribution"):
        value = entry[key]
        if not isinstance(value, str) or not value.strip():
            raise ConvertError(f"{model_id} 的 {key} 必须是非空字符串", code=2)
    token = entry["src_prefix_token"]
    if token is not None and not isinstance(token, str):
        raise ConvertError(f"{model_id} 的 src_prefix_token 必须是字符串或 null", code=2)
    if entry["tier"] not in ("mvp", "optional"):
        raise ConvertError(f"{model_id} 的 tier 必须是 mvp 或 optional", code=2)
    if not isinstance(entry["notes"], str):
        raise ConvertError(f"{model_id} 的 notes 必须是字符串", code=2)


def _validate_weights_source(model_id: object, source: object) -> None:
    if not isinstance(source, dict):
        raise ConvertError(f"{model_id} 的 weights_source 必须是对象", code=2)
    if source.get("type") != _ZIP_SOURCE_TYPE:
        raise ConvertError(
            f"{model_id} 的 weights_source.type 只支持 {_ZIP_SOURCE_TYPE}：{source.get('type')!r}",
            code=2,
        )
    url = source.get("url")
    if not isinstance(url, str) or not url.startswith(_ZIP_URL_PREFIX) or not url.endswith(".zip"):
        raise ConvertError(
            f"{model_id} 的 weights_source.url 必须是 {_ZIP_URL_PREFIX} 下的 .zip：{url!r}",
            code=2,
        )
    sha = source.get("sha256")
    if not isinstance(sha, str) or _SHA256_RE.fullmatch(sha) is None:
        raise ConvertError(f"{model_id} 的 weights_source.sha256 必须是 64 位小写十六进制", code=2)
    model_file = source.get("model_file")
    if not isinstance(model_file, str) or not model_file.endswith(".npz"):
        raise ConvertError(f"{model_id} 的 weights_source.model_file 必须是 .npz 文件名", code=2)
    vocab_files = source.get("vocab_files")
    if (
        not isinstance(vocab_files, list)
        or len(vocab_files) not in (1, 2)
        or not all(isinstance(name, str) and name for name in vocab_files)
    ):
        raise ConvertError(
            f"{model_id} 的 weights_source.vocab_files 必须是 1 或 2 个文件名",
            code=2,
        )
    copy_files = source.get("copy_files")
    if not isinstance(copy_files, list) or not all(
        isinstance(name, str) and name for name in copy_files
    ):
        raise ConvertError(f"{model_id} 的 weights_source.copy_files 必须是文件名数组", code=2)
    missing = [name for name in _SPM_FILES if name not in copy_files]
    if missing:
        raise ConvertError(
            f"{model_id} 的 weights_source.copy_files 必须包含 {', '.join(missing)}",
            code=2,
        )
    for name in [model_file, *vocab_files, *copy_files]:
        if name.startswith(("/", "\\")) or ".." in Path(name).parts:
            raise ConvertError(f"{model_id} 的 weights_source 文件名非法：{name!r}", code=2)


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _is_current(model_dir: Path, key: str, quantization: str) -> bool:
    meta_path = model_dir / METADATA_NAME
    if not meta_path.is_file() or not (model_dir / "model.bin").is_file():
        return False
    try:
        metadata = json.loads(meta_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return False
    if not isinstance(metadata, dict):
        return False
    try:
        current = source_key(metadata)
    except (KeyError, TypeError):
        return False
    return current == key and metadata.get("quantization") == quantization


def _require_outputs(directory: Path) -> None:
    missing = [name for name in _REQUIRED_OUTPUTS if not (directory / name).is_file()]
    if missing:
        raise ConvertError(f"转换结果缺少文件：{', '.join(missing)}", code=1)
    has_vocab = any(
        "vocabulary" in path.name or path.name in _VOCAB_FILES
        for path in directory.iterdir()
        if path.is_file()
    )
    if not has_vocab:
        raise ConvertError("转换结果缺少词表文件（shared_vocabulary.* 或 vocab.json）", code=1)


def _model_dir(out_dir: Path, model_id: str) -> Path:
    final_dir = (out_dir / model_id).resolve()
    if not final_dir.is_relative_to(out_dir.resolve()):
        raise ConvertError(f"模型 id 越出输出目录：{model_id}", code=2)
    return final_dir


def _print_list(manifest: Mapping[str, object]) -> None:
    models = manifest["models"]
    if not isinstance(models, list):
        raise ConvertError("清单字段 models 必须是数组", code=2)
    for entry in models:
        token = entry["src_prefix_token"]
        token_text = "null" if token is None else str(token)
        source = entry.get("weights_source")
        if isinstance(source, Mapping):
            where = f"{source['url']}（sha256 {str(source['sha256'])[:12]}…）"
        else:
            where = f"{entry['hf_repo']}@{entry['hf_revision']}"
        _emit(
            f"{entry['id']}\t{entry['src']}→{entry['tgt']}\ttier={entry['tier']}\t"
            f"{where}\tprefix={token_text}"
        )


def _write_json(path: Path, payload: Mapping[str, object]) -> None:
    text = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
    path.write_text(text, encoding="utf-8")


def _emit(message: str) -> None:
    print(message, flush=True)


def _missing_convert_extra() -> str:
    return '缺少转换依赖。请先安装：pip install -e "engine[convert]"'


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


def _is_int(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)
