using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.Presentation.Services;

/// <summary>
/// 默认新建连接分组的 App 层编排：决定「是否首次、是否需要创建默认组」并维护
/// AppSettings.DefaultGroupSeedDone，避免在 GroupService（Application 层）里反向依赖设置存储。
/// </summary>
public sealed class DefaultGroupResolver(GroupService groupService, AppSettings settings, JsonSettingsStore settingsStore)
{
    /// <summary>返回默认新建连接分组 Id；null = 回落未分组。</summary>
    public async Task<Guid?> ResolveDefaultAsync(CancellationToken ct = default)
    {
        Guid? result;
        if (!settings.DefaultGroupSeedDone)
        {
            result = await groupService.EnsureSeedAsync(ct, createIfEmpty: true);
            settings.DefaultGroupSeedDone = true;
            try
            {
                await settingsStore.SaveAsync(settings);
            }
            catch
            {
                // 种子标记保存失败不影响使用：下次启动视为首启，仍只种一次（无普通分组才会种）。
            }
        }
        else
        {
            result = await groupService.EnsureSeedAsync(ct, createIfEmpty: false);
        }

        return result;
    }
}
