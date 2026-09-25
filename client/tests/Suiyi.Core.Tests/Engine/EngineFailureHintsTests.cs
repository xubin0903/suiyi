using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public class EngineFailureHintsTests
{
    [Fact]
    public void ModuleMissing()
    {
        Assert.Equal(
            EngineFailureHints.ModuleNotFound,
            EngineFailureHints.ForEarlyExit(["C:\\Python311\\python.exe: No module named suiyi_engine"], 18780, 1));
    }

    [Fact]
    public void MissingModel_ListsIds()
    {
        Assert.Equal(
            "缺少模型：opus-mt-zh-en，请按 docs/engine/模型目录约定.md 转换",
            EngineFailureHints.ForEarlyExit(["INFO: x", "不支持的语向 zh→en，未下载模型：opus-mt-zh-en"], 18780, 1));
    }

    [Fact]
    public void PairNotInManifest()
    {
        Assert.Equal(
            "不支持的语向 ko→zh：模型清单里没有对应模型，请检查 engine.preload",
            EngineFailureHints.ForEarlyExit(["不支持的语向 ko→zh，未下载模型：（清单未定义该语向的模型 id）"], 18780, 1));
    }

    [Theory]
    [InlineData("端口 18790 已被占用或无法在 127.0.0.1 上监听：[WinError 10048] 通常每个套接字地址只允许使用一次")]
    [InlineData("OSError: [Errno 98] Address already in use")]
    public void PortInUse_UsesConfiguredPort(string line)
    {
        Assert.True(EngineFailureHints.IsPortInUse([line]));
        Assert.Equal("端口 18790 被其他程序占用，请在设置中修改 engine.port", EngineFailureHints.ForEarlyExit([line], 18790, 1));
    }

    [Fact]
    public void Unknown_ShowsLastLineAndExitCode()
    {
        Assert.Equal(
            "翻译服务启动失败（退出码 2）：ValueError: boom",
            EngineFailureHints.ForEarlyExit(["Traceback", "ValueError: boom", "  "], 18780, 2));
        Assert.Equal("翻译服务启动失败", EngineFailureHints.ForEarlyExit([], 18780, null));
    }

    [Theory]
    [InlineData(EngineCommandSource.ConfiguredCommand, "engine.command")]
    [InlineData(EngineCommandSource.ConfiguredPython, "engine.pythonPath")]
    [InlineData(EngineCommandSource.RepositoryVenv, "未找到 Python")]
    [InlineData(EngineCommandSource.PathPython, "未找到 Python")]
    public void LaunchFailure_PointsAtTheRightSetting(EngineCommandSource source, string expected)
    {
        var command = new EngineCommand { FileName = "x", Arguments = [], WorkingDirectory = ".", Source = source };

        Assert.Contains(expected, EngineFailureHints.ForLaunchFailure(command), StringComparison.Ordinal);
    }
}
