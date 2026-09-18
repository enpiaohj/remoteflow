using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Settings;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 检测「当前 Vault 与本地同步指针不是同一套」并把本地同步状态清干净。
/// <para>
/// 触发场景：服务端账号被重建 / 数据被清空后重新初始化 Vault（VaultId 变化），
/// 而客户端仍保留着旧 Vault 的游标与各实体 server_version —— 于是 reconcile 认为
/// 「都已同步」，客户端既不推也不拉，界面永远显示「已同步」却什么都不同步。
/// 清掉同步表后，下一次同步会把本机既有数据作为新 Vault 的全量内容重新推送。
/// </para>
/// </summary>
public sealed class VaultSwitchDetector(
    SqliteSyncStore store,
    AppSettings settings,
    JsonSettingsStore settingsStore,
    ILogger<VaultSwitchDetector> logger)
{
    /// <summary>
    /// 比对并（必要时）重置。返回是否发生了重置。
    /// <paramref name="vaultId"/> 为当前服务端 Vault 标识（null 表示 Vault 不存在）。
    /// </summary>
    public async Task<bool> EnsureSameVaultAsync(string? vaultId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(vaultId))
        {
            return false;
        }

        if (string.Equals(settings.CloudVaultId, vaultId, StringComparison.Ordinal))
        {
            return false;
        }

        var reset = false;
        if (!string.IsNullOrEmpty(settings.CloudVaultId))
        {
            logger.LogWarning(
                "检测到 Vault 已更换（{Old} → {New}）——清除本地同步指针后重新全量同步",
                settings.CloudVaultId, vaultId);
            await store.ResetAsync(ct);
            reset = true;
        }

        settings.CloudVaultId = vaultId;
        await settingsStore.SaveAsync(settings, ct);
        return reset;
    }
}
