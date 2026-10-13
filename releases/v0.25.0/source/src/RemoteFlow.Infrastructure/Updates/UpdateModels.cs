namespace RemoteFlow.Infrastructure.Updates;

/// <summary>一个可用的正式版本及其本平台产物。</summary>
/// <param name="Sha256">产物的 SHA-256（十六进制，小写）。发布源未提供时为 null，此时拒绝自动安装。</param>
public sealed record UpdateRelease(
    Version Version,
    string Tag,
    string Notes,
    string PageUrl,
    string AssetName,
    string DownloadUrl,
    long Size,
    string? Sha256);

/// <summary>更新源：查询最新版本、下载并校验产物。</summary>
public interface IUpdateService
{
    /// <summary>查询最新正式版本；发布源里没有本平台产物时返回 null。网络失败抛异常。</summary>
    Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 下载产物到临时目录并校验大小与 SHA-256，返回本地文件路径。
    /// 校验不通过抛 <see cref="UpdateVerificationException"/> 并删除已下载的文件。
    /// </summary>
    Task<string> DownloadAsync(UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>下载的更新包未通过完整性校验。</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);
