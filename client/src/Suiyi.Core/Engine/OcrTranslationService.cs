using System.Globalization;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Engine;

/// <summary>
/// <see cref="IOcrTranslationService"/> 的实现：套用与 <see cref="TranslationService"/> 相同的主/次目标规则，
/// 但通过 <c>fallback_target</c> 一次往返完成（服务端检测到原文就是主目标语种时改译为次目标）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>固定 <c>source=auto</c>（框选模式下语种标签不可点，见 #57）。</item>
/// <item>最新请求优先：新调用开始时取消上一条未完成的调用。</item>
/// <item>不重试；失败抛 <see cref="EngineException"/>。</item>
/// <item>日志只记图片尺寸、字节数、段落数、耗时、错误码，不记识别文本与服务端错误说明。</item>
/// </list>
/// </remarks>
public sealed class OcrTranslationService : IOcrTranslationService, IDisposable
{
    private readonly EngineClient _client;
    private readonly Func<(string Primary, string Secondary)> _targets;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private bool _disposed;

    /// <summary>创建框选翻译服务。</summary>
    /// <param name="client">引擎客户端（不随本服务释放）。</param>
    /// <param name="targets">每次调用时读取当前的主、次目标语种，例如从设置读取。</param>
    /// <param name="logger">日志；默认不记录。</param>
    /// <param name="timeProvider">测量往返耗时的时钟；默认 <see cref="TimeProvider.System"/>。</param>
    public OcrTranslationService(
        EngineClient client,
        Func<(string Primary, string Secondary)> targets,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(targets);
        _client = client;
        _targets = targets;
        _logger = logger ?? NullAppLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<OcrTranslationOutcome> TranslateImageAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken = default)
    {
        var mine = BeginRequest(cancellationToken);
        var started = _timeProvider.GetTimestamp();
        var hasSize = EngineClient.TryReadPngSize(png.Span, out var width, out var height);
        var size = hasSize ? string.Create(CultureInfo.InvariantCulture, $"{width}x{height}") : "?";
        try
        {
            var (primary, secondary) = _targets();
            ArgumentException.ThrowIfNullOrWhiteSpace(primary);
            var fallback = !string.IsNullOrWhiteSpace(secondary) && !SameLanguage(secondary, primary) ? secondary : null;

            var response = await _client.OcrTranslateAsync(png, EngineClient.AutoSource, primary, fallback, mine.Token)
                .ConfigureAwait(false);
            var elapsed = _timeProvider.GetElapsedTime(started);
            var results = response.Translation?.Results ?? [];
            var retargeted = fallback is not null && results.Any(r => r is not null && SameLanguage(r.Target, fallback));
            _logger.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"框选翻译完成：size={size} bytes={png.Length} paragraphs={response.Paragraphs.Count} retargeted={retargeted} server={response.ElapsedMs?.Total ?? 0:F0}ms client={elapsed.TotalMilliseconds:F0}ms"));
            return new OcrTranslationOutcome
            {
                Response = response,
                Target = primary,
                FallbackTarget = fallback,
                Retargeted = retargeted,
                ByteCount = png.Length,
                ImageWidth = hasSize ? width : null,
                ImageHeight = hasSize ? height : null,
                ClientElapsed = elapsed,
            };
        }
        catch (EngineException ex)
        {
            _logger.Warn(string.Create(
                CultureInfo.InvariantCulture,
                $"框选翻译失败：size={size} bytes={png.Length} kind={ex.Kind} code={ex.ErrorCode ?? "-"} status={ex.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-"} precheck={ex.IsClientPrecheck} client={_timeProvider.GetElapsedTime(started).TotalMilliseconds:F0}ms"));
            throw;
        }
        finally
        {
            EndRequest(mine);
        }
    }

    /// <inheritdoc />
    public void CancelCurrent()
    {
        lock (_gate)
        {
            _current?.Cancel();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current?.Cancel();
        }
    }

    private CancellationTokenSource BeginRequest(CancellationToken cancellationToken)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            _current = next;
        }

        previous?.Cancel();
        return next;
    }

    private void EndRequest(CancellationTokenSource mine)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, mine))
            {
                _current = null;
            }
        }

        mine.Dispose();
    }

    private static bool SameLanguage(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(TimeoutPolicy.Normalize(a), TimeoutPolicy.Normalize(b), StringComparison.Ordinal);
}
