using System.Security.Cryptography;

namespace Suiyi.Core.Packaging;

/// <summary>HTTP 下载并校验；测试可注入 <see cref="HttpMessageHandler"/>。</summary>
public sealed class ProvisionDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>创建下载器。</summary>
    /// <param name="handler">可选消息处理（单测 mock）；为 <see langword="null"/> 时用默认 handler。</param>
    public ProvisionDownloader(HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("suiyi-client/model-provision");
            _ownsClient = true;
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("suiyi-client/model-provision");
            _ownsClient = true;
        }
    }

    /// <summary>下载到 <paramref name="destination"/>，校验字节数与 sha256；先写 <c>.partial</c> 再替换。</summary>
    public async Task DownloadAsync(
        string url,
        string destination,
        string expectedSha256,
        long expectedBytes,
        string itemId,
        IProgress<ModelProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedBytes);

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partial = destination + ".partial";
        if (File.Exists(partial))
        {
            File.Delete(partial);
        }

        progress?.Report(new ModelProvisionProgress("download", itemId, $"正在下载 {itemId}…", 0, expectedBytes));

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.Asynchronous))
        {
            var buffer = new byte[1024 * 64];
            long written = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);
                written += read;
                if (written > expectedBytes)
                {
                    output.Dispose();
                    File.Delete(partial);
                    throw new InvalidOperationException($"{itemId}：下载内容超过清单字节数 {expectedBytes}");
                }

                progress?.Report(new ModelProvisionProgress("download", itemId, $"正在下载 {itemId}…", written, expectedBytes));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (written != expectedBytes)
            {
                File.Delete(partial);
                throw new InvalidOperationException($"{itemId}：下载了 {written} 字节，清单为 {expectedBytes}");
            }

            var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                File.Delete(partial);
                throw new InvalidOperationException($"{itemId}：sha256 不符（实际 {actual}）");
            }
        }

        progress?.Report(new ModelProvisionProgress("verify", itemId, $"已校验 {itemId}", expectedBytes, expectedBytes));
        File.Move(partial, destination, overwrite: true);
    }

    /// <summary>若目标已存在且 sha256/字节匹配则跳过。</summary>
    public static bool IsValidFile(string path, string expectedSha256, long expectedBytes)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (info.Length != expectedBytes)
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return string.Equals(hash, expectedSha256, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
