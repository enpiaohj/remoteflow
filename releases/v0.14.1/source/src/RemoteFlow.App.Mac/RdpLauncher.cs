using System.Diagnostics;
using System.Text;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// macOS RDP 首版（方案 v1.3 §8.E stopgap）。优先用 FreeRDP 命令行客户端
/// （<c>sdl-freerdp</c> / <c>xfreerdp</c>）——它能直接吃用户名 / 域 / 口令；
/// 找不到时回落生成标准 <c>.rdp</c> 交系统 RDP 客户端（口令不落盘，由外部客户端提示输入）。
/// <para>应用内嵌入式 RDP 画面（P/Invoke libfreerdp）仍为后续项。</para>
/// </summary>
public static class RdpLauncher
{
    private static readonly string[] FreeRdpNames =
    {
        "sdl3-freerdp", "sdl-freerdp", "xfreerdp", "wlfreerdp", "freerdp",
    };

    private static readonly string[] FreeRdpDirs =
    {
        "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin",
    };

    private static IEnumerable<string> FreeRdpCandidates =>
        from d in FreeRdpDirs from n in FreeRdpNames select Path.Combine(d, n);

    public static string Launch(ConnectionProfile profile, ResolvedCredential? credential)
    {
        var freeRdp = FreeRdpCandidates.FirstOrDefault(File.Exists);
        return freeRdp is not null
            ? LaunchFreeRdp(freeRdp, profile, credential)
            : LaunchViaRdpFile(profile, credential);
    }

    // ── FreeRDP CLI（带凭据） ────────────────────────────────────
    private static string LaunchFreeRdp(string bin, ConnectionProfile profile, ResolvedCredential? cred)
    {
        var args = new List<string>
        {
            $"/v:{profile.Host}:{profile.Port}",
            "/cert:ignore",
            "+clipboard",
            "/dynamic-resolution",
        };

        var domain = FirstNonEmpty(cred?.Domain, profile.Rdp.Domain);
        if (!string.IsNullOrEmpty(cred?.Username))
        {
            args.Add($"/u:{cred.Username}");
        }
        if (!string.IsNullOrEmpty(domain))
        {
            args.Add($"/d:{domain}");
        }
        if (!string.IsNullOrEmpty(cred?.Password))
        {
            args.Add($"/p:{cred.Password}");
        }

        if (profile.Rdp.RedirectAudio)
        {
            args.Add("/sound");
        }
        if (profile.Rdp.UseMultimon)
        {
            args.Add("/multimon");
        }
        if (profile.Rdp.DisplayMode == RdpDisplayMode.FixedResolution)
        {
            args.Add($"/size:{profile.Rdp.DesktopWidth}x{profile.Rdp.DesktopHeight}");
        }

        var psi = new ProcessStartInfo { FileName = bin, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Process.Start(psi);

        // 日志 / 返回文本里不回显口令。
        return $"已用 {Path.GetFileName(bin)} 连接 {profile.Host}:{profile.Port}" +
               (string.IsNullOrEmpty(cred?.Username) ? "（未附凭据）" : $"（账号 {cred.Username}）") + "。\n" +
               "画面在 FreeRDP 独立窗口；应用内嵌入式画面见方案 §8.E。";
    }

    // ── .rdp 回落（口令不落盘） ─────────────────────────────────
    private static string LaunchViaRdpFile(ConnectionProfile profile, ResolvedCredential? cred)
    {
        var sb = new StringBuilder();
        sb.AppendLine("screen mode id:i:1");
        sb.AppendLine($"full address:s:{profile.Host}:{profile.Port}");

        var domain = FirstNonEmpty(cred?.Domain, profile.Rdp.Domain);
        if (!string.IsNullOrEmpty(domain))
        {
            sb.AppendLine($"domain:s:{domain}");
        }
        if (!string.IsNullOrEmpty(cred?.Username))
        {
            sb.AppendLine($"username:s:{cred.Username}");
        }

        sb.AppendLine("prompt for credentials:i:1");
        sb.AppendLine($"redirectclipboard:i:{(profile.Rdp.RedirectClipboard ? 1 : 0)}");
        sb.AppendLine($"audiomode:i:{(profile.Rdp.RedirectAudio ? 0 : 2)}");
        sb.AppendLine($"use multimon:i:{(profile.Rdp.UseMultimon ? 1 : 0)}");

        if (profile.Rdp.DisplayMode == RdpDisplayMode.FixedResolution)
        {
            sb.AppendLine($"desktopwidth:i:{profile.Rdp.DesktopWidth}");
            sb.AppendLine($"desktopheight:i:{profile.Rdp.DesktopHeight}");
        }
        else
        {
            sb.AppendLine("smart sizing:i:1");
            sb.AppendLine("dynamic resolution:i:1");
        }

        var dir = Path.Combine(Path.GetTempPath(), "RemoteFlow-rdp");
        Directory.CreateDirectory(dir);
        var safeName = string.Concat((profile.Name ?? "connection").Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var path = Path.Combine(dir, $"{safeName}.rdp");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            ArgumentList = { path },
            UseShellExecute = false,
        });

        return $"已生成 {Path.GetFileName(path)} 交系统 RDP 客户端打开" +
               (string.IsNullOrEmpty(cred?.Username) ? "" : $"（账号 {cred.Username} 已预填）") + "。\n" +
               "口令出于安全不写入 .rdp，请在客户端输入；\n" +
               "装 FreeRDP（brew install freerdp）后可带口令直连、见方案 §8.E。";
    }

    private static string FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a! : (b ?? string.Empty);
}
