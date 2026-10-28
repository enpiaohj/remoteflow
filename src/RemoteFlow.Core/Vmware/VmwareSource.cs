namespace RemoteFlow.Core.Vmware;

/// <summary>
/// 一台 VMware ESXi 主机（资源来源）。只保存主机端点、凭据引用和证书指纹；
/// 虚拟机不落库，每次从主机实时读取，因此永远不会与真实情况脱节，也不会与普通连接重复。
/// <para>不保存任何秘密：账号密码在凭据保险库里，这里只有 <see cref="CredentialId"/> 引用。</para>
/// </summary>
public sealed class VmwareSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>ESXi 主机名或 IP（管理端点，不含协议和路径）。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>来源类型。首次连接时由服务器自报的 API 类型识别，不由用户手选。</summary>
    public VmwareSourceKind Kind { get; set; } = VmwareSourceKind.EsxiHost;

    /// <summary>HTTPS 管理端口，默认 443。</summary>
    public int Port { get; set; } = 443;

    /// <summary>用于登录 ESXi 的凭据。</summary>
    public Guid? CredentialId { get; set; }

    /// <summary>
    /// 已核对的证书 SHA-256 指纹（十六进制）。只接受与之一致的证书；证书变化会被阻止，
    /// 需用户在编辑来源时重新核对。它是公钥证书的摘要，不是秘密。
    /// </summary>
    public string CertPinSha256 { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public VmwareSource Clone() => (VmwareSource)MemberwiseClone();
}

/// <summary>管理端点类型：独立 ESXi 主机，或 vCenter Server。</summary>
public enum VmwareSourceKind
{
    EsxiHost = 0,
    VCenter = 1
}

public interface IVmwareSourceRepository
{
    Task<IReadOnlyList<VmwareSource>> GetAllAsync(CancellationToken ct = default);
    Task<VmwareSource?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(VmwareSource source, CancellationToken ct = default);
    Task UpdateAsync(VmwareSource source, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
