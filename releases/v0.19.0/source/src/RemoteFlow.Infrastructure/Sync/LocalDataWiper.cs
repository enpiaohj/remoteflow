using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Infrastructure.Data;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 「清除本地数据并从云端恢复」的落地：清空本机全部业务数据（连接 / 凭据及 Secret /
/// 分组 / 标签 / 连接历史）+ 四张同步表，保留云会话与设备身份。
/// 与 <see cref="SqliteSyncStore.ResetAsync"/>（清同步状态、保留本地业务数据）语义相反。
/// 系统分组「未分组」保留（本机概念，不参与同步）；ssh_host_keys 为本地安全数据，不在此清除。
/// </summary>
public sealed class LocalDataWiper(RemoteFlowDatabase database, ICredentialVault vault, ILogger<LocalDataWiper> logger)
{
    /// <summary>
    /// 事务性清空。凭据的 Secret / 私钥先经平台密钥库删除（不写入 SQLite）。
    /// 成功后 sync_state 已清空 → 游标归 0，下一次同步会从云端完整拉取。
    /// </summary>
    public async Task WipeLocalDataForCloudRestoreAsync(CancellationToken ct = default)
    {
        // 先取全部凭据 Secret 引用并删除（ICredentialVault 不参与 SQLite 事务）。
        var (secretRefs, keyRefs) = await CollectCredentialReferencesAsync(ct);
        foreach (var reference in secretRefs.Concat(keyRefs))
        {
            await vault.DeleteSecretAsync(reference, ct);
        }

        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM connection_tags;
                DELETE FROM connection_history;
                DELETE FROM connections;
                DELETE FROM credentials;
                DELETE FROM tags;
                DELETE FROM connection_groups WHERE is_system = 0;

                DELETE FROM sync_outbox;
                DELETE FROM sync_state;
                DELETE FROM sync_entity_state;
                DELETE FROM sync_conflict;
                """;
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        logger.LogInformation("已清除本地业务数据与同步状态（{SecretRefs} 个凭据 Secret 已从平台保险库删除）",
            secretRefs.Count + keyRefs.Count);
    }

    private async Task<(IReadOnlyList<string> SecretRefs, IReadOnlyList<string> KeyRefs)> CollectCredentialReferencesAsync(
        CancellationToken ct)
    {
        var secrets = new List<string>();
        var keys = new List<string>();

        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret_reference, key_reference FROM credentials;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0) && !string.IsNullOrEmpty(reader.GetString(0)))
            {
                secrets.Add(reader.GetString(0));
            }

            if (!reader.IsDBNull(1) && !string.IsNullOrEmpty(reader.GetString(1)))
            {
                keys.Add(reader.GetString(1));
            }
        }

        return (secrets, keys);
    }
}
