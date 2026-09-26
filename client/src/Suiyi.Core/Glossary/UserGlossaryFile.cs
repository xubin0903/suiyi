using System.Text;
using Suiyi.Core.Settings;

namespace Suiyi.Core.Glossary;

/// <summary>「编辑我的术语表」：用户术语表路径与模板（格式见 #83 约定第 2 节）。</summary>
public static class UserGlossaryFile
{
    /// <summary>新建术语表的模板（UTF-8 无 BOM，LF）。只有注释与被注释掉的示例，创建后不改变翻译结果。</summary>
    public const string Template =
        "# 随译 · 我的术语表\n"
        + "#\n"
        + "# 每行一条，列之间用 Tab 分隔：源词<Tab>目标词<Tab>方向（可选）\n"
        + "#   方向只能写 en-zh 或 zh-en；不写时双向生效（含中文的一列当中文，另一列当英文）。\n"
        + "#   两列都不含中文时，第 1 列当英文原文，第 2 列当中文译文里的写法；两列相同就是「保持原样、不要翻译」。\n"
        + "#   只对中文和英文之间的翻译生效。\n"
        + "#   以 # 开头的行是注释，空行忽略；行内不支持注释（术语本身可能含 #，如 C#）。\n"
        + "#   英文源词不区分大小写（全大写缩写如 API 除外），常见复数形式（-s、-es、-ies）自动匹配。\n"
        + "#   同一个词我的术语优先于内置术语。文件用 UTF-8 保存，不超过 1 MiB、5000 条。\n"
        + "#   有问题的行会被跳过，原因显示在托盘「专业术语」菜单里。\n"
        + "#\n"
        + "# 保存后约 1 秒内自动生效（下一次翻译时），也可以在托盘「专业术语 ▸ 重新加载术语表」立即生效。\n"
        + "#\n"
        + "# 示例（删掉行首的 # 才会生效）：\n"
        + "# container orchestration\t容器编排\n"
        + "# Kubernetes\tKubernetes\n"
        + "# 预发布环境\tstaging environment\tzh-en\n";

    /// <summary>
    /// 用户术语表路径：<c>&lt;设置目录&gt;\glossary.tsv</c>（默认 <c>%APPDATA%\suiyi\glossary.tsv</c>，设置目录可被 <c>SUIYI_CONFIG_DIR</c> 覆盖）。
    /// 客户端启动服务时把同一路径交给服务（<c>SUIYI_USER_GLOSSARY</c>），两边一定一致。
    /// </summary>
    /// <param name="settingsDirectory">设置目录（<see cref="SettingsPaths.ResolveDirectory()"/>）。</param>
    public static string ResolvePath(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        return Path.GetFullPath(Path.Combine(settingsDirectory, GlossaryContract.UserFileName));
    }

    /// <summary>文件不存在时按 <see cref="Template"/> 创建（连同目录）；已存在时不动它。</summary>
    /// <param name="path">术语表路径。</param>
    /// <returns>是否新建了文件。</returns>
    /// <exception cref="IOException">无法创建。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    public static bool EnsureExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            return false;
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Template);
        AtomicFile.Write(path, stream => stream.Write(bytes));
        return true;
    }

    /// <summary>
    /// 服务报告的路径（<c>glossary_user_path</c>）与客户端交给它的路径不同（例如复用了别处启动的服务）时返回 <see langword="true"/>，
    /// 此时编辑的文件对该服务不生效，需要提示。服务没报告路径时返回 <see langword="false"/>。
    /// </summary>
    /// <param name="clientPath">客户端的路径。</param>
    /// <param name="reportedPath">服务报告的路径。</param>
    public static bool IsMismatch(string clientPath, string? reportedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);
        if (string.IsNullOrWhiteSpace(reportedPath))
        {
            return false;
        }

        string reported;
        try
        {
            reported = Path.GetFullPath(reportedPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        // Windows 路径不区分大小写；服务端可能报告 %APPDATA%\Suiyi 这样的大小写写法。
        return !string.Equals(Path.GetFullPath(clientPath), reported, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
