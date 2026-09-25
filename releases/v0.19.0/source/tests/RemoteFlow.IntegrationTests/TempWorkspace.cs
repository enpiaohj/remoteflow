namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 每个测试用一个独立的临时目录，互不干扰；测试结束自动清理。
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "RemoteFlow.IT", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, "remoteflow.db");

    public string VaultPath => Path.Combine(Root, "vault.dat");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite 连接池可能仍持有文件句柄，测试环境下清理失败可忽略。
        }
    }
}
