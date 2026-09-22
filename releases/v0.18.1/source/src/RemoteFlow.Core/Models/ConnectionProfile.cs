namespace RemoteFlow.Core.Models;

/// <summary>
/// 连接配置（连接资产）。这是 RemoteFlow 的核心领域对象。
/// <para>
/// 安全约束：本对象<b>只保存 <see cref="CredentialId"/> 引用</b>，
/// 任何情况下都不得持有明文 Password / Private Key。
/// Secret 的读取由 Credential Vault 在建立连接的瞬间完成。
/// </para>
/// </summary>
public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>显示名称，如 DC01。列表中的主视觉字段。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>主机名或 IP。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>端口。新建时按协议自动带出默认值，允许手工指定非标准端口。</summary>
    public int Port { get; set; }

    public ProtocolType Protocol { get; set; }

    /// <summary>所属分组。一个连接只能有一个主分组，可为空表示未分组。</summary>
    public Guid? GroupId { get; set; }

    /// <summary>引用的凭据 Id。可为空表示连接时再询问。</summary>
    public Guid? CredentialId { get; set; }

    public bool Favorite { get; set; }

    public string Notes { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastConnectedAt { get; set; }

    /// <summary>标签 Id 集合。标签用于跨分组的多维筛选。</summary>
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>RDP 专项参数，仅当 <see cref="Protocol"/> 为 Rdp 时有意义。</summary>
    public RdpOptions Rdp { get; set; } = new();

    /// <summary>SSH 专项参数，仅当 <see cref="Protocol"/> 为 Ssh 时有意义。</summary>
    public SshOptions Ssh { get; set; } = new();

    /// <summary>VNC 专项参数，仅当 <see cref="Protocol"/> 为 Vnc 时有意义。</summary>
    public VncOptions Vnc { get; set; } = new();

    /// <summary>按协议返回标准默认端口。</summary>
    public static int GetDefaultPort(ProtocolType protocol) => protocol switch
    {
        ProtocolType.Rdp => 3389,
        ProtocolType.Ssh => 22,
        ProtocolType.Vnc => 5900,
        _ => 0
    };

    /// <summary>创建一份副本，用于「复制连接」功能。副本拥有新的 Id 与名称。</summary>
    public ConnectionProfile Clone(string newName)
    {
        return new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = newName,
            Host = Host,
            Port = Port,
            Protocol = Protocol,
            GroupId = GroupId,
            CredentialId = CredentialId,
            Favorite = false,
            Notes = Notes,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            LastConnectedAt = null,
            TagIds = [.. TagIds],
            Rdp = Rdp.Clone(),
            Ssh = Ssh.Clone(),
            Vnc = Vnc.Clone()
        };
    }
}
