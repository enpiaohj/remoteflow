using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using RemoteFlow.Infrastructure.Sync.Sources;

namespace RemoteFlow.Infrastructure;

/// <summary>
/// 云同步整套服务的注册。两端组合根各调一次；`HttpClient` 由调用方按需配置代理 / 超时。
/// </summary>
public static class CloudSyncRegistration
{
    public static IServiceCollection AddCloudSync(this IServiceCollection services)
    {
        services.AddSingleton<CloudEndpoint>();
        services.AddSingleton<CloudSyncGate>();

        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        });

        services.AddSingleton<ICloudTokenStore, CredentialVaultTokenStore>();
        services.AddSingleton<IVaultKeyStore, CredentialVaultKeyStore>();
        services.AddSingleton<ICloudClient, AppsCloudClient>();

        services.AddSingleton<RecoveryKeyService>();
        services.AddSingleton<VaultMasterKeyService>();
        services.AddSingleton<Core.Diagnostics.ISystemInfoCollector, Diagnostics.SystemInfoCollector>();

        services.AddSingleton<SqliteSyncStore>();
        services.AddSingleton<ISyncEntitySource, ConnectionSyncSource>();
        services.AddSingleton<ISyncEntitySource, CredentialSyncSource>();
        services.AddSingleton<ISyncEntitySource, CredentialSecretSyncSource>();
        services.AddSingleton<ISyncEntitySource, GroupSyncSource>();
        services.AddSingleton<ISyncEntitySource, TagSyncSource>();

        services.AddSingleton<SyncEntityLabeler>();
        services.AddSingleton<ICloudFirstJoinAdopter, CloudFirstJoinAdopter>();
        services.AddSingleton<VaultSwitchDetector>();
        services.AddSingleton<ConflictService>();
        services.AddSingleton<SyncCoordinator>();

        services.AddSingleton<ISyncChangeTracker, OutboxSyncChangeTracker>();
        services.AddSingleton<ICloudSyncService, CloudSyncService>();
        services.AddSingleton<CloudSyncAutoRunner>();

        return services;
    }
}
