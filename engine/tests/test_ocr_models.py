"""#51：OCR 模型清单、下载校验与本地检查。全部离线，不装 rapidocr 也能跑。"""

from __future__ import annotations

import copy
import hashlib
import io
import json
import socket
from pathlib import Path

import pytest

from suiyi_engine.tools import ocr_models
from suiyi_engine.tools.ocr_models import (
    OcrModelError,
    OcrModelsMissingError,
    download_model,
    find_problems,
    forbid_network,
    load_manifest,
    local_model_paths,
    parse_manifest,
    select_models,
    write_metadata,
)

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "ocr_model_manifest.json"
RUNTIME_FORBIDDEN = ("torch", "paddle", "paddlepaddle", "transformers")


@pytest.fixture()
def raw_manifest() -> dict[str, object]:
    return json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))


def _fake_manifest(payloads: dict[str, bytes]) -> dict[str, object]:
    """三个假模型（det/cls/rec），内容与哈希由 payloads 决定。"""

    models = []
    for task, data in payloads.items():
        models.append(
            {
                "id": f"fake_{task}",
                "task": task,
                "ocr_version": "PP-OCRv6" if task != "cls" else "PP-OCRv4",
                "file": f"fake_{task}.onnx",
                "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
                "url": f"{ocr_models.ALLOWED_URL_PREFIX}v0/onnx/fake_{task}.onnx",
                "upstream": "x",
                "upstream_url": "https://example.invalid",
                "license": "Apache-2.0",
                "license_url": "https://example.invalid",
                "languages": "x",
                "tier": "recommended",
                "notes": "",
            }
        )
    return {
        "schema_version": 1,
        "rapidocr": {"requirement": "rapidocr>=3.9.2,<3.10"},
        "recommended": {"det": "fake_det", "cls": "fake_cls", "rec": "fake_rec", "use_cls": False},
        "models": models,
    }


PAYLOADS = {"det": b"det-bytes" * 100, "cls": b"cls", "rec": b"rec-bytes" * 50}


def _opener_for(payloads: dict[str, bytes], calls: list[str] | None = None):
    by_file = {f"fake_{task}.onnx": data for task, data in payloads.items()}

    def opener(url: str) -> io.BytesIO:
        if calls is not None:
            calls.append(url)
        return io.BytesIO(by_file[url.rsplit("/", 1)[1]])

    return opener


def test_repo_manifest_is_valid_and_recommended_set_is_small(raw_manifest: dict) -> None:
    manifest = load_manifest(MANIFEST_PATH)
    recommended = manifest.recommended_models()
    assert [m.task for m in recommended] == ["det", "cls", "rec"]
    assert manifest.recommended == {
        "det": "PP-OCRv6_det_small",
        "cls": "ch_ppocr_mobile_v2.0_cls_mobile",
        "rec": "PP-OCRv6_rec_small",
    }
    assert manifest.use_cls is False
    # 验收：推荐组合合计 ≤ 60 MB，否则须说明
    assert sum(m.bytes for m in recommended) <= 60_000_000
    assert {m.license for m in manifest.models.values()} == {"Apache-2.0"}
    assert raw_manifest["rapidocr"]["requirement"].startswith("rapidocr>=3.9.2")


def test_repo_manifest_covers_candidates_named_in_issue(raw_manifest: dict) -> None:
    ids = {m["id"] for m in raw_manifest["models"]}
    assert {
        "ch_PP-OCRv5_det_mobile",
        "ch_PP-OCRv5_rec_mobile",
        "ch_PP-OCRv5_det_server",
        "ch_PP-OCRv5_rec_server",
        "japan_PP-OCRv4_rec_mobile",
        "en_PP-OCRv5_rec_mobile",
        "PP-OCRv6_det_small",
        "PP-OCRv6_rec_small",
    } <= ids
    for model in raw_manifest["models"]:
        assert model["url"].startswith(ocr_models.ALLOWED_URL_PREFIX + "v3.9.2/")
        assert model["upstream_url"].startswith("https://")


def test_ocr_extra_keeps_forbidden_packages_out_of_runtime() -> None:
    tomllib = pytest.importorskip("tomllib")
    pyproject = tomllib.loads((MANIFEST_PATH.parent / "pyproject.toml").read_text(encoding="utf-8"))
    runtime = [d.lower() for d in pyproject["project"]["dependencies"]]
    ocr = [d.lower() for d in pyproject["project"]["optional-dependencies"]["ocr"]]
    assert any(d.startswith("rapidocr") for d in ocr)
    assert any(d.startswith("onnxruntime") for d in ocr)
    for dep in runtime + ocr:
        assert not dep.startswith(RUNTIME_FORBIDDEN), dep
    assert not any(d.startswith(("rapidocr", "onnxruntime", "opencv")) for d in runtime)


@pytest.mark.parametrize(
    ("mutate", "message"),
    [
        (lambda m: m["models"][0].update(license="CC-BY-NC-4.0"), "不可默认分发"),
        (lambda m: m["models"][0].update(sha256="ABC"), "sha256"),
        (lambda m: m["models"][0].update(url="https://evil.example/x.onnx"), "url"),
        (lambda m: m["models"][0].update(file="../x.onnx"), "file"),
        (lambda m: m["models"][0].update(bytes=0), "bytes"),
        (lambda m: m["models"][0].update(task="layout"), "task"),
        (lambda m: m["recommended"].update(det="nope"), "recommended.det"),
        (lambda m: m["recommended"].update(det="fake_rec"), "不是 det"),
        (lambda m: m["recommended"].pop("use_cls"), "use_cls"),
        (lambda m: m["models"].append(copy.deepcopy(m["models"][0])), "重复"),
        (lambda m: m.update(schema_version=2), "schema_version"),
    ],
)
def test_parse_manifest_rejects_bad_entries(mutate, message: str) -> None:
    data = _fake_manifest(PAYLOADS)
    mutate(data)
    with pytest.raises(OcrModelError, match=message):
        parse_manifest(data)


def test_select_models() -> None:
    manifest = parse_manifest(_fake_manifest(PAYLOADS))
    assert [m.id for m in select_models(manifest)] == ["fake_det", "fake_cls", "fake_rec"]
    assert [m.id for m in select_models(manifest, ["fake_rec", "fake_rec"])] == ["fake_rec"]
    assert len(select_models(manifest, everything=True)) == 3
    with pytest.raises(OcrModelError, match="nope"):
        select_models(manifest, ["nope"])


def test_download_verifies_and_skips_existing(tmp_path: Path) -> None:
    manifest = parse_manifest(_fake_manifest(PAYLOADS))
    calls: list[str] = []
    for model in manifest.recommended_models():
        download_model(model, tmp_path, opener=_opener_for(PAYLOADS, calls), log=lambda _m: None)
    assert len(calls) == 3
    paths = local_model_paths(tmp_path, manifest)
    assert paths["rec"] == tmp_path / "ocr" / "fake_rec.onnx"
    assert paths["rec"].read_bytes() == PAYLOADS["rec"]
    assert not list((tmp_path / "ocr").glob(".*.partial"))

    logs: list[str] = []
    download_model(
        manifest.models["fake_det"], tmp_path, opener=_opener_for(PAYLOADS, calls), log=logs.append
    )
    assert len(calls) == 3
    assert "已存在" in logs[0]

    meta = json.loads(write_metadata(tmp_path, manifest).read_text(encoding="utf-8"))
    assert set(meta["files"]) == {"fake_det.onnx", "fake_cls.onnx", "fake_rec.onnx"}
    assert meta["files"]["fake_rec.onnx"]["sha256"] == hashlib.sha256(PAYLOADS["rec"]).hexdigest()
    assert meta["recommended"]["use_cls"] is False


@pytest.mark.parametrize(
    ("served", "message"),
    [
        (b"det-bytes" * 99 + b"det-bytez", "sha256"),  # 同长度、内容不同
        (b"short", "字节"),
        (b"det-bytes" * 101, "超过"),
    ],
)
def test_download_rejects_mismatch_without_leaving_files(
    tmp_path: Path, served: bytes, message: str
) -> None:
    manifest = parse_manifest(_fake_manifest(PAYLOADS))
    with pytest.raises(OcrModelError, match=message):
        download_model(
            manifest.models["fake_det"],
            tmp_path,
            opener=_opener_for({**PAYLOADS, "det": served}),
            log=lambda _m: None,
        )
    assert list((tmp_path / "ocr").iterdir()) == []


def test_download_network_error_is_reported(tmp_path: Path) -> None:
    manifest = parse_manifest(_fake_manifest(PAYLOADS))

    def broken(_url: str) -> io.BytesIO:
        raise OSError("connection refused")

    with pytest.raises(OcrModelError, match="下载失败"):
        download_model(manifest.models["fake_det"], tmp_path, opener=broken, log=lambda _m: None)
    assert list((tmp_path / "ocr").iterdir()) == []


def test_missing_and_corrupt_models_are_named(tmp_path: Path) -> None:
    manifest = parse_manifest(_fake_manifest(PAYLOADS))
    base = tmp_path / "ocr"
    base.mkdir()
    (base / "fake_det.onnx").write_bytes(PAYLOADS["det"])
    (base / "fake_rec.onnx").write_bytes(b"x" * len(PAYLOADS["rec"]))  # 损坏
    problems = find_problems(tmp_path, manifest.recommended_models())
    assert set(problems) == {"fake_cls", "fake_rec"}
    assert "不存在" in problems["fake_cls"]
    assert "sha256" in problems["fake_rec"]
    with pytest.raises(OcrModelsMissingError) as excinfo:
        local_model_paths(tmp_path, manifest)
    assert excinfo.value.missing == ("fake_cls", "fake_rec")
    assert "fake_cls" in str(excinfo.value)
    assert "download_ocr_models.py" in str(excinfo.value)


def test_check_command_reports_missing_without_network(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    manifest_path = tmp_path / "m.json"
    manifest_path.write_text(json.dumps(_fake_manifest(PAYLOADS)), encoding="utf-8")
    with forbid_network():
        code = ocr_models.main(
            ["--manifest", str(manifest_path), "--models-dir", str(tmp_path), "check"]
        )
    assert code == 1
    captured = capsys.readouterr()
    assert "fake_det" in captured.err
    assert "缺失/不符  fake_rec" in captured.out


def test_smoke_refuses_before_importing_rapidocr_when_models_missing(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    import sys

    manifest_path = tmp_path / "m.json"
    manifest_path.write_text(json.dumps(_fake_manifest(PAYLOADS)), encoding="utf-8")
    image = tmp_path / "x.png"
    image.write_bytes(b"\x89PNG\r\n\x1a\n")
    before = set(sys.modules)
    code = ocr_models.main(
        ["--manifest", str(manifest_path), "--models-dir", str(tmp_path), "smoke", str(image)]
    )
    assert code == 1
    assert "缺少 OCR 模型：fake_det、fake_cls、fake_rec" in capsys.readouterr().err
    assert not any(
        name.startswith(("rapidocr", "onnxruntime")) for name in set(sys.modules) - before
    )


def test_forbid_network_blocks_and_restores() -> None:
    original = socket.create_connection
    with forbid_network(), pytest.raises(OcrModelError, match="网络"):
        socket.create_connection(("127.0.0.1", 9))
    with forbid_network(), pytest.raises(OcrModelError):
        socket.getaddrinfo("example.com", 443)
    assert socket.create_connection is original


def test_importing_tool_does_not_pull_heavy_modules() -> None:
    import subprocess
    import sys

    code = (
        "import sys, suiyi_engine.tools.ocr_models\n"
        "bad = [m for m in sys.modules if m.split('.')[0] in "
        "('rapidocr','onnxruntime','cv2','torch','paddle','transformers')]\n"
        "print(bad); sys.exit(1 if bad else 0)"
    )
    completed = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True)
    assert completed.returncode == 0, completed.stdout + completed.stderr


def test_audit_flags_forbidden_distributions(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    from importlib import metadata

    class FakeDist:
        def __init__(self, name: str) -> None:
            self.metadata = {"Name": name}
            self.version = "1.0"
            self.files = ()

    monkeypatch.setattr(metadata, "distributions", lambda: [FakeDist("numpy"), FakeDist("torch")])
    assert ocr_models.main(["audit"]) == 1
    assert "torch" in capsys.readouterr().err
    monkeypatch.setattr(
        metadata, "distributions", lambda: [FakeDist("numpy"), FakeDist("rapidocr")]
    )
    assert ocr_models.main(["audit"]) == 0
    assert "未发现" in capsys.readouterr().out
