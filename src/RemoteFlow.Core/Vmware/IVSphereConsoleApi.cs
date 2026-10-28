namespace RemoteFlow.Core.Vmware;

/// <summary>ESXi 管理 API 的最小抽象；调用方负责凭据解析与生命周期，不在模型中保存秘密。</summary>
public interface IVSphereConsoleApi : IAsyncDisposable
{
    /// <summary>登录后可用：服务器自报的类型与版本（不含任何秘密）。</summary>
    VmwareServerInfo? ServerInfo { get; }

    Task LoginAsync(string userName, string password, CancellationToken ct = default);
    Task<IReadOnlyList<VsphereVm>> ListVirtualMachinesAsync(CancellationToken ct = default);
    Task<WebMksTicket> AcquireWebMksTicketAsync(string vmReference, CancellationToken ct = default);

    /// <summary>执行电源操作。Task 型操作等到服务端任务终态；正常关机/重启只发出请求（见 <see cref="VmPowerResult"/>）。</summary>
    Task<VmPowerResult> PowerAsync(string vmReference, VmPowerAction action, CancellationToken ct = default);

    /// <summary>
    /// 设置虚拟机控制台分辨率（vSphere 公开方法 SetScreenResolution）。需要客户机运行 VMware Tools，
    /// 否则服务器返回 ToolsUnavailable。ESXi 的 RFB 服务不支持由客户端改分辨率，只能走这条路。
    /// </summary>
    Task SetScreenResolutionAsync(string vmReference, int width, int height, CancellationToken ct = default);

    /// <summary>读取虚拟机显卡的显存（KB）。取不到返回 null。显存决定客户机最大能用多大的分辨率。</summary>
    Task<int?> GetVideoMemoryKbAsync(string vmReference, CancellationToken ct = default);

    Task<IReadOnlyList<VmSnapshot>> ListSnapshotsAsync(string vmReference, CancellationToken ct = default);
    Task CreateSnapshotAsync(string vmReference, string name, string description, CancellationToken ct = default);
    Task RevertSnapshotAsync(string snapshotReference, CancellationToken ct = default);
    Task RemoveSnapshotAsync(string snapshotReference, CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);
}

/// <summary>按 ESXi 端点 + 证书指纹 + 账号建立已登录的 API 会话。实现负责证书固定。</summary>
public interface IVmwareConnector
{
    /// <summary>只做 TLS 握手取证书指纹，供用户首次核对；不发送任何应用数据，也不信任该证书。</summary>
    Task<string> FetchCertificatePinAsync(string host, int port, CancellationToken ct = default);

    /// <summary>返回已登录的 API；调用方负责释放（释放时注销）。</summary>
    Task<IVSphereConsoleApi> ConnectAsync(string host, int port, string certPinSha256,
        string userName, string password, CancellationToken ct = default);
}

/// <param name="ToolsStatus">
/// VMware Tools 运行状态（guest.toolsRunningStatus 原值，如 guestToolsRunning / guestToolsNotRunning）；
/// 未取到时为 null。它决定鼠标能否精确定位、分辨率能否随窗口调整。
/// </param>
public sealed record VsphereVm(string Reference, string Name, string PowerState, string? InstanceUuid, string? ToolsStatus = null)
{
    /// <summary>VMware Tools 是否在运行。状态未知时按未运行处理（宁可提示，也不假装可用）。</summary>
    public bool ToolsRunning => ToolsStatus is "guestToolsRunning" or "guestToolsExecutingScripts";
}

/// <summary>服务器自报信息：用于识别来源类型并在界面显示版本。</summary>
public sealed record VmwareServerInfo(VmwareSourceKind Kind, string FullName, string Version, string Build);

public enum VmPowerAction
{
    PowerOn,
    /// <summary>经 VMware Tools 的正常关机。</summary>
    ShutdownGuest,
    /// <summary>经 VMware Tools 的正常重启。</summary>
    RebootGuest,
    Suspend,
    /// <summary>强制断电，相当于拔电源，客户机未保存的数据会丢失。</summary>
    PowerOff,
    /// <summary>强制重置，相当于按复位键。</summary>
    Reset
}

/// <summary>电源操作的真实结果：正常关机/重启没有服务端任务，只能说明“请求已发出”。</summary>
public enum VmPowerResult
{
    /// <summary>服务端任务已成功结束。</summary>
    Completed,
    /// <summary>请求已发给客户机，最终是否完成需要看虚拟机状态。</summary>
    RequestSent
}

/// <summary>快照树节点（已按层级展开为带深度的扁平列表）。</summary>
public sealed record VmSnapshot(string Reference, string Name, string Description, DateTimeOffset? CreatedAt, bool IsCurrent, int Depth);

/// <summary>虚拟机控制台分辨率的档位。自适应是默认；固定档用于纯文本控制台等不适合放大的场景。</summary>
public enum VmwareResolutionMode
{
    /// <summary>跟随窗口大小（受显存上限约束）。</summary>
    Auto = 0,
    Hd720 = 1,
    Xga = 2,
    Svga = 3,

    /// <summary>不再请求，保持虚拟机当前分辨率。</summary>
    Keep = 4
}

/// <summary>
/// 把期望的分辨率收敛到虚拟机显存能支撑的范围内。客户机的显卡驱动要求 宽×高×4 字节不超过显存，
/// 超过时它会整个忽略请求，结果是控制台变大了、客户机画面却原地不动并留出大片黑边。
/// </summary>
public static class VmwareResolution
{
    /// <summary>ESXi 里常见的默认显存（KB）。读不到真实值时按它保守估计。</summary>
    public const int DefaultVideoRamKb = 8192;

    /// <summary>固定档对应的分辨率；自适应与保持不变没有固定值，返回 null。</summary>
    public static (int Width, int Height)? PresetSize(VmwareResolutionMode mode) => mode switch
    {
        VmwareResolutionMode.Hd720 => (1280, 720),
        VmwareResolutionMode.Xga => (1024, 768),
        VmwareResolutionMode.Svga => (800, 600),
        _ => null
    };

    /// <summary>档位循环顺序：自适应 → 1280×720 → 1024×768 → 800×600 → 保持不变 → 自适应。</summary>
    public static VmwareResolutionMode Next(VmwareResolutionMode mode) => mode switch
    {
        VmwareResolutionMode.Auto => VmwareResolutionMode.Hd720,
        VmwareResolutionMode.Hd720 => VmwareResolutionMode.Xga,
        VmwareResolutionMode.Xga => VmwareResolutionMode.Svga,
        VmwareResolutionMode.Svga => VmwareResolutionMode.Keep,
        _ => VmwareResolutionMode.Auto
    };

    public static string Label(VmwareResolutionMode mode) => mode switch
    {
        VmwareResolutionMode.Hd720 => "分辨率：1280×720（点击切换）",
        VmwareResolutionMode.Xga => "分辨率：1024×768（点击切换）",
        VmwareResolutionMode.Svga => "分辨率：800×600（点击切换）",
        VmwareResolutionMode.Keep => "分辨率：保持不变（点击切换）",
        _ => "分辨率：自适应窗口（点击切换）"
    };

    public const int MinWidth = 640;
    public const int MinHeight = 480;

    /// <summary>返回不超过显存上限、保持宽高比、宽取 8 的倍数、高取偶数的分辨率。</summary>
    public static (int Width, int Height) Fit(int width, int height, int? videoRamKb)
    {
        var maxPixels = (long)(videoRamKb is > 0 ? videoRamKb.Value : DefaultVideoRamKb) * 1024 / 4;
        double scale = (long)width * height > maxPixels ? Math.Sqrt((double)maxPixels / ((long)width * height)) : 1.0;

        var w = (int)(width * scale) / 8 * 8;
        var h = (int)(height * scale) / 2 * 2;

        // 取整后仍可能略超上限（极端宽高比），逐步收缩到合规为止。
        while ((long)w * h > maxPixels && w > MinWidth)
        {
            w -= 8;
        }

        return (Math.Max(w, MinWidth), Math.Max(h, MinHeight));
    }

    /// <summary>期望尺寸被显存明显压缩（面积不足 90%）。</summary>
    public static bool IsConstrained(int requestedWidth, int requestedHeight, int fittedWidth, int fittedHeight)
        => (long)fittedWidth * fittedHeight < (long)requestedWidth * requestedHeight * 9 / 10;
}

/// <summary>短期敏感数据：禁止序列化、持久化或记录完整对象。</summary>
public sealed class WebMksTicket(string host, int port, string ticket, string? sslThumbprint = null)
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Ticket { get; } = ticket;

    /// <summary>
    /// 票据目标主机证书的 SHA-1 指纹（十六进制，可能带冒号）。vCenter 的票据指向承载虚拟机的 ESXi 主机，
    /// 它经已核对的 vCenter 通道送达，可作为校验该主机证书的依据。独立 ESXi 可能不返回。
    /// </summary>
    public string? SslThumbprint { get; } = sslThumbprint;

    public override string ToString() => nameof(WebMksTicket);
}

/// <summary>只暴露可安全显示的 fault 类别，不携带原始 SOAP 内容。</summary>
public sealed class VsphereApiException(string faultCode)
    : Exception(VmwareErrorText.Describe(faultCode))
{
    public string FaultCode { get; } = faultCode;
}

/// <summary>把 fault 类别翻成面向用户的中文说明。文案只来自这里，不回显服务器返回的任意文本。</summary>
public static class VmwareErrorText
{
    public static string Describe(string faultCode)
    {
        var code = faultCode.Split(':')[0];
        return code switch
        {
            "InvalidLogin" => "ESXi 账号或密码不正确。",
            "NotAuthenticated" => "ESXi 登录已失效，请重试。",
            "NoPermission" => "当前账号没有执行该操作的权限。",
            "RestrictedVersion" => "该 ESXi 授权版本限制了此操作（免费版的写操作通常被禁用）。",
            "InvalidState" => "虚拟机当前状态不允许该操作。",
            "ToolsUnavailable" => "虚拟机未运行 VMware Tools，无法正常关机/重启。可改用强制断电或强制重置。",
            "ManagedObjectNotFound" => "找不到该虚拟机或快照，可能已被删除或重新注册。",
            "TaskTimeout" => "操作等待超时，服务器上可能仍在执行，请稍后刷新确认结果。",
            "TaskFailed" => "服务器报告任务失败。",
            "InvalidTicketEndpoint" => "ESXi 返回的控制台地址无效。",
            "MissingResponseField" => "ESXi 返回的数据缺少必要字段。",
            "InvalidResponse" => "ESXi 返回了无法识别的数据。",
            _ when code.StartsWith("HTTP", StringComparison.Ordinal) => $"ESXi 返回 {code}。",
            _ => $"VMware 请求失败（{faultCode}）。"
        };
    }
}
