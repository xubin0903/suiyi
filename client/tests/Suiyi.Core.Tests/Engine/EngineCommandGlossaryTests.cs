using Suiyi.Core.Engine;
using Suiyi.Core.Glossary;

namespace Suiyi.Core.Tests.Engine;

/// <summary>启动服务时传递术语保护设置（#83 约定：环境变量 SUIYI_GLOSSARY / SUIYI_USER_GLOSSARY）。</summary>
public sealed class EngineCommandGlossaryTests
{
    private static readonly string UserPath = Path.Combine(Path.GetTempPath(), "suiyi", "glossary.tsv");

    private static EngineCommandEnvironment Env() => new()
    {
        BaseDirectory = Path.GetTempPath(),
        IsWindows = true,
        FileExists = _ => false,
        PathVariable = null,
    };

    [Fact]
    public void Default_UsesEnvironmentVariables()
    {
        Assert.Equal(GlossaryStartupTransport.EnvironmentVariables, GlossaryContract.StartupTransport);
        Assert.Equal(GlossaryStartupTransport.EnvironmentVariables, new EngineOptions().GlossaryTransport);
        Assert.True(new EngineOptions().Glossary);
        Assert.Equal("SUIYI_GLOSSARY", GlossaryContract.EnabledVariable);
        Assert.Equal("SUIYI_USER_GLOSSARY", GlossaryContract.UserGlossaryVariable);
    }

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public void ServeEnvironment_WritesSwitchAndFullPath(bool enabled, string expected)
    {
        var env = EngineCommandResolver.ServeEnvironment(new EngineOptions { Glossary = enabled, UserGlossaryPath = UserPath });

        Assert.Equal(expected, env["SUIYI_GLOSSARY"]);
        Assert.Equal(Path.GetFullPath(UserPath), env["SUIYI_USER_GLOSSARY"]);
        Assert.Equal(2, env.Count);
    }

    [Fact]
    public void ServeEnvironment_NoPath_OnlySwitch()
    {
        var env = EngineCommandResolver.ServeEnvironment(new EngineOptions { Glossary = false });

        Assert.Equal("0", Assert.Single(env).Value);
    }

    [Fact]
    public void ServeArguments_Default_HasNoGlossaryArguments()
    {
        var args = EngineCommandResolver.ServeArguments(new EngineOptions { Glossary = false, UserGlossaryPath = UserPath });

        Assert.DoesNotContain(args, a => a.Contains("glossary", StringComparison.Ordinal));
    }

    [Fact]
    public void ArgumentsTransport_AppendsArgumentsAndNoEnvironment()
    {
        var options = new EngineOptions { Glossary = false, UserGlossaryPath = UserPath, GlossaryTransport = GlossaryStartupTransport.Arguments };

        Assert.Equal(["--no-glossary", "--user-glossary", UserPath], EngineCommandResolver.GlossaryArguments(options));
        Assert.Equal(["--glossary"], EngineCommandResolver.GlossaryArguments(new EngineOptions()));
        Assert.Equal(["--no-glossary", "--user-glossary", UserPath], EngineCommandResolver.ServeArguments(options).TakeLast(3));
        Assert.Empty(EngineCommandResolver.ServeEnvironment(options));
    }

    [Fact]
    public void Resolve_ConfiguredCommand_CarriesEnvironment()
    {
        var command = EngineCommandResolver.Resolve(
            new EngineOptions { Command = @"C:\suiyi\suiyi-engine.exe", Glossary = false, UserGlossaryPath = UserPath },
            Env());

        Assert.Equal("0", command.Environment["SUIYI_GLOSSARY"]);
        Assert.Equal(Path.GetFullPath(UserPath), command.Environment["SUIYI_USER_GLOSSARY"]);
        Assert.DoesNotContain(command.Arguments, a => a.Contains("glossary", StringComparison.Ordinal));
        Assert.Contains("SUIYI_GLOSSARY=0", command.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_Fallback_CarriesEnvironment()
    {
        var command = EngineCommandResolver.Resolve(new EngineOptions { UserGlossaryPath = UserPath }, Env());

        Assert.Equal("1", command.Environment["SUIYI_GLOSSARY"]);
    }
}
