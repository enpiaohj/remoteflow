using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.Presentation.Services;

/// <summary>
/// 默认新建连接分组编排：永久保证内置“我的设备”存在并返回其 Id。
/// 仍保留设置依赖以兼容现有 DI 构造签名；旧 <c>DefaultGroupSeedDone</c> 不再决定是否创建。
/// </summary>
public sealed class DefaultGroupResolver(GroupService groupService, AppSettings settings, JsonSettingsStore settingsStore)
{
    public Task<Guid?> ResolveDefaultAsync(CancellationToken ct = default)
    {
        _ = settings;
        _ = settingsStore;
        return groupService.EnsureSeedAsync(ct);
    }
}
