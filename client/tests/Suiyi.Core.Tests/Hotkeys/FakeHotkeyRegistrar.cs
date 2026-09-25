using Suiyi.Core.Hotkeys;

namespace Suiyi.Core.Tests.Hotkeys;

internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public event EventHandler? Pressed;

    /// <summary>这些快捷键被「其他程序」占用。</summary>
    public HashSet<HotkeyGesture> Occupied { get; } = [];

    public int NextErrorCode { get; set; } = 1409;

    public HotkeyGesture? Registered { get; private set; }

    public int RegisterCalls { get; private set; }

    public int UnregisterCalls { get; private set; }

    public bool TryRegister(HotkeyGesture gesture, out int errorCode)
    {
        RegisterCalls++;
        if (Registered is not null)
        {
            throw new InvalidOperationException("注册新快捷键前应先注销旧的");
        }

        if (Occupied.Contains(gesture))
        {
            errorCode = NextErrorCode;
            return false;
        }

        Registered = gesture;
        errorCode = 0;
        return true;
    }

    public void Unregister()
    {
        UnregisterCalls++;
        Registered = null;
    }

    public void Press() => Pressed?.Invoke(this, EventArgs.Empty);
}
