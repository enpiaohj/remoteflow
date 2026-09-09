using System.Text;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>内存版 <see cref="ISyncEntitySource"/>：把「本地明文」当作字符串字典管理，记录 Apply 调用。</summary>
public sealed class FakeSyncEntitySource(string entityType) : ISyncEntitySource
{
    private readonly Dictionary<string, string> _local = [];

    public List<(string Id, string? Plaintext, bool Deleted)> Applied { get; } = [];

    public int SchemaVersion => 1;

    public bool Handles(string type) => type == entityType;

    public IReadOnlyList<string> EntityTypes => [entityType];

    public Task<IReadOnlyList<string>> ListEntityIdsAsync(string type, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. _local.Keys]);

    public void SetLocal(string id, string plaintext) => _local[id] = plaintext;

    public void RemoveLocal(string id) => _local.Remove(id);

    public string? GetLocal(string id) => _local.GetValueOrDefault(id);

    public Task<byte[]?> GetPlaintextAsync(string type, string id, CancellationToken ct = default) =>
        Task.FromResult(_local.TryGetValue(id, out var text) ? Encoding.UTF8.GetBytes(text) : null);

    public Task ApplyAsync(
        string type, string id, byte[]? plaintext, bool deleted, int schemaVersion, CancellationToken ct = default)
    {
        var text = plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
        if (deleted)
        {
            _local.Remove(id);
        }
        else if (text is not null)
        {
            _local[id] = text;
        }

        Applied.Add((id, text, deleted));
        return Task.CompletedTask;
    }
}
