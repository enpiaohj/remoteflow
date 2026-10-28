using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Vmware;

namespace RemoteFlow.Application.Vmware;

/// <summary>
/// VMware 资源来源（ESXi 主机 / vCenter）管理与运维。虚拟机不落库，一律实时读取。
/// 凭据只在每次操作期间短暂解析，用后即弃；每次操作使用独立的已登录会话并在结束时注销，
/// 不缓存 Cookie，也不跨操作共享。
/// </summary>
public sealed class VmwareService(
    IVmwareConnector connector,
    IVmwareSourceRepository sources,
    CredentialService credentials,
    ILogger<VmwareService> logger)
{
    // ── 来源管理 ──────────────────────────────────────────────────

    public Task<IReadOnlyList<VmwareSource>> GetSourcesAsync(CancellationToken ct = default)
        => sources.GetAllAsync(ct);

    public Task<VmwareSource?> GetSourceAsync(Guid id, CancellationToken ct = default)
        => sources.GetByIdAsync(id, ct);

    /// <summary>只做 TLS 握手取证书指纹，供用户首次核对；不信任该证书。</summary>
    public Task<string> FetchCertificatePinAsync(string host, int port, CancellationToken ct = default)
        => connector.FetchCertificatePinAsync(NormalizeHost(host), NormalizePort(port), ct);

    /// <summary>
    /// 新增或更新来源。保存前先用给定凭据真实登录一次：确认账号可用，并由服务器自报类型
    /// （ESXi 主机或 vCenter），不让用户手选。<paramref name="newPassword"/> 非空时同时新建凭据。
    /// </summary>
    public async Task<VmwareSource> SaveSourceAsync(
        VmwareSource source, string? newUserName, string? newPassword, CancellationToken ct = default)
    {
        source.Host = NormalizeHost(source.Host);
        source.Port = NormalizePort(source.Port);
        source.Name = string.IsNullOrWhiteSpace(source.Name) ? source.Host : source.Name.Trim();
        if (source.Host.Length == 0)
        {
            throw new ArgumentException("请填写 ESXi / vCenter 的主机地址。");
        }

        source.CertPinSha256 = VmwareTlsPin.Normalize(source.CertPinSha256);
        if (source.CertPinSha256.Length != 64)
        {
            throw new InvalidOperationException("请先获取并核对证书指纹。");
        }

        Credential? created = null;
        string userName;
        string password;
        if (!string.IsNullOrEmpty(newPassword))
        {
            userName = (newUserName ?? string.Empty).Trim();
            password = newPassword;
            if (userName.Length == 0)
            {
                throw new ArgumentException("请填写 ESXi 用户名。");
            }
        }
        else
        {
            using var resolved = await ResolveCredentialAsync(source.CredentialId, ct);
            userName = resolved.Username;
            password = resolved.Password!;
        }

        // 先登录验证再落库：账号、证书指纹或网络有问题时在这里就报错，不会存下一个连不上的来源。
        await using (var api = await connector.ConnectAsync(source.Host, source.Port, source.CertPinSha256, userName, password, ct))
        {
            source.Kind = api.ServerInfo?.Kind ?? VmwareSourceKind.EsxiHost;
        }

        if (created is null && !string.IsNullOrEmpty(newPassword))
        {
            created = await credentials.CreateAsync(new Credential
            {
                Name = $"ESXi {source.Name}",
                Type = CredentialType.LocalPassword,
                Username = userName,
                Description = "VMware ESXi / vCenter 管理凭据（由添加来源创建）"
            }, newPassword, null, ct);
            source.CredentialId = created.Id;
        }

        source.UpdatedAt = DateTimeOffset.Now;
        var existing = await sources.GetByIdAsync(source.Id, ct);
        if (existing is null)
        {
            source.CreatedAt = DateTimeOffset.Now;
            await sources.AddAsync(source, ct);
        }
        else
        {
            await sources.UpdateAsync(source, ct);
        }

        logger.LogInformation("已保存 VMware 来源 {Name}（{Kind}）", source.Name, source.Kind);
        return source;
    }

    public Task DeleteSourceAsync(Guid id, CancellationToken ct = default) => sources.DeleteAsync(id, ct);

    // ── 虚拟机（实时） ────────────────────────────────────────────

    public async Task<IReadOnlyList<VsphereVm>> ListVirtualMachinesAsync(VmwareSource source, CancellationToken ct = default)
    {
        await using var api = await OpenAsync(source, ct);
        return await api.ListVirtualMachinesAsync(ct);
    }

    /// <summary>
    /// 为一台虚拟机构造控制台会话用的临时连接配置（不落库）。会话 Id 由来源与虚拟机 UUID 确定性生成，
    /// 因此同一台虚拟机再次打开时会聚焦已有标签，而不是重复创建。
    /// </summary>
    public static ConnectionProfile BuildConsoleProfile(VmwareSource source, VsphereVm vm)
    {
        if (string.IsNullOrWhiteSpace(vm.InstanceUuid))
        {
            throw new InvalidOperationException($"虚拟机“{vm.Name}”没有可用的 UUID，无法打开控制台。");
        }

        return new ConnectionProfile
        {
            Id = StableId(source.Id, vm.InstanceUuid),
            Name = vm.Name,
            Host = source.Host,
            Port = source.Port,
            Protocol = ProtocolType.Vnc,
            DeviceType = DeviceType.VirtualMachine,
            CredentialId = source.CredentialId,
            Vnc = new VncOptions { ClipboardToLocal = false },
            Vmware = new VmwareOptions
            {
                Enabled = true,
                InstanceUuid = vm.InstanceUuid,
                VmName = vm.Name,
                CertPinSha256 = source.CertPinSha256
            }
        };
    }

    public async Task<VmPowerResult> PowerAsync(VmwareSource source, string vmReference, VmPowerAction action, CancellationToken ct = default)
    {
        await using var api = await OpenAsync(source, ct);
        var result = await api.PowerAsync(vmReference, action, ct);
        logger.LogInformation("ESXi 电源操作 {Action} 结果 {Result}", action, result);
        return result;
    }

    public async Task<IReadOnlyList<VmSnapshot>> ListSnapshotsAsync(VmwareSource source, string vmReference, CancellationToken ct = default)
    {
        await using var api = await OpenAsync(source, ct);
        return await api.ListSnapshotsAsync(vmReference, ct);
    }

    public async Task CreateSnapshotAsync(VmwareSource source, string vmReference, string name, string description, CancellationToken ct = default)
    {
        await using var api = await OpenAsync(source, ct);
        await api.CreateSnapshotAsync(vmReference, name, description, ct);
        logger.LogInformation("已创建 ESXi 快照");
    }

    public async Task RevertSnapshotAsync(VmwareSource source, string vmReference, string snapshotReference, CancellationToken ct = default)
    {
        await using var api = await OpenAsync(source, ct);
        await EnsureSnapshotBelongsAsync(api, vmReference, snapshotReference, ct);
        await api.RevertSnapshotAsync(snapshotReference, ct);
        logger.LogInformation("已恢复 ESXi 快照");
    }

    public async Task RemoveSnapshotAsync(VmwareSource source, string vmReference, string snapshotReference, CancellationToken ct = default)
    {
        await using var api = await OpenAsync(source, ct);
        await EnsureSnapshotBelongsAsync(api, vmReference, snapshotReference, ct);
        await api.RemoveSnapshotAsync(snapshotReference, ct);
        logger.LogInformation("已删除 ESXi 快照");
    }

    /// <summary>写操作前再确认快照确属这台虚拟机，防止用过期引用误操作别的虚拟机。</summary>
    private static async Task EnsureSnapshotBelongsAsync(IVSphereConsoleApi api, string vmReference, string snapshotReference, CancellationToken ct)
    {
        var snapshots = await api.ListSnapshotsAsync(vmReference, ct);
        if (snapshots.All(s => s.Reference != snapshotReference))
        {
            throw new VsphereApiException("ManagedObjectNotFound");
        }
    }

    private async Task<IVSphereConsoleApi> OpenAsync(VmwareSource source, CancellationToken ct)
    {
        using var resolved = await ResolveCredentialAsync(source.CredentialId, ct);
        return await connector.ConnectAsync(source.Host, NormalizePort(source.Port), source.CertPinSha256,
            resolved.Username, resolved.Password!, ct);
    }

    private async Task<ResolvedCredential> ResolveCredentialAsync(Guid? credentialId, CancellationToken ct)
    {
        if (credentialId is not { } id)
        {
            throw new InvalidOperationException("该来源没有选择登录凭据，请编辑来源后选择。");
        }

        var resolved = await credentials.ResolveAsync(id, ct)
            ?? throw new InvalidOperationException("该来源引用的凭据已不存在，请编辑来源重新选择。");
        if (resolved.SecretMissing || string.IsNullOrEmpty(resolved.Password))
        {
            resolved.Dispose();
            throw new InvalidOperationException("该凭据的密码在本机保险库中找不到，请重新保存凭据。");
        }

        return resolved;
    }

    /// <summary>由来源 Id 与虚拟机 UUID 生成稳定的会话配置 Id（非安全用途，仅用于去重聚焦）。</summary>
    private static Guid StableId(Guid sourceId, string instanceUuid)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"vmware:{sourceId:N}:{instanceUuid.Trim().ToLowerInvariant()}"));
        return new Guid(hash);
    }

    private static string NormalizeHost(string? host)
    {
        var value = (host ?? string.Empty).Trim();
        // 用户可能粘贴整条 URL（如 Host Client 登录页地址）：只取主机部分。
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host;
        }

        return value.TrimEnd('/');
    }

    private static int NormalizePort(int port) => port <= 0 ? 443 : port;
}

/// <summary>指纹格式辅助（与传输层规则一致：只接受 64 位十六进制 SHA-256）。</summary>
internal static class VmwareTlsPin
{
    public static string Normalize(string? value)
    {
        var hex = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 64 ? hex : string.Empty;
    }
}
