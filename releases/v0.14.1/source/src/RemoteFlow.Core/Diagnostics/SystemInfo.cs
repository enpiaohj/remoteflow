namespace RemoteFlow.Core.Diagnostics;

/// <summary>
/// 本机基础系统信息（资产信息，非机密）。登录后上报给 AppsCloud，供管理台展示设备资产。
/// 不含任何凭据 / 用户数据。
/// </summary>
public sealed record SystemInfo(
    string HostName,
    string OsName,
    string OsVersion,
    string Architecture,
    string CpuModel,
    int CpuPhysicalCores,
    int CpuLogicalCores,
    int CpuFrequencyMhz,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    string DiskSummary,
    IReadOnlyList<SystemDisk> Disks,
    string PrimaryIpv4,
    string CurrentUser,
    string TimeZone,
    long UptimeSeconds,
    string ScreenSummary,
    string RuntimeVersion,
    string ClientVersion);

/// <summary>单个磁盘卷。</summary>
public sealed record SystemDisk(string Name, string FileSystem, long TotalBytes, long FreeBytes);

/// <summary>采集本机基础系统信息。</summary>
public interface ISystemInfoCollector
{
    SystemInfo Collect();
}
