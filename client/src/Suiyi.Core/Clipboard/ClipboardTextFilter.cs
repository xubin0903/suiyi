using System.Text;
using System.Text.RegularExpressions;

namespace Suiyi.Core.Clipboard;

/// <summary>
/// 判断剪贴板文本是否值得翻译。规则见 <see cref="Classify"/>；
/// 实例额外负责「短时间内重复复制同一段」的去重，因此需要时钟。
/// 线程安全。
/// </summary>
public sealed partial class ClipboardTextFilter
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private string? _lastAccepted;
    private DateTimeOffset _lastAcceptedAt;

    /// <summary>创建过滤器。</summary>
    /// <param name="timeProvider">时钟，测试时注入；默认 <see cref="TimeProvider.System"/>。</param>
    public ClipboardTextFilter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 规范化并检查文本，含去重。被接受的文本会成为下一次去重的比较对象。
    /// </summary>
    public ClipboardFilterResult Evaluate(string? text, ClipboardFilterOptions options)
    {
        var result = Classify(text, options);
        if (!result.IsAccepted || options.DuplicateWindow <= TimeSpan.Zero)
        {
            if (result.IsAccepted)
            {
                Remember(result.Text!);
            }

            return result;
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_lastAccepted is not null
                && string.Equals(_lastAccepted, result.Text, StringComparison.Ordinal)
                && now - _lastAcceptedAt < options.DuplicateWindow)
            {
                return ClipboardFilterResult.Reject(RejectReason.Duplicate, result.Length);
            }

            _lastAccepted = result.Text;
            _lastAcceptedAt = now;
            return result;
        }
    }

    /// <summary>清除去重记录。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _lastAccepted = null;
        }
    }

    /// <summary>
    /// 去首尾空白，并把 <c>\r\n</c> / <c>\r</c> 统一为 <c>\n</c>。
    /// </summary>
    public static string Normalize(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.ReplaceLineEndings("\n").Trim();

    /// <summary>
    /// 纯函数：按固定顺序检查规则，不含去重。顺序为
    /// 空白 → 过短 → 过长 → 数字样式 → 纯符号 → 网址 → 邮箱 → 文件路径 → GUID/哈希 → 代码块。
    /// </summary>
    public static ClipboardFilterResult Classify(string? text, ClipboardFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var normalized = Normalize(text);
        var length = CountCodePoints(normalized);

        if (length == 0)
        {
            return ClipboardFilterResult.Reject(RejectReason.Empty, 0);
        }

        if (length < options.MinChars)
        {
            return ClipboardFilterResult.Reject(RejectReason.TooShort, length);
        }

        if (length > options.MaxChars)
        {
            return ClipboardFilterResult.Reject(RejectReason.TooLong, length);
        }

        var reason = FindRejectReason(normalized);
        return reason is { } r
            ? ClipboardFilterResult.Reject(r, length)
            : ClipboardFilterResult.Accept(normalized, length);
    }

    private static RejectReason? FindRejectReason(string text)
    {
        if (IsNumericLike(text))
        {
            return RejectReason.NumericLike;
        }

        if (!ContainsLetter(text))
        {
            return RejectReason.SymbolsOnly;
        }

        var singleLine = !text.Contains('\n', StringComparison.Ordinal);
        if (singleLine)
        {
            if (UrlRegex().IsMatch(text))
            {
                return RejectReason.Url;
            }

            if (EmailRegex().IsMatch(text))
            {
                return RejectReason.Email;
            }

            if (IsFilePath(text))
            {
                return RejectReason.FilePath;
            }

            if (HexOrGuidRegex().IsMatch(text))
            {
                return RejectReason.HexOrGuid;
            }
        }

        if (IsCodeLike(text))
        {
            return RejectReason.CodeLike;
        }

        return null;
    }

    private static int CountCodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>只由数字、空白与 <c>+-.,:/()%¥$</c> 等组成，且至少有一个数字。</summary>
    private static bool IsNumericLike(string text)
    {
        var hasDigit = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsDigit(rune))
            {
                hasDigit = true;
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            if (rune.IsBmp && NumericSymbols.Contains((char)rune.Value, StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return hasDigit;
    }

    private const string NumericSymbols = "+-.,:/()%¥$￥€£#*，。：（）－／";

    private static bool ContainsLetter(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFilePath(string text)
    {
        // Windows：C:\ 或 C:/ 开头，或 UNC \\server\share。允许空格（Program Files）。
        if (WindowsPathRegex().IsMatch(text))
        {
            return true;
        }

        // Unix：/、~/、./、../ 开头，且不含空白（避免误伤「/ 是斜杠」这类句子）。
        return UnixPathRegex().IsMatch(text);
    }

    /// <summary>至少 3 个非空行，且超过一半的非空行以 <c>;</c>、<c>{</c>、<c>}</c> 结尾。</summary>
    private static bool IsCodeLike(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3)
        {
            return false;
        }

        var codeLines = lines.Count(static line => line.EndsWith(';') || line.EndsWith('{') || line.EndsWith('}'));
        return codeLines * 2 > lines.Length;
    }

    private void Remember(string text)
    {
        lock (_gate)
        {
            _lastAccepted = text;
            _lastAcceptedAt = _timeProvider.GetUtcNow();
        }
    }

    [GeneratedRegex(@"^(?:(?:https?|ftp)://|www\.)\S+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(?:mailto:)?[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|\\\\[^\\\s]+\\)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"^(?:~|\.{1,2})?/\S*$", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"^\{?[0-9a-fA-F-]{16,}\}?$", RegexOptions.CultureInvariant)]
    private static partial Regex HexOrGuidRegex();
}
