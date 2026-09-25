"""上游 Marian zip 来源（weights_source.type = opus-mt-zip）的转换测试。

下载与转换都被替换，不访问网络，也不需要 ctranslate2。
"""

import hashlib
import io
import json
import re
import sys
import types
import zipfile
from pathlib import Path

import pytest

from suiyi_engine.tools import convert as convert_mod

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "engine" / "model_manifest.json"
ZIP_URL = "https://object.pouta.csc.fi/Tatoeba-MT-models/zho-jpn/example.zip"
MODEL_FILE = "model.npz"
SRC_VOCAB = "m.src.vocab"
TRG_VOCAB = "m.trg.vocab"


def _zip_bytes() -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as bundle:
        bundle.writestr(MODEL_FILE, b"npz")
        bundle.writestr(SRC_VOCAB, "<unk>\n<s>\n</s>\n▁中\n")
        bundle.writestr(TRG_VOCAB, "<unk>\n<s>\n</s>\n▁日\n")
        bundle.writestr("source.spm", b"source-spm")
        bundle.writestr("target.spm", b"target-spm")
        bundle.writestr("LICENSE", "Attribution 4.0 International\n")
        bundle.writestr("README.md", "# example\n")
        bundle.writestr("train.log", "unused\n")
    return buffer.getvalue()


def _source(sha256: str, **overrides: object) -> dict:
    source: dict[str, object] = {
        "type": "opus-mt-zip",
        "url": ZIP_URL,
        "sha256": sha256,
        "model_file": MODEL_FILE,
        "vocab_files": [SRC_VOCAB, TRG_VOCAB],
        "copy_files": ["source.spm", "target.spm", "LICENSE", "README.md"],
    }
    source.update(overrides)
    return source


def _entry(sha256: str = "a" * 64, **overrides: object) -> dict:
    entry: dict[str, object] = {
        "id": "zip-zh-ja",
        "src": "zh",
        "tgt": "ja",
        "hf_repo": None,
        "hf_revision": None,
        "weights_source": _source(sha256),
        "license": "CC-BY-4.0",
        "license_url": "https://creativecommons.org/licenses/by/4.0/legalcode",
        "attribution": "Helsinki-NLP Tatoeba-MT",
        "src_prefix_token": None,
        "tier": "mvp",
        "notes": "",
    }
    entry.update(overrides)
    return entry


def _write_manifest(directory: Path, *entries: dict) -> Path:
    path = directory / "manifest.json"
    document = {"schema_version": 1, "models": list(entries), "pivots": []}
    path.write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")
    return path


def _fake_converter(calls: list) -> types.ModuleType:
    class MarianConverter:
        def __init__(self, model_path: str, vocab_paths: list[str]) -> None:
            calls.append({"model_path": model_path, "vocab_paths": list(vocab_paths)})

        def convert(self, output_dir: str, quantization: str, force: bool) -> None:
            out = Path(output_dir)
            out.mkdir(parents=True, exist_ok=True)
            (out / "model.bin").write_bytes(b"bin")
            (out / "config.json").write_text("{}\n", encoding="utf-8")
            (out / "source_vocabulary.json").write_text("[]\n", encoding="utf-8")
            (out / "target_vocabulary.json").write_text("[]\n", encoding="utf-8")
            calls[-1]["quantization"] = quantization

    module = types.ModuleType("ctranslate2")
    module.__version__ = "4.8.2"
    module.converters = types.SimpleNamespace(MarianConverter=MarianConverter)
    return module


def _serve_zip(monkeypatch: pytest.MonkeyPatch, payload: bytes) -> list[str]:
    requested: list[str] = []

    def urlopen(url: str, timeout: float) -> io.BytesIO:
        requested.append(url)
        return io.BytesIO(payload)

    monkeypatch.setattr(convert_mod.urllib.request, "urlopen", urlopen)
    return requested


def test_official_manifest_ja_models_use_verified_zips() -> None:
    manifest = convert_mod.load_manifest(MANIFEST)
    mvp = {(m["src"], m["tgt"]): m for m in manifest["models"] if m["tier"] == "mvp"}
    for direction in (("zh", "ja"), ("en", "ja")):
        entry = mvp[direction]
        source = entry["weights_source"]
        assert source["type"] == "opus-mt-zip"
        assert source["url"].startswith("https://object.pouta.csc.fi/Tatoeba-MT-models/")
        assert len(source["sha256"]) == 64
        assert "LICENSE" in source["copy_files"]
        assert entry["license"] == "CC-BY-4.0"
        assert "NC" not in entry["license"]
    rejected = {item.get("hf_repo") for item in manifest["rejected"]}
    assert "Helsinki-NLP/opus-mt-tc-big-zh-ja" in rejected
    assert "Helsinki-NLP/opus-tatoeba-en-ja" in rejected
    covered = set(mvp)
    for pivot in manifest["pivots"]:
        assert (pivot["src"], pivot["via"]) in covered
        assert (pivot["via"], pivot["tgt"]) in covered
        covered.add((pivot["src"], pivot["tgt"]))
    need = {("zh", "en"), ("en", "zh"), ("zh", "ja"), ("ja", "zh"), ("en", "ja"), ("ja", "en")}
    assert need <= covered


@pytest.mark.parametrize(
    ("overrides", "message"),
    [
        ({"type": "hf"}, "type"),
        ({"url": "https://example.com/model.zip"}, "url"),
        ({"sha256": "abc"}, "sha256"),
        ({"model_file": "model.bin"}, "model_file"),
        ({"vocab_files": []}, "vocab_files"),
        ({"copy_files": ["LICENSE"]}, "source.spm"),
        ({"copy_files": ["source.spm", "target.spm", "../evil"]}, "文件名非法"),
    ],
)
def test_invalid_weights_source_is_rejected(tmp_path: Path, overrides: dict, message: str) -> None:
    entry = _entry()
    source = _source("a" * 64)
    source.update(overrides)
    entry["weights_source"] = source
    with pytest.raises(convert_mod.ConvertError, match=message):
        convert_mod.load_manifest(_write_manifest(tmp_path, entry))


def test_null_hf_fields_require_weights_source(tmp_path: Path) -> None:
    entry = _entry()
    del entry["weights_source"]
    with pytest.raises(convert_mod.ConvertError, match="hf_repo"):
        convert_mod.load_manifest(_write_manifest(tmp_path, entry))


def _ct2_unquote(token: str) -> str:
    """与 ctranslate2/converters/marian.py 的 load_vocab 相同的去引号逻辑。"""
    if token.startswith('"') and token.endswith('"'):
        token = re.sub(r"\\([^x])", r"\1", token)
        token = token[1:-1]
        if token.startswith("\\x"):
            token = chr(int(token[2:], base=16))
    return token


def test_marian_vocab_to_yaml_escapes_tokens(tmp_path: Path) -> None:
    tokens = ["<unk>", '"quoted"', "a:b", "back\\slash", "▁日本", "\x85", "\r"]
    plain = tmp_path / "plain.vocab"
    plain.write_bytes(("\n".join(tokens) + "\n").encode("utf-8"))
    out = convert_mod.marian_vocab_to_yaml(plain, tmp_path / "out.yml")
    lines = out.read_bytes().decode("utf-8").split("\n")[:-1]
    parsed = []
    for index, line in enumerate(lines):
        key, value = line.rsplit(":", 1)
        assert int(value) == index
        parsed.append(_ct2_unquote(key))
    assert parsed == tokens
    yml = tmp_path / "already.yml"
    yml.write_text('"a": 0\n', encoding="utf-8")
    assert convert_mod.marian_vocab_to_yaml(yml, tmp_path / "x.yml") == yml


def test_marian_vocab_rejects_control_chars_inside_tokens(tmp_path: Path) -> None:
    plain = tmp_path / "ctrl.vocab"
    plain.write_bytes(b"a\nb\tc\n")
    with pytest.raises(convert_mod.ConvertError, match="控制字符"):
        convert_mod.marian_vocab_to_yaml(plain, tmp_path / "out.yml")


def test_marian_vocab_rejects_duplicates(tmp_path: Path) -> None:
    plain = tmp_path / "dup.vocab"
    plain.write_bytes(b"a\nb\na\n")
    with pytest.raises(convert_mod.ConvertError, match="重复"):
        convert_mod.marian_vocab_to_yaml(plain, tmp_path / "out.yml")


def test_zip_conversion_end_to_end_with_cache(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    payload = _zip_bytes()
    sha = hashlib.sha256(payload).hexdigest()
    requested = _serve_zip(monkeypatch, payload)
    calls: list = []
    monkeypatch.setitem(sys.modules, "ctranslate2", _fake_converter(calls))
    monkeypatch.setattr(convert_mod, "utc_now_iso", lambda: "2026-09-25T00:00:00+00:00")
    manifest = _write_manifest(tmp_path, _entry(sha))
    out_dir = tmp_path / "models"
    args = ["--manifest", str(manifest), "--out-dir", str(out_dir), "--ids", "zip-zh-ja"]

    assert convert_mod.main(args) == 0
    model_dir = out_dir / "zip-zh-ja"
    assert requested == [ZIP_URL]
    assert (out_dir / ".cache" / f"{sha}.zip").is_file()
    assert not (out_dir / ".zip-zh-ja.work").exists()
    for name in ("model.bin", "source.spm", "target.spm", "LICENSE", "README.md"):
        assert (model_dir / name).is_file()
    assert not (model_dir / "train.log").exists()
    assert calls[0]["quantization"] == "int8"
    assert calls[0]["model_path"].endswith(MODEL_FILE)
    assert [Path(p).suffix for p in calls[0]["vocab_paths"]] == [".yml", ".yml"]
    metadata = json.loads((model_dir / "suiyi-model.json").read_text(encoding="utf-8"))
    assert metadata["weights_source"]["sha256"] == sha
    assert metadata["hf_revision"] is None
    assert metadata["ctranslate2_version"] == "4.8.2"

    # 同一 sha256 与量化：跳过，不再下载。
    assert convert_mod.main(args) == 0
    assert "已存在" in capsys.readouterr().out
    assert requested == [ZIP_URL]

    # --force：复用缓存 zip，不再下载。
    assert convert_mod.main([*args, "--force"]) == 0
    assert "复用缓存" in capsys.readouterr().out
    assert requested == [ZIP_URL]


def test_zip_sha_mismatch_fails_and_leaves_nothing(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    _serve_zip(monkeypatch, _zip_bytes())
    monkeypatch.setitem(sys.modules, "ctranslate2", _fake_converter([]))
    manifest = _write_manifest(tmp_path, _entry("b" * 64))
    out_dir = tmp_path / "models"
    code = convert_mod.main(
        ["--manifest", str(manifest), "--out-dir", str(out_dir), "--ids", "zip-zh-ja"]
    )
    assert code == 1
    assert not (out_dir / "zip-zh-ja").exists()
    assert not (out_dir / ".zip-zh-ja.partial").exists()
    assert not (out_dir / ".zip-zh-ja.work").exists()
    assert not list((out_dir / ".cache").glob("*.zip"))


def test_changed_zip_sha_requires_force(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    payload = _zip_bytes()
    sha = hashlib.sha256(payload).hexdigest()
    _serve_zip(monkeypatch, payload)
    monkeypatch.setitem(sys.modules, "ctranslate2", _fake_converter([]))
    out_dir = tmp_path / "models"
    manifest = _write_manifest(tmp_path, _entry(sha))
    args = ["--manifest", str(manifest), "--out-dir", str(out_dir), "--ids", "zip-zh-ja"]
    assert convert_mod.main(args) == 0
    _write_manifest(tmp_path, _entry("c" * 64))
    assert convert_mod.main(args) == 2


@pytest.mark.model
def test_converted_zh_ja_is_usable() -> None:
    """Issue #25 回归：zh→ja 不再输出「本、」「↓↓↓」。模型不在 SUIYI_MODELS_DIR 时跳过。"""
    import os

    from suiyi_engine.translator import Translator

    models_dir = Path(os.environ["SUIYI_MODELS_DIR"])
    if not (models_dir / "opus-mt-zho-jpn-tc-big-2022-07-28" / "suiyi-model.json").is_file():
        pytest.skip("尚未转换 opus-mt-zho-jpn-tc-big-2022-07-28")
    translator = Translator(models_dir)
    water = translator.translate("请给我一杯水。", "zh", "ja").text
    sony = translator.translate("索尼总部位于东京。", "zh", "ja").text
    assert "水" in water
    assert "ソニー" in sony
    assert "東京" in sony
    assert "↓" not in sony
