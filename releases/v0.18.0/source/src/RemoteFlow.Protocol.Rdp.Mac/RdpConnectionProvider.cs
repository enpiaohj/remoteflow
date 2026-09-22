using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Rdp.Mac;

/// <summary>
/// macOS RDP 协议 Provider —— 应用内嵌入式 FreeRDP。
/// 仅在 macOS 且 libremoteflow_rdp.dylib 可加载时可用；否则 UI 回落 <c>RdpLauncher</c>（外部客户端）。
/// </summary>
public sealed class RdpConnectionProvider(ILoggerFactory loggerFactory, IHostKeyRepository hostKeys) : IConnectionProvider
{
    public ProtocolType Protocol => ProtocolType.Rdp;

    public int DefaultPort => 3389;

    public bool IsAvailable(out string? unavailableReason)
    {
        if (!OperatingSystem.IsMacOS())
        {
            unavailableReason = "内嵌 RDP 仅 macOS 可用。";
            return false;
        }

        // 用 .NET 的 NativeLibrary 解析（含 runtimes/<rid>/native 与 MonoBundle），
        // 而不是 OS 裸加载。dylib 或其 FreeRDP 依赖缺失都视为不可用。
        if (NativeLibrary.TryLoad("libremoteflow_rdp", typeof(RdpConnectionProvider).Assembly, null, out var h))
        {
            NativeLibrary.Free(h);
            unavailableReason = null;
            return true;
        }

        unavailableReason = "内嵌 RDP 不可用（缺 libremoteflow_rdp.dylib 或 FreeRDP，需 brew install freerdp + bash native/rdp/build.sh）。";
        return false;
    }

    public IRemoteSession CreateSession(SessionRequest request) => new RdpSession(request, loggerFactory, hostKeys);
}
