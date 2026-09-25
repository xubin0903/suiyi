using System.Globalization;

namespace Suiyi.Core.Logging;

/// <summary>
/// 日志目录与文件名规则。
/// 默认目录 <c>%LOCALAPPDATA%\suiyi\logs</c>，可用环境变量 <c>SUIYI_LOG_DIR</c> 覆盖；
/// 文件名 <c>&lt;前缀&gt;-yyyyMMdd.log</c>，按天滚动。
/// </summary>
public static class LogPaths
{
    /// <summary>覆盖日志目录的环境变量名。</summary>
    public const string DirectoryOverrideVariable = "SUIYI_LOG_DIR";

    /// <summary>客户端日志文件名前缀。</summary>
    public const string ClientPrefix = "client";

    private const string DateFormat = "yyyyMMdd";
    private const string Extension = ".log";

    /// <summary>按当前进程环境解析日志目录。</summary>
    public static string ResolveDirectory() =>
        ResolveDirectory(
            Environment.GetEnvironmentVariable(DirectoryOverrideVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>解析日志目录（纯函数，便于测试）。</summary>
    /// <param name="overrideDirectory"><c>SUIYI_LOG_DIR</c> 的值；为空或全空白时忽略。</param>
    /// <param name="localAppData"><c>%LOCALAPPDATA%</c> 路径。</param>
    /// <returns>绝对路径。</returns>
    public static string ResolveDirectory(string? overrideDirectory, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory.Trim());
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        return Path.GetFullPath(Path.Combine(localAppData, "suiyi", "logs"));
    }

    /// <summary>某一天的日志文件名，例如 <c>client-20260925.log</c>。</summary>
    public static string GetFileName(string prefix, DateOnly date)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return prefix + "-" + date.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension;
    }

    /// <summary>从文件名解析日期；不符合 <c>&lt;前缀&gt;-yyyyMMdd.log</c> 时返回 <see langword="false"/>。</summary>
    public static bool TryParseDate(string fileName, string prefix, out DateOnly date)
    {
        date = default;
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var head = prefix + "-";
        if (!fileName.StartsWith(head, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var middle = fileName.AsSpan(head.Length, fileName.Length - head.Length - Extension.Length);
        return DateOnly.TryParseExact(middle, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
