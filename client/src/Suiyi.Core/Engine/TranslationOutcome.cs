namespace Suiyi.Core.Engine;

/// <summary><see cref="TranslationService.TranslateAsync"/> 的结果。</summary>
public sealed record TranslationOutcome
{
    /// <summary>译文。原文已是目标语种且没有可用的第二目标时为原文。</summary>
    public required string Text { get; init; }

    /// <summary>原文语种：自动检测的结果，或用户手动指定的语种。</summary>
    public required string SourceLanguage { get; init; }

    /// <summary>原文语种是否由服务自动检测得到（用户手动指定时为 <see langword="false"/>）。</summary>
    public required bool SourceDetected { get; init; }

    /// <summary>实际目标语种（主目标，或改译后的第二目标）。</summary>
    public required string TargetLanguage { get; init; }

    /// <summary>是否因原文已是主目标语种而改译为第二目标语种。</summary>
    public required bool Retargeted { get; init; }

    /// <summary>最后一次请求实际使用的模型 id；空表示原样返回，长度 2 表示英文中转。</summary>
    public required IReadOnlyList<string> Route { get; init; }

    /// <summary>服务端报告的翻译耗时（毫秒），改译时为两次请求之和。</summary>
    public required double ServerElapsedMs { get; init; }

    /// <summary>客户端测得的往返耗时（含 HTTP、检测，改译时含两次请求）。</summary>
    public required TimeSpan ClientElapsed { get; init; }

    /// <summary>本次发出的 <c>/translate</c> 请求数（1 或 2）。</summary>
    public required int RequestCount { get; init; }
}
