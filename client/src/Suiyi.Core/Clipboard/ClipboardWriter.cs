using Suiyi.Core.Logging;

namespace Suiyi.Core.Clipboard;

/// <summary>
/// 本程序写剪贴板的唯一入口（浮窗「复制译文」、快捷键 #33 共用）。
/// 写入后记录序号，监听器据此忽略这次变化，不会自触发翻译。
/// </summary>
public sealed class ClipboardWriter
{
    private readonly IClipboardSource _source;
    private readonly SelfWriteTracker _tracker;
    private readonly IAppLogger _logger;

    /// <summary>创建写入器。<paramref name="tracker"/> 必须与 <see cref="ClipboardMonitor"/> 使用同一个实例。</summary>
    public ClipboardWriter(IClipboardSource source, SelfWriteTracker tracker, IAppLogger? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger = logger ?? NullAppLogger.Instance;
    }

    /// <summary>写入文本；成功返回 <see langword="true"/>。日志只记录长度。</summary>
    public bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var ok = _source.TrySetText(text);
        if (ok)
        {
            _tracker.RecordOwnWrite(_source.GetSequenceNumber());
            _logger.Info($"剪贴板：写入 {text.Length} 个字符（已标记为自身写入）");
        }
        else
        {
            _logger.Warn($"剪贴板：写入 {text.Length} 个字符失败（剪贴板被占用）");
        }

        return ok;
    }

    /// <summary>在 <paramref name="window"/> 内让监听器忽略下一次剪贴板变化（快捷键模拟复制用）。</summary>
    public void SuppressNext(TimeSpan window) => _tracker.SuppressNext(window);
}
