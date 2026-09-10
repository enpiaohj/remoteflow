namespace RemoteFlow.Core.Cloud;

/// <summary>同步实体类型标识。与 AppsCloud 的 <c>SyncEntity.EntityType</c> 对应，服务端不解析。</summary>
public static class SyncEntityTypes
{
    public const string Connection = "connection";
    public const string Credential = "credential";

    /// <summary>凭据 Secret 本体（Password / Private Key）。EntityId 与 <see cref="Credential"/> 相同。</summary>
    public const string CredentialSecret = "credential-secret";
    public const string Group = "group";
    public const string Tag = "tag";
}
