using System.Globalization;

namespace Suiyi.Core.Engine;

/// <summary><see cref="EngineCommandResolver"/> 依赖的环境，测试时可替换。</summary>
public sealed record EngineCommandEnvironment
{
    /// <summary>客户端可执行文件所在目录（<see cref="AppContext.BaseDirectory"/>）。</summary>
    public required string BaseDirectory { get; init; }

    /// <summary>是否 Windows（决定 venv 布局与 <c>py</c> 启动器）。</summary>
    public required bool IsWindows { get; init; }

    /// <summary>判断文件是否存在。</summary>
    public required Func<string, bool> FileExists { get; init; }

    /// <summary>环境变量 <c>PATH</c> 的值。</summary>
    public string? PathVariable { get; init; }

    /// <summary>按当前进程创建。</summary>
    public static EngineCommandEnvironment Current() => new()
    {
        BaseDirectory = AppContext.BaseDirectory,
        IsWindows = OperatingSystem.IsWindows(),
        FileExists = File.Exists,
        PathVariable = Environment.GetEnvironmentVariable("PATH"),
    };
}

/// <summary>
/// 解析启动翻译服务的命令（纯函数）。优先级：
/// <list type="number">
/// <item><c>engine.command</c> + <c>engine.args</c>（M4 打包 exe）</item>
/// <item><c>engine.pythonPath</c></item>
/// <item>从 <see cref="EngineCommandEnvironment.BaseDirectory"/> 向上找含 <c>engine/pyproject.toml</c> 的仓库根，用 <c>&lt;仓库根&gt;\.venv\Scripts\python.exe</c></item>
/// <item>Windows 上 PATH 里有 <c>py.exe</c> 时用 <c>py -3.11</c>，否则 PATH 上的 <c>python</c></item>
/// </list>
/// Python 的参数为 <c>-m suiyi_engine serve --port &lt;port&gt; --preload &lt;preload&gt; [--models-dir &lt;dir&gt;]</c>。
/// </summary>
public static class EngineCommandResolver
{
    /// <summary>Windows Python Launcher 选择的版本参数。</summary>
    public const string LauncherVersionArgument = "-3.11";

    /// <summary>解析命令。</summary>
    /// <param name="options">服务配置。</param>
    /// <param name="environment">运行环境。</param>
    public static EngineCommand Resolve(EngineOptions options, EngineCommandEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var repoRoot = FindRepositoryRoot(environment.BaseDirectory, environment.FileExists);
        var workingDirectory = repoRoot ?? environment.BaseDirectory;
        var serve = ServeArguments(options);

        if (!string.IsNullOrWhiteSpace(options.Command))
        {
            return new EngineCommand
            {
                FileName = options.Command.Trim(),
                Arguments = [.. options.Args, .. serve],
                WorkingDirectory = workingDirectory,
                Source = EngineCommandSource.ConfiguredCommand,
            };
        }

        string[] module = ["-m", "suiyi_engine", .. serve];
        if (!string.IsNullOrWhiteSpace(options.PythonPath))
        {
            return Python(options.PythonPath.Trim(), module, EngineCommandSource.ConfiguredPython);
        }

        if (repoRoot is not null)
        {
            var venvPython = environment.IsWindows
                ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
                : Path.Combine(repoRoot, ".venv", "bin", "python");
            if (environment.FileExists(venvPython))
            {
                return Python(venvPython, module, EngineCommandSource.RepositoryVenv);
            }
        }

        if (environment.IsWindows && FindOnPath("py.exe", environment) is { } launcher)
        {
            return Python(launcher, [LauncherVersionArgument, .. module], EngineCommandSource.PythonLauncher);
        }

        return Python(environment.IsWindows ? "python.exe" : "python", module, EngineCommandSource.PathPython);

        EngineCommand Python(string fileName, IReadOnlyList<string> arguments, EngineCommandSource source) => new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Source = source,
        };
    }

    /// <summary>启动时预热 OCR 模型的服务参数（#53 草案）。</summary>
    public const string PreloadOcrArgument = "--preload-ocr";

    /// <summary><c>serve</c> 子命令及其参数。</summary>
    /// <param name="options">服务配置。</param>
    public static IReadOnlyList<string> ServeArguments(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string> { "serve", "--port", options.Port.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(options.Preload))
        {
            args.Add("--preload");
            args.Add(options.Preload.Trim());
        }

        if (options.PreloadOcr)
        {
            args.Add(PreloadOcrArgument);
        }

        if (!string.IsNullOrWhiteSpace(options.ModelsDir))
        {
            args.Add("--models-dir");
            args.Add(options.ModelsDir.Trim());
        }

        return args;
    }

    /// <summary>从 <paramref name="startDirectory"/> 向上查找含 <c>engine/pyproject.toml</c> 的目录。</summary>
    /// <param name="startDirectory">起始目录。</param>
    /// <param name="fileExists">判断文件是否存在。</param>
    /// <returns>仓库根；找不到时为 <see langword="null"/>。</returns>
    public static string? FindRepositoryRoot(string startDirectory, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            if (fileExists(Path.Combine(dir.FullName, "engine", "pyproject.toml")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName, EngineCommandEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(environment.PathVariable))
        {
            return null;
        }

        var separator = environment.IsWindows ? ';' : ':';
        foreach (var entry in environment.PathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(entry.Trim('"'), fileName);
            if (environment.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
