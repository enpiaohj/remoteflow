using Renci.SshNet.Common;
using RemoteFlow.Core.Models;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>界面走查（2026-11-27）各项修复中不依赖 WPF 的部分。</summary>
public sealed class UxAuditFixTests
{
    [Fact]
    public void 状态栏按会话实际状态描述_开着会话时不说无活动会话()
    {
        Assert.Equal("无会话", MainViewModel.DescribeSessions([]));
        Assert.Equal("已连接 2 个会话", MainViewModel.DescribeSessions([ConnectionState.Connected, ConnectionState.Connected]));
        Assert.Equal("8 个会话 · 1 个连接中 · 7 个已中断", MainViewModel.DescribeSessions(
            [ConnectionState.Connecting, .. Enumerable.Repeat(ConnectionState.Failed, 6), ConnectionState.Disconnected]));
        Assert.Equal("3 个会话 · 1 个已连接 · 1 个连接中 · 1 个已中断", MainViewModel.DescribeSessions(
            [ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Failed]));
    }

    [Fact]
    public void 按状态排序时在线在前_离线在后()
    {
        var online = new ConnectionItemViewModel(new ConnectionProfile { Name = "b" });
        online.SetProbeResult(true);
        var offline = new ConnectionItemViewModel(new ConnectionProfile { Name = "a" });
        offline.SetProbeResult(false);
        var unknown = new ConnectionItemViewModel(new ConnectionProfile { Name = "c" });

        var sorted = new[] { offline, unknown, online }.OrderBy(ConnectionsPageViewModel.StatusRank).ToList();

        Assert.Equal([online, unknown, offline], sorted);
        Assert.Contains(ConnectionSortMode.Status, new ConnectionsPageViewModelSortOptions().Options);
    }

    private sealed class ConnectionsPageViewModelSortOptions
    {
        public IReadOnlyList<ConnectionSortMode> Options { get; } = Enum.GetValues<ConnectionSortMode>();
    }

    [Theory]
    [InlineData(CredentialType.LocalPassword, "", "zhang", "zhang")]
    [InlineData(CredentialType.WindowsDomain, "CORP", "admin", "CORP\\admin")]
    public void 新建凭据名称留空时按用户名生成(CredentialType type, string domain, string user, string expected)
    {
        var editor = new CredentialEditorViewModel(null) { Type = type, Domain = domain, Username = user, Name = "" };

        var credential = editor.Build();

        Assert.NotNull(credential);
        Assert.Equal(expected, credential!.Name);
    }

    [Fact]
    public void 名称和用户名都空时仍提示填写名称()
    {
        var editor = new CredentialEditorViewModel(null) { Type = CredentialType.VncPassword, Name = "" };
        Assert.Null(editor.Build());
        Assert.Contains("名称", editor.ValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SSH已连上端口但被对方断开_不再归为网络不通()
    {
        var ex = new SshConnectionException("An established connection was aborted by the server.");

        var code = SshErrorMapper.Map(ex, ConnectionErrorCode.None);

        Assert.Equal(ConnectionErrorCode.ProtocolNegotiationFailed, code);
        Assert.Contains("已连上目标端口", SshErrorMapper.DescribeSpecific(ex, code), StringComparison.Ordinal);
        Assert.Null(SshErrorMapper.DescribeSpecific(new SshAuthenticationException("x"), ConnectionErrorCode.AuthenticationFailed));
    }

    [Fact]
    public void VNC未指定设备类型时用通用显示器图标()
    {
        var info = DeviceTypeCatalog.InferFromProtocol(ProtocolType.Vnc);
        Assert.Equal("DeviceIcon.Display", info.IconResourceKey);
        Assert.DoesNotContain(DeviceTypeCatalog.All, item => item.IconResourceKey == info.IconResourceKey);
    }

    [Fact]
    public void 列表项的无障碍名称是设备名而不是类名()
        => Assert.Equal("PRD-DB-01", new ConnectionItemViewModel(new ConnectionProfile { Name = "PRD-DB-01" }).ToString());
}
