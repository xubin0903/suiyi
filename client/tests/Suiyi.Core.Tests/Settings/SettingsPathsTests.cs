using Suiyi.Core.Settings;

namespace Suiyi.Core.Tests.Settings;

public sealed class SettingsPathsTests
{
    [Fact]
    public void Default_UnderAppDataSuiyi()
    {
        var appData = Path.Combine(Path.GetTempPath(), "AppData", "Roaming");

        Assert.Equal(Path.GetFullPath(Path.Combine(appData, "suiyi")), SettingsPaths.ResolveDirectory(null, appData));
    }

    [Fact]
    public void Override_Wins()
    {
        var custom = Path.Combine(Path.GetTempPath(), "portable");

        Assert.Equal(Path.GetFullPath(custom), SettingsPaths.ResolveDirectory($"  {custom} ", "/ignored"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverride_Ignored(string value)
    {
        var appData = Path.GetTempPath();

        Assert.Equal(Path.GetFullPath(Path.Combine(appData, "suiyi")), SettingsPaths.ResolveDirectory(value, appData));
    }

    [Fact]
    public void BlankAppDataWithoutOverride_Throws()
    {
        Assert.Throws<ArgumentException>(() => SettingsPaths.ResolveDirectory(null, " "));
    }

    [Fact]
    public void Constants()
    {
        Assert.Equal("SUIYI_CONFIG_DIR", SettingsPaths.DirectoryOverrideVariable);
        Assert.Equal("settings.json", SettingsPaths.FileName);
        Assert.Equal(SettingsPaths.FileName, Path.GetFileName(SettingsPaths.ResolveFile()));
    }
}
