using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Suiyi.Core.Popup;
using Suiyi.Core.Settings;
using Suiyi.Core.Tests.Engine;
using Suiyi.Core.Tests.Settings;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Flow;

/// <summary>编排器 + 真实 <see cref="TranslationService"/> / <see cref="EngineClient"/>（HTTP 由假处理器应答）+ 设置存储。</summary>
public sealed class MainFlowIntegrationTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeEngineHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SettingsStore _settings;
    private readonly EngineClient _client;
    private readonly TranslationService _service;
    private readonly PopupViewModel _popup;
    private readonly TranslateFlowCoordinator _flow;
    private readonly List<TranslateFlowCompletedEventArgs> _completed = [];
    private readonly SemaphoreSlim _done = new(0);

    public MainFlowIntegrationTests()
    {
        _settings = new SettingsStore(Path.Combine(_dir.Path, "settings.json"));
        _settings.Load();
        _client = new EngineClient(EngineClient.DefaultPort, _handler, _time);
        _service = new TranslationService(_client, () => (_settings.Current.PrimaryTarget, _settings.Current.SecondaryTarget), _time);
        _popup = new PopupViewModel(timeProvider: _time);
        _handler.Translate = Echo;

        // 真实服务在线程池上完成：dispatch 用锁串行化，模拟 UI 线程。
        var gate = new object();
        void Dispatch(Action a)
        {
            lock (gate)
            {
                a();
            }
        }

        _flow = new TranslateFlowCoordinator(_service, new FakeEngineStatus(), _popup, new TrayController(), timeProvider: _time, dispatch: Dispatch);
        _flow.Completed += (_, e) =>
        {
            _completed.Add(e);
            _done.Release();
        };
    }

    public void Dispose()
    {
        _flow.Dispose();
        _popup.Dispose();
        _service.Dispose();
        _client.Dispose();
        _done.Dispose();
        _dir.Dispose();
    }

    /// <summary>模拟服务：含汉字判为 zh，否则 en。</summary>
    private static Task<HttpResponseMessage> Echo(string body, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("text").GetString()!;
        var source = doc.RootElement.GetProperty("source").GetString()!;
        var target = doc.RootElement.GetProperty("target").GetString()!;
        var actual = source != "auto" ? source : text.Any(c => c is >= '\u4e00' and <= '\u9fff') ? "zh" : "en";
        var route = actual == target ? "[]" : $"[\"opus-mt-{actual}-{target}\"]";
        var json = $$"""{"text":"[{{actual}}->{{target}}]","source":"{{actual}}","detected":{{(source == "auto" ? "true" : "false")}},"target":"{{target}}","route":{{route}},"elapsed_ms":12.5}""";
        return Task.FromResult(FakeEngineHandler.Json(HttpStatusCode.OK, json));
    }

    private async Task<TranslateFlowCompletedEventArgs> TranslateAsync(string text, ClipboardTrigger trigger = ClipboardTrigger.Hotkey)
    {
        var before = _completed.Count;
        _flow.OnTextCaptured(text, trigger);
        Assert.True(await _done.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(before + 1, _completed.Count);
        return _completed[^1];
    }

    private IEnumerable<string> Targets => _handler.TranslateRequests.Select(r =>
    {
        using var doc = JsonDocument.Parse(r.Body!);
        return doc.RootElement.GetProperty("target").GetString()!;
    });

    [Fact]
    public async Task TargetChangeInSettings_AppliesToNextRequest()
    {
        var first = await TranslateAsync("Hello");
        Assert.Equal("zh", first.Outcome.TargetLanguage);

        _settings.Update(s => s.WithPrimaryTarget("ja"));
        var second = await TranslateAsync("Hello");

        Assert.Equal("ja", second.Outcome.TargetLanguage);
        Assert.Equal(["zh", "ja"], Targets);
        Assert.Equal("[en->ja]", _popup.Translation);
    }

    [Fact]
    public async Task SourceEqualsPrimary_UsesSecondaryTarget()
    {
        var outcome = (await TranslateAsync("你好")).Outcome;

        Assert.Equal("zh", outcome.SourceLanguage);
        Assert.Equal("en", outcome.TargetLanguage);
        Assert.True(outcome.Retargeted);
        Assert.Equal("[zh->en]", _popup.Translation);
    }

    [Fact]
    public async Task SourceOverride_IsSentToEngine()
    {
        await TranslateAsync("Hello");

        _flow.TranslateWithSource("ja");
        Assert.True(await _done.WaitAsync(TimeSpan.FromSeconds(10)));

        using var doc = JsonDocument.Parse(_handler.TranslateRequests[^1].Body!);
        Assert.Equal("ja", doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task EngineError_ReachesPopup()
    {
        _handler.Translate = (_, _) => Task.FromResult(FakeEngineHandler.Json(
            HttpStatusCode.UnprocessableEntity,
            """{"error":{"code":"unsupported_pair","message":"no model","details":{"source":"en","target":"zh","missing_models":["opus-mt-en-zh"]}}}"""));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _popup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PopupViewModel.Kind) && _popup.Kind == PopupKind.Error)
            {
                failed.TrySetResult();
            }
        };

        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PopupErrorKind.MissingModels, _popup.Error!.Kind);
    }

    /// <summary>
    /// 端到端计时的可测方式：10 次热路径请求（服务应答时间由假处理器决定），
    /// <see cref="TranslateFlowCoordinator.Latency"/> 汇总 P50 / P95；实机数值见日志 <c>e2e_ms</c> 与「关于」。
    /// </summary>
    [Fact]
    public async Task TenHotPathRequests_ProduceP95Summary()
    {
        for (var i = 0; i < 10; i++)
        {
            await TranslateAsync($"Hello {i}", ClipboardTrigger.Monitor);
        }

        Assert.Equal(10, _flow.Latency.Count);
        Assert.True(_flow.Latency.Percentile(95) <= 2000);
        Assert.StartsWith("最近 10 次：P50 ", _flow.Latency.Summary(), StringComparison.Ordinal);
    }
}
