using System.Globalization;
using System.Text.RegularExpressions;

namespace Suiyi.Core.Engine;

/// <summary>从服务输出尾部提炼一句中文失败提示（纯函数）。</summary>
public static partial class EngineFailureHints
{
    /// <summary>未安装 suiyi_engine 时的提示。</summary>
    public const string ModuleNotFound = "未找到 suiyi_engine：请在仓库根目录创建 .venv 并执行 pip install -e engine";

    /// <summary>服务是否因端口被占用而退出（服务打印「端口 N 已被占用…」）。</summary>
    /// <param name="output">输出尾部。</param>
    public static bool IsPortInUse(IEnumerable<string> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Any(line => line.Contains("已被占用", StringComparison.Ordinal)
            || line.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Errno 98", StringComparison.Ordinal)
            || line.Contains("Errno 10048", StringComparison.Ordinal));
    }

    /// <summary>就绪前退出时的提示。</summary>
    /// <param name="output">输出尾部（stdout 与 stderr 按时间顺序）。</param>
    /// <param name="port">服务端口。</param>
    /// <param name="exitCode">退出码。</param>
    public static string ForEarlyExit(IReadOnlyList<string> output, int port, int? exitCode)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (output.Any(line => line.Contains("No module named", StringComparison.Ordinal)))
        {
            return ModuleNotFound;
        }

        foreach (var line in output.Reverse())
        {
            var match = MissingModelPattern().Match(line);
            if (match.Success)
            {
                var models = match.Groups["models"].Value.Trim();
                return models.StartsWith('（')
                    ? $"不支持的语向 {match.Groups["pair"].Value}：模型清单里没有对应模型，请检查 engine.preload"
                    : $"缺少模型：{models}，请按 docs/engine/模型目录约定.md 转换";
            }
        }

        if (IsPortInUse(output))
        {
            return string.Create(CultureInfo.InvariantCulture, $"端口 {port} 被其他程序占用，请在设置中修改 engine.port");
        }

        var last = output.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
        var code = exitCode is { } c ? string.Create(CultureInfo.InvariantCulture, $"（退出码 {c}）") : string.Empty;
        return last is null ? $"翻译服务启动失败{code}" : $"翻译服务启动失败{code}：{last}";
    }

    /// <summary>无法启动可执行文件时的提示。</summary>
    /// <param name="command">命令。</param>
    public static string ForLaunchFailure(EngineCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Source switch
        {
            EngineCommandSource.ConfiguredCommand => $"无法启动翻译服务：{command.FileName}，请检查设置 engine.command",
            EngineCommandSource.ConfiguredPython => $"无法启动 Python：{command.FileName}，请检查设置 engine.pythonPath",
            _ => "未找到 Python：请在仓库根目录创建 .venv（pip install -e engine），或安装 Python 3.11，或在设置中指定 engine.pythonPath",
        };
    }

    // 服务打印：不支持的语向 fr→de，未下载模型：opus-mt-fr-en、opus-mt-en-de
    [GeneratedRegex(@"不支持的语向\s*(?<pair>\S+?)，未下载模型：(?<models>.+)$")]
    private static partial Regex MissingModelPattern();
}
