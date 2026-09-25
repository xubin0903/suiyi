namespace Suiyi.Core.Engine;

/// <summary>
/// 翻译服务进程的配置，由集成层从设置（<c>engine.*</c>）映射而来。
/// </summary>
public sealed record EngineOptions
{
    /// <summary>默认预热语向（性能基线建议托盘启动时预加载）。</summary>
    public const string DefaultPreload = "zh-en,en-zh";

    /// <summary>服务端口（<c>engine.port</c>），默认 <see cref="EngineClient.DefaultPort"/>。</summary>
    public int Port { get; init; } = EngineClient.DefaultPort;

    /// <summary>
    /// 自定义启动命令（<c>engine.command</c>），为 M4 的打包 exe 预留。设置后优先级最高，
    /// 实际参数为 <see cref="Args"/> 后接 <c>serve --port … --preload … [--models-dir …]</c>。
    /// </summary>
    public string? Command { get; init; }

    /// <summary>与 <see cref="Command"/> 一起使用的前置参数（<c>engine.args</c>）。</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>指定 Python 解释器路径（<c>engine.pythonPath</c>）。</summary>
    public string? PythonPath { get; init; }

    /// <summary>启动时预热的语向（<c>engine.preload</c>），逗号分隔；空表示不预热。</summary>
    public string Preload { get; init; } = DefaultPreload;

    /// <summary>启动时预热 OCR 模型（<c>engine.preloadOcr</c>），为 true（默认）时追加 <c>--preload-ocr</c>。</summary>
    public bool PreloadOcr { get; init; } = true;

    /// <summary>模型目录（<c>engine.modelsDir</c>）；为空时由服务使用默认目录。</summary>
    public string? ModelsDir { get; init; }

    /// <summary>启动期间探测 <c>/health</c> 的间隔。</summary>
    public TimeSpan StartupProbeInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>从拉起进程到 <c>/health</c> 返回 ok 的上限，超过则结束进程并失败。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>就绪后看门狗探测 <c>/health</c> 的间隔。</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>看门狗连续失败多少次后结束进程并按崩溃处理。</summary>
    public int WatchdogFailureThreshold { get; init; } = 3;

    /// <summary>崩溃后第 1、2、3… 次重启前的等待；次数超出列表时用最后一项。</summary>
    public IReadOnlyList<TimeSpan> RestartBackoff { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>统计崩溃次数的滑动窗口。</summary>
    public TimeSpan CrashWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary><see cref="CrashWindow"/> 内允许自动重启的崩溃次数；超过即失败。</summary>
    public int MaxCrashesInWindow { get; init; } = 3;

    /// <summary><see cref="EngineSupervisor.StopAsync"/> 结束进程后等待其退出的上限。</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>内存里保留的服务输出行数（用于失败提示）。</summary>
    public int OutputTailLines { get; init; } = 50;
}
