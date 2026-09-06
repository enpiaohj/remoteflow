using System.Diagnostics;
using System.Text;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// macOS RDP 首版：把连接配置写成标准 <c>.rdp</c> 文件并交给系统 RDP 客户端
/// （Microsoft「Windows App」/「Microsoft Remote Desktop」）打开。
/// <para>
/// <b>说明：</b>方案 v1.3 §8.E 计划用 FreeRDP 做「应用内嵌入式」RDP 画面；在其落地前，
/// 本 stopgap 让 macOS 端 RDP 立即可用。口令不写入 <c>.rdp</c>（明文风险），
/// 由外部客户端按用户名提示输入。
/// </para>
/// </summary>
public static class RdpLauncher
{
    /// <summary>生成 .rdp 并用系统默认程序打开。返回向用户展示的说明文本。</summary>
    public static string Launch(ConnectionProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("screen mode id:i:1");
        sb.AppendLine($"full address:s:{profile.Host}:{profile.Port}");

        var domain = profile.Rdp.Domain?.Trim() ?? string.Empty;
        if (domain.Length > 0)
        {
            sb.AppendLine($"domain:s:{domain}");
        }

        // 用户名来自凭据（在 UI 层解析后传入 Profile 前无法拿到），此处只写连接自身可得的信息。
        sb.AppendLine("prompt for credentials:i:1");
        sb.AppendLine("administrative session:i:0");
        sb.AppendLine($"redirectclipboard:i:{(profile.Rdp.RedirectClipboard ? 1 : 0)}");
        sb.AppendLine($"audiomode:i:{(profile.Rdp.RedirectAudio ? 0 : 2)}");
        sb.AppendLine($"use multimon:i:{(profile.Rdp.UseMultimon ? 1 : 0)}");

        if (profile.Rdp.DisplayMode == RdpDisplayMode.FixedResolution)
        {
            sb.AppendLine($"desktopwidth:i:{profile.Rdp.DesktopWidth}");
            sb.AppendLine($"desktopheight:i:{profile.Rdp.DesktopHeight}");
            sb.AppendLine("screen mode id:i:1");
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

        return $"已生成 {Path.GetFileName(path)} 并交给系统 RDP 客户端打开。\n" +
               "首版 macOS RDP 经系统客户端连接；应用内嵌入式画面（FreeRDP）见方案 §8.E。";
    }
}
