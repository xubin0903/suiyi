namespace Suiyi.Core.Settings;

/// <summary>原子写文件：先写同目录临时文件并刷盘，再替换目标，写到一半失败不会破坏旧文件。</summary>
internal static class AtomicFile
{
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path, overwrite: true);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留的临时文件不影响设置；下次写入会用新名字。
        }
    }
}
