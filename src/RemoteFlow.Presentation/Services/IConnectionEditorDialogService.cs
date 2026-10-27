using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.Services;

/// <summary>
/// 可将调用方指定的用户分组传入连接编辑器的对话框服务能力。
/// 不支持该能力的平台继续使用 <see cref="IDialogService.EditConnectionAsync"/> 的默认分组行为。
/// </summary>
public interface IConnectionEditorDialogService
{
    Task<ConnectionEditorResult?> EditConnectionAsync(
        ConnectionProfile? existing,
        ProtocolType? preselectedProtocol,
        Guid defaultGroupId);
}

/// <summary>
/// 新建连接时预填的内容：引用的凭据、连接名称、协议、分组。全部可空，未提供的沿用编辑器默认。
/// 预填只是起点——编辑器里每一项仍可由用户继续修改。
/// </summary>
public sealed record ConnectionEditorSeed(
    Guid? CredentialId = null,
    string? Name = null,
    ProtocolType? Protocol = null,
    Guid? GroupId = null);

/// <summary>
/// 支持用 <see cref="ConnectionEditorSeed"/> 预填新建连接的对话框服务能力（如「用此凭据新建主机连接」）。
/// 不支持的平台回退到只带协议预选的 <see cref="IDialogService.EditConnectionAsync"/>。
/// </summary>
public interface IConnectionSeedDialogService
{
    Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing, ConnectionEditorSeed seed);
}
