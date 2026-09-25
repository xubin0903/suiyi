"""OcrEngine 的加载、并发、低置信度、图片解码与导入约束（#52）。

用假后端，不需要模型和 rapidocr；解码字节图片的用例需要 PIL（``engine[ocr]`` 带入），没有就跳过。
"""

from __future__ import annotations

import io
import json
import subprocess
import sys
import threading
import time
from pathlib import Path

import numpy as np
import pytest

from suiyi_engine.ocr import (
    InvalidImageError,
    OcrEngine,
    OcrError,
    OcrModelsMissingError,
)
from suiyi_engine.ocr.engine import ImageTooLargeError, decode_image
from suiyi_engine.tools.ocr_models import forbid_network, parse_manifest

FORBIDDEN = ("torch", "paddle", "paddleocr", "transformers")
LAZY = ("rapidocr", "onnxruntime", "cv2", "PIL")


def _manifest_and_models(tmp_path: Path):
    """造一个指向小文件的清单，让 OcrEngine 的本地校验通过（假后端不读文件内容）。"""

    import hashlib

    ocr = tmp_path / "models" / "ocr"
    ocr.mkdir(parents=True)
    models = []
    for task in ("det", "cls", "rec"):
        data = f"fake {task} model".encode()
        (ocr / f"{task}.onnx").write_bytes(data)
        models.append(
            {
                "id": f"fake_{task}",
                "task": task,
                "ocr_version": "PP-OCRv6",
                "file": f"{task}.onnx",
                "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
                "url": f"https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/{task}.onnx",
                "upstream": "fake",
                "upstream_url": "https://example.invalid/",
                "license": "Apache-2.0",
                "license_url": "https://example.invalid/",
                "languages": "测试",
                "tier": "recommended",
                "notes": "",
            }
        )
    real = json.loads((Path(__file__).parents[1] / "ocr_model_manifest.json").read_text("utf-8"))
    real["models"] = models
    real["recommended"] = {
        "det": "fake_det",
        "cls": "fake_cls",
        "rec": "fake_rec",
        "use_cls": False,
    }
    return parse_manifest(real), tmp_path / "models"


def _box(x0: float, y0: float, x1: float, y1: float) -> list[list[float]]:
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


class FakeFactory:
    def __init__(self, raw=None, delay: float = 0.0) -> None:
        self.calls = 0
        self.active = 0
        self.max_active = 0
        self.raw = (
            raw
            if raw is not None
            else [
                (_box(10, 10, 300, 30), "第一行文字写得很长很长", 0.98),
                (_box(10, 36, 200, 56), "第二行。", 0.91),
                (_box(10, 90, 300, 110), "看不清", 0.2),
            ]
        )
        self.delay = delay
        self.lock = threading.Lock()

    def __call__(self, paths, manifest, threads):
        with self.lock:
            self.calls += 1
        time.sleep(self.delay)
        assert set(paths) == {"det", "cls", "rec"}
        assert threads >= 1

        def run(image: np.ndarray):
            assert image.dtype == np.uint8 and image.ndim == 3 and image.shape[2] == 3
            with self.lock:
                self.active += 1
                self.max_active = max(self.max_active, self.active)
            time.sleep(self.delay)
            with self.lock:
                self.active -= 1
            return list(self.raw)

        return run


@pytest.fixture
def fake_engine(tmp_path: Path):
    manifest, models = _manifest_and_models(tmp_path)
    factory = FakeFactory(delay=0.01)
    return OcrEngine(models, manifest=manifest, backend_factory=factory), factory


def test_import_does_not_load_heavy_or_forbidden_modules() -> None:
    code = (
        "import sys, suiyi_engine, suiyi_engine.ocr\n"
        "from suiyi_engine.ocr import OcrEngine, merge_paragraphs\n"
        f"bad = [m for m in {FORBIDDEN + LAZY!r} if m in sys.modules]\n"
        "print(','.join(bad))\n"
    )
    out = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, check=True)
    assert out.stdout.strip() == ""


def test_missing_models_raise_specific_error_without_network(tmp_path: Path) -> None:
    engine = OcrEngine(tmp_path / "empty-models")
    with forbid_network(), pytest.raises(OcrModelsMissingError) as info:
        engine.recognize(np.zeros((10, 10, 3), dtype=np.uint8))
    assert str(tmp_path / "empty-models") in str(info.value)
    assert not engine.loaded
    code = (
        "import sys, pathlib\n"
        "from suiyi_engine.ocr import OcrEngine, OcrModelsMissingError\n"
        "try:\n"
        "    OcrEngine(pathlib.Path(sys.argv[1])).load()\n"
        "except OcrModelsMissingError:\n"
        "    print('missing', 'rapidocr' in sys.modules)\n"
    )
    out = subprocess.run(
        [sys.executable, "-c", code, str(tmp_path)], capture_output=True, text=True, check=True
    )
    assert out.stdout.strip() == "missing False"


def test_corrupt_model_is_reported_as_missing(tmp_path: Path) -> None:
    manifest, models = _manifest_and_models(tmp_path)
    (models / "ocr" / "rec.onnx").write_bytes(b"truncated")
    engine = OcrEngine(models, manifest=manifest, backend_factory=FakeFactory())
    with pytest.raises(OcrModelsMissingError, match="fake_rec"):
        engine.load()


def test_recognize_builds_lines_and_paragraphs(fake_engine) -> None:
    engine, _factory = fake_engine
    result = engine.recognize(np.full((120, 320, 3), 255, dtype=np.uint8))
    assert (result.width, result.height) == (320, 120)
    assert [line.text for line in result.lines] == ["第一行文字写得很长很长", "第二行。", "看不清"]
    assert [line.low_confidence for line in result.lines] == [False, False, True]
    assert result.low_confidence_count == 1
    assert result.text == "第一行文字写得很长很长第二行。"
    assert result.paragraphs[0].line_indices == (0, 1)
    assert result.elapsed_ms >= 0
    payload = result.to_dict()
    assert payload["image"] == {"width": 320, "height": 120}
    assert payload["lines"][2]["low_confidence"] is True
    assert payload["lines"][0]["box"] == [[10, 10], [300, 10], [300, 30], [10, 30]]
    json.dumps(payload, ensure_ascii=False)


def test_min_score_is_configurable(tmp_path: Path) -> None:
    manifest, models = _manifest_and_models(tmp_path)
    engine = OcrEngine(models, manifest=manifest, min_score=0.1, backend_factory=FakeFactory())
    result = engine.recognize(np.zeros((120, 320, 3), dtype=np.uint8))
    assert result.low_confidence_count == 0
    with pytest.raises(ValueError):
        OcrEngine(models, manifest=manifest, min_score=1.5)


def test_all_low_confidence_keeps_lines(tmp_path: Path) -> None:
    manifest, models = _manifest_and_models(tmp_path)
    factory = FakeFactory(raw=[(_box(0, 0, 50, 20), "模糊", 0.1)])
    engine = OcrEngine(models, manifest=manifest, backend_factory=factory)
    result = engine.recognize(np.zeros((40, 60, 3), dtype=np.uint8))
    assert result.text == ""
    assert [(line.text, line.low_confidence) for line in result.lines] == [("模糊", True)]


def test_empty_result(tmp_path: Path) -> None:
    manifest, models = _manifest_and_models(tmp_path)
    engine = OcrEngine(models, manifest=manifest, backend_factory=FakeFactory(raw=[]))
    result = engine.recognize(np.zeros((40, 60, 3), dtype=np.uint8))
    assert result.lines == () and result.paragraphs == () and result.text == ""


def test_model_loads_once_and_concurrent_calls_do_not_crash(fake_engine) -> None:
    engine, factory = fake_engine
    image = np.full((120, 320, 3), 255, dtype=np.uint8)
    results: list[str] = []
    errors: list[BaseException] = []

    def worker() -> None:
        try:
            for _ in range(3):
                results.append(engine.recognize(image).text)
        except BaseException as exc:  # noqa: BLE001 - 汇总到主线程断言
            errors.append(exc)

    threads = [threading.Thread(target=worker) for _ in range(4)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert errors == []
    assert factory.calls == 1
    assert factory.max_active == 1  # 推理串行
    assert results == ["第一行文字写得很长很长第二行。"] * 12
    assert engine.loaded


def test_lang_hint_is_validated(fake_engine) -> None:
    engine, _factory = fake_engine
    image = np.zeros((40, 60, 3), dtype=np.uint8)
    for lang in ("auto", "zh", "en", "ja"):
        engine.recognize(image, lang=lang)
    with pytest.raises(OcrError, match="语言"):
        engine.recognize(image, lang="fr")


def test_missing_rapidocr_is_a_clear_error(tmp_path: Path, monkeypatch) -> None:
    from suiyi_engine.ocr import engine as engine_module

    manifest, models = _manifest_and_models(tmp_path)
    monkeypatch.setitem(sys.modules, "rapidocr", None)
    engine = engine_module.OcrEngine(models, manifest=manifest)
    with pytest.raises(OcrError, match="engine\\[ocr\\]"):
        engine.load()


# ---- 解码 ---------------------------------------------------------------------


def test_decode_array_variants() -> None:
    gray = np.full((5, 7), 128, dtype=np.uint8)
    assert decode_image(gray).shape == (5, 7, 3)
    bgra = np.zeros((2, 2, 4), dtype=np.uint8)  # 全透明 → 白底
    assert decode_image(bgra).tolist() == [[[255, 255, 255]] * 2] * 2
    with pytest.raises(InvalidImageError):
        decode_image(np.zeros((5, 5), dtype=np.float32))
    with pytest.raises(InvalidImageError):
        decode_image(np.zeros((0, 5, 3), dtype=np.uint8))
    with pytest.raises(ImageTooLargeError):
        decode_image(np.zeros((100, 100, 3), dtype=np.uint8), max_pixels=9999)
    with pytest.raises(InvalidImageError):
        decode_image(12345)


def test_decode_bytes_and_paths(tmp_path: Path) -> None:
    pil = pytest.importorskip("PIL.Image")
    image = pil.new("RGBA", (4, 3), (255, 0, 0, 255))
    image.putpixel((0, 0), (0, 0, 0, 0))
    buf = io.BytesIO()
    image.save(buf, format="PNG")
    data = buf.getvalue()

    array = decode_image(data)
    assert array.shape == (3, 4, 3)
    assert array[0, 0].tolist() == [255, 255, 255]  # 透明像素合成白底
    assert array[1, 1].tolist() == [0, 0, 255]  # 红色 → BGR

    path = tmp_path / "x.png"
    path.write_bytes(data)
    assert decode_image(path).shape == (3, 4, 3)
    assert decode_image(str(path)).shape == (3, 4, 3)
    assert decode_image(memoryview(data)).shape == (3, 4, 3)

    with pytest.raises(ImageTooLargeError):
        decode_image(data, max_pixels=11)
    with pytest.raises(InvalidImageError):
        decode_image(b"not an image")
    with pytest.raises(InvalidImageError):
        decode_image(b"")
    with pytest.raises(InvalidImageError):
        decode_image(data[:40])  # 截断的 PNG
    # 字符串只当本地路径，不会当 URL 去下载
    with forbid_network(), pytest.raises(InvalidImageError):
        decode_image("https://example.com/a.png")
