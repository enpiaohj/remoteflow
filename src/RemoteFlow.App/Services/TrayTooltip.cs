using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Services;

/// <summary>
/// 托盘图标悬停提示的文案。
/// <para>
/// 第一行是产品名 + 版本号；有会话时第二行汇总各状态的会话数（已连接 / 连接中 / 异常），
/// 接着列出最多 <see cref="MaxListedSessions"/> 个会话名；没有会话时给出一句操作提示。
/// 抽成纯函数，不依赖 NotifyIcon，便于测试。
/// </para>
/// </summary>
internal static class TrayTooltip
{
    /// <summary>系统托盘提示（NotifyIcon.Text）的长度上限，超出会抛异常。</summary>
    public const int MaxLength = 127;

    private const int MaxListedSessions = 3;
    private const int MaxNameLength = 22;

    public static string Build(string version, IReadOnlyList<(string Name, ConnectionState State)> sessions)
    {
        var lines = new List<string> { $"RemoteFlow {version}" };

        if (sessions.Count == 0)
        {
            lines.Add("当前没有活动会话");
            lines.Add("单击显示 / 隐藏，右键打开菜单");
        }
        else
        {
            lines.Add(Summarize(sessions));

            foreach (var (name, _) in sessions.Take(MaxListedSessions))
            {
                lines.Add($"· {ShortName(name)}");
            }

            if (sessions.Count > MaxListedSessions)
            {
                lines.Add($"… 另有 {sessions.Count - MaxListedSessions} 个");
            }
        }

        var text = string.Join('\n', lines);
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    /// <summary>「2 个已连接 · 1 个连接中 · 1 个异常」，数量为 0 的状态不写。</summary>
    private static string Summarize(IReadOnlyList<(string Name, ConnectionState State)> sessions)
    {
        var connected = sessions.Count(s => s.State == ConnectionState.Connected);
        var busy = sessions.Count(s => s.State is ConnectionState.Connecting or ConnectionState.Reconnecting);
        var problem = sessions.Count(s => s.State is ConnectionState.Failed or ConnectionState.Disconnected);

        var parts = new List<string>();
        if (connected > 0)
        {
            parts.Add($"{connected} 个已连接");
        }

        if (busy > 0)
        {
            parts.Add($"{busy} 个连接中");
        }

        if (problem > 0)
        {
            parts.Add($"{problem} 个异常");
        }

        // 只剩断开中 / 已关闭这类过渡态时，仍给出总数，不留空白行。
        return parts.Count > 0 ? string.Join(" · ", parts) : $"{sessions.Count} 个会话";
    }

    private static string ShortName(string name)
    {
        var single = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= MaxNameLength ? single : single[..(MaxNameLength - 1)] + "…";
    }
}
