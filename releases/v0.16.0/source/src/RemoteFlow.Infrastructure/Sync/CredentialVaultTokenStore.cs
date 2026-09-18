using System.Text.Json;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary><see cref="ICloudTokenStore"/> 把整个 <see cref="CloudSession"/> 序列化后经保险库保护落盘。</summary>
public sealed class CredentialVaultTokenStore(ICredentialVault vault) : ICloudTokenStore
{
    private const string Reference = "cloud:session";

    public async Task<CloudSession?> GetAsync(CancellationToken ct = default)
    {
        var json = await vault.RetrieveSecretAsync(Reference, ct);
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<CloudSession>(json);
    }

    public Task SetAsync(CloudSession session, CancellationToken ct = default) =>
        vault.StoreSecretAsync(Reference, JsonSerializer.Serialize(session), ct);

    public Task ClearAsync(CancellationToken ct = default) => vault.DeleteSecretAsync(Reference, ct);
}
