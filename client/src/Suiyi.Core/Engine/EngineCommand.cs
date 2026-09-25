namespace Suiyi.Core.Engine;

/// <summary><see cref="EngineCommand"/> 是按哪一级规则找到的。</summary>
public enum EngineCommandSource
{
    /// <summary>设置里的 <c>engine.command</c> + <c>engine.args</c>。</summary>
    ConfiguredCommand,

    /// <summary>设置里的 <c>engine.pythonPath</c>。</summary>
    ConfiguredPython,

    /// <summary>仓库根的 <c>.venv</c>。</summary>
    RepositoryVenv,

    /// <summary>Windows Python Launcher：<c>py -3.11</c>。</summary>
    PythonLauncher,

    /// <summary>PATH 上的 <c>python</c>。</summary>
    PathPython,
}

/// <summary>启动翻译服务的完整命令。参数逐个传给 <c>ProcessStartInfo.ArgumentList</c>，不拼字符串。</summary>
public sealed record EngineCommand
{
    /// <summary>可执行文件（绝对路径，或交给系统在 PATH 里查找的名字）。</summary>
    public required string FileName { get; init; }

    /// <summary>参数列表。</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>工作目录：仓库根，找不到时为客户端可执行文件目录。</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>命中的规则。</summary>
    public required EngineCommandSource Source { get; init; }

    /// <summary>用于日志的单行展示（带引号，仅供阅读，不用于启动）。</summary>
    public override string ToString() =>
        string.Join(' ', new[] { FileName }.Concat(Arguments).Select(Quote));

    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;
}
