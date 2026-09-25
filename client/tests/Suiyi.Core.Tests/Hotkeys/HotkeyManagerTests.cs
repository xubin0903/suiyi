using Suiyi.Core.Hotkeys;
using Suiyi.Core.Tests.Clipboard;

namespace Suiyi.Core.Tests.Hotkeys;

public sealed class HotkeyManagerTests : IDisposable
{
    private static readonly HotkeyGesture CtrlAltT = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'T');
    private static readonly HotkeyGesture CtrlShiftY = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'Y');

    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly RecordingLogger _logger = new();
    private readonly HotkeyManager _manager;
    private readonly List<HotkeyRegistrationFailedEventArgs> _failures = [];
    private int _pressed;

    public HotkeyManagerTests()
    {
        _manager = new HotkeyManager(_registrar, _logger);
        _manager.RegistrationFailed += (_, e) => _failures.Add(e);
        _manager.Pressed += (_, _) => _pressed++;
    }

    public void Dispose() => _manager.Dispose();

    [Fact]
    public void Update_RegistersDefault()
    {
        Assert.True(_manager.Update(HotkeyParser.DefaultTranslate));

        Assert.Equal(CtrlAltT, _manager.Current);
        Assert.Equal(CtrlAltT, _registrar.Registered);
        Assert.Empty(_failures);
    }

    [Fact]
    public void Update_NewGestureReplacesOld()
    {
        _manager.Update("Ctrl+Alt+T");

        Assert.True(_manager.Update("Ctrl+Shift+Y"));

        Assert.Equal(CtrlShiftY, _manager.Current);
        Assert.Equal(CtrlShiftY, _registrar.Registered);
        Assert.Equal(1, _registrar.UnregisterCalls);
    }

    [Fact]
    public void Update_SameGestureDoesNotReregister()
    {
        _manager.Update("Ctrl+Alt+T");

        Assert.True(_manager.Update("ctrl + alt + t"));

        Assert.Equal(1, _registrar.RegisterCalls);
        Assert.Equal(0, _registrar.UnregisterCalls);
    }

    [Fact]
    public void Update_OccupiedRaisesRegistrationFailed_AndDoesNotThrow()
    {
        _registrar.Occupied.Add(CtrlAltT);

        Assert.False(_manager.Update("Ctrl+Alt+T"));

        Assert.Null(_manager.Current);
        var failure = Assert.Single(_failures);
        Assert.False(failure.InvalidFormat);
        Assert.Equal("Ctrl+Alt+T", failure.Hotkey);
        Assert.Equal("快捷键 Ctrl+Alt+T 已被其他程序占用，请在设置中修改", failure.Message);
    }

    [Fact]
    public void Update_OtherErrorCodeIsReported()
    {
        _registrar.Occupied.Add(CtrlAltT);
        _registrar.NextErrorCode = 5;

        _manager.Update("Ctrl+Alt+T");

        Assert.Contains("错误码 5", Assert.Single(_failures).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_FailureAfterSuccess_LeavesNothingRegistered()
    {
        _manager.Update("Ctrl+Alt+T");
        _registrar.Occupied.Add(CtrlShiftY);

        Assert.False(_manager.Update("Ctrl+Shift+Y"));

        Assert.Null(_manager.Current);
        Assert.Null(_registrar.Registered);
    }

    [Fact]
    public void Update_InvalidFormatRaisesFailure()
    {
        _manager.Update("Ctrl+Alt+T");

        Assert.False(_manager.Update("Ctrl+Foo"));

        var failure = Assert.Single(_failures);
        Assert.True(failure.InvalidFormat);
        Assert.Contains("未知键名", failure.Reason, StringComparison.Ordinal);
        Assert.Null(_manager.Current);
        Assert.Null(_registrar.Registered);
    }

    [Fact]
    public void Update_EmptyDisables()
    {
        _manager.Update("Ctrl+Alt+T");

        Assert.True(_manager.Update(""));

        Assert.Null(_manager.Current);
        Assert.Null(_registrar.Registered);
        Assert.Empty(_failures);
    }

    [Fact]
    public void Pressed_IsForwardedOnlyWhileRegistered()
    {
        _registrar.Press();
        Assert.Equal(0, _pressed);

        _manager.Update("Ctrl+Alt+T");
        _registrar.Press();
        Assert.Equal(1, _pressed);

        _manager.Update("");
        _registrar.Press();
        Assert.Equal(1, _pressed);
    }

    [Fact]
    public void Dispose_Unregisters()
    {
        _manager.Update("Ctrl+Alt+T");

        _manager.Dispose();

        Assert.Null(_registrar.Registered);
        Assert.Throws<ObjectDisposedException>(() => _manager.Update("Ctrl+Alt+T"));
    }
}
