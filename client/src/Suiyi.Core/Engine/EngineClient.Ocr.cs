using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Suiyi.Core.Logging;
using Suiyi.Core.Ocr;

namespace Suiyi.Core.Engine;

// ⚠ 按 Issue #53 的接口草案实现（docs/engine/HTTP-API.md 的 OCR 部分尚未定稿合入）。
// EngineClient 里依赖草案的请求构造、错误码映射、客户端预检都在本文件；字段名、路径、上限常量在 Ocr/OcrDraftContract.cs。
// 定稿后同步这两处与 EngineClientOcrTests。

/// <summary><see cref="EngineClient"/> 的 OCR 部分（<c>POST /ocr</c>、<c>POST /ocr_translate</c>）。</summary>
public sealed partial class EngineClient
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 客户端所知的 OCR 模型加载状态：最近一次 <c>/health</c> 的 <c>ocr_loaded</c>，之后成功识别过则为 <see langword="true"/>。
    /// 未知（未取过 <c>/health</c>、旧引擎没有该字段、或已 <see cref="Invalidate"/>）时为 <see langword="null"/>。
    /// </summary>
    public bool? KnownOcrLoaded
    {
        get
        {
            lock (_gate)
            {
                return _ocrLoaded;
            }
        }
    }

    /// <summary>
    /// 客户端所知的 OCR 不可用原因：最近一次 <c>/health</c> 的 <c>ocr_error</c>；之后成功识别过、或 <see cref="Invalidate"/> 后为 <see langword="null"/>。
    /// 开启 <c>--preload-ocr</c>（设置 <c>engine.preloadOcr</c>）时，服务启动就会尝试加载，缺模型在第一次框选前就能从这里知道。
    /// </summary>
    public OcrHealthError? KnownOcrError
    {
        get
        {
            lock (_gate)
            {
                return _ocrError;
            }
        }
    }

    /// <summary>计算本次 <c>/ocr_translate</c> 将使用的超时（<see cref="TimeoutPolicy.ComputeOcrTranslateMilliseconds"/>），基于当前缓存，不发请求。</summary>
    /// <param name="source">原文语种或 <c>"auto"</c>。</param>
    /// <param name="target">目标语种。</param>
    /// <param name="fallbackTarget">次目标；没有时为 <see langword="null"/>。</param>
    /// <remarks>#94：与 <see cref="GetTimeout"/> 一样，可能已被空闲卸载的翻译模型按未加载处理（OCR 模型服务端不卸载）。</remarks>
    public TimeSpan GetOcrTranslateTimeout(string source, string target, string? fallbackTarget) =>
        TimeSpan.FromMilliseconds(ComputeOcrTranslateTimeout(source, target, fallbackTarget).Effective);

    private (int Effective, int Baseline, LanguagesResponse? Languages, double? Idle) ComputeOcrTranslateTimeout(
        string source,
        string target,
        string? fallbackTarget)
    {
        LanguagesResponse? languages;
        IReadOnlyCollection<string>? loaded;
        bool? ocrLoaded;
        double? idle;
        lock (_gate)
        {
            languages = _languages;
            loaded = _loadedModels is null ? null : [.. _loadedModels];
            ocrLoaded = _ocrLoaded;
            idle = _modelIdleUnloadSeconds;
        }

        var baseline = TimeoutPolicy.ComputeOcrTranslateMilliseconds(source, target, fallbackTarget, ocrLoaded, languages, loaded);
        var effective = TimeoutPolicy.ComputeOcrTranslateMilliseconds(
            source, target, fallbackTarget, ocrLoaded, languages, _usage.FilterWarm(loaded, idle));
        return (effective, baseline, languages, idle);
    }

    /// <summary>
    /// 调用 <c>POST /ocr_translate</c>：上传原始 PNG，一次往返拿到识别结果与按段落对应的译文。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>请求体为原始 PNG 字节，<c>Content-Type: image/png</c>；查询参数 <c>source</c>、<c>target</c>、
    /// 可选 <c>fallback_target</c>（为空或与 <c>target</c> 相同则不发）。</item>
    /// <item>先做客户端预检（<see cref="PrecheckImage"/>），超限直接抛 <see cref="EngineErrorKind.ImageTooLarge"/>，不发请求。</item>
    /// <item>超时见 <see cref="TimeoutPolicy.ComputeOcrTranslateMilliseconds"/>（可能已被空闲卸载的翻译模型按未加载处理，#94）；缓存未知时先各取一次 <c>/languages</c>、<c>/health</c>（失败忽略）。</item>
    /// <item>超时后按 <see cref="TimeoutPolicy.OcrColdMs"/> 自动重试一次（<see cref="TimeoutRetry"/>，#94），调用方取消不重试。</item>
    /// <item>成功后把 OCR 记为已加载、把各段 <c>route</c> 里的模型记为已加载。识别为空是正常结果（200，<c>paragraphs: []</c>）。</item>
    /// </list>
    /// </remarks>
    /// <param name="png">PNG 字节（框选截屏的编码结果）。</param>
    /// <param name="source">原文语种（ISO 639-1）或 <c>"auto"</c>。</param>
    /// <param name="target">目标语种，不能是 <c>"auto"</c>。</param>
    /// <param name="fallbackTarget">次目标：检测到的原文语种等于 <paramref name="target"/> 时改译为它；没有时为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">调用方取消令牌；取消时抛 <see cref="OperationCanceledException"/>。</param>
    /// <exception cref="EngineException">失败，见 <see cref="EngineException.Kind"/>。</exception>
    public async Task<OcrTranslateResponse> OcrTranslateAsync(
        ReadOnlyMemory<byte> png,
        string source,
        string target,
        string? fallbackTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ThrowIfPrecheckFails(png);

        await WarmCachesAsync(cancellationToken).ConfigureAwait(false);
        var (timeoutMs, baselineMs, languages, idle) = ComputeOcrTranslateTimeout(source, target, fallbackTarget);
        var direction = TimeoutPolicy.Normalize(source) + "→" + TimeoutPolicy.Normalize(target);
        if (timeoutMs != baselineMs && languages is not null)
        {
            var models = TimeoutPolicy.CandidateRoutes(source, target, languages)
                .Concat(string.IsNullOrWhiteSpace(fallbackTarget) ? [] : TimeoutPolicy.CandidateRoutes(target, fallbackTarget, languages))
                .SelectMany(pair => pair.Models);
            Logger?.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"框选翻译：{direction} 的模型可能已被空闲卸载（model_idle_unload_s={idle}；{_usage.Describe(models, idle)}），按冷启动超时 {timeoutMs} ms（原 {baselineMs} ms）"));
        }

        var pathAndQuery = BuildOcrTranslatePath(source, target, fallbackTarget, GlossaryOverride?.Invoke());
        var response = await TimeoutRetry.ExecuteAsync(
            (timeout, ct) => SendAsync<OcrTranslateResponse>(HttpMethod.Post, pathAndQuery, PngContent(png), timeout, ct),
            TimeSpan.FromMilliseconds(timeoutMs),
            TimeSpan.FromMilliseconds(TimeoutPolicy.OcrColdMs),
            (ex, retry) => LogTimeoutRetry("/ocr_translate", direction, ex, retry),
            cancellationToken).ConfigureAwait(false);
        var used = new List<string>();
        lock (_gate)
        {
            _ocrLoaded = true;
            _ocrError = null; // 与服务端一致：加载成功后清空。
            foreach (var result in response.Translation?.Results ?? [])
            {
                if (result?.Route is { Count: > 0 } route)
                {
                    _loadedModels?.UnionWith(route);
                    used.AddRange(route);
                }
            }
        }

        _usage.MarkUsed(used);
        return response;
    }

    /// <summary>调用 <c>POST /ocr</c>：只识别不翻译。预检、取消、错误映射同 <see cref="OcrTranslateAsync"/>。</summary>
    /// <param name="png">PNG 字节。</param>
    /// <param name="lang">识别语种：<c>"auto"</c>（默认），或草案允许的 <c>zh</c>/<c>en</c>/<c>ja</c>。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <exception cref="EngineException">失败，见 <see cref="EngineException.Kind"/>。</exception>
    public async Task<OcrResponse> OcrAsync(
        ReadOnlyMemory<byte> png,
        string lang = AutoSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lang);
        ThrowIfPrecheckFails(png);

        var timeout = TimeSpan.FromMilliseconds(TimeoutPolicy.ComputeOcrMilliseconds(KnownOcrLoaded));
        var pathAndQuery = OcrDraftContract.OcrPath + "?" + Query((OcrDraftContract.LangParameter, lang));
        var response = await SendAsync<OcrResponse>(HttpMethod.Post, pathAndQuery, PngContent(png), timeout, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _ocrLoaded = true;
            _ocrError = null; // 与服务端一致：加载成功后清空。
        }

        return response;
    }

    /// <summary>
    /// 客户端预检：超过 <see cref="OcrDraftContract.MaxImageBytes"/> 字节，或 PNG 头里的宽×高超过
    /// <see cref="OcrDraftContract.MaxImagePixels"/> 时，返回 <see cref="EngineErrorKind.ImageTooLarge"/>
    /// （<see cref="EngineException.IsClientPrecheck"/> 为 <see langword="true"/>，<c>Details</c> 与服务端 413 同形：<c>limit</c>、<c>actual</c>）；
    /// 通过时返回 <see langword="null"/>。读不出 PNG 头时不判像素，交给服务端（415 / 422）。
    /// </summary>
    /// <param name="png">PNG 字节。</param>
    public static EngineException? PrecheckImage(ReadOnlySpan<byte> png)
    {
        if (png.Length > OcrDraftContract.MaxImageBytes)
        {
            return TooLarge("字节", OcrDraftContract.MaxImageBytes, png.Length);
        }

        if (TryReadPngSize(png, out var width, out var height))
        {
            var pixels = (long)width * height;
            if (pixels > OcrDraftContract.MaxImagePixels)
            {
                return TooLarge("像素", OcrDraftContract.MaxImagePixels, pixels);
            }
        }

        return null;
    }

    /// <summary>从 PNG 签名后的 IHDR 块读宽高（不解码图片）。不是 PNG 或头部不完整时返回 <see langword="false"/>。</summary>
    /// <param name="png">PNG 字节。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    public static bool TryReadPngSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = 0;
        height = 0;
        // 签名 8 字节 + 块长度 4 + 类型 "IHDR" 4 + 宽 4 + 高 4。
        if (png.Length < 24 || !png[..8].SequenceEqual(PngSignature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var w = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>
    /// <c>/ocr_translate</c> 的相对路径与查询串。<paramref name="fallbackTarget"/> 为空或与 <paramref name="target"/> 同语种时省略；
    /// <paramref name="glossary"/> 非空时追加 <c>glossary=true|false</c>（#88，与 <c>/translate</c> 的 <c>glossary</c> 字段同义）。
    /// </summary>
    internal static string BuildOcrTranslatePath(string source, string target, string? fallbackTarget, bool? glossary = null)
    {
        var parameters = new List<(string, string)>
        {
            (OcrDraftContract.SourceParameter, source.Trim()),
            (OcrDraftContract.TargetParameter, target.Trim()),
        };
        if (!string.IsNullOrWhiteSpace(fallbackTarget)
            && !string.Equals(TimeoutPolicy.Normalize(fallbackTarget), TimeoutPolicy.Normalize(target), StringComparison.Ordinal))
        {
            parameters.Add((OcrDraftContract.FallbackTargetParameter, fallbackTarget.Trim()));
        }

        if (glossary is { } enabled)
        {
            parameters.Add((Glossary.GlossaryContract.OcrQueryParameter, enabled ? "true" : "false"));
        }

        return OcrDraftContract.OcrTranslatePath + "?" + Query([.. parameters]);
    }

    /// <summary>草案新增的 OCR 错误码 → <see cref="EngineException"/>；不是 OCR 错误码时返回 <see langword="null"/>（沿用翻译侧映射）。</summary>
    private static EngineException? MapOcrError(int status, ErrorBody body, string message)
    {
        var details = body.Details;
        EngineErrorKind? kind = body.Code switch
        {
            OcrErrorCodes.ImageTooLarge => EngineErrorKind.ImageTooLarge,
            OcrErrorCodes.UnsupportedMediaType => EngineErrorKind.UnsupportedMediaType,
            OcrErrorCodes.InvalidImage => EngineErrorKind.InvalidImage,
            OcrErrorCodes.OcrUnavailable => EngineErrorKind.OcrUnavailable,
            _ => null,
        };
        if (kind is not { } k)
        {
            return null;
        }

        return new EngineException(k, message)
        {
            StatusCode = status,
            ErrorCode = body.Code,
            Details = details,
            Limit = k == EngineErrorKind.ImageTooLarge ? GetInt(details, OcrDraftContract.LimitDetail) : null,
            Length = k == EngineErrorKind.ImageTooLarge ? GetInt(details, OcrDraftContract.ActualDetail) : null,
            MissingModels = k == EngineErrorKind.OcrUnavailable ? GetStringArray(details, OcrDraftContract.MissingModelsDetail) : [],
        };
    }

    private static void ThrowIfPrecheckFails(ReadOnlyMemory<byte> png)
    {
        if (png.IsEmpty)
        {
            throw new ArgumentException("PNG 不能为空", nameof(png));
        }

        if (PrecheckImage(png.Span) is { } error)
        {
            throw error;
        }
    }

    private static EngineException TooLarge(string unit, long limit, long actual)
    {
        var details = JsonSerializer.SerializeToElement(new Dictionary<string, long>
        {
            [OcrDraftContract.LimitDetail] = limit,
            [OcrDraftContract.ActualDetail] = actual,
        });
        return new EngineException(
            EngineErrorKind.ImageTooLarge,
            string.Create(CultureInfo.InvariantCulture, $"客户端预检：截图 {actual} {unit}，超过上限 {limit} {unit}，未发送请求"))
        {
            ErrorCode = OcrErrorCodes.ImageTooLarge,
            Details = details,
            Limit = (int)Math.Min(int.MaxValue, limit),
            Length = (int)Math.Min(int.MaxValue, actual),
            IsClientPrecheck = true,
        };
    }

    private static ReadOnlyMemoryContent PngContent(ReadOnlyMemory<byte> png)
    {
        var content = new ReadOnlyMemoryContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue(OcrDraftContract.PngMediaType);
        return content;
    }

    private static string Query(params (string Name, string Value)[] parameters) =>
        string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value)));
}
