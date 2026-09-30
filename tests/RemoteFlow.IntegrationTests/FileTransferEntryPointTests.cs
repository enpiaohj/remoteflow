using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 文件传输的两个入口：会话内侧栏（SessionTabViewModel）与不建立会话的文件传输 Tab
/// （连接列表 / 首页触发的 OpenFileTransfer 命令）。
/// </summary>
public class FileTransferEntryPointTests
{
    // ── 会话内侧栏 ──

    [Fact]
    public void 具备文件传输能力的会话且有对话框时才创建侧栏()
    {
        var withCapability = NewTab(new TransferSession(), dialogs: new StubDialogs());
        var withoutDialogs = NewTab(new TransferSession(), dialogs: null);
        var withoutCapability = NewTab(new PlainSession(), dialogs: new StubDialogs());

        Assert.True(withCapability.SupportsFileTransfer);
        Assert.NotNull(withCapability.FileTransfer);
        Assert.False(withoutDialogs.SupportsFileTransfer);
        Assert.Null(withoutDialogs.FileTransfer);
        Assert.False(withoutCapability.SupportsFileTransfer);
        Assert.Null(withoutCapability.FileTransfer);
    }

    [Fact]
    public void 侧栏标题带连接名()
    {
        var tab = NewTab(new TransferSession(), new StubDialogs());

        Assert.Equal("文件传输 · 测试", tab.FileTransfer!.Title);
    }

    [Fact]
    public async Task 首次展开才发起连接再次展开收起不重复连接()
    {
        var session = new TransferSession { State = ConnectionState.Connected };
        var tab = NewTab(session, new StubDialogs());
        Assert.Equal(0, session.OpenCalls); // 创建 Tab 不连接

        await tab.ToggleFileTransferCommand.ExecuteAsync(null);
        Assert.True(tab.IsFileTransferOpen);
        Assert.Equal(1, session.OpenCalls);
        Assert.Equal(FileTransferConnectionState.Connected, tab.FileTransfer!.State);

        await tab.ToggleFileTransferCommand.ExecuteAsync(null);
        Assert.False(tab.IsFileTransferOpen);

        await tab.ToggleFileTransferCommand.ExecuteAsync(null);
        Assert.True(tab.IsFileTransferOpen);
        Assert.Equal(1, session.OpenCalls);
    }

    [Fact]
    public async Task 会话未连接时展开显示失败说明可稍后重试()
    {
        var session = new TransferSession { State = ConnectionState.Connecting, FailOpen = true };
        var tab = NewTab(session, new StubDialogs());

        await tab.ToggleFileTransferCommand.ExecuteAsync(null);

        Assert.Equal(FileTransferConnectionState.Failed, tab.FileTransfer!.State);
        Assert.Contains("未连接", tab.FileTransfer.ErrorMessage);
    }

    [Fact]
    public async Task 不支持文件传输的会话切换命令什么都不做()
    {
        var tab = NewTab(new PlainSession(), new StubDialogs());

        await tab.ToggleFileTransferCommand.ExecuteAsync(null);

        Assert.False(tab.IsFileTransferOpen);
    }

    [Fact]
    public async Task 会话断开时侧栏随之转为断开()
    {
        var session = new TransferSession { State = ConnectionState.Connected };
        var tab = NewTab(session, new StubDialogs());
        await tab.ToggleFileTransferCommand.ExecuteAsync(null);

        session.Raise(ConnectionState.Connected, ConnectionState.Disconnected);

        Assert.Equal(FileTransferConnectionState.Disconnected, tab.FileTransfer!.State);
        Assert.Equal("会话已断开，文件传输不可用。", tab.FileTransfer.ErrorMessage);
    }

    [Fact]
    public async Task 释放Tab会释放侧栏与它打开的文件系统()
    {
        var session = new TransferSession { State = ConnectionState.Connected };
        var tab = NewTab(session, new StubDialogs());
        await tab.ToggleFileTransferCommand.ExecuteAsync(null);

        tab.Dispose();

        for (var i = 0; i < 100 && !session.FileSystem.Disposed; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(session.FileSystem.Disposed);
    }

    // ── 文件传输 Tab 入口 ──

    [Theory]
    [InlineData(ProtocolType.Ssh, true)]
    [InlineData(ProtocolType.Rdp, true)]
    [InlineData(ProtocolType.Vnc, false)]
    public void 连接是否支持文件传输只按协议判断(ProtocolType protocol, bool expected)
    {
        var item = new ConnectionItemViewModel(new ConnectionProfile { Name = "x", Protocol = protocol });

        Assert.Equal(expected, item.SupportsFileTransfer);
        Assert.Equal(expected, !item.FileTransferHint.Contains("VNC"));
    }

    [Fact]
    public void 命令把连接交给窗口服务打开()
    {
        var windows = new RecordingWindows();
        var vm = NewPage(windows);
        var item = new ConnectionItemViewModel(new ConnectionProfile { Name = "srv", Protocol = ProtocolType.Rdp });

        Assert.True(vm.OpenFileTransferCommand.CanExecute(item));
        vm.OpenFileTransferCommand.Execute(item);

        Assert.Same(item.Profile, Assert.Single(windows.Opened));
    }

    [Fact]
    public void 未传入目标时回退到当前选中项()
    {
        var windows = new RecordingWindows();
        var vm = NewPage(windows);
        var item = new ConnectionItemViewModel(new ConnectionProfile { Name = "srv", Protocol = ProtocolType.Ssh });
        vm.SelectedItem = item;

        vm.OpenFileTransferCommand.Execute(null);

        Assert.Same(item.Profile, Assert.Single(windows.Opened));
    }

    [Fact]
    public void VNC连接不能打开文件传输()
    {
        var windows = new RecordingWindows();
        var vm = NewPage(windows);
        var item = new ConnectionItemViewModel(new ConnectionProfile { Name = "mac", Protocol = ProtocolType.Vnc });

        Assert.False(vm.OpenFileTransferCommand.CanExecute(item));
        vm.OpenFileTransferCommand.Execute(item);

        Assert.Empty(windows.Opened);
    }

    [Fact]
    public void 没有窗口宿主的平台入口不可用且不抛异常()
    {
        var vm = NewPage(launcher: null);
        var item = new ConnectionItemViewModel(new ConnectionProfile { Name = "srv", Protocol = ProtocolType.Ssh });

        Assert.False(vm.OpenFileTransferCommand.CanExecute(item));
        vm.OpenFileTransferCommand.Execute(item);
    }

    // ── 辅助 ──

    private static ConnectionsPageViewModel NewPage(IFileTransferLauncher? launcher)
        => new(null!, null!, null!, null!, null!, null!, null!, null!, null!, SynchronousUiDispatcher.Instance, null!,
            probe: null, fileTransferLauncher: launcher);

    private static SessionTabViewModel NewTab(IRemoteSession session, IDialogService? dialogs)
        => new(
            session,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            SynchronousUiDispatcher.Instance,
            new ManualTimerFactory(),
            dialogs,
            NullLogger.Instance);

    private sealed class RecordingWindows : IFileTransferLauncher
    {
        public List<ConnectionProfile> Opened { get; } = [];

        public event EventHandler<ConnectionProfile>? OpenRequested;

        public void Open(ConnectionProfile profile)
        {
            Opened.Add(profile);
            OpenRequested?.Invoke(this, profile);
        }
    }

    private class PlainSession : IRemoteSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();

        public ProtocolType Protocol => ProtocolType.Rdp;

        public ConnectionProfile Profile { get; } = new() { Name = "测试", Protocol = ProtocolType.Rdp };

        public ConnectionState State { get; set; } = ConnectionState.Idle;

        public ConnectionErrorCode ErrorCode => ConnectionErrorCode.None;

        public string? ErrorMessage => null;

        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

        public void Raise(ConnectionState oldState, ConnectionState newState)
        {
            State = newState;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(oldState, newState));
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransferSession : PlainSession, IFileTransferSession
    {
        public int OpenCalls { get; private set; }

        public bool FailOpen { get; init; }

        public FakeRemoteFileSystem FileSystem { get; } = new FakeRemoteFileSystem().AddDirectory("/home/user");

        public Task<IRemoteFileSystem> OpenFileSystemAsync(CancellationToken cancellationToken)
        {
            OpenCalls++;
            if (FailOpen)
            {
                throw new InvalidOperationException("会话未连接，无法打开文件传输。");
            }

            return Task.FromResult<IRemoteFileSystem>(FileSystem);
        }
    }

    private sealed class StubDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
            => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info) => Task.CompletedTask;

        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;

        public Task<ConnectionEditorResult?> EditConnectionAsync(ConnectionProfile? existing, ProtocolType? preselectedProtocol = null)
            => throw new NotSupportedException();

        public Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing) => throw new NotSupportedException();

        public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt) => throw new NotSupportedException();

        public Task<DefaultGroupOption?> PickDefaultGroupAsync(string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options)
            => throw new NotSupportedException();

        public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt) => throw new NotSupportedException();

        public Task<IReadOnlyList<Tag>> ManageTagsAsync() => throw new NotSupportedException();

        public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context) => throw new NotSupportedException();

        public Task<string?> PromptPasswordAsync(string title, string message, bool confirm) => throw new NotSupportedException();

        public string? PickFileToOpen(string title, string filter) => throw new NotSupportedException();

        public string? PickFileToSave(string title, string filter, string defaultFileName) => throw new NotSupportedException();

        public string? PickFolder(string title) => throw new NotSupportedException();

        public Task ShowConnectionTestAsync(ConnectionProfile profile) => throw new NotSupportedException();
    }
}
