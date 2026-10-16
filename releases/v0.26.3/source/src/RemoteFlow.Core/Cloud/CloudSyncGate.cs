namespace RemoteFlow.Core.Cloud;

/// <summary>
/// 云同步启用开关。单例，由 <see cref="ICloudSyncService"/> 在登录 / 登出时翻转，
/// 变更追踪器据此决定是否登记 Outbox——省去在启用时重新装配业务服务。
/// </summary>
public sealed class CloudSyncGate
{
    public bool Enabled { get; set; }
}
