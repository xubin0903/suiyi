using Suiyi.Core.Capture;
using Suiyi.Core.Engine;
using Suiyi.Core.Ocr;

namespace Suiyi.Core.Tests.Flow;

/// <summary>可控的框选：每次调用挂起，由测试完成、取消或抛异常。</summary>
internal sealed class FakeRegionCapture : IRegionCapture
{
    private TaskCompletionSource<RegionCaptureResult?>? _pending;

    public int Calls { get; private set; }

    public Task<RegionCaptureResult?> CaptureAsync(CancellationToken cancellationToken)
    {
        Calls++;
        _pending = new TaskCompletionSource<RegionCaptureResult?>();
        cancellationToken.Register(() => _pending.TrySetResult(null));
        return _pending.Task;
    }

    public void Complete(RegionCaptureResult? result) => _pending!.SetResult(result);

    public void Fail(Exception ex) => _pending!.SetException(ex);

    public static RegionCaptureResult Sample(byte[]? png = null, PixelRect? bounds = null) => new(
        png ?? [0x89, 0x50, 0x4E, 0x47, 1, 2, 3],
        bounds ?? new PixelRect(200, 150, 640, 180),
        new DisplayMonitor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), 96, true));
}

/// <summary>可控的框选翻译服务：每次调用挂起；取消时抛 <see cref="OperationCanceledException"/>。</summary>
internal sealed class FakeOcrService : IOcrTranslationService
{
    public const string TwoParagraphs = """
        {"lines":[],"paragraphs":[{"text":"机密第一段"},{"text":"机密第二段"}],"text":"机密第一段\n机密第二段","image":{"width":640,"height":180},
         "translation":{"results":[{"text":"Secret one","source":"zh","detected":true,"target":"en","route":["m"],"elapsed_ms":20},
                                   {"text":"Secret two","source":"zh","detected":true,"target":"en","route":["m"],"elapsed_ms":20}]},
         "elapsed_ms":{"ocr":210,"translate":40,"total":255}}
        """;

    public const string Empty = """
        {"lines":[],"paragraphs":[],"text":"","image":{"width":640,"height":180},"translation":{"results":[]},"elapsed_ms":{"ocr":30,"translate":0,"total":31}}
        """;

    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls => _calls;

    public int CancelCurrentCount { get; private set; }

    public Task<OcrTranslationOutcome> TranslateImageAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken = default)
    {
        var call = new Call(png, new TaskCompletionSource<OcrTranslationOutcome>(), cancellationToken);
        cancellationToken.Register(() => call.Completion.TrySetCanceled(cancellationToken));
        _calls.Add(call);
        return call.Completion.Task;
    }

    public void CancelCurrent() => CancelCurrentCount++;

    public OcrHealthError? KnownOcrError { get; set; }

    public void Complete(int index, string json = TwoParagraphs, string target = "en") =>
        FakeTranslationService.Inline(() => _calls[index].Completion.SetResult(new OcrTranslationOutcome
        {
            Response = OcrDraftContract.ParseResponse(json),
            Target = target,
            FallbackTarget = "zh",
            Retargeted = false,
            ByteCount = _calls[index].Png.Length,
            ImageWidth = 640,
            ImageHeight = 180,
            ClientElapsed = TimeSpan.FromMilliseconds(300),
        }));

    public void Fail(int index, Exception exception) => FakeTranslationService.Inline(() => _calls[index].Completion.SetException(exception));

    internal sealed record Call(ReadOnlyMemory<byte> Png, TaskCompletionSource<OcrTranslationOutcome> Completion, CancellationToken Token);
}
