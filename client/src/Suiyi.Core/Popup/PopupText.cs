using System.Globalization;
using System.Text;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Popup;

/// <summary>浮窗文案与字体的纯函数。</summary>
public static class PopupText
{
    /// <summary>Preparing 状态文字。</summary>
    public const string PreparingText = "正在准备翻译服务…";

    /// <summary>框选翻译加载文字（#57）。</summary>
    public const string OcrLoadingText = "正在识别并翻译…";

    /// <summary>框选翻译空结果文字（#57）。</summary>
    public const string OcrEmptyText = "未识别到文字";

    /// <summary>原文区折叠时的按钮文字。</summary>
    public const string ShowOriginalText = "原文 ▸";

    /// <summary>原文区展开时的按钮文字。</summary>
    public const string HideOriginalText = "原文 ▾";

    /// <summary>原文摘要的最大字符数。</summary>
    public const int SourcePreviewLength = 80;

    /// <summary>全部段落都没有译文时的提示。</summary>
    public const string AllUntranslatedHint = "未能翻译，以上为识别出的原文";

    /// <summary>
    /// 框选翻译中译文缺失、用原文代替的段落的小字提示（例如「第 2、3 段未能翻译，显示为原文」）；没有这种段落时为空。
    /// 段落序号从 1 起，按浮窗里显示的段落计。
    /// </summary>
    /// <param name="result">框选翻译结果。</param>
    public static string UntranslatedHint(PopupOcrResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var indices = result.UntranslatedParagraphs
            .Where(i => i >= 0 && i < result.TranslationParagraphs.Count)
            .Distinct()
            .Order()
            .ToList();
        if (indices.Count == 0)
        {
            return string.Empty;
        }

        if (indices.Count == result.TranslationParagraphs.Count)
        {
            return AllUntranslatedHint;
        }

        return "第 " + string.Join("、", indices.Select(i => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) + " 段未能翻译，显示为原文";
    }

    /// <summary>可手动指定的原文语种（中文 / English / 日本語）。</summary>
    public static IReadOnlyList<TrayLanguage> SourceChoices => TrayLanguages.All;

    /// <summary>语种标签，例如「中文 → English」；自动检测时「中文（自动） → English」，未知时「自动 → English」。</summary>
    public static string LanguageLabel(string? source, string target, bool sourceDetected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var to = TrayLanguages.GetDisplayName(target);
        if (string.IsNullOrWhiteSpace(source) || string.Equals(source, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return $"自动 → {to}";
        }

        var from = TrayLanguages.GetDisplayName(source);
        return sourceDetected ? $"{from}（自动） → {to}" : $"{from} → {to}";
    }

    /// <summary>耗时小字：1 秒内为「320 ms」，否则「1.2 s」。</summary>
    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed < TimeSpan.FromSeconds(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)Math.Round(elapsed.TotalMilliseconds))} ms")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.0} s");

    /// <summary>原文摘要：空白折叠为单个空格，超长截断并以「…」结尾。</summary>
    public static string SourcePreview(string text, int maxLength = SourcePreviewLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            if (builder.Length > maxLength)
            {
                break;
            }
        }

        if (builder.Length <= maxLength)
        {
            return builder.ToString();
        }

        builder.Length = maxLength - 1;
        return builder.Append('…').ToString();
    }

    /// <summary>
    /// 按语种给 WPF 的字体回退链：中文优先 Microsoft YaHei UI，日文优先 Yu Gothic UI（汉字用日文字形），英文 Segoe UI。
    /// </summary>
    public static string FontFamilyFor(string? language) => language?.ToLowerInvariant() switch
    {
        "ja" => "Yu Gothic UI, Microsoft YaHei UI, Segoe UI",
        "en" => "Segoe UI, Microsoft YaHei UI, Yu Gothic UI",
        _ => "Microsoft YaHei UI, Yu Gothic UI, Segoe UI",
    };
}
