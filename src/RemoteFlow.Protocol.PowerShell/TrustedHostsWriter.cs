using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RemoteFlow.Protocol.PowerShell;

/// <summary>把一台主机加入本机 TrustedHosts 的结果。</summary>
public enum HostTrustOutcome
{
    /// <summary>已在列表中（本次添加或原本就有）。</summary>
    Trusted,

    /// <summary>用户在 Windows 管理员确认框中取消。</summary>
    Declined,

    /// <summary>执行失败（组策略锁定、WinRM 服务被禁用等）。</summary>
    Failed
}

/// <summary>维护本机 PowerShell 远程的信任列表。</summary>
public interface ITrustedHostsWriter
{
    Task<HostTrustOutcome> AddAsync(string host, CancellationToken ct = default);
}

/// <summary>
/// 以提权进程把<b>一个</b>主机地址追加进 <c>WSMan:\localhost\Client\TrustedHosts</c>。
/// 提权由 Windows 的管理员确认框完成；不覆盖原有条目，也从不写入通配符。
/// </summary>
public sealed partial class ElevatedTrustedHostsWriter(ILogger<ElevatedTrustedHostsWriter> logger) : ITrustedHostsWriter
{
    private const int ElevationCancelled = 1223;

    /// <summary>
    /// 地址会拼进提权执行的脚本，所以只接受主机名与 IP 会用到的字符：
    /// 引号、空格、分号、<c>$</c>、通配符等一律拒绝。
    /// </summary>
    internal static bool IsSafeHost(string host) => host.Length is > 0 and <= 253 && SafeHost().IsMatch(host);

    internal static string BuildScript(string host)
    {
        if (!IsSafeHost(host))
        {
            throw new ArgumentException("主机地址含有不允许的字符。", nameof(host));
        }

        // 修改信任列表要求本机 WinRM 服务在运行；原本没运行的，改完停回去。
        return $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $name = '{{host}}'
                $started = (Get-Service WinRM).Status -ne 'Running'
                if ($started) { Start-Service WinRM }
                try {
                    $list = @(([string](Get-Item WSMan:\localhost\Client\TrustedHosts).Value) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
                    if (($list -notcontains $name) -and ($list -notcontains '*')) {
                        Set-Item WSMan:\localhost\Client\TrustedHosts -Value $name -Concatenate -Force
                    }
                }
                finally { if ($started) { Stop-Service WinRM } }
                exit 0
            }
            catch { exit 1 }
            """;
    }

    public async Task<HostTrustOutcome> AddAsync(string host, CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(host)));
        var start = new ProcessStartInfo(PowerShellSession.PowerShellPath)
        {
            Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            // 管理员确认框会阻塞启动调用，放到后台线程，避免卡住界面。
            using var process = await Task.Run(() => Process.Start(start), ct);
            if (process is null)
            {
                return HostTrustOutcome.Failed;
            }

            await process.WaitForExitAsync(ct);
            var outcome = process.ExitCode == 0 ? HostTrustOutcome.Trusted : HostTrustOutcome.Failed;
            // 改动了本机的系统设置，留下可追溯的记录。
            logger.LogInformation("加入本机 TrustedHosts：{Host}，结果 {Outcome}", host, outcome);
            return outcome;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ElevationCancelled)
        {
            logger.LogInformation("加入本机 TrustedHosts 被用户取消：{Host}", host);
            return HostTrustOutcome.Declined;
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning("无法以管理员身份启动 PowerShell：{Code}", ex.NativeErrorCode);
            return HostTrustOutcome.Failed;
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._:\-\[\]]+\z")]
    private static partial Regex SafeHost();
}
