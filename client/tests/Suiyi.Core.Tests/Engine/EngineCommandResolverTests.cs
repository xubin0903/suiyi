using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public class EngineCommandResolverTests
{
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "suiyi-repo");
    private static readonly string BinDir = Path.Combine(Repo, "client", "src", "Suiyi.App", "bin", "Debug", "net8.0-windows");
    private static readonly string Pyproject = Path.Combine(Repo, "engine", "pyproject.toml");
    private static readonly string WinVenv = Path.Combine(Repo, ".venv", "Scripts", "python.exe");
    private static readonly string PosixVenv = Path.Combine(Repo, ".venv", "bin", "python");
    private static readonly string PyDir = Path.Combine(Path.GetTempPath(), "Windows");
    private static readonly string PyExe = Path.Combine(PyDir, "py.exe");

    private static EngineCommandEnvironment Env(bool isWindows = true, string? path = null, params string[] files) => new()
    {
        BaseDirectory = BinDir,
        IsWindows = isWindows,
        FileExists = f => files.Contains(f),
        PathVariable = path,
    };

    private static readonly string[] ServeDefault = ["serve", "--port", "18780", "--preload", "zh-en,en-zh", "--preload-ocr"];

    [Fact]
    public void Level1_ConfiguredCommand_WinsAndAppendsServeArgs()
    {
        var options = new EngineOptions
        {
            Command = @"C:\suiyi\engine\suiyi-engine.exe",
            Args = ["--flag"],
            PythonPath = @"C:\py\python.exe",
        };

        var command = EngineCommandResolver.Resolve(options, Env(files: [Pyproject, WinVenv]));

        Assert.Equal(EngineCommandSource.ConfiguredCommand, command.Source);
        Assert.Equal(@"C:\suiyi\engine\suiyi-engine.exe", command.FileName);
        Assert.Equal(["--flag", .. ServeDefault], command.Arguments);
        Assert.Equal(Repo, command.WorkingDirectory);
    }

    [Fact]
    public void Level2_ConfiguredPython_BeatsVenv()
    {
        var options = new EngineOptions { PythonPath = @"C:\py\python.exe" };

        var command = EngineCommandResolver.Resolve(options, Env(files: [Pyproject, WinVenv]));

        Assert.Equal(EngineCommandSource.ConfiguredPython, command.Source);
        Assert.Equal(@"C:\py\python.exe", command.FileName);
        Assert.Equal(["-m", "suiyi_engine", .. ServeDefault], command.Arguments);
    }

    [Fact]
    public void Level3_RepositoryVenv_FoundByWalkingUp()
    {
        var command = EngineCommandResolver.Resolve(new EngineOptions(), Env(path: PyDir, files: [Pyproject, WinVenv, PyExe]));

        Assert.Equal(EngineCommandSource.RepositoryVenv, command.Source);
        Assert.Equal(WinVenv, command.FileName);
        Assert.Equal(Repo, command.WorkingDirectory);
        Assert.Equal(["-m", "suiyi_engine", .. ServeDefault], command.Arguments);
    }

    [Fact]
    public void Level3_NonWindows_UsesBinPython()
    {
        var command = EngineCommandResolver.Resolve(new EngineOptions(), Env(isWindows: false, files: [Pyproject, PosixVenv]));

        Assert.Equal(PosixVenv, command.FileName);
    }

    [Fact]
    public void Level4_RepoWithoutVenv_UsesPyLauncher311()
    {
        var command = EngineCommandResolver.Resolve(
            new EngineOptions(),
            Env(path: string.Join(';', "", "\"" + PyDir + "\""), files: [Pyproject, PyExe]));

        Assert.Equal(EngineCommandSource.PythonLauncher, command.Source);
        Assert.Equal(PyExe, command.FileName);
        Assert.Equal(["-3.11", "-m", "suiyi_engine", .. ServeDefault], command.Arguments);
        Assert.Equal(Repo, command.WorkingDirectory);
    }

    [Fact]
    public void Level4_NoLauncher_FallsBackToPathPython_InBaseDirectory()
    {
        var command = EngineCommandResolver.Resolve(new EngineOptions(), Env(path: PyDir));

        Assert.Equal(EngineCommandSource.PathPython, command.Source);
        Assert.Equal("python.exe", command.FileName);
        Assert.Equal(BinDir, command.WorkingDirectory);
    }

    [Fact]
    public void Level4_NonWindows_IgnoresPyLauncher()
    {
        var command = EngineCommandResolver.Resolve(new EngineOptions(), Env(isWindows: false, path: PyDir, files: [PyExe]));

        Assert.Equal(EngineCommandSource.PathPython, command.Source);
        Assert.Equal("python", command.FileName);
    }

    [Fact]
    public void ServeArguments_IncludePortModelsDirAndOmitEmptyPreload()
    {
        var args = EngineCommandResolver.ServeArguments(new EngineOptions { Port = 18999, Preload = " ", ModelsDir = @"D:\模型 目录" });

        Assert.Equal(["serve", "--port", "18999", "--preload-ocr", "--models-dir", @"D:\模型 目录"], args);
    }

    [Fact]
    public void ServeArguments_CustomPreload()
    {
        var args = EngineCommandResolver.ServeArguments(new EngineOptions { Preload = "fr-de" });

        Assert.Equal(["serve", "--port", "18780", "--preload", "fr-de", "--preload-ocr"], args);
    }

    [Fact]
    public void ServeArguments_PreloadOcrOnByDefault_OffWhenDisabled()
    {
        Assert.Contains(EngineCommandResolver.PreloadOcrArgument, EngineCommandResolver.ServeArguments(new EngineOptions()));
        Assert.DoesNotContain(EngineCommandResolver.PreloadOcrArgument, EngineCommandResolver.ServeArguments(new EngineOptions { PreloadOcr = false }));
    }

    [Fact]
    public void ServeArguments_PreloadOcr_AppendsFlag()
    {
        var args = EngineCommandResolver.ServeArguments(new EngineOptions { PreloadOcr = true, ModelsDir = "m" });

        Assert.Equal(["serve", "--port", "18780", "--preload", EngineOptions.DefaultPreload, "--preload-ocr", "--models-dir", "m"], args);
    }

    [Fact]
    public void FindRepositoryRoot_NotFound_ReturnsNull()
    {
        Assert.Null(EngineCommandResolver.FindRepositoryRoot(BinDir, _ => false));
    }

    [Fact]
    public void ToString_QuotesArgumentsWithSpaces()
    {
        var command = new EngineCommand
        {
            FileName = @"C:\Program Files\py.exe",
            Arguments = ["-m", "x"],
            WorkingDirectory = ".",
            Source = EngineCommandSource.PathPython,
        };

        Assert.Equal(@"""C:\Program Files\py.exe"" -m x", command.ToString());
    }

    [Fact]
    public void ResolvePathAgainstBase_Relative_JoinsBaseDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "suiyi-pack-base");
        var resolved = EngineCommandResolver.ResolvePathAgainstBase(Path.Combine("engine", "suiyi-engine.exe"), baseDir);

        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "engine", "suiyi-engine.exe")), resolved);
    }

    [Fact]
    public void ResolvePathAgainstBase_WindowsDrive_UnchangedOnAnyOs()
    {
        const string AbsoluteWindows = @"C:\Suiyi\engine\suiyi-engine.exe";
        Assert.Equal(AbsoluteWindows, EngineCommandResolver.ResolvePathAgainstBase(AbsoluteWindows, BinDir));
    }

    [Fact]
    public void Level1_RelativeCommand_ResolvedAgainstBaseDirectory()
    {
        var packRoot = Path.Combine(Path.GetTempPath(), "suiyi-portable-rel");
        var env = new EngineCommandEnvironment
        {
            BaseDirectory = packRoot,
            IsWindows = true,
            FileExists = _ => false,
            PathVariable = null,
        };
        var options = new EngineOptions
        {
            Command = Path.Combine("engine", "suiyi-engine.exe"),
            ModelsDir = "models",
        };

        var command = EngineCommandResolver.Resolve(options, env);

        Assert.Equal(EngineCommandSource.ConfiguredCommand, command.Source);
        Assert.Equal(Path.GetFullPath(Path.Combine(packRoot, "engine", "suiyi-engine.exe")), command.FileName);
        Assert.Equal(packRoot, command.WorkingDirectory);
        Assert.Contains(Path.GetFullPath(Path.Combine(packRoot, "models")), command.Arguments);
    }

    [Fact]
    public void Level1b_BundledExecutable_BeatsPythonWhenPresent()
    {
        var packRoot = Path.Combine(Path.GetTempPath(), "suiyi-portable-bundled");
        var bundled = Path.GetFullPath(Path.Combine(packRoot, "engine", "suiyi-engine.exe"));
        var env = new EngineCommandEnvironment
        {
            BaseDirectory = packRoot,
            IsWindows = true,
            FileExists = f => f == bundled,
            PathVariable = PyDir,
        };

        var command = EngineCommandResolver.Resolve(new EngineOptions { PythonPath = @"C:\py\python.exe" }, env);

        Assert.Equal(EngineCommandSource.BundledExecutable, command.Source);
        Assert.Equal(bundled, command.FileName);
        Assert.Equal(["serve", "--port", "18780", "--preload", "zh-en,en-zh", "--preload-ocr"], command.Arguments);
        Assert.Equal(packRoot, command.WorkingDirectory);
    }

    [Fact]
    public void Level1b_BundledMissing_FallsThroughToConfiguredPython()
    {
        var packRoot = Path.Combine(Path.GetTempPath(), "suiyi-portable-nobundle");
        var env = new EngineCommandEnvironment
        {
            BaseDirectory = packRoot,
            IsWindows = true,
            FileExists = _ => false,
            PathVariable = null,
        };

        var command = EngineCommandResolver.Resolve(new EngineOptions { PythonPath = @"C:\py\python.exe" }, env);

        Assert.Equal(EngineCommandSource.ConfiguredPython, command.Source);
        Assert.Equal(@"C:\py\python.exe", command.FileName);
    }

    [Fact]
    public void BundledEnginePath_MatchesConvention()
    {
        var packRoot = Path.Combine(Path.GetTempPath(), "suiyi-portable-path");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(packRoot, "engine", "suiyi-engine.exe")),
            EngineCommandResolver.BundledEnginePath(packRoot));
        Assert.Equal("engine/suiyi-engine.exe", EngineCommandResolver.BundledEngineRelativePath);
    }

    [Fact]
    public void ConfiguredCommand_BeatsBundledExecutable()
    {
        var packRoot = Path.Combine(Path.GetTempPath(), "suiyi-portable-cfg-wins");
        var bundled = Path.GetFullPath(Path.Combine(packRoot, "engine", "suiyi-engine.exe"));
        var env = new EngineCommandEnvironment
        {
            BaseDirectory = packRoot,
            IsWindows = true,
            FileExists = f => f == bundled,
            PathVariable = null,
        };

        var command = EngineCommandResolver.Resolve(
            new EngineOptions { Command = @"D:\custom\engine.exe" },
            env);

        Assert.Equal(EngineCommandSource.ConfiguredCommand, command.Source);
        Assert.Equal(@"D:\custom\engine.exe", command.FileName);
    }
}
