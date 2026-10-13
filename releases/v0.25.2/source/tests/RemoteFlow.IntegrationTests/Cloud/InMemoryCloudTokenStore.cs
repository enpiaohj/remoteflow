using RemoteFlow.Core.Cloud;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class InMemoryCloudTokenStore : ICloudTokenStore
{
    private CloudSession? _session;

    public Task<CloudSession?> GetAsync(CancellationToken ct = default) => Task.FromResult(_session);

    public Task SetAsync(CloudSession session, CancellationToken ct = default)
    {
        _session = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _session = null;
        return Task.CompletedTask;
    }
}
