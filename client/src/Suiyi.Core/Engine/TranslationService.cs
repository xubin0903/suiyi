namespace Suiyi.Core.Engine;

/// <summary>
/// 封装「中英日自动检测 + 主/次目标语言」的翻译入口，供浮窗等界面调用。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>默认 <c>source=auto, target=主目标</c>。若服务检测到原文就是主目标语种（原样返回），
/// 再以 <c>source=检测结果, target=第二目标</c> 请求一次。</item>
/// <item>传入 <c>sourceOverride</c>（用户在浮窗手动指定原文语种，例如纯汉字日语被检测成 zh）时不再自动检测；
/// 该语种等于主目标时直接译为第二目标。</item>
/// <item>最新请求优先：新调用开始时取消上一条未完成的调用，被取消的调用抛 <see cref="OperationCanceledException"/>，不算错误。</item>
/// <item>不重试；失败抛 <see cref="EngineException"/>，由界面决定是否重试。</item>
/// </list>
/// </remarks>
public sealed class TranslationService : ITranslationService, IDisposable
{
    private readonly EngineClient _client;
    private readonly Func<(string Primary, string Secondary)> _targets;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private bool _disposed;

    /// <summary>创建翻译服务。</summary>
    /// <param name="client">引擎客户端（不随本服务释放）。</param>
    /// <param name="targets">每次翻译时读取当前的主、次目标语种（ISO 639-1），例如从设置读取。</param>
    /// <param name="timeProvider">测量客户端往返耗时的时钟；默认 <see cref="TimeProvider.System"/>。</param>
    public TranslationService(
        EngineClient client,
        Func<(string Primary, string Secondary)> targets,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(targets);
        _client = client;
        _targets = targets;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>翻译一段文本。</summary>
    /// <param name="text">原文。</param>
    /// <param name="sourceOverride">用户手动指定的原文语种；<see langword="null"/> 表示自动检测。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <exception cref="OperationCanceledException">调用方取消，或被更新的调用取代。</exception>
    /// <exception cref="EngineException">翻译失败。</exception>
    public async Task<TranslationOutcome> TranslateAsync(
        string text,
        string? sourceOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var mine = BeginRequest(cancellationToken);
        var token = mine.Token;
        var started = _timeProvider.GetTimestamp();
        try
        {
            var (primary, secondary) = _targets();
            ArgumentException.ThrowIfNullOrWhiteSpace(primary);
            var hasSecondary = !string.IsNullOrWhiteSpace(secondary) && !SameLanguage(secondary, primary);

            string source;
            string target;
            var retargeted = false;
            if (!string.IsNullOrWhiteSpace(sourceOverride))
            {
                source = sourceOverride;
                target = primary;
                if (SameLanguage(source, primary) && hasSecondary)
                {
                    target = secondary;
                    retargeted = true;
                }
            }
            else
            {
                source = EngineClient.AutoSource;
                target = primary;
            }

            var first = await _client.TranslateAsync(text, source, target, token).ConfigureAwait(false);
            var requests = 1;
            var serverMs = first.ElapsedMs;
            var final = first;
            if (!retargeted && hasSecondary && first.Route.Count == 0 && SameLanguage(first.Source, primary))
            {
                token.ThrowIfCancellationRequested();
                final = await _client.TranslateAsync(text, first.Source, secondary, token).ConfigureAwait(false);
                requests = 2;
                serverMs += final.ElapsedMs;
                retargeted = true;
            }

            return new TranslationOutcome
            {
                Text = final.Text,
                SourceLanguage = string.IsNullOrEmpty(final.Source) ? source : final.Source,
                SourceDetected = sourceOverride is null or { Length: 0 } && first.Detected,
                TargetLanguage = string.IsNullOrEmpty(final.Target) ? target : final.Target,
                Retargeted = retargeted,
                Route = final.Route,
                ServerElapsedMs = serverMs,
                ClientElapsed = _timeProvider.GetElapsedTime(started),
                RequestCount = requests,
            };
        }
        finally
        {
            EndRequest(mine);
        }
    }

    /// <summary>取消当前未完成的调用（如用户关闭浮窗）。</summary>
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

    private static bool SameLanguage(string a, string b) =>
        string.Equals(TimeoutPolicy.Normalize(a), TimeoutPolicy.Normalize(b), StringComparison.Ordinal);
}
