using System.Globalization;

namespace Suiyi.Core.Capture;

/// <summary>
/// 临时演示入口 <c>--region-demo</c>：框选完成后把 PNG 保存到临时目录供手测比对。正式流程不保存任何截图。
/// </summary>
public static class RegionCaptureDemo
{
    /// <summary>命令行开关。</summary>
    public const string Switch = "--region-demo";

    /// <summary>临时目录下的子目录名。</summary>
    public const string DirectoryName = "suiyi-region-demo";

    /// <summary>保存目录：<c>%TEMP%\suiyi-region-demo</c>。</summary>
    public static string ResolveDirectory(string tempPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        return Path.Combine(tempPath, DirectoryName);
    }

    /// <summary>文件名：<c>region-yyyyMMdd-HHmmss-fff-宽x高.png</c>（本地时间）。</summary>
    public static string BuildFileName(DateTimeOffset localTime, PixelRect bounds) =>
        string.Create(CultureInfo.InvariantCulture, $"region-{localTime:yyyyMMdd-HHmmss-fff}-{bounds.Width}x{bounds.Height}.png");
}
