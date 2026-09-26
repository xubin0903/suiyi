using System.Globalization;

namespace Suiyi.Core.Glossary;

/// <summary>术语保护状态的显示文字（托盘「专业术语」子菜单、「关于」、气泡）。纯函数。</summary>
public static class GlossaryStatusText
{
    /// <summary>菜单一行的最大长度，超出截断。</summary>
    public const int MaxLineLength = 60;

    /// <summary>还没取到服务状态。</summary>
    public const string Unknown = "术语表状态：等待翻译服务就绪";

    /// <summary>服务不报告术语表状态（#83 之前的引擎）。</summary>
    public const string NotSupported = "当前引擎不支持术语保护（需要更新引擎，见 #83）";

    /// <summary>
    /// 子菜单里的状态行（不可点）：第一行是条数，其后依次是错误、警告条数与第一条警告。
    /// </summary>
    /// <param name="status">服务状态；<see langword="null"/> 时看 <paramref name="supported"/>。</param>
    /// <param name="supported">服务是否报告了术语表状态；<see langword="null"/> 表示还不知道。</param>
    public static IReadOnlyList<string> MenuLines(GlossaryStatus? status, bool? supported)
    {
        if (status is null)
        {
            return [supported == false ? NotSupported : Unknown];
        }

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"内置 {status.BuiltinEntries} 条 · 我的 {status.UserEntries} 条"),
        };
        if (status.HasError)
        {
            lines.Add(Truncate("我的术语表未生效：" + OneLine(status.Error!)));
        }

        if (status.Warnings.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"有 {status.Warnings.Count} 行被跳过，例如："));
            lines.Add(Truncate(OneLine(status.Warnings[0])));
        }

        return lines;
    }

    /// <summary>「关于」里的一段：开关、条数、错误与全部警告。</summary>
    /// <param name="enabled">客户端设置。</param>
    /// <param name="status">服务状态。</param>
    /// <param name="supported">服务是否报告了术语表状态。</param>
    public static string About(bool enabled, GlossaryStatus? status, bool? supported)
    {
        var text = $"专业术语保护：{(enabled ? "开" : "关")}";
        if (status is null)
        {
            return text + "\n" + (supported == false ? NotSupported : Unknown);
        }

        text += string.Create(CultureInfo.InvariantCulture, $"（内置 {status.BuiltinEntries} 条，我的 {status.UserEntries} 条）");
        if (status.HasError)
        {
            text += "\n我的术语表未生效：" + OneLine(status.Error!);
        }

        foreach (var warning in status.Warnings)
        {
            text += "\n· " + OneLine(warning);
        }

        return text;
    }

    /// <summary>「重新加载术语表」完成后的气泡文字。</summary>
    /// <param name="status">重读后的状态。</param>
    public static string Reloaded(GlossaryStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.HasError)
        {
            return Truncate("我的术语表未生效：" + OneLine(status.Error!), 200) + "（已改用内置术语表）";
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"术语表已重新加载：我的 {status.UserEntries} 条，内置 {status.BuiltinEntries} 条");
        return status.Warnings.Count > 0
            ? text + string.Create(CultureInfo.InvariantCulture, $"；{status.Warnings.Count} 行被跳过：{Truncate(OneLine(status.Warnings[0]))}")
            : text;
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Truncate(string text, int max = MaxLineLength) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
}
