using RemoteFlow.Core.Vmware;

namespace RemoteFlow.Infrastructure.Vmware;

/// <summary>按端点 + 证书指纹建立已登录的 ESXi API 会话。证书只接受指纹一致的一张，不做全局放行。</summary>
public sealed class VmwareConnector : IVmwareConnector
{
    public Task<string> FetchCertificatePinAsync(string host, int port, CancellationToken ct = default)
        => VmwareTls.FetchPinAsync(host, port <= 0 ? 443 : port, ct);

    public async Task<IVSphereConsoleApi> ConnectAsync(string host, int port, string certPinSha256,
        string userName, string password, CancellationToken ct = default)
    {
        var endpoint = new UriBuilder(Uri.UriSchemeHttps, host, port <= 0 ? 443 : port).Uri;
        var api = new VsphereConsoleApi(endpoint, VmwareTls.CreatePinnedHandler(certPinSha256));
        try
        {
            await api.LoginAsync(userName, password, ct);
            return api;
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }
}
