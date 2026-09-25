using System.Text.Json;
using Suiyi.Core.Flow;
using Suiyi.Core.Ocr;

namespace Suiyi.Core.Popup;

/// <summary>
/// <c>--popup-demo</c>：依次展示各状态，便于截图与手测。前半是复制翻译（M2），后半是框选翻译（#57）：
/// OCR 数据是按 #53 草案写的响应 JSON，经 <see cref="OcrDraftContract"/> 与 <see cref="OcrResultMapper"/> 映射，
/// 不需要启动引擎。选区锚点用固定屏幕坐标（主屏左上区域与右下角附近各一个），用来看放置与回退。
/// </summary>
public static class PopupDemo
{
    /// <summary>主屏左上区域的演示选区（物理像素）。</summary>
    public static PopupRect DemoSelection { get; } = new(160, 160, 640, 240);

    /// <summary>靠近 1080p 主屏右下角的演示选区，右下外侧放不下，应回退到上方或左侧。</summary>
    public static PopupRect DemoSelectionNearCorner { get; } = new(1500, 820, 380, 200);

    /// <summary>草案格式的演示响应：中文两段 → 英文。</summary>
    public const string SampleZhEnJson = """
        {
          "lines": [
            { "text": "随译是一款开源免费的", "box": [[10, 10], [300, 10], [300, 40], [10, 40]], "score": 0.98 },
            { "text": "本地翻译工具。", "box": [[10, 44], [220, 44], [220, 74], [10, 74]], "score": 0.97 },
            { "text": "所有识别和翻译都在本机完成。", "box": [[10, 100], [400, 100], [400, 130], [10, 130]], "score": 0.95 }
          ],
          "paragraphs": [
            { "text": "随译是一款开源免费的本地翻译工具。", "box": [10, 10, 290, 64] },
            { "text": "所有识别和翻译都在本机完成，截图不会上传。", "box": [10, 100, 390, 30] }
          ],
          "text": "随译是一款开源免费的本地翻译工具。\n所有识别和翻译都在本机完成，截图不会上传。",
          "image": { "width": 640, "height": 240 },
          "translation": {
            "results": [
              { "text": "Suiyi is a free, open-source local translation tool.", "source": "zh", "detected": true, "target": "en", "route": ["opus-mt-zh-en"], "elapsed_ms": 120.5 },
              { "text": "All recognition and translation happen on this computer; screenshots are never uploaded.", "source": "zh", "detected": true, "target": "en", "route": ["opus-mt-zh-en"], "elapsed_ms": 140.1 }
            ]
          },
          "elapsed_ms": { "ocr": 610.2, "translate": 260.6, "total": 890.4 }
        }
        """;

    /// <summary>草案格式的演示响应：日文 → 中文（英文中转）。</summary>
    public const string SampleJaZhJson = """
        {
          "paragraphs": [ { "text": "本日は晴天なり。", "box": [0, 0, 200, 30] } ],
          "text": "本日は晴天なり。",
          "image": { "width": 220, "height": 40 },
          "translation": { "results": [ { "text": "今天天气晴朗。", "source": "ja", "detected": true, "target": "zh", "route": ["opus-mt-ja-en", "opus-mt-en-zh"], "elapsed_ms": 300.0 } ] },
          "elapsed_ms": { "ocr": 380.0, "translate": 300.0, "total": 700.0 }
        }
        """;

    /// <summary>草案格式的演示响应：识别为空。</summary>
    public const string SampleEmptyJson = """
        { "lines": [], "paragraphs": [], "text": "", "image": { "width": 300, "height": 200 }, "translation": { "results": [] }, "elapsed_ms": { "ocr": 150.0, "translate": 0, "total": 151.0 } }
        """;

    /// <summary>每一步的间隔。</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(3);

    /// <summary>步骤数（一轮）。</summary>
    public static int StepCount => Steps.Length;

    private static readonly Action<PopupViewModel>[] Steps =
    [
        p => p.ShowPreparing(),
        p => p.ShowLoading("The quick brown fox jumps over the lazy dog. Local, offline and free."),
        p => p.ShowResult(new PopupResult("敏捷的棕色狐狸跳过了那只懒狗。本地、离线、免费。", "en", "zh") { SourceDetected = true, Elapsed = TimeSpan.FromMilliseconds(320) }),
        p => p.ShowResult(new PopupResult("Suiyi is a free, open-source local translator that runs entirely on your computer.", "zh", "en") { Elapsed = TimeSpan.FromMilliseconds(1450) }),
        p => p.ShowResult(new PopupResult("随訳はパソコン上で動作する無料のオープンソース翻訳ツールです。", "zh", "ja") { SourceDetected = true, Elapsed = TimeSpan.FromMilliseconds(680) }),
        p => p.ShowResult(new PopupResult(string.Join("\n\n", Enumerable.Repeat("这是一段很长的译文，用来检查浮窗的最大宽度、自动换行和滚动条。长文本不应超出屏幕工作区的一半高度。", 12)), "en", "zh") { SourceDetected = true, Elapsed = TimeSpan.FromSeconds(2.4) }),
        p => p.ShowError(new PopupError(PopupErrorKind.ServiceUnavailable)),
        p => p.ShowError(new PopupError(PopupErrorKind.EngineStartTimeout)),
        p => p.ShowError(new PopupError(PopupErrorKind.Timeout)),
        p => p.ShowError(new PopupError(PopupErrorKind.MissingModels) { MissingModels = ["opus-mt-en-zh", "opus-mt-ja-en"] }),
        p => p.ShowError(new PopupError(PopupErrorKind.DetectFailed)),
        p => p.ShowError(new PopupError(PopupErrorKind.TextTooLong) { Limit = 5000, Length = 6234 }),

        // ---- 框选翻译（#57）----
        p => p.ShowOcrLoading(DemoSelection),
        p => p.ShowOcrResult(Ocr(SampleZhEnJson, "en")),
        p =>
        {
            // 同一浮窗内展开原文：展开状态保持到下一次翻译。
            p.ShowOcrResult(Ocr(SampleZhEnJson, "en"), DemoSelection);
            p.ToggleOriginal();
        },
        p => p.ShowOcrResult(Ocr(SampleJaZhJson, "zh"), DemoSelectionNearCorner),

        // 第 2 段服务端没有给出译文：以原文代替，译文下方灰色小字提示（#58）。
        p => p.ShowOcrResult(Ocr(SamplePartialJson, "zh"), DemoSelection),
        p =>
        {
            p.ShowOcrResult(LongOcrResult(), DemoSelectionNearCorner);
            p.ToggleOriginal();
        },
        p => p.ShowOcrResult(Ocr(SampleEmptyJson, "en"), DemoSelection),
        p =>
        {
            p.ShowOcrLoading(DemoSelection);
            p.ShowError(OcrResultMapper.MapError(OcrErrorCodes.ImageTooLarge, Details("""{ "limit": 8388608, "actual": 9437184 }""")));
        },
        p =>
        {
            p.ShowOcrLoading(DemoSelection);
            p.ShowError(OcrResultMapper.MapError(OcrErrorCodes.InvalidImage));
        },
        p =>
        {
            p.ShowOcrLoading(DemoSelection);
            p.ShowError(OcrResultMapper.MapError(OcrErrorCodes.OcrUnavailable, Details("""{ "missing_models": ["ch_PP-OCRv4_det", "ch_PP-OCRv4_rec"] }""")));
        },
    ];

    /// <summary>演示用：两段识别结果，只有第 1 段有译文。</summary>
    public const string SamplePartialJson = """
        {
          "paragraphs": [{ "text": "Suiyi runs entirely on your computer." }, { "text": "Ctrl+Alt+S" }],
          "text": "Suiyi runs entirely on your computer.\nCtrl+Alt+S",
          "translation": { "results": [{ "text": "随译完全在你的电脑上运行。", "source": "en", "detected": true, "target": "zh", "route": ["opus-mt-en-zh"], "elapsed_ms": 40 }] }
        }
        """;

    /// <summary>演示用：解析草案 JSON 并映射为浮窗结果。</summary>
    public static PopupOcrResult Ocr(string json, string target) => OcrResultMapper.Map(OcrDraftContract.ParseResponse(json), target);

    /// <summary>演示用：多段长文本（检查滚动与展开原文后不超出屏幕）。</summary>
    public static PopupOcrResult LongOcrResult()
    {
        var sources = Enumerable.Range(1, 10).Select(i => $"第 {i} 段：这是一段较长的识别原文，用来检查浮窗的最大高度、段落分隔和滚动条。识别结果可能有错字，需要展开原文核对。").ToArray();
        var translations = Enumerable.Range(1, 10).Select(i => $"Paragraph {i}: This is a fairly long recognized passage used to check the maximum height, paragraph breaks and scrolling of the popup.").ToArray();
        return new PopupOcrResult(sources, translations, "zh", "en") { SourceDetected = true, Elapsed = TimeSpan.FromSeconds(1.8) };
    }

    private static JsonElement Details(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>执行第 <paramref name="step"/> 步（按一轮取模）。</summary>
    public static void ApplyStep(PopupViewModel popup, int step)
    {
        ArgumentNullException.ThrowIfNull(popup);
        Steps[((step % Steps.Length) + Steps.Length) % Steps.Length](popup);
    }
}
