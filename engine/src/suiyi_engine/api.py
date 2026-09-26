"""本机 HTTP API。

``create_app(translator, detector, settings)`` 组装 FastAPI 应用，测试可以注入
假的翻译器和检测器。默认不开启 CORS，也不暴露 ``/docs``。
"""

from __future__ import annotations

import logging
import threading
import time
from collections.abc import AsyncIterator, Callable, Sequence
from contextlib import asynccontextmanager
from dataclasses import dataclass
from typing import TYPE_CHECKING, Protocol

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, StrictBool

from suiyi_engine import __version__
from suiyi_engine.errors import UnsupportedPairError
from suiyi_engine.registry import normalize_lang
from suiyi_engine.translator import TranslationResult

if TYPE_CHECKING:
    from suiyi_engine.api_ocr import OcrProvider

logger = logging.getLogger(__name__)

Detector = Callable[[str], object]

DEFAULT_MAX_IMAGE_BYTES = 8 * 1024 * 1024
DEFAULT_MAX_IMAGE_PIXELS = 4096 * 4096


class SupportsTranslation(Protocol):
    """HTTP 层用到的翻译器方法。真实实现是 :class:`Translator`。"""

    registry: object

    def translate(self, text: str, src: str, tgt: str) -> TranslationResult: ...

    def translate_many(
        self, texts: Sequence[str], src: str, tgt: str
    ) -> list[TranslationResult]: ...

    def available_pairs(self) -> Sequence[tuple[str, str, str]]: ...

    def loaded_model_ids(self) -> Sequence[str]: ...


@dataclass(frozen=True, slots=True)
class ApiSettings:
    """与监听地址无关的接口设置。

    ``max_text_chars`` 是单条 ``text``（以及 ``texts`` 里每一条）的字符上限。
    ``max_image_bytes`` / ``max_image_pixels`` 是 OCR 请求体的字节上限与总像素上限。
    ``dev`` 为真时才挂载 ``/docs`` 和 ``/openapi.json``。
    """

    max_text_chars: int = 10_000
    dev: bool = False
    max_image_bytes: int = DEFAULT_MAX_IMAGE_BYTES
    max_image_pixels: int = DEFAULT_MAX_IMAGE_PIXELS

    def __post_init__(self) -> None:
        for name in ("max_text_chars", "max_image_bytes", "max_image_pixels"):
            value = getattr(self, name)
            if isinstance(value, bool) or not isinstance(value, int):
                raise ValueError(f"{name} 必须是整数")
            if value < 1:
                raise ValueError(f"{name} 必须 >= 1")


class TranslateRequest(BaseModel):
    """翻译请求。未知字段忽略，以便以后增加可选项。

    ``glossary``：本次是否做术语保护（#83）。省略或 ``null`` 时用服务端默认（``--glossary`` /
    ``SUIYI_GLOSSARY``）；必须是 JSON 布尔值，其他类型返回 422。
    """

    model_config = ConfigDict(extra="ignore")

    text: str | None = None
    texts: list[str] | None = None
    source: str
    target: str
    glossary: StrictBool | None = None


class ApiError(Exception):
    """调用方可以看见的接口错误。"""

    def __init__(
        self,
        status_code: int,
        code: str,
        message: str,
        details: dict[str, object] | None = None,
    ) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.payload = error_payload(code, message, details)


def error_payload(
    code: str,
    message: str,
    details: dict[str, object] | None = None,
) -> dict[str, object]:
    """统一错误信封。``details`` 始终是对象。"""

    return {
        "error": {
            "code": code,
            "message": message,
            "details": {} if details is None else details,
        }
    }


def create_app(
    translator: SupportsTranslation,
    detector: Detector | None = None,
    settings: ApiSettings | None = None,
    ocr: OcrProvider | None = None,
) -> FastAPI:
    """组装应用。``detector`` 为空时使用 :func:`suiyi_engine.langdetect.detect`。

    ``ocr`` 为空时按翻译模型目录懒建一个 :class:`OcrProvider`：OCR 依赖或模型缺失不影响启动，
    只让 OCR 接口返回 503。
    """

    from suiyi_engine.api_ocr import OcrProvider, register_ocr_routes

    if detector is None:
        from suiyi_engine.langdetect import detect

        detector = detect
    if settings is None:
        settings = ApiSettings()

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        app.state.started_at = time.perf_counter()
        yield

    app = FastAPI(
        title="随译本机翻译服务",
        version=__version__,
        docs_url="/docs" if settings.dev else None,
        redoc_url=None,
        openapi_url="/openapi.json" if settings.dev else None,
        redirect_slashes=False,
        lifespan=lifespan,
    )
    app.state.translator = translator
    app.state.detector = detector
    app.state.settings = settings
    app.state.ocr = ocr if ocr is not None else OcrProvider(translator.registry.models_dir)
    app.state.started_at = time.perf_counter()
    # 翻译在线程池里串行，避免并行解码抢 CPU。健康检查不拿这把锁。
    app.state.translate_lock = threading.Lock()

    @app.exception_handler(RequestValidationError)
    async def invalid_body(_request: Request, exc: RequestValidationError) -> JSONResponse:
        errors: list[dict[str, object]] = []
        for item in exc.errors():
            loc = item.get("loc", ())
            errors.append(
                {
                    "loc": [str(part) for part in loc],
                    "msg": str(item.get("msg", "")),
                    "type": str(item.get("type", "")),
                }
            )
        return JSONResponse(
            status_code=422,
            content=error_payload("invalid_request", "请求无效", {"errors": errors}),
        )

    @app.get("/health")
    async def health() -> JSONResponse:
        try:
            current = app.state.translator
            uptime = time.perf_counter() - app.state.started_at
            payload = {
                "status": "ok",
                "version": __version__,
                "models_dir": str(current.registry.models_dir),
                "loaded_models": list(current.loaded_model_ids()),
                "uptime_s": round(float(uptime), 1),
                "ocr_loaded": bool(app.state.ocr.loaded),
                "ocr_error": app.state.ocr.health(),
                **glossary_status(current),
            }
        except Exception:
            logger.exception("读取健康状态失败")
            return _internal_error()
        return JSONResponse(status_code=200, content=payload)

    @app.get("/languages")
    async def languages() -> JSONResponse:
        try:
            payload = language_catalog(app.state.translator)
        except Exception:
            logger.exception("读取可用语向失败")
            return _internal_error()
        return JSONResponse(status_code=200, content=payload)

    @app.post("/translate")
    def translate(body: TranslateRequest) -> JSONResponse:
        # 同步路由运行在线程池中，不占用事件循环。
        with app.state.translate_lock:
            try:
                payload = perform_translate(
                    body,
                    app.state.translator,
                    app.state.detector,
                    app.state.settings,
                )
            except ApiError as exc:
                return JSONResponse(status_code=exc.status_code, content=exc.payload)
            except UnsupportedPairError as exc:
                return JSONResponse(status_code=422, content=_unsupported_payload(exc, None))
            except Exception:
                logger.exception("翻译请求失败")
                return _internal_error()
        return JSONResponse(status_code=200, content=payload)

    @app.post("/glossary/reload")
    def reload_glossary() -> JSONResponse:
        # 同步路由在线程池中运行；读文件不拿翻译锁，术语表自己有锁。
        try:
            reload = getattr(app.state.translator, "reload_glossary", None)
            payload = reload() if callable(reload) else glossary_status(app.state.translator)
        except Exception:
            logger.exception("重读术语表失败")
            return _internal_error()
        return JSONResponse(status_code=200, content=payload)

    register_ocr_routes(app)
    return app


_GLOSSARY_OFF: dict[str, object] = {
    "glossary_enabled": False,
    "glossary_builtin_entries": 0,
    "glossary_user_path": None,
    "glossary_user_entries": 0,
    "glossary_error": None,
    "glossary_warnings": [],
}


def glossary_status(translator: object) -> dict[str, object]:
    """``/health`` 与 ``/glossary/reload`` 的 ``glossary_*`` 字段；翻译器没有术语表时报告关闭。"""

    status = getattr(translator, "glossary_status", None)
    if not callable(status):
        return dict(_GLOSSARY_OFF)
    return {**_GLOSSARY_OFF, **status()}


def language_catalog(translator: SupportsTranslation) -> dict[str, object]:
    """已安装模型实际能走的语向，含英文中转。"""

    languages: set[str] = set()
    pairs: list[dict[str, object]] = []
    for src, tgt, kind in translator.available_pairs():
        records = translator.registry.resolve(src, tgt)
        models = [record.id for record in records]
        languages.add(src)
        languages.add(tgt)
        pairs.append({"src": src, "tgt": tgt, "route": kind, "models": models})
    return {"languages": sorted(languages), "pairs": pairs}


def perform_translate(
    body: TranslateRequest,
    translator: SupportsTranslation,
    detector: Detector,
    settings: ApiSettings,
) -> dict[str, object]:
    """校验请求并翻译。成功时返回可直接序列化的对象。"""

    texts, batched = _take_texts(body)
    for index, text in enumerate(texts):
        _check_length(text, index if batched else None, settings.max_text_chars)
    target = _normalize_target(body.target)
    auto = _is_auto(body.source)
    if auto:
        sources = [_detect_source(detector, text, index) for index, text in enumerate(texts)]
    else:
        sources = [_normalize_source(body.source)] * len(texts)
    for index, (text, src) in enumerate(zip(texts, sources, strict=True)):
        _preflight(translator, src, target, text, index if batched else None)
    # 只在请求带了 glossary 时才传，测试里的假翻译器和旧实现不必认识这个参数。
    extra: dict[str, object] = {} if body.glossary is None else {"glossary": body.glossary}
    if not auto and batched:
        translated = translator.translate_many(texts, sources[0], target, **extra)
        return {"results": [_public_result(item, detected=False) for item in translated]}
    if not auto:
        result = translator.translate(texts[0], sources[0], target, **extra)
        return _public_result(result, detected=False)
    results = [
        _public_result(translator.translate(text, src, target, **extra), detected=True)
        for text, src in zip(texts, sources, strict=True)
    ]
    if batched:
        return {"results": results}
    return results[0]


def _take_texts(body: TranslateRequest) -> tuple[list[str], bool]:
    has_text = body.text is not None
    has_texts = body.texts is not None
    if has_text == has_texts:
        raise ApiError(
            422,
            "invalid_request",
            "text 与 texts 必须提供且只能提供一个",
        )
    if body.texts is not None:
        if len(body.texts) == 0:
            raise ApiError(422, "invalid_request", "texts 不能为空")
        return list(body.texts), True
    text = body.text
    if text is None:
        raise ApiError(422, "invalid_request", "text 与 texts 必须提供且只能提供一个")
    return [text], False


def _check_length(text: str, index: int | None, limit: int) -> None:
    length = len(text)
    if length <= limit:
        return
    details: dict[str, object] = {"limit": limit, "length": length}
    if index is not None:
        details["index"] = index
    raise ApiError(
        413,
        "text_too_long",
        f"文本长度为 {length}，超过上限 {limit}",
        details,
    )


def _is_auto(source: str) -> bool:
    return source.strip().lower() == "auto"


def _normalize_target(target: str) -> str:
    try:
        return normalize_lang(target)
    except ValueError as exc:
        raise ApiError(422, "invalid_request", str(exc), {"field": "target"}) from exc


def _normalize_source(source: str) -> str:
    try:
        return normalize_lang(source)
    except ValueError as exc:
        raise ApiError(422, "invalid_request", str(exc), {"field": "source"}) from exc


def _detect_source(detector: Detector, text: str, index: int) -> str:
    found = detector(text)
    lang = getattr(found, "lang", None)
    if not isinstance(lang, str) or lang == "und":
        shown = lang if isinstance(lang, str) else None
        raise ApiError(
            422,
            "detect_failed",
            "无法识别文本语种",
            {"index": index, "detected": shown},
        )
    try:
        return normalize_lang(lang)
    except ValueError as exc:
        raise ApiError(
            422,
            "detect_failed",
            "无法识别文本语种",
            {"index": index, "detected": lang},
        ) from exc


def _preflight(
    translator: SupportsTranslation,
    src: str,
    tgt: str,
    text: str,
    index: int | None,
) -> None:
    if src == tgt or text.strip() == "":
        return
    try:
        translator.registry.resolve(src, tgt)
    except UnsupportedPairError as exc:
        raise ApiError(422, "unsupported_pair", str(exc), _unsupported_details(exc, index)) from exc


def _unsupported_payload(exc: UnsupportedPairError, index: int | None) -> dict[str, object]:
    return error_payload("unsupported_pair", str(exc), _unsupported_details(exc, index))


def _unsupported_details(exc: UnsupportedPairError, index: int | None) -> dict[str, object]:
    details: dict[str, object] = {
        "source": exc.src,
        "target": exc.tgt,
        "missing_models": list(exc.missing_ids),
    }
    if index is not None:
        details["index"] = index
    return details


def _public_result(result: TranslationResult, *, detected: bool) -> dict[str, object]:
    return {
        "text": result.text,
        "source": result.src,
        "detected": detected,
        "target": result.tgt,
        "route": list(result.route),
        "elapsed_ms": round(float(result.elapsed_ms), 1),
    }


def _internal_error() -> JSONResponse:
    return JSONResponse(
        status_code=500,
        content=error_payload("internal_error", "翻译服务内部错误"),
    )
