using System.Text;
using Suiyi.Core.Engine;
using Suiyi.Core.Glossary;
using Xunit.Abstractions;

namespace Suiyi.Core.Tests.Engine;

/// <summary>
/// 术语保护（#84）与真实引擎（#83）的联调：用 <see cref="EngineSupervisor"/> 拉起服务，验证环境变量
/// <c>SUIYI_GLOSSARY</c> / <c>SUIYI_USER_GLOSSARY</c> 生效、<c>/health</c> 的 glossary_* 字段、
/// <c>POST /glossary/reload</c>，以及 <c>/translate</c> 接受 <c>glossary</c> 字段、<c>/ocr_translate</c> 接受 <c>glossary</c> query。
/// 不需要翻译模型（没有模型时翻译返回 unsupported_pair，说明字段已被接受）。默认跳过，设置 <c>SUIYI_ENGINE_PYTHON</c> 后运行。
/// </summary>
[Trait("Category", "Engine")]
public sealed class EngineGlossaryLiveTests(ITestOutputHelper output) : IDisposable
{
    private const int Port = 18795;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "suiyi-glossary-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [SupervisorLiveFact]
    public Task GlossaryOn() => Run(enabled: true);

    [SupervisorLiveFact]
    public Task GlossaryOff() => Run(enabled: false);

    private async Task Run(bool enabled)
    {
        Directory.CreateDirectory(_dir);
        var path = UserGlossaryFile.ResolvePath(_dir);
        Assert.True(UserGlossaryFile.EnsureExists(path));
        var options = new EngineOptions
        {
            Port = Port,
            PythonPath = Environment.GetEnvironmentVariable(EngineOptionsOverrides.PythonVariable),
            ModelsDir = Environment.GetEnvironmentVariable(EngineOptionsOverrides.ModelsDirVariable) ?? _dir,
            Preload = string.Empty,
            PreloadOcr = false,
            Glossary = enabled,
            UserGlossaryPath = path,
        };
        using var client = new EngineClient(Port) { GlossaryOverride = () => enabled };
        await using var supervisor = new EngineSupervisor(options, new ProcessEngineLauncher(), new EngineClientEndpoint(client));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.StateChanged += (_, e) =>
        {
            output.WriteLine(e.ToString());
            if (e.State == EngineState.Ready)
            {
                ready.TrySetResult();
            }
        };
        supervisor.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"command: {supervisor.LastCommand}");

        // 模板全是注释：服务端默认开关跟随 SUIYI_GLOSSARY，用户表路径跟随 SUIYI_USER_GLOSSARY，0 条、无错误。
        await client.GetHealthAsync();
        var status = client.KnownGlossaryStatus;
        Assert.NotNull(status);
        output.WriteLine($"health: {status}");
        Assert.True(client.GlossarySupported);
        Assert.Equal(enabled, status.ServerEnabled);
        Assert.False(UserGlossaryFile.IsMismatch(path, status.UserPath));
        Assert.True(status.BuiltinEntries > 0);
        Assert.Equal(0, status.UserEntries);
        Assert.Null(status.Error);
        Assert.Empty(status.Warnings);

        // 两列都不含中文的行有效（双向 2 条），缺目标词的行被跳过。
        File.WriteAllText(path, UserGlossaryFile.Template + "k8s\tKubernetes\n只有一列\n", new UTF8Encoding(false));
        var reloaded = await client.ReloadGlossaryAsync();
        output.WriteLine($"reload: {reloaded}");
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded.UserEntries);
        Assert.Single(reloaded.Warnings);

        // 文件级错误：不是 UTF-8。
        File.WriteAllBytes(path, [0xB2, 0xE2, 0xCA, 0xD4, 0x09, 0x41, 0x0A]);
        var broken = await client.ReloadGlossaryAsync();
        output.WriteLine($"broken: {broken}");
        Assert.NotNull(broken);
        Assert.True(broken.HasError);
        Assert.Equal(0, broken.UserEntries);

        // /translate 带 glossary：有模型时正常返回；没有模型时是 unsupported_pair（不是 invalid_request），说明字段被接受。
        try
        {
            var result = await client.TranslateAsync("Kubernetes is an open-source container orchestration engine.", "en", "zh");
            output.WriteLine($"translate: {result.Text}");
        }
        catch (EngineException ex) when (ex.Kind == EngineErrorKind.UnsupportedPair)
        {
            output.WriteLine($"translate: {ex.ErrorCode} {ex.Message}");
        }

        // /ocr_translate 带 glossary query（#88）：#87 之前的引擎忽略它；没有 OCR 模型时是 ocr_unavailable（不是 invalid_request）。
        try
        {
            var ocr = await client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");
            output.WriteLine($"ocr_translate: {ocr.Text}");
        }
        catch (EngineException ex) when (ex.Kind is EngineErrorKind.OcrUnavailable or EngineErrorKind.InvalidImage)
        {
            output.WriteLine($"ocr_translate: {ex.ErrorCode} {ex.Message}");
        }

        await supervisor.StopAsync();
    }
}
