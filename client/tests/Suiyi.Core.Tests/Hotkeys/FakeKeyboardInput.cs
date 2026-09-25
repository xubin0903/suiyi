using Suiyi.Core.Hotkeys;

namespace Suiyi.Core.Tests.Hotkeys;

internal sealed class FakeKeyboardInput : IKeyboardInput
{
    private int _modifierPolls;

    /// <summary>前 N 次查询报告修饰键仍按住；<see cref="int.MaxValue"/> 表示一直按住。</summary>
    public int ModifiersHeldForPolls { get; set; }

    public int ModifierPolls => _modifierPolls;

    /// <summary>模拟 Ctrl+C 时执行（例如往假剪贴板写入选中文本）。</summary>
    public Action? OnCopy { get; set; }

    public bool SendCopyResult { get; set; } = true;

    public int CopyCalls { get; private set; }

    /// <summary>发送 Ctrl+C 时修饰键是否已松开。</summary>
    public bool ModifiersReleasedAtCopy { get; private set; }

    public bool AreModifierKeysDown()
    {
        var polls = Interlocked.Increment(ref _modifierPolls);
        return polls <= ModifiersHeldForPolls;
    }

    public bool SendCopy()
    {
        CopyCalls++;
        ModifiersReleasedAtCopy = _modifierPolls > ModifiersHeldForPolls;
        OnCopy?.Invoke();
        return SendCopyResult;
    }
}
