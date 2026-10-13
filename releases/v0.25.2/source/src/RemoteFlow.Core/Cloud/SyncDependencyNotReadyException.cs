namespace RemoteFlow.Core.Cloud;

/// <summary>
/// 拉取时某个实体依赖的另一个实体还没落地（如 <c>credential-secret</c> 先于其
/// <c>credential</c> 元数据到达——服务端按 Revision 排序，父实体被后续更新推高了 Revision 时会这样）。
/// <see cref="SyncCoordinator"/> 捕获后延后重试，而不是让整轮同步失败。
/// </summary>
public sealed class SyncDependencyNotReadyException(string entityType, string entityId, string missingDependencyType)
    : Exception($"{entityType}/{entityId} 依赖的 {missingDependencyType} 尚未落地，稍后重试。")
{
    public string EntityType { get; } = entityType;

    public string EntityId { get; } = entityId;

    public string MissingDependencyType { get; } = missingDependencyType;
}
