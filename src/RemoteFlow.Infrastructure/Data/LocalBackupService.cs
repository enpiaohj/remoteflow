using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RemoteFlow.Infrastructure.Data;

/// <summary>一次本地备份的结果。</summary>
/// <param name="BackupDirectory">实际写入的带时间戳子目录。</param>
/// <param name="FileNames">目录内已写入的文件名。</param>
public sealed record LocalBackupResult(string BackupDirectory, IReadOnlyList<string> FileNames);

/// <summary>
/// 应用数据的完整本地备份：把 SQLite 数据库、凭据保险库与设置文件一并复制到
/// 用户选定目录下的带时间戳子目录。
/// <para>
/// 一致性：复制数据库前先执行 <c>wal_checkpoint(TRUNCATE)</c>，把 WAL 中已提交的改动
/// 落回主库文件，避免只复制到半截数据。凭据保险库按本机加密，换机器 / 换账户通常无法直接解密，
/// 这一点由界面文案提示用户（跨设备迁移走「导出 .rfbackup」）。
/// </para>
/// </summary>
public sealed class LocalBackupService(
    RemoteFlowDatabase database,
    AppPaths paths,
    ILogger<LocalBackupService> logger)
{
    /// <summary>
    /// 把当前应用数据备份到 <paramref name="destinationRoot"/> 下的一个新子目录，
    /// 子目录名形如 <c>RemoteFlow-备份-20260904-143012</c>。
    /// </summary>
    public async Task<LocalBackupResult> BackupToAsync(string destinationRoot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        CheckpointDatabase();

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(destinationRoot, $"RemoteFlow-备份-{stamp}");
        Directory.CreateDirectory(backupDir);

        // 数据库主文件必须复制；WAL / SHM 若仍存在也一并带走以求稳妥；
        // 保险库与设置文件按存在与否复制。
        var sources = new[]
        {
            paths.DatabasePath,
            paths.DatabasePath + "-wal",
            paths.DatabasePath + "-shm",
            paths.VaultPath,
            paths.SettingsPath,
        };

        var written = new List<string>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(source))
            {
                continue;
            }

            var fileName = Path.GetFileName(source);
            var target = Path.Combine(backupDir, fileName);

            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, ct);
            }

            written.Add(fileName);
        }

        logger.LogInformation("已创建本地备份：{Dir}（{Count} 个文件）", backupDir, written.Count);
        return new LocalBackupResult(backupDir, written);
    }

    /// <summary>把 WAL 中已提交的改动落回主库文件，让随后复制的 .db 是完整快照。</summary>
    private void CheckpointDatabase()
    {
        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // checkpoint 失败不阻断备份：此时 .db + -wal 一起复制仍是可恢复的完整状态。
            logger.LogWarning(ex, "备份前 WAL checkpoint 未成功，将连同 -wal 文件一起复制。");
        }
    }
}
