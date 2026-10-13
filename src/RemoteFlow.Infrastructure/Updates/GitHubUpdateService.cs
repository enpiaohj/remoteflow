using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace RemoteFlow.Infrastructure.Updates;

/// <summary>
/// 从公开仓库的 GitHub Releases 检查与下载更新。无需登录；只在用户主动检查或开启了自动检查时才会访问。
/// 下载的产物必须与 Release 上登记的大小、SHA-256 逐位一致，否则丢弃。
/// </summary>
public sealed class GitHubUpdateService(HttpClient http, string assetSuffix, TimeSpan? stallTimeout = null) : IUpdateService
{
    private const string ReleasesUrl = "https://api.github.com/repos/enpiaohj/remoteflow/releases?per_page=30";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>下载过程中连续这么久收不到任何数据就判定为连接停滞并放弃（不是总时长上限）。</summary>
    private readonly TimeSpan _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(45);

    /// <summary>创建带默认请求头的客户端。使用系统代理设置（访问 GitHub 常需代理）。</summary>
    public static HttpClient CreateHttpClient(string productVersion)
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemoteFlow", productVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        using var response = await http.GetAsync(ReleasesUrl, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return GitHubReleaseParser.SelectLatest(json, assetSuffix);
    }

    public async Task<string> DownloadAsync(
        UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (release.Sha256 is null)
        {
            throw new UpdateVerificationException("发布源没有提供该版本的 SHA-256，无法校验，已取消自动更新。");
        }

        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateVerificationException("更新包的下载地址不是预期的 GitHub 地址，已取消。");
        }

        var directory = Path.Combine(Path.GetTempPath(), "RemoteFlow-update");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.AssetName + ".download");

        // 停滞看门狗：每收到一块数据就把超时往后推；用户取消与停滞超时共用一个令牌，事后靠原令牌区分。
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(_stallTimeout);
        var token = watchdog.Token;

        try
        {
            using var response = await http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = release.Size > 0 ? release.Size : response.Content.Headers.ContentLength ?? 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    watchdog.CancelAfter(_stallTimeout);
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(100d, received * 100d / total));
                    }
                }
            }

            if (release.Size > 0 && received != release.Size)
            {
                throw new UpdateVerificationException("下载的更新包大小与发布记录不一致，已丢弃。");
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, release.Sha256, StringComparison.Ordinal))
            {
                throw new UpdateVerificationException("下载的更新包未通过 SHA-256 校验，已丢弃。");
            }

            return path;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 不是用户取消，而是看门狗触发：连接停滞。
            TryDelete(path);
            throw new TimeoutException("下载连接长时间没有数据。");
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响结论；下次下载会覆盖。
        }
    }
}
