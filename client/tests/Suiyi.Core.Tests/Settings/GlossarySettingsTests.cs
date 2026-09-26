using Suiyi.Core.Glossary;
using Suiyi.Core.Settings;

namespace Suiyi.Core.Tests.Settings;

public sealed class GlossarySettingsTests
{
    private readonly List<string> _warnings = [];

    private SettingsParseResult Parse(string json) => SettingsSerializer.Parse(json, _warnings.Add);

    [Fact]
    public void Default_IsEnabled()
    {
        Assert.True(AppSettings.Default.Glossary.Enabled);
        Assert.True(GlossaryContract.DefaultEnabled);
    }

    [Fact]
    public void MissingSection_FallsBackToEnabled()
    {
        var result = Parse("""{ "primaryTarget": "zh" }""");

        Assert.True(result.Settings.Glossary.Enabled);
        Assert.Empty(_warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enabled_RoundTrips(bool enabled)
    {
        var custom = AppSettings.Default with { Glossary = new GlossarySettings { Enabled = enabled } };

        var json = SettingsSerializer.Serialize(custom);
        var result = Parse(json);

        Assert.Equal(custom, result.Settings);
        Assert.Contains($"\"enabled\": {(enabled ? "true" : "false")}", json, StringComparison.Ordinal);
        Assert.Empty(_warnings);
    }

    [Theory]
    [InlineData("""{ "glossary": { "enabled": "no" } }""")]
    [InlineData("""{ "glossary": { "enabled": 0 } }""")]
    [InlineData("""{ "glossary": false }""")]
    public void WrongType_FallsBackToDefaultWithWarning(string json)
    {
        var result = Parse(json);

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.True(result.Settings.Glossary.Enabled);
        Assert.Contains(_warnings, w => w.Contains("glossary", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_ParsesFalse()
    {
        Assert.False(Parse("""{ "glossary": { "enabled": false } }""").Settings.Glossary.Enabled);
    }

    [Fact]
    public void SchemaVersion_Unchanged()
    {
        // 新增节不升 schemaVersion：旧客户端读到新文件时忽略 glossary 节，而不是判为 TooNew。
        Assert.Contains("\"schemaVersion\": 2", SettingsSerializer.Serialize(AppSettings.Default), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NullGlossary_UsesDefault()
    {
        var validated = SettingsRules.Validate(AppSettings.Default with { Glossary = null! }, _ => { });

        Assert.NotNull(validated.Glossary);
        Assert.True(validated.Glossary.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToEngineOptions_CarriesGlossaryAndUserPath(bool enabled)
    {
        var dir = Path.Combine(Path.GetTempPath(), "suiyi-settings");
        var settings = AppSettings.Default with { Glossary = new GlossarySettings { Enabled = enabled } };

        var options = settings.ToEngineOptions(dir);

        Assert.Equal(enabled, options.Glossary);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "glossary.tsv")), options.UserGlossaryPath);
        Assert.Equal(GlossaryStartupTransport.EnvironmentVariables, options.GlossaryTransport);
        Assert.Equal(settings.Engine.Port, options.Port);
        Assert.Equal(settings.Engine.ToEngineOptions().Preload, options.Preload);
    }
}
