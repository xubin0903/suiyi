namespace Suiyi.Core.Settings;

/// <summary>
/// 设置文件位置：默认 <c>%APPDATA%\suiyi\settings.json</c>（漫游配置），可用环境变量 <c>SUIYI_CONFIG_DIR</c> 覆盖目录。
/// </summary>
public static class SettingsPaths
{
    /// <summary>覆盖设置目录的环境变量名。</summary>
    public const string DirectoryOverrideVariable = "SUIYI_CONFIG_DIR";

    /// <summary>设置文件名。</summary>
    public const string FileName = "settings.json";

    /// <summary>按当前进程环境解析设置目录。</summary>
    public static string ResolveDirectory() =>
        ResolveDirectory(
            Environment.GetEnvironmentVariable(DirectoryOverrideVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>解析设置目录（纯函数）。</summary>
    /// <param name="overrideDirectory"><c>SUIYI_CONFIG_DIR</c> 的值；为空或全空白时忽略。</param>
    /// <param name="appData"><c>%APPDATA%</c> 路径。</param>
    public static string ResolveDirectory(string? overrideDirectory, string appData)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory.Trim());
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(appData);
        return Path.GetFullPath(Path.Combine(appData, "suiyi"));
    }

    /// <summary>按当前进程环境解析设置文件完整路径。</summary>
    public static string ResolveFile() => Path.Combine(ResolveDirectory(), FileName);
}
