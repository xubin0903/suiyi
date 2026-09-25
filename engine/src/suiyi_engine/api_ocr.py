"""OCR 接口：``POST /ocr`` 与 ``POST /ocr_translate``（#53）。

请求体是原始 PNG 字节（不用 multipart，避免 ``python-multipart`` 依赖）。按顺序检查：
字节上限（先看 ``Content-Length``，超限不读请求体）→ PNG 魔数 → IHDR 宽高与像素上限 →
OCR 是否可用 → 解码与识别。OCR 在线程池里执行，有自己的锁（见 :class:`OcrEngine`），
与翻译锁互不影响，``/health`` 两把锁都不拿。

日志只记录尺寸、字节数、行数、段数和耗时，不记录图片与识别文本。
"""

from __future__ import annotations

import logging
import struct
import threading
import time
from collections import Counter
from collections.abc import Callable
from pathlib import Path
from typing import TYPE_CHECKING

from fastapi import FastAPI, Query, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from suiyi_engine.api import (
    ApiError,
    ApiSettings,
    Detector,
    SupportsTranslation,
    _check_length,
    _internal_error,
    _normalize_source,
    _normalize_target,
    _preflight,
    _public_result,
    _unsupported_payload,
)
from suiyi_engine.errors import UnsupportedPairError
from suiyi_engine.ocr import OCR_LANGS  # 导入 suiyi_engine.ocr 不加载 rapidocr / numpy
from suiyi_engine.registry import normalize_lang

if TYPE_CHECKING:
    from suiyi_engine.ocr import OcrEngine, OcrResult

logger = logging.getLogger(__name__)

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
INSTALL_HINT = '请安装 OCR 依赖（pip install -e "engine[ocr]"）'
DOWNLOAD_HINT = "请执行 python scripts/download_ocr_models.py download 下载 OCR 模型"


class OcrUnavailable(Exception):
    """OCR 依赖未安装、清单缺失或模型缺失/损坏。对应 503 ``ocr_unavailable``。"""

    def __init__(self, message: str, *, reason: str, missing_models: tuple[str, ...] = ()) -> None:
        super().__init__(message)
        self.reason = reason
        self.missing_models = missing_models

    def details(self) -> dict[str, object]:
        return {"reason": self.reason, "missing_models": list(self.missing_models)}


class OcrProvider:
    """懒加载 :class:`OcrEngine`。失败不缓存：补齐模型后下一次请求就能用，不用重启服务。"""

    def __init__(
        self,
        models_dir: Path | str,
        *,
        engine_factory: Callable[[], OcrEngine] | None = None,
    ) -> None:
        self.models_dir = Path(models_dir)
        self._factory = engine_factory
        self._engine: OcrEngine | None = None
        self._lock = threading.Lock()
        self.last_error: OcrUnavailable | None = None
        """最近一次加载失败的原因（``/health.ocr_error``）；加载成功后清空。"""

    @property
    def loaded(self) -> bool:
        engine = self._engine
        return engine is not None and engine.loaded

    def engine(self) -> OcrEngine:
        """返回已加载模型的引擎；不可用时抛 :class:`OcrUnavailable` 并记到 :attr:`last_error`。"""

        try:
            engine = self._engine_or_raise()
        except OcrUnavailable as exc:
            self.last_error = exc
            raise
        self.last_error = None
        return engine

    def health(self) -> dict[str, object] | None:
        """``/health.ocr_error``：最近一次加载失败的原因，没有失败（或尚未尝试）时为 ``None``。"""

        error = self.last_error
        if error is None:
            return None
        return {"message": str(error), **error.details()}

    def _engine_or_raise(self) -> OcrEngine:
        from suiyi_engine.ocr import OcrEngine, OcrError, OcrModelError, OcrModelsMissingError

        with self._lock:
            if self._engine is None:
                try:
                    if self._factory is not None:
                        self._engine = self._factory()
                    else:
                        self._engine = OcrEngine(self.models_dir)
                except OcrModelError as exc:
                    raise OcrUnavailable(
                        f"OCR 模型清单不可用：{exc}", reason="manifest_unavailable"
                    ) from exc
            engine = self._engine
        try:
            engine.load()
        except OcrModelsMissingError as exc:
            raise OcrUnavailable(
                f"缺少 OCR 模型：{'、'.join(exc.missing)}（目录 {exc.ocr_dir}）。{DOWNLOAD_HINT}",
                reason="models_missing",
                missing_models=exc.missing,
            ) from exc
        except OcrModelError as exc:
            raise OcrUnavailable(f"OCR 模型不可用：{exc}", reason="models_invalid") from exc
        except OcrError as exc:
            raise OcrUnavailable(
                f"OCR 依赖未安装：{exc}。{INSTALL_HINT}", reason="dependency_missing"
            ) from exc
        return engine

    def warmup(self) -> float:
        """加载模型并预热，返回耗时毫秒；不可用时抛 :class:`OcrUnavailable`。"""

        started = time.perf_counter()
        self.engine().warmup()
        return 1000 * (time.perf_counter() - started)


def register_ocr_routes(app: FastAPI) -> None:
    """在 :func:`suiyi_engine.api.create_app` 组装的应用上挂 OCR 路由。"""

    @app.post("/ocr")
    async def ocr(request: Request, lang: str = Query("auto")) -> JSONResponse:
        started = time.perf_counter()
        try:
            lang_code = _ocr_lang(lang)
            data = await read_png_body(request, app.state.settings)
            result = await _recognize(app, data, lang_code)
        except ApiError as exc:
            return JSONResponse(status_code=exc.status_code, content=exc.payload)
        except Exception:
            logger.exception("OCR 请求失败")
            return _internal_error()
        payload = _ocr_payload(result)
        payload["elapsed_ms"] = _ms(time.perf_counter() - started)
        _log("ocr", data, result, payload["elapsed_ms"], None)
        return JSONResponse(status_code=200, content=payload)

    @app.post("/ocr_translate")
    async def ocr_translate(
        request: Request,
        target: str = Query(...),
        source: str = Query("auto"),
        fallback_target: str | None = Query(None),
    ) -> JSONResponse:
        started = time.perf_counter()
        try:
            target_code = _normalize_target(target)
            fallback_code = _normalize_fallback(fallback_target)
            source_code = None if source.strip().lower() == "auto" else _normalize_source(source)
            data = await read_png_body(request, app.state.settings)
            result = await _recognize(app, data, "auto")
            ocr_done = time.perf_counter()
            texts = [p.text for p in result.paragraphs]
            results = await run_in_threadpool(
                _translate_locked, app, texts, source_code, target_code, fallback_code
            )
        except ApiError as exc:
            return JSONResponse(status_code=exc.status_code, content=exc.payload)
        except UnsupportedPairError as exc:
            return JSONResponse(status_code=422, content=_unsupported_payload(exc, None))
        except Exception:
            logger.exception("OCR 翻译请求失败")
            return _internal_error()
        done = time.perf_counter()
        payload = _ocr_payload(result)
        payload["translation"] = {"results": results}
        payload["elapsed_ms"] = {
            "ocr": _ms(ocr_done - started),
            "translate": _ms(done - ocr_done),
            "total": _ms(done - started),
        }
        _log("ocr_translate", data, result, payload["elapsed_ms"]["total"], results)
        return JSONResponse(status_code=200, content=payload)


async def read_png_body(request: Request, settings: ApiSettings) -> bytes:
    """读请求体并做字节上限、PNG 魔数、IHDR 像素上限检查。

    ``Content-Length`` 超限时直接 413，不读请求体；没有 ``Content-Length``（分块上传）时
    边读边计数，超过上限立即停止。不看 ``Content-Type``，只按魔数判断。
    """

    limit = settings.max_image_bytes
    declared = request.headers.get("content-length")
    if declared is not None:
        try:
            length = int(declared)
        except ValueError as exc:
            raise ApiError(400, "invalid_request", "Content-Length 不是整数") from exc
        if length > limit:
            raise _too_large("bytes", limit, length)
    buf = bytearray()
    async for chunk in request.stream():
        buf += chunk
        if len(buf) > limit:
            raise _too_large("bytes", limit, len(buf))
    data = bytes(buf)
    if not data.startswith(PNG_SIGNATURE):
        what = "请求体为空" if not data else "请求体不是 PNG（按文件头判断）"
        raise ApiError(415, "unsupported_media_type", f"{what}，只接受 image/png")
    width, height = png_size(data)
    pixels = width * height
    if pixels > settings.max_image_pixels:
        raise _too_large(
            "pixels", settings.max_image_pixels, pixels, {"width": width, "height": height}
        )
    return data


def png_size(data: bytes) -> tuple[int, int]:
    """从 IHDR 读宽高（不解码像素）。头部不完整或尺寸为 0 时 422 ``invalid_image``。"""

    if len(data) < 24 or data[12:16] != b"IHDR":
        raise ApiError(422, "invalid_image", "PNG 文件头不完整，无法读取尺寸")
    width, height = struct.unpack(">II", data[16:24])
    if width == 0 or height == 0:
        raise ApiError(422, "invalid_image", f"PNG 尺寸无效：{width}x{height}")
    return width, height


def _too_large(
    kind: str, limit: int, actual: int, extra: dict[str, object] | None = None
) -> ApiError:
    unit = "字节" if kind == "bytes" else "像素"
    details: dict[str, object] = {"kind": kind, "limit": limit, "actual": actual, **(extra or {})}
    return ApiError(
        413, "image_too_large", f"图片 {actual} {unit}，超过上限 {limit} {unit}", details
    )


async def _recognize(app: FastAPI, data: bytes, lang: str) -> OcrResult:
    provider: OcrProvider = app.state.ocr
    try:
        engine = await run_in_threadpool(provider.engine)
    except OcrUnavailable as exc:
        raise ApiError(503, "ocr_unavailable", str(exc), exc.details()) from exc

    from suiyi_engine.ocr import InvalidImageError
    from suiyi_engine.ocr.engine import ImageTooLargeError

    try:  # 像素上限已按 IHDR 检查过；这里的 ImageTooLargeError 只是兜底
        return await run_in_threadpool(engine.recognize, data, lang=lang)
    except ImageTooLargeError as exc:
        raise ApiError(413, "image_too_large", str(exc), {"kind": "pixels"}) from exc
    except InvalidImageError as exc:
        raise ApiError(422, "invalid_image", f"PNG 无法解码：{exc}") from exc


def _translate_locked(
    app: FastAPI,
    texts: list[str],
    source: str | None,
    target: str,
    fallback: str | None,
) -> list[dict[str, object]]:
    with app.state.translate_lock:
        return translate_paragraphs(
            texts,
            source=source,
            target=target,
            fallback=fallback,
            translator=app.state.translator,
            detector=app.state.detector,
            settings=app.state.settings,
        )


def translate_paragraphs(
    texts: list[str],
    *,
    source: str | None,
    target: str,
    fallback: str | None,
    translator: SupportsTranslation,
    detector: Detector,
    settings: ApiSettings,
) -> list[dict[str, object]]:
    """按段落翻译，结果与 ``texts`` 一一对应，形状同 ``/translate`` 批量结果的 ``results``。

    - ``source=None``（auto）：每段各自检测；检测不出（``und``，例如只有数字或符号）的段落
      用整张图的语种。整张图的语种先对全文检测，检测不出时取各段检测结果里最多的。
      一段都检测不出时 422 ``detect_failed``。
    - 次目标：整张图的语种（或显式 ``source``）等于 ``target`` 且给了 ``fallback`` 时，
      所有段落都改译为 ``fallback``。按整张图而不是按段落决定，浮窗里的译文语种一致。
    - 某段语向没有模型时整个请求 422 ``unsupported_pair``（``details.index`` 是段落序号）。
    """

    if not texts:
        return []
    for index, text in enumerate(texts):
        _check_length(text, index, settings.max_text_chars)
    auto = source is None
    if auto:
        per_paragraph = [_try_detect(detector, text) for text in texts]
        overall = _try_detect(detector, "\n".join(texts))
        if overall is None:
            found = [lang for lang in per_paragraph if lang is not None]
            overall = Counter(found).most_common(1)[0][0] if found else None
        if overall is None:
            raise ApiError(422, "detect_failed", "无法识别文本语种", {"index": 0, "detected": None})
        sources = [lang or overall for lang in per_paragraph]
    else:
        overall = source
        sources = [source] * len(texts)
    effective = fallback if fallback is not None and overall == target else target
    for index, (text, src) in enumerate(zip(texts, sources, strict=True)):
        _preflight(translator, src, effective, text, index)
    return [
        _public_result(translator.translate(text, src, effective), detected=auto)
        for text, src in zip(texts, sources, strict=True)
    ]


def _try_detect(detector: Detector, text: str) -> str | None:
    lang = getattr(detector(text), "lang", None)
    if not isinstance(lang, str) or lang == "und":
        return None
    try:
        return normalize_lang(lang)
    except ValueError:
        return None


def _ocr_lang(lang: str) -> str:
    cleaned = lang.strip().lower()
    if cleaned not in OCR_LANGS:
        raise ApiError(
            422,
            "invalid_request",
            f"lang 只接受 {', '.join(OCR_LANGS)}，收到 {lang!r}",
            {"field": "lang"},
        )
    return cleaned


def _normalize_fallback(value: str | None) -> str | None:
    if value is None or not value.strip():
        return None
    try:
        return normalize_lang(value)
    except ValueError as exc:
        raise ApiError(422, "invalid_request", str(exc), {"field": "fallback_target"}) from exc


def _ocr_payload(result: OcrResult) -> dict[str, object]:
    payload = result.to_dict()
    payload.pop("elapsed_ms", None)
    return payload


def _ms(seconds: float) -> float:
    return round(1000 * seconds, 1)


def _log(
    route: str,
    data: bytes,
    result: OcrResult,
    total_ms: float,
    results: list[dict[str, object]] | None,
) -> None:
    targets = sorted({str(r["target"]) for r in results}) if results else []
    logger.info(
        "%s size=%dx%d bytes=%d lines=%d low_confidence=%d paragraphs=%d targets=%s total=%.0fms",
        route,
        result.width,
        result.height,
        len(data),
        len(result.lines),
        result.low_confidence_count,
        len(result.paragraphs),
        ",".join(targets) or "-",
        total_ms,
    )
