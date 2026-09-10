namespace RemoteFlow.Core.Cloud;

/// <summary>
/// AppsCloud 服务地址持有者（单例，可变）。登录时由 <see cref="ICloudSyncService"/> 设置，
/// 使 <c>AppsCloudClient</c> 无需在构造期就知道地址。
/// </summary>
public sealed class CloudEndpoint
{
    private string? _baseUrl;

    /// <summary>含 PathBase、末尾带斜杠，例：<c>https://host/appscloud/</c>。</summary>
    public string? BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = string.IsNullOrWhiteSpace(value)
            ? null
            : value.EndsWith('/') ? value : value + "/";
    }

    public Uri RequireBase() => new(
        _baseUrl ?? throw new CloudAuthRequiredException("AppsCloud 服务地址未配置。"));
}
