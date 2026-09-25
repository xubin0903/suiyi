using System.Globalization;
using System.Text;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Settings;

/// <summary><see cref="SettingsStore.Changed"/> 参数。</summary>
/// <param name="oldSettings">修改前。</param>
/// <param name="newSettings">修改后。</param>
public sealed class SettingsChangedEventArgs(AppSettings oldSettings, AppSettings newSettings) : EventArgs
{
    /// <summary>修改前。</summary>
    public AppSettings OldSettings { get; } = oldSettings;

    /// <summary>修改后。</summary>
    public AppSettings NewSettings { get; } = newSettings;
}

/// <summary>
/// 设置读写。文件不存在时使用默认值（不落盘）；损坏或版本过高时备份为 <c>settings.json.bad-yyyyMMddHHmmss</c> 并使用默认值；
/// 写入为原子写。<see cref="Update"/> 线程安全。M2 不做热重载：手工编辑后重启生效
/// （但 <see cref="Update"/> 写回前若发现文件被外部修改，会先重新读取再应用修改，避免覆盖手工编辑）。
/// </summary>
public sealed class SettingsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private AppSettings _current = AppSettings.Default;
    private DateTime? _knownWriteTimeUtc;

    /// <summary>创建设置存储。</summary>
    /// <param name="filePath">设置文件路径；默认 <see cref="SettingsPaths.ResolveFile"/>。</param>
    /// <param name="logger">日志。</param>
    /// <param name="timeProvider">时钟（备份文件名用本地时间），测试时注入。</param>
    public SettingsStore(string? filePath = null, IAppLogger? logger = null, TimeProvider? timeProvider = null)
    {
        FilePath = Path.GetFullPath(filePath ?? SettingsPaths.ResolveFile());
        _logger = logger ?? NullAppLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>设置改变（<see cref="Update"/> 产生了不同的值）。在调用 <see cref="Update"/> 的线程上触发。</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>设置文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>当前设置。</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>从磁盘读取并设为 <see cref="Current"/>。任何情况下都不抛出（读不了就用默认值）。</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            _current = ReadFromDisk();
            return _current;
        }
    }

    /// <summary>把 <see cref="Current"/> 写入磁盘（原子写，必要时创建目录）。</summary>
    /// <exception cref="IOException">写入失败。</exception>
    /// <exception cref="UnauthorizedAccessException">无权限。</exception>
    public void Save()
    {
        lock (_gate)
        {
            WriteToDisk(_current);
        }
    }

    /// <summary>
    /// 修改设置：校验（越界字段回落默认值）→ 与当前值不同时写盘并触发 <see cref="Changed"/>。
    /// 写盘失败只记日志，内存中的值仍然更新。
    /// </summary>
    /// <returns>修改后的设置。</returns>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings oldSettings;
        AppSettings newSettings;
        lock (_gate)
        {
            if (FileChangedExternally())
            {
                _logger.Info("设置：检测到 settings.json 被外部修改，先重新读取");
                _current = ReadFromDisk();
            }

            oldSettings = _current;
            newSettings = SettingsRules.Validate(change(oldSettings) ?? oldSettings, Warn);
            if (newSettings == oldSettings)
            {
                return oldSettings;
            }

            _current = newSettings;
            try
            {
                WriteToDisk(newSettings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error($"设置：写入 {FilePath} 失败，本次修改仅在内存中生效", ex);
            }
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(oldSettings, newSettings));
        return newSettings;
    }

    private AppSettings ReadFromDisk()
    {
        string json;
        try
        {
            if (!File.Exists(FilePath))
            {
                _knownWriteTimeUtc = null;
                _logger.Info($"设置：{FilePath} 不存在，使用默认值");
                return AppSettings.Default;
            }

            _knownWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
            json = File.ReadAllText(FilePath, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"设置：读取 {FilePath} 失败，使用默认值", ex);
            return AppSettings.Default;
        }

        var result = SettingsSerializer.Parse(json, Warn, message => _logger.Info(message));
        if (result.Status == SettingsParseStatus.Ok)
        {
            return result.Settings;
        }

        var backup = BackUpBadFile();
        _logger.Warn($"设置：{result.Error}；原文件已备份为 {backup ?? "（备份失败）"}，使用默认值");
        return AppSettings.Default;
    }

    private void WriteToDisk(AppSettings settings)
    {
        var bytes = Utf8NoBom.GetBytes(SettingsSerializer.Serialize(settings));
        AtomicFile.Write(FilePath, stream => stream.Write(bytes));
        _knownWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
    }

    private bool FileChangedExternally()
    {
        try
        {
            var exists = File.Exists(FilePath);
            return exists
                ? File.GetLastWriteTimeUtc(FilePath) != _knownWriteTimeUtc
                : _knownWriteTimeUtc is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? BackUpBadFile()
    {
        var stamp = _timeProvider.GetLocalNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backup = $"{FilePath}.bad-{stamp}";
        for (var i = 1; File.Exists(backup); i++)
        {
            backup = string.Create(CultureInfo.InvariantCulture, $"{FilePath}.bad-{stamp}-{i}");
        }

        try
        {
            File.Move(FilePath, backup);
            _knownWriteTimeUtc = null;
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"设置：备份损坏文件 {FilePath} 失败", ex);
            return null;
        }
    }

    private void Warn(string message) => _logger.Warn(message);
}
