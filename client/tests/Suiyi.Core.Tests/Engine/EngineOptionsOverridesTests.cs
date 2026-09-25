using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public class EngineOptionsOverridesTests
{
    [Fact]
    public void NoVariables_KeepsDefaults()
    {
        var options = EngineOptionsOverrides.Apply(new EngineOptions(), _ => null);

        Assert.Equal(new EngineOptions().Port, options.Port);
        Assert.Equal(EngineOptions.DefaultPreload, options.Preload);
        Assert.Null(options.PythonPath);
    }

    [Fact]
    public void Variables_Override()
    {
        var env = new Dictionary<string, string>
        {
            [EngineOptionsOverrides.PortVariable] = "18790",
            [EngineOptionsOverrides.PreloadVariable] = "fr-de",
            [EngineOptionsOverrides.PythonVariable] = @"C:\py\python.exe",
            [EngineOptionsOverrides.ModelsDirVariable] = @"D:\models",
            [EngineOptionsOverrides.CommandVariable] = "suiyi-engine.exe",
        };

        var options = EngineOptionsOverrides.Apply(new EngineOptions(), env.GetValueOrDefault);

        Assert.Equal(18790, options.Port);
        Assert.Equal("fr-de", options.Preload);
        Assert.Equal(@"C:\py\python.exe", options.PythonPath);
        Assert.Equal(@"D:\models", options.ModelsDir);
        Assert.Equal("suiyi-engine.exe", options.Command);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    public void InvalidPort_Ignored(string port)
    {
        var options = EngineOptionsOverrides.Apply(new EngineOptions(), name => name == EngineOptionsOverrides.PortVariable ? port : null);

        Assert.Equal(EngineClient.DefaultPort, options.Port);
    }

    [Fact]
    public void EmptyPreload_DisablesPreload()
    {
        var options = EngineOptionsOverrides.Apply(new EngineOptions(), name => name == EngineOptionsOverrides.PreloadVariable ? "" : null);

        Assert.Equal("", options.Preload);
    }
}
