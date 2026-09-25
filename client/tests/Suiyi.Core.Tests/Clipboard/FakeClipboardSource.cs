using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Tests.Clipboard;

/// <summary>内存中的假剪贴板：每次写入递增序号并触发 Changed。</summary>
internal sealed class FakeClipboardSource : IClipboardSource
{
    private ClipboardReadResult _content = ClipboardReadResult.NoText;
    private int _readCount;
    private int _startCount;
    private int _stopCount;

    public event EventHandler? Changed;

    public uint Sequence { get; private set; } = 100;

    public int ReadCount => Volatile.Read(ref _readCount);

    public int StartCount => _startCount;

    public int StopCount => _stopCount;

    public bool Listening { get; private set; }

    /// <summary>接下来 N 次读取返回 Busy。</summary>
    public int BusyReads { get; set; }

    public bool FailWrites { get; set; }

    public void StartListening()
    {
        _startCount++;
        Listening = true;
    }

    public void StopListening()
    {
        _stopCount++;
        Listening = false;
    }

    public uint GetSequenceNumber() => Sequence;

    public ClipboardReadResult TryReadText()
    {
        Interlocked.Increment(ref _readCount);
        if (BusyReads > 0)
        {
            BusyReads--;
            return ClipboardReadResult.Busy;
        }

        return _content;
    }

    public bool TrySetText(string text)
    {
        if (FailWrites)
        {
            return false;
        }

        Put(ClipboardReadResult.FromText(text));
        return true;
    }

    /// <summary>模拟用户复制文本。</summary>
    public void CopyText(string text) => Put(ClipboardReadResult.FromText(text));

    /// <summary>模拟复制图片 / 文件列表。</summary>
    public void CopyNonText() => Put(ClipboardReadResult.NoText);

    /// <summary>模拟密码管理器复制（带隐私标记）。</summary>
    public void CopyPrivate() => Put(ClipboardReadResult.PrivateContent);

    /// <summary>只发通知、序号不变（系统重复通知）。</summary>
    public void RaiseWithoutChange() => Changed?.Invoke(this, EventArgs.Empty);

    private void Put(ClipboardReadResult content)
    {
        _content = content;
        Sequence++;
        if (Listening)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
