using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Tray;

public sealed class TrayControllerTests
{
    private readonly TrayController _tray = new();
    private readonly List<string> _events = [];
    private int _stateChanges;

    public TrayControllerTests()
    {
        _tray.StateChanged += (_, _) => _stateChanges++;
        _tray.TranslateClipboardRequested += (_, _) => _events.Add("translate");
        _tray.PauseToggled += (_, e) => _events.Add($"pause:{e.Paused}:{_tray.State.Paused}");
        _tray.TargetChanged += (_, e) => _events.Add($"target:{e.Language}:{_tray.State.Target}");
        _tray.RestartEngineRequested += (_, _) => _events.Add("restart");
        _tray.OpenSettingsRequested += (_, _) => _events.Add("settings");
        _tray.OpenLogsRequested += (_, _) => _events.Add("logs");
        _tray.AboutRequested += (_, _) => _events.Add("about");
        _tray.ExitRequested += (_, _) => _events.Add("exit");
        _tray.ShowLastPopupRequested += (_, _) => _events.Add("popup");
        _tray.NotificationRequested += (_, e) => _events.Add($"notify:{e.Title}:{e.Message}");
    }

    [Theory]
    [InlineData(TrayCommand.TranslateClipboard, "translate")]
    [InlineData(TrayCommand.RestartEngine, "restart")]
    [InlineData(TrayCommand.OpenSettings, "settings")]
    [InlineData(TrayCommand.OpenLogs, "logs")]
    [InlineData(TrayCommand.About, "about")]
    [InlineData(TrayCommand.Exit, "exit")]
    public void Invoke_RaisesMatchingEvent(TrayCommand command, string expected)
    {
        _tray.Invoke(command);

        Assert.Equal([expected], _events);
        Assert.Equal(0, _stateChanges);
    }

    [Fact]
    public void TogglePause_UpdatesStateBeforeEvent()
    {
        _tray.Invoke(TrayCommand.TogglePause);
        _tray.Invoke(TrayCommand.TogglePause);

        Assert.Equal(["pause:True:True", "pause:False:False"], _events);
        Assert.Equal(2, _stateChanges);
    }

    [Fact]
    public void TogglePause_ViaMenuItem()
    {
        _tray.Invoke(TrayMenuBuilderTests.Find(_tray.Menu, TrayCommand.TogglePause));

        Assert.True(_tray.State.Paused);
        Assert.True(TrayMenuBuilderTests.Find(_tray.Menu, TrayCommand.TogglePause).IsChecked);
    }

    [Fact]
    public void SetTargetCommand_UpdatesStateAndRaises()
    {
        _tray.Invoke(TrayCommand.SetTarget, "EN");

        Assert.Equal(["target:en:en"], _events);
        Assert.Equal("en", _tray.State.Target);
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("fr")]
    [InlineData(null)]
    public void SetTargetCommand_SameOrUnsupported_Ignored(string? language)
    {
        _tray.Invoke(TrayCommand.SetTarget, language);

        Assert.Empty(_events);
        Assert.Equal(0, _stateChanges);
        Assert.Equal("zh", _tray.State.Target);
    }

    [Fact]
    public void DisabledAndSeparatorItems_DoNothing()
    {
        _tray.Invoke(_tray.Menu[0]);
        _tray.Invoke(TrayMenuItem.Separator);
        _tray.Invoke(new TrayMenuItem { Command = TrayCommand.Exit, IsEnabled = false });
        _tray.Invoke(TrayCommand.None);

        Assert.Empty(_events);
    }

    [Fact]
    public void Setters_ChangeStateWithoutCommandEvents()
    {
        _tray.SetStatus(TrayStatus.Ready);
        _tray.SetPaused(true);
        _tray.SetTarget("ja");

        Assert.Empty(_events);
        Assert.Equal(3, _stateChanges);
        Assert.Equal(new TrayState { Status = TrayStatus.Ready, Paused = true, Target = "ja" }, _tray.State);
    }

    [Fact]
    public void StateChanged_NotRaisedForSameState()
    {
        _tray.SetStatus(TrayStatus.Error, "x");
        _tray.SetStatus(TrayStatus.Error, "x");
        _tray.SetPaused(false);

        Assert.Equal(1, _stateChanges);
    }

    [Fact]
    public void SetTarget_Unsupported_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tray.SetTarget("fr"));
    }

    [Fact]
    public void LeftClick_RequestsLastPopup()
    {
        _tray.OnLeftClick();

        Assert.Equal(["popup"], _events);
    }

    [Fact]
    public void ShowNotification_RaisesNotificationRequested()
    {
        _tray.ShowNotification("随译", "随译已在运行");

        Assert.Equal(["notify:随译:随译已在运行"], _events);
    }

    [Fact]
    public void Constructor_AcceptsInitialState()
    {
        var tray = new TrayController(new TrayState { Status = TrayStatus.Ready, Target = "en" });

        Assert.Equal("随译 · 就绪（English）", tray.State.ToolTip);
    }
}
