namespace Suiyi.Core.Tray;

/// <summary>托盘菜单命令。</summary>
public enum TrayCommand
{
    /// <summary>无动作（状态行、分隔线、子菜单标题）。</summary>
    None,

    /// <summary>翻译剪贴板（手动触发一次）。</summary>
    TranslateClipboard,

    /// <summary>框选翻译（#58）：打开框选遮罩，识别并翻译选区。</summary>
    TranslateRegion,

    /// <summary>暂停 / 恢复监听。</summary>
    TogglePause,

    /// <summary>切换目标语言，参数为语言代码。</summary>
    SetTarget,

    /// <summary>重启翻译服务。</summary>
    RestartEngine,

    /// <summary>切换专业术语保护（#84）。</summary>
    ToggleGlossary,

    /// <summary>编辑我的术语表（不存在时按模板创建，再用默认编辑器打开）。</summary>
    EditGlossary,

    /// <summary>让服务立即重读用户术语表（<c>POST /glossary/reload</c>）。</summary>
    ReloadGlossary,

    /// <summary>打开设置文件。</summary>
    OpenSettings,

    /// <summary>打开日志目录。</summary>
    OpenLogs,

    /// <summary>关于。</summary>
    About,

    /// <summary>退出。</summary>
    Exit,
}

/// <summary>托盘菜单项（界面无关的模型，由 <c>Suiyi.App</c> 渲染）。</summary>
public sealed record TrayMenuItem
{
    /// <summary>分隔线。</summary>
    public static TrayMenuItem Separator { get; } = new() { IsSeparator = true };

    /// <summary>显示文字。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>点击时的命令。</summary>
    public TrayCommand Command { get; init; }

    /// <summary>命令参数（<see cref="TrayCommand.SetTarget"/> 时为语言代码）。</summary>
    public string? Argument { get; init; }

    /// <summary>是否可点击。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>是否显示勾选。</summary>
    public bool IsChecked { get; init; }

    /// <summary>是否为分隔线。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>子菜单；为空表示普通项。</summary>
    public IReadOnlyList<TrayMenuItem> Children { get; init; } = [];
}

/// <summary>按当前 <see cref="TrayState"/> 生成托盘右键菜单（纯函数）。</summary>
public static class TrayMenuBuilder
{
    /// <summary>生成菜单。</summary>
    public static IReadOnlyList<TrayMenuItem> Build(TrayState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var targets = TrayLanguages.All
            .Select(l => new TrayMenuItem
            {
                Text = l.DisplayName,
                Command = TrayCommand.SetTarget,
                Argument = l.Code,
                IsChecked = string.Equals(l.Code, state.Target, StringComparison.OrdinalIgnoreCase),
            })
            .ToArray();

        return
        [
            new TrayMenuItem { Text = state.StatusText, IsEnabled = false },
            TrayMenuItem.Separator,
            new TrayMenuItem { Text = "翻译剪贴板", Command = TrayCommand.TranslateClipboard },
            new TrayMenuItem { Text = state.RegionMenuText, Command = TrayCommand.TranslateRegion },
            new TrayMenuItem { Text = "暂停监听", Command = TrayCommand.TogglePause, IsChecked = state.Paused },
            new TrayMenuItem { Text = "目标语言", Children = targets },
            new TrayMenuItem { Text = "专业术语", Children = GlossaryItems(state) },
            TrayMenuItem.Separator,
            new TrayMenuItem { Text = "重启翻译服务", Command = TrayCommand.RestartEngine },
            new TrayMenuItem { Text = "打开设置文件", Command = TrayCommand.OpenSettings },
            new TrayMenuItem { Text = "打开日志目录", Command = TrayCommand.OpenLogs },
            TrayMenuItem.Separator,
            new TrayMenuItem { Text = "关于", Command = TrayCommand.About },
            new TrayMenuItem { Text = "退出", Command = TrayCommand.Exit },
        ];
    }

    /// <summary>「专业术语」子菜单：开关、编辑、重新加载，下面是不可点的状态行（条数、错误、警告）。</summary>
    private static TrayMenuItem[] GlossaryItems(TrayState state) =>
    [
        new TrayMenuItem { Text = "专业术语保护", Command = TrayCommand.ToggleGlossary, IsChecked = state.GlossaryEnabled },
        new TrayMenuItem { Text = "编辑我的术语表…", Command = TrayCommand.EditGlossary },
        new TrayMenuItem { Text = "重新加载术语表", Command = TrayCommand.ReloadGlossary },
        TrayMenuItem.Separator,
        .. state.GlossaryStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new TrayMenuItem { Text = line, IsEnabled = false }),
    ];
}
