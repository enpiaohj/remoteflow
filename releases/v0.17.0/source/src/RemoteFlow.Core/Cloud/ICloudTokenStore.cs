namespace RemoteFlow.Core.Cloud;

/// <summary>云会话（含 Refresh Token）的本地持久化。落盘内容须经平台密钥库保护。</summary>
public interface ICloudTokenStore
{
    Task<CloudSession?> GetAsync(CancellationToken ct = default);

    Task SetAsync(CloudSession session, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
