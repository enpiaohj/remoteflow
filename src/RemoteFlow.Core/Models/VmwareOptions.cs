namespace RemoteFlow.Core.Models;

/// <summary>
/// VMware ESXi 虚拟机控制台参数。<see cref="Enabled"/> 为 true 的连接，协议仍是 VNC（RFB），
/// 但连接时先用 <see cref="ConnectionProfile.CredentialId"/> 指向的账号登录 ESXi，按
/// <see cref="InstanceUuid"/> 找到虚拟机并取一次性控制台票据，再经 WSS 建立 RFB 流。
/// <para>
/// 此时 <see cref="ConnectionProfile.Host"/> / <see cref="ConnectionProfile.Port"/> 是 ESXi 的 HTTPS 管理端点，
/// 不是虚拟机自己的地址。票据不落库、不进日志。老连接缺该字段时取默认值（Enabled = false），行为不变。
/// </para>
/// </summary>
public sealed class VmwareOptions
{
    public bool Enabled { get; set; }

    /// <summary>虚拟机的 BIOS/实例 UUID。用它而不是 ManagedObjectReference 定位，改名、重新注册后仍能找到。</summary>
    public string InstanceUuid { get; set; } = string.Empty;

    /// <summary>导入时的虚拟机名称，仅用于提示；UUID 找不到时作为兜底匹配。</summary>
    public string VmName { get; set; } = string.Empty;

    /// <summary>ESXi 证书的 SHA-256 指纹（十六进制）。公钥证书的摘要，不是秘密。只接受与之一致的证书。</summary>
    public string CertPinSha256 { get; set; } = string.Empty;

    /// <summary>字段均为值类型或不可变字符串，浅拷贝即完整拷贝。</summary>
    public VmwareOptions Clone() => (VmwareOptions)MemberwiseClone();
}
