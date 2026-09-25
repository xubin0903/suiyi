using System.Text;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Settings;
using Suiyi.Core.Tests.Clipboard;

namespace Suiyi.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly RecordingLogger _logger = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 25, 12, 34, 56, TimeSpan.Zero));
    private readonly string _file;

    public SettingsStoreTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        _file = Path.Combine(_dir.Path, "sub", "settings.json");
    }

    public void Dispose() => _dir.Dispose();

    private SettingsStore NewStore() => new(_file, _logger, _clock);

    private void WriteFile(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, content);
    }

    private string SubEntries() => string.Join(",", Directory.GetFileSystemEntries(Path.GetDirectoryName(_file)!).Select(Path.GetFileName).Order());

    [Fact]
    public void MissingFile_DefaultsWithoutCreatingFile()
    {
        var store = NewStore();

        Assert.Equal(AppSettings.Default, store.Load());
        Assert.Equal(AppSettings.Default, store.Current);
        Assert.False(Directory.Exists(Path.GetDirectoryName(_file)));
    }

    [Fact]
    public void Save_CreatesDirectory_Utf8NoBomIndented()
    {
        var store = NewStore();
        store.Save();

        var bytes = File.ReadAllBytes(_file);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal(SettingsSerializer.Serialize(AppSettings.Default), text);
        Assert.Contains("\n  \"primaryTarget\": \"zh\",\n", text, StringComparison.Ordinal);
        Assert.Equal("settings.json", SubEntries());
    }

    [Fact]
    public void Load_ReadsSavedValues()
    {
        var first = NewStore();
        first.Update(s => s.WithPrimaryTarget("ja") with { Hotkey = new HotkeySettings { Translate = "Ctrl+Shift+Y" } });

        var second = NewStore();
        var loaded = second.Load();

        Assert.Equal("ja", loaded.PrimaryTarget);
        Assert.Equal("Ctrl+Shift+Y", loaded.Hotkey.Translate);
    }

    [Fact]
    public void Load_Utf8BomFileAccepted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, """{ "primaryTarget": "en" }""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("en", NewStore().Load().PrimaryTarget);
    }

    [Fact]
    public void CorruptFile_BackedUpAndDefaults()
    {
        WriteFile("{ \"primaryTarget\": ");
        var store = NewStore();

        Assert.Equal(AppSettings.Default, store.Load());
        Assert.False(File.Exists(_file));
        var backup = _file + ".bad-20260925123456";
        Assert.Equal("{ \"primaryTarget\": ", File.ReadAllText(backup));
        Assert.Contains(_logger.Messages, m => m.StartsWith("[Warning]", StringComparison.Ordinal) && m.Contains("已备份", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptFile_BackupNameCollision_GetsSuffix()
    {
        WriteFile("bad1");
        NewStore().Load();
        WriteFile("bad2");
        NewStore().Load();

        Assert.Equal("bad1", File.ReadAllText(_file + ".bad-20260925123456"));
        Assert.Equal("bad2", File.ReadAllText(_file + ".bad-20260925123456-1"));
    }

    [Fact]
    public void TooNewFile_BackedUpAndDefaults()
    {
        WriteFile("""{ "schemaVersion": 99, "primaryTarget": "en" }""");

        var loaded = NewStore().Load();

        Assert.Equal(AppSettings.Default, loaded);
        Assert.True(File.Exists(_file + ".bad-20260925123456"));
        Assert.Contains(_logger.Messages, m => m.Contains("99", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterCorruptBackup_UpdateWritesFreshFile()
    {
        WriteFile("garbage");
        var store = NewStore();
        store.Load();

        store.Update(s => s.WithPrimaryTarget("en"));

        Assert.Equal("en", NewStore().Load().PrimaryTarget);
    }

    [Fact]
    public void OutOfRangeField_FallsBackLoggedFileUntouched()
    {
        const string Json = """{ "clipboard": { "debounceMs": -1, "maxChars": 4000 } }""";
        WriteFile(Json);

        var loaded = NewStore().Load();

        Assert.Equal(150, loaded.Clipboard.DebounceMs);
        Assert.Equal(4000, loaded.Clipboard.MaxChars);
        Assert.Contains(_logger.Messages, m => m.StartsWith("[Warning]", StringComparison.Ordinal) && m.Contains("clipboard.debounceMs", StringComparison.Ordinal));
        Assert.Equal(Json, File.ReadAllText(_file)); // 读取不改写文件
    }

    [Fact]
    public void Update_WritesAndRaisesChangedWithOldAndNew()
    {
        var store = NewStore();
        store.Load();
        var events = new List<SettingsChangedEventArgs>();
        store.Changed += (_, e) => events.Add(e);

        var result = store.Update(s => s with { Clipboard = s.Clipboard with { MonitorEnabled = false } });

        Assert.False(result.Clipboard.MonitorEnabled);
        Assert.Same(result, store.Current);
        var e = Assert.Single(events);
        Assert.Equal(AppSettings.Default, e.OldSettings);
        Assert.Same(result, e.NewSettings);
        Assert.False(NewStore().Load().Clipboard.MonitorEnabled);
    }

    [Fact]
    public void Update_NoChange_NoEventNoWrite()
    {
        var store = NewStore();
        store.Load();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Update(s => s with { PrimaryTarget = "zh" });

        Assert.Equal(0, raised);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void Update_ValidatesResult()
    {
        var store = NewStore();

        var result = store.Update(s => s with { Popup = s.Popup with { AutoHideSeconds = 999, MaxWidth = 600 } });

        Assert.Equal(8, result.Popup.AutoHideSeconds);
        Assert.Equal(600, result.Popup.MaxWidth);
    }

    [Fact]
    public void Update_TargetSameAsSecondary_Resolved()
    {
        var store = NewStore();

        var result = store.Update(s => s.WithPrimaryTarget("en"));

        Assert.Equal(("en", "zh"), (result.PrimaryTarget, result.SecondaryTarget));
    }

    [Fact]
    public void Update_IsThreadSafe()
    {
        var store = NewStore();
        store.Update(s => s with { Clipboard = s.Clipboard with { MaxChars = 1000 } });
        var changed = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changed);

        Parallel.For(0, 200, _ => store.Update(s => s with { Clipboard = s.Clipboard with { MaxChars = s.Clipboard.MaxChars + 1 } }));

        Assert.Equal(1200, store.Current.Clipboard.MaxChars);
        Assert.Equal(200, changed);
        Assert.Equal(1200, NewStore().Load().Clipboard.MaxChars);
        Assert.Equal("settings.json", SubEntries()); // 无残留临时文件
    }

    [Fact]
    public void Update_ExternalEditReloadedBeforeApplying()
    {
        var store = NewStore();
        store.Update(s => s.WithPrimaryTarget("en"));

        // 用户在程序运行时手工改了快捷键。
        var edited = SettingsSerializer.Serialize(store.Current with { Hotkey = new HotkeySettings { Translate = "Ctrl+Shift+Y" } });
        File.WriteAllText(_file, edited);
        File.SetLastWriteTimeUtc(_file, DateTime.UtcNow.AddMinutes(1));

        store.Update(s => s with { Clipboard = s.Clipboard with { MonitorEnabled = false } });

        var loaded = NewStore().Load();
        Assert.Equal("Ctrl+Shift+Y", loaded.Hotkey.Translate);
        Assert.False(loaded.Clipboard.MonitorEnabled);
        Assert.Equal("en", loaded.PrimaryTarget);
    }

    [Fact]
    public void Update_WriteFailure_KeepsInMemoryAndLogs()
    {
        // 目标路径是一个目录：写入必然失败。
        Directory.CreateDirectory(_file);
        var store = new SettingsStore(_file, _logger, _clock);

        var result = store.Update(s => s.WithPrimaryTarget("ja"));

        Assert.Equal("ja", result.PrimaryTarget);
        Assert.Equal("ja", store.Current.PrimaryTarget);
        Assert.Contains(_logger.Messages, m => m.StartsWith("[Error]", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_WriteFailure_Throws()
    {
        Directory.CreateDirectory(_file);

        // Linux 上是 IOException，Windows 上可能是 UnauthorizedAccessException。
        var ex = Record.Exception(() => NewStore().Save());
        Assert.True(ex is IOException or UnauthorizedAccessException, ex?.ToString());
    }

    [Fact]
    public void Update_NullChange_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NewStore().Update(null!));
    }

    [Fact]
    public void DefaultConstructor_UsesResolvedPath()
    {
        Assert.Equal(Path.GetFullPath(SettingsPaths.ResolveFile()), new SettingsStore().FilePath);
    }
}
