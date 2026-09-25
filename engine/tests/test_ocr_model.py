"""真实 OCR 模型上的识别、段落与竖排顺序（#52）。

需要 ``SUIYI_MODELS_DIR`` 指向含 ``ocr/`` 的模型目录
（``scripts/download_ocr_models.py download``），并安装 ``engine[ocr]``。
CI 的 OCR 步骤用 rapidocr wheel 自带的同一批 ONNX 跑这里。
"""

from __future__ import annotations

import os
import threading
import unicodedata
from pathlib import Path

import pytest

pytestmark = pytest.mark.model

FIXTURES = Path(__file__).parent / "fixtures" / "ocr"
MIN_SIMILARITY = 0.9


def _normalize(text: str) -> str:
    return "".join(unicodedata.normalize("NFKC", text).split())


def similarity(expected: str, actual: str) -> float:
    """1 - 字符编辑距离 / 期望长度（去空白、NFKC 后）。"""

    a, b = _normalize(expected), _normalize(actual)
    if not a:
        return 1.0 if not b else 0.0
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return max(0.0, 1.0 - prev[-1] / len(a))


def expected_paragraphs(name: str) -> list[str]:
    raw = (FIXTURES / f"{name}.txt").read_text(encoding="utf-8")
    return [p.strip() for p in raw.split("\n\n") if p.strip()]


def _unavailable(reason: str) -> None:
    # CI 的 OCR 步骤设置 SUIYI_OCR_TESTS_REQUIRED=1：缺依赖或模型时直接失败，不静默跳过
    if os.environ.get("SUIYI_OCR_TESTS_REQUIRED") == "1":
        pytest.fail(reason)
    pytest.skip(reason)


@pytest.fixture(scope="module")
def engine():
    try:
        import rapidocr  # noqa: F401
    except ImportError:
        _unavailable('未安装 OCR 依赖：pip install -e "engine[ocr]"')
    from suiyi_engine.ocr import OcrEngine, OcrModelsMissingError
    from suiyi_engine.tools.ocr_models import forbid_network

    engine = OcrEngine(Path(os.environ["SUIYI_MODELS_DIR"]))
    try:
        engine.check()
    except OcrModelsMissingError as exc:
        _unavailable(f"OCR 模型不全：{exc}")
    with forbid_network():  # 加载与首次识别都不能联网
        engine.warmup()
    return engine


@pytest.mark.parametrize("name", ["zh_web_01", "en_doc_01", "ja_web_01"])
def test_horizontal_samples(engine, name: str) -> None:
    from suiyi_engine.tools.ocr_models import forbid_network

    with forbid_network():
        result = engine.recognize((FIXTURES / f"{name}.png").read_bytes())
    expected = expected_paragraphs(name)
    assert similarity("".join(expected), result.text) >= MIN_SIMILARITY, result.text
    assert len(result.paragraphs) == len(expected), [p.text for p in result.paragraphs]
    for want, got in zip(expected, result.paragraphs, strict=True):
        assert similarity(want, got.text) >= MIN_SIMILARITY, (want, got.text)
        assert not got.vertical
    assert result.low_confidence_count == 0


@pytest.mark.parametrize("name", ["ja_vertical_01", "ja_vertical_03"])
def test_vertical_japanese_reads_right_to_left(engine, name: str) -> None:
    result = engine.recognize(FIXTURES / f"{name}.png")
    expected = expected_paragraphs(name)
    assert len(result.paragraphs) == len(expected), [p.text for p in result.paragraphs]
    for want, got in zip(expected, result.paragraphs, strict=True):
        assert got.vertical
        assert similarity(want, got.text) >= MIN_SIMILARITY, (want, got.text)
    # 段内列从右到左
    for para in result.paragraphs:
        rights = [result.lines[i].rect[2] for i in para.line_indices]
        for previous, current in zip(rights, rights[1:], strict=False):
            assert current <= previous + 5, rights  # 同一列被切开的碎片右缘相近


def test_model_is_reused_and_concurrent_calls_agree(engine) -> None:
    images = [(FIXTURES / f"{n}.png").read_bytes() for n in ("zh_web_01", "ja_vertical_01")]
    baseline = [engine.recognize(img).text for img in images]
    backend = engine.load()
    outputs: list[list[str]] = []
    errors: list[BaseException] = []

    def worker() -> None:
        try:
            outputs.append([engine.recognize(img).text for img in images])
        except BaseException as exc:  # noqa: BLE001
            errors.append(exc)

    threads = [threading.Thread(target=worker) for _ in range(4)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert errors == []
    assert outputs == [baseline] * 4
    assert engine.load() is backend
