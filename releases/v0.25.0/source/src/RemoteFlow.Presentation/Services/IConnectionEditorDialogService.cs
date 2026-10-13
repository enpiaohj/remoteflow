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
