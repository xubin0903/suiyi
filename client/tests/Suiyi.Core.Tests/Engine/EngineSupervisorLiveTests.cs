using System.Diagnostics;
using Suiyi.Core.Engine;
using Xunit.Abstractions;

namespace Suiyi.Core.Tests.Engine;

/// <summary>
/// 用真实 Python 拉起服务的监管联调。默认跳过；设置 <c>SUIYI_ENGINE_PYTHON</c>（装了 suiyi_engine 的解释器）
/// 与可选的 <c>SUIYI_ENGINE_MODELS_DIR</c> 后运行：<c>dotnet test client/Suiyi.sln --filter Category=Engine</c>。
/// </summary>
[Trait("Category", "Engine")]
public sealed class EngineSupervisorLiveTests(ITestOutputHelper output)
{
    private const int Port = 18794;

    private static string? Python => Environment.GetEnvironmentVariable(EngineOptionsOverrides.PythonVariable);

    private (EngineSupervisor Supervisor, EngineClient Client, List<string> States) Create(string preload)
    {
        var options = new EngineOptions
        {
            Port = Port,
            PythonPath = Python,
            Preload = preload,
            ModelsDir = Environment.GetEnvironmentVariable(EngineOptionsOverrides.ModelsDirVariable),
        };
        var client = new EngineClient(Port);
        var supervisor = new EngineSupervisor(options, new ProcessEngineLauncher(), new EngineClientEndpoint(client));
        var states = new List<string>();
        var started = Stopwatch.StartNew();
        supervisor.StateChanged += (_, e) =>
        {
            var line = $"[{started.ElapsedMilliseconds,6} ms] {e}";
            lock (states)
            {
                states.Add(line);
            }

            output.WriteLine(line);
        };
        return (supervisor, client, states);
    }

    private static async Task<EngineStateChangedEventArgs> WaitFor(EngineSupervisor supervisor, EngineState state, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<EngineStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.StateChanged += (_, e) =>
        {
            if (e.State == state)
            {
                tcs.TrySetResult(e);
            }
        };
        return await tcs.Task.WaitAsync(timeout);
    }

    [SupervisorLiveFact]
    public async Task StartTranslateCrashRestartStop()
    {
        var (supervisor, client, _) = Create(EngineOptions.DefaultPreload);
        await using var _s = supervisor;
        using var _c = client;

        var ready = WaitFor(supervisor, EngineState.Ready, TimeSpan.FromSeconds(40));
        supervisor.Start();
        Assert.Equal(EngineOwnership.Managed, (await ready).Ownership);
        output.WriteLine($"command: {supervisor.LastCommand}");

        var result = await client.TranslateAsync("今天天气很好。", "zh", "en");
        output.WriteLine($"translate: {result.Text}");

        var pid = supervisor.ProcessId!.Value;
        var restarted = WaitFor(supervisor, EngineState.Ready, TimeSpan.FromSeconds(40));
        Process.GetProcessById(pid).Kill();
        await restarted;
        Assert.NotEqual(pid, supervisor.ProcessId);

        var newPid = supervisor.ProcessId!.Value;
        await supervisor.StopAsync();
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(newPid));
        output.WriteLine($"stopped, pid {newPid} gone");
    }

    [SupervisorLiveFact]
    public async Task MissingPreloadModel_FailsWithHint()
    {
        var (supervisor, client, _) = Create("fr-de");
        await using var _s = supervisor;
        using var _c = client;

        var failed = WaitFor(supervisor, EngineState.Failed, TimeSpan.FromSeconds(40));
        supervisor.Start();
        var e = await failed;

        Assert.Equal(EngineFailureReason.ExitedBeforeReady, e.Failure!.Reason);
        Assert.StartsWith("缺少模型：", e.Failure.Message, StringComparison.Ordinal);
    }
}

/// <summary>未设置 <c>SUIYI_ENGINE_PYTHON</c> 时跳过。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SupervisorLiveFactAttribute : FactAttribute
{
    public SupervisorLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EngineOptionsOverrides.PythonVariable)))
        {
            Skip = $"需要设置 {EngineOptionsOverrides.PythonVariable}（已安装 suiyi_engine 的 Python）";
        }
    }
}
