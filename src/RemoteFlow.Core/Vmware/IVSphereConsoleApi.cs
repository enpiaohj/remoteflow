namespace RemoteFlow.Core.Vmware;

/// <summary>最小控制台 API；调用方负责凭据解析与来源生命周期，不在模型中保存秘密。</summary>
public interface IVSphereConsoleApi : IAsyncDisposable
{
    Task LoginAsync(string userName, string password, CancellationToken ct = default);
    Task<IReadOnlyList<VsphereVm>> ListVirtualMachinesAsync(CancellationToken ct = default);
    Task<WebMksTicket> AcquireWebMksTicketAsync(string vmReference, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}

public sealed record VsphereVm(string Reference, string Name, string PowerState, string? InstanceUuid);

/// <summary>短期敏感数据：禁止序列化、持久化或记录完整对象。</summary>
public sealed class WebMksTicket(string host, int port, string ticket)
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Ticket { get; } = ticket;

    public override string ToString() => nameof(WebMksTicket);
}

/// <summary>只暴露可安全显示的 fault 类别，不携带原始 SOAP 内容。</summary>
public sealed class VsphereApiException(string faultCode)
    : Exception($"VMware API 请求失败（{faultCode}）。")
{
    public string FaultCode { get; } = faultCode;
}
