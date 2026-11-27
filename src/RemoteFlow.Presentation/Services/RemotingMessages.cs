using System.Text;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Presentation.Services;

/// <summary>远程启用 PowerShell 远程的确认与结果文案。改动他人电脑的设置前后都要说清楚。</summary>
public static class RemotingMessages
{
    private const string Undo =
        "撤销方法（在目标电脑上以管理员运行）：\n"
        + "Disable-PSRemoting -Force\n"
        + "Stop-Service WinRM; Set-Service WinRM -StartupType Manual\n"
        + "Disable-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*'";

    private const string PolicyPath = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static string PublicNetwork(RemotingTargetInfo target)
        => $"目标电脑 {target.ComputerName} 的网络「{string.Join("、", target.PublicNetworks)}」属于「公用」类别，"
           + "在这种网络上开启远程管理风险较高，因此没有做任何改动。\n\n"
           + "确认该网络可信后，请在目标电脑上把它改为「专用」，再重试。";

    public static string Confirm(string host, RemotingTargetInfo target)
    {
        var text = new StringBuilder()
            .AppendLine($"目标：{target.ComputerName}（{host}）")
            .AppendLine($"系统：{target.OsName}")
            .AppendLine($"当前 WinRM 服务：{target.WinRmState}（启动类型 {target.WinRmStartMode}）")
            .AppendLine()
            .AppendLine("将在目标电脑上执行 Enable-PSRemoting，改动它的系统设置：")
            .AppendLine("• 启动 WinRM 服务并设为自动启动")
            .AppendLine("• 创建 HTTP 5985 监听，放行对应的防火墙规则");

        if (!target.PartOfDomain && !target.IsServer)
        {
            text.AppendLine("• 非域电脑上还可能设置 LocalAccountTokenFilterPolicy=1，即放开本地管理员账户经网络登录时的权限限制")
                .AppendLine(target.UacEnabled
                    ? "  目标开启着 UAC，该设置会生效并保留（撤掉后本地管理员账户可能无法远程登录）"
                    : "  目标未开启 UAC，该设置不起作用，如被改动会自动恢复原状");
        }

        return text.AppendLine()
            .Append("请只对你有权管理、且处于可信网络中的电脑执行。要继续吗？")
            .ToString();
    }

    public static string Result(RemotingEnableResult result)
    {
        var after = result.After;
        var text = new StringBuilder()
            .AppendLine($"{after.ComputerName} 的 WinRM 服务：{after.WinRmState}（启动类型 {after.WinRmStartMode}）")
            .AppendLine(result.PortOpen
                ? $"端口 {PowerShellEndpoint.DefaultPort}：已可以访问"
                : $"端口 {PowerShellEndpoint.DefaultPort}：仍然访问不到，请检查目标或中间网络的防火墙");

        switch (result.PolicyOutcome)
        {
            case TokenFilterPolicyOutcome.Restored:
                text.AppendLine("LocalAccountTokenFilterPolicy 曾被改动；目标未开启 UAC，它不起作用，已恢复原状。");
                break;
            case TokenFilterPolicyOutcome.Kept:
                var previous = result.Before.TokenFilterPolicy;
                text.AppendLine($"LocalAccountTokenFilterPolicy 已变为 {after.TokenFilterPolicy}（原来{(previous is null ? "未设置" : $"为 {previous}")}），已保留："
                                + "本地管理员账户经网络登录时将拥有完整权限。")
                    .AppendLine("如需恢复（可能导致本地管理员账户无法远程登录）：")
                    .AppendLine(previous is null
                        ? $"Remove-ItemProperty -Path '{PolicyPath}' -Name LocalAccountTokenFilterPolicy"
                        : $"Set-ItemProperty -Path '{PolicyPath}' -Name LocalAccountTokenFilterPolicy -Value {previous}");
                break;
        }

        return text.AppendLine().Append(Undo).ToString();
    }
}
