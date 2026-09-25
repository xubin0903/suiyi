using Suiyi.Core.Ocr;

namespace Suiyi.Core.Engine;

/// <summary><see cref="IOcrTranslationService.TranslateImageAsync"/> 的结果。浮窗内容由 <c>OcrResultMapper.Map(Response, Target)</c> 得到。</summary>
public sealed record OcrTranslationOutcome
{
    /// <summary>服务端响应（⚠ #53 草案 DTO）。</summary>
    public required OcrTranslateResponse Response { get; init; }

    /// <summary>请求的主目标语种（<c>target</c>）。</summary>
    public required string Target { get; init; }

    /// <summary>请求的次目标（<c>fallback_target</c>）；没有发送时为 <see langword="null"/>。</summary>
    public string? FallbackTarget { get; init; }

    /// <summary>是否有段落被改译为次目标（某段结果的 <c>target</c> 等于 <see cref="FallbackTarget"/>）。</summary>
    public required bool Retargeted { get; init; }

    /// <summary>上传的字节数。</summary>
    public required int ByteCount { get; init; }

    /// <summary>PNG 头里的宽（像素）；读不出时为 <see langword="null"/>。</summary>
    public int? ImageWidth { get; init; }

    /// <summary>PNG 头里的高（像素）；读不出时为 <see langword="null"/>。</summary>
    public int? ImageHeight { get; init; }

    /// <summary>客户端测得的往返耗时（含预热 <c>/languages</c>、<c>/health</c>）。</summary>
    public required TimeSpan ClientElapsed { get; init; }
}
