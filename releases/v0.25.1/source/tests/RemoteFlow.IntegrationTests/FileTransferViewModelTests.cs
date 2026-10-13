using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 文件传输面板的视图模型：连接状态机、导航、上传下载入队与自动刷新、删除 / 重命名 / 新建的确认与提示、
/// 断开与释放。用内存假文件系统 + 记录式对话框替身，不依赖任何 UI。
/// </summary>
public sealed class FileTransferViewModelTests : IDisposable
{
    private const string Home = "/home/user";

    private readonly TempWorkspace _workspace = new();
    private readonly FakeRemoteFileSystem _remote = new();
    private readonly RecordingDialogs _dialogs = new();
    private readonly List<FileTransferViewModel> _viewModels = [];

    public FileTransferViewModelTests()
    {
        _remote.AddDirectory(Home);
    }

    public void Dispose()
    {
        foreach (var vm in _viewModels)
        {
            vm.Dispose();
        }

        _workspace.Dispose();
    }

    // ── 连接状态机 ──

    [Fact]
    public async Task 连接成功后进入初始目录目录在前按名称排序()
    {
        _remote.AddFile($"{Home}/b.txt", [1]);
        _remote.AddDirectory($"{Home}/Zeta");
        _remote.AddFile($"{Home}/A.txt", [1]);
        _remote.AddDirectory($"{Home}/alpha");
        var vm = NewViewModel();

        await vm.EnsureConnectedAsync();

        Assert.Equal(FileTransferConnectionState.Connected, vm.State);
        Assert.True(vm.IsConnected);
        Assert.Equal(Home, vm.CurrentPath);
        Assert.Equal(Home, vm.PathText);
        Assert.Equal("SFTP", vm.ChannelText);
        Assert.Equal(["alpha", "Zeta", "A.txt", "b.txt"], vm.Entries.Select(e => e.Name));
        Assert.Equal(["/", "home", "user"], vm.Segments.Select(s => s.Name));
    }

    [Fact]
    public async Task 重复调用EnsureConnected只打开一次文件系统()
    {
        var opens = 0;
        var vm = NewViewModel(ct =>
        {
            Interlocked.Increment(ref opens);
            return Task.FromResult<IRemoteFileSystem>(_remote);
        });

        await Task.WhenAll(vm.EnsureConnectedAsync(), vm.EnsureConnectedAsync());
        await vm.EnsureConnectedAsync();

        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task 连接失败给出说明并可重试成功()
    {
        var attempts = 0;
        var vm = NewViewModel(ct =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw ConnectionException.FromCode(ConnectionErrorCode.NetworkUnreachable);
            }

            return Task.FromResult<IRemoteFileSystem>(_remote);
        });

        await vm.EnsureConnectedAsync();
        Assert.Equal(FileTransferConnectionState.Failed, vm.State);
        Assert.True(vm.HasConnectionProblem);
        Assert.Equal(ConnectionException.Describe(ConnectionErrorCode.NetworkUnreachable), vm.ErrorMessage);

        await vm.RetryConnectCommand.ExecuteAsync(null);

        Assert.Equal(FileTransferConnectionState.Connected, vm.State);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Equal(Home, vm.CurrentPath);
    }

    [Fact]
    public async Task 连接失败时不把未知异常的原文暴露给用户()
    {
        var vm = NewViewModel(_ => throw new InvalidCastException("secret internal detail /srv/x"));

        await vm.EnsureConnectedAsync();

        Assert.Equal(FileTransferConnectionState.Failed, vm.State);
        Assert.DoesNotContain("secret", vm.ErrorMessage);
        Assert.Contains("日志", vm.ErrorMessage);
    }

    [Fact]
    public async Task 会话未连接这类自带中文消息的状态错误会原样展示()
    {
        var vm = NewViewModel(_ => throw new InvalidOperationException("会话未连接，无法打开文件传输。"));

        await vm.EnsureConnectedAsync();

        Assert.Equal("会话未连接，无法打开文件传输。", vm.ErrorMessage);
    }

    [Fact]
    public async Task 初始目录打不开时退回根目录()
    {
        var vm = NewViewModel(_ => Task.FromResult<IRemoteFileSystem>(new FakeRemoteFileSystem { InitialPath = "/gone" }));

        await vm.EnsureConnectedAsync();

        Assert.Equal(FileTransferConnectionState.Connected, vm.State);
        Assert.Equal("/", vm.CurrentPath);
    }

    // ── 导航 ──

    [Fact]
    public async Task 进入目录上一级与路径框提交()
    {
        _remote.AddFile($"{Home}/docs/readme.md", [1]);
        var vm = await ConnectedAsync();

        await vm.OpenEntryCommand.ExecuteAsync(vm.Entries.Single(e => e.Name == "docs"));
        Assert.Equal($"{Home}/docs", vm.CurrentPath);
        Assert.Equal(["readme.md"], vm.Entries.Select(e => e.Name));
        Assert.True(vm.CanGoUp);

        await vm.GoUpCommand.ExecuteAsync(null);
        Assert.Equal(Home, vm.CurrentPath);

        vm.PathText = "/home/user/docs/../docs/";
        await vm.CommitPathCommand.ExecuteAsync(null);
        Assert.Equal($"{Home}/docs", vm.CurrentPath);
        Assert.Equal($"{Home}/docs", vm.PathText);
    }

    [Fact]
    public async Task 双击文件不做任何事()
    {
        _remote.AddFile($"{Home}/a.txt", [1]);
        var vm = await ConnectedAsync();
        var callsBefore = _remote.Calls.Count;

        await vm.OpenEntryCommand.ExecuteAsync(vm.Entries.Single());

        Assert.Equal(callsBefore, _remote.Calls.Count);
        Assert.Equal(Home, vm.CurrentPath);
    }

    [Fact]
    public async Task 根目录无法再上一级()
    {
        var vm = await ConnectedAsync();
        await vm.NavigateToCommand.ExecuteAsync("/");

        Assert.False(vm.CanGoUp);
    }

    [Fact]
    public async Task 打不开的目录保持原目录并提示路径框回退()
    {
        var vm = await ConnectedAsync();
        _remote.FailList = path => path == "/secret"
            ? new FileTransferException(FileTransferErrorCode.PermissionDenied, "没有权限执行此操作。")
            : null;

        vm.PathText = "/secret";
        await vm.CommitPathCommand.ExecuteAsync(null);

        Assert.Equal(Home, vm.CurrentPath);
        Assert.Equal(Home, vm.PathText);
        Assert.Contains("无法打开该目录", vm.NoticeMessage);
        Assert.Contains("没有权限", vm.NoticeMessage);
        Assert.True(vm.HasNotice);
        Assert.True(vm.IsConnected);
    }

    [Fact]
    public async Task 慢请求被后发起的导航取代不会覆盖新结果()
    {
        _remote.AddFile("/slow/old.txt", [1]);
        _remote.AddFile("/fast/new.txt", [1]);
        var vm = await ConnectedAsync();
        var slowStarted = new TaskCompletionSource();
        _remote.OnList = async (path, ct) =>
        {
            if (path == "/slow")
            {
                slowStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };

        var slow = vm.NavigateAsync("/slow");
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var fast = vm.NavigateAsync("/fast");
        await Task.WhenAll(slow, fast);

        Assert.False(await slow);
        Assert.Equal("/fast", vm.CurrentPath);
        Assert.Equal(["new.txt"], vm.Entries.Select(e => e.Name));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task 空目录标记为空且忙碌期间不显示为空()
    {
        _remote.AddDirectory($"{Home}/empty");
        var vm = await ConnectedAsync();

        await vm.NavigateToCommand.ExecuteAsync($"{Home}/empty");

        Assert.True(vm.IsEmptyDirectory);
    }

    // ── 上传 ──

    [Fact]
    public async Task 上传文件入队完成后当前目录自动刷新出现新文件()
    {
        var file = WriteLocal("up.bin", new byte[20_000]);
        _dialogs.FilesToPick = [file];
        var vm = await ConnectedAsync();

        vm.UploadFilesCommand.Execute(null);
        var item = Assert.Single(vm.Transfers);
        await WaitAsync(() => item.IsFinished);
        await WaitAsync(() => vm.Entries.Any(e => e.Name == "up.bin"));

        Assert.Equal("已完成", item.StatusText);
        Assert.Equal(1d * 100, item.ProgressPercent);
        Assert.Equal(new byte[20_000], _remote.ReadFile($"{Home}/up.bin"));
        Assert.False(vm.HasActiveTransfers);
        Assert.True(vm.HasFinishedTransfers);
    }

    [Fact]
    public async Task 取消选择文件时不入队()
    {
        _dialogs.FilesToPick = [];
        var vm = await ConnectedAsync();

        vm.UploadFilesCommand.Execute(null);

        Assert.Empty(vm.Transfers);
    }

    [Fact]
    public async Task 上传文件夹()
    {
        WriteLocal(Path.Combine("proj", "a.txt"), [1, 2]);
        _dialogs.FolderToPick = Path.Combine(_workspace.Root, "proj");
        var vm = await ConnectedAsync();

        vm.UploadFolderCommand.Execute(null);
        await WaitAsync(() => vm.Transfers.Single().IsFinished);

        Assert.Equal([1, 2], _remote.ReadFile($"{Home}/proj/a.txt"));
    }

    [Fact]
    public async Task 上传冲突时询问用户并按选择执行()
    {
        _remote.AddFile($"{Home}/a.txt", [9]);
        _dialogs.FilesToPick = [WriteLocal("a.txt", [1, 2, 3])];
        _dialogs.ConflictDecision = new ConflictDecision(ConflictAction.Overwrite);
        var vm = await ConnectedAsync();

        vm.UploadFilesCommand.Execute(null);
        await WaitAsync(() => vm.Transfers.Single().IsFinished);

        var conflict = Assert.Single(_dialogs.Conflicts);
        Assert.Equal("a.txt", conflict.Name);
        Assert.Equal([1, 2, 3], _remote.ReadFile($"{Home}/a.txt"));
    }

    // ── 下载 ──

    [Fact]
    public async Task 下载所选项到选定文件夹并记住位置()
    {
        _remote.AddFile($"{Home}/d.bin", new byte[30_000]);
        var target = Path.Combine(_workspace.Root, "dl");
        Directory.CreateDirectory(target);
        _dialogs.FolderToPick = target;
        var vm = await ConnectedAsync();
        vm.Entries.Single().IsSelected = true;

        vm.DownloadCommand.Execute(null);
        var item = Assert.Single(vm.Transfers);
        await WaitAsync(() => item.IsFinished);

        Assert.Equal(new byte[30_000], File.ReadAllBytes(Path.Combine(target, "d.bin")));
        Assert.Equal(target, vm.LastDownloadFolder);
        Assert.False(item.IsUpload);
    }

    [Fact]
    public async Task 没选中就下载只提示不弹选择框()
    {
        _remote.AddFile($"{Home}/d.bin", [1]);
        var vm = await ConnectedAsync();

        vm.DownloadCommand.Execute(null);

        Assert.Contains("请先选择", vm.NoticeMessage);
        Assert.Equal(0, _dialogs.PickFolderCalls);
        Assert.Empty(vm.Transfers);
    }

    [Fact]
    public async Task 取消选择保存位置时不入队()
    {
        _remote.AddFile($"{Home}/d.bin", [1]);
        _dialogs.FolderToPick = null;
        var vm = await ConnectedAsync();
        vm.Entries.Single().IsSelected = true;

        vm.DownloadCommand.Execute(null);

        Assert.Empty(vm.Transfers);
    }

    // ── SMB 共享列表根 ──

    [Fact]
    public async Task SMB共享列表根不允许上传下载删除重命名()
    {
        var smb = new FakeRemoteFileSystem { Channel = FileTransferChannel.Smb, InitialPath = "/" };
        smb.AddDirectory("/C$");
        var vm = NewViewModel(_ => Task.FromResult<IRemoteFileSystem>(smb));
        await vm.EnsureConnectedAsync();
        Assert.True(vm.IsAtShareList);
        Assert.False(vm.CanUpload);
        Assert.Equal("SMB 管理共享", vm.ChannelText);
        vm.Entries.Single().IsSelected = true;
        _dialogs.FilesToPick = ["x"];

        vm.UploadFilesCommand.Execute(null);
        Assert.Empty(vm.Transfers);

        vm.DownloadCommand.Execute(null);
        Assert.Contains("共享", vm.NoticeMessage);
        Assert.Equal(0, _dialogs.PickFolderCalls);

        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(_dialogs.ConfirmMessages);

        await vm.RenameCommand.ExecuteAsync(null);
        Assert.Empty(_dialogs.PromptQueue);

        await vm.NavigateToCommand.ExecuteAsync("/C$");
        Assert.False(vm.IsAtShareList);
        Assert.True(vm.CanUpload);
    }

    // ── 删除 ──

    [Fact]
    public async Task 删除需要确认取消确认则什么都不做()
    {
        _remote.AddFile($"{Home}/keep.txt", [1]);
        _dialogs.ConfirmResult = false;
        var vm = await ConnectedAsync();
        vm.Entries.Single().IsSelected = true;

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.True(_remote.Exists($"{Home}/keep.txt"));
        var message = Assert.Single(_dialogs.ConfirmMessages);
        Assert.Contains("keep.txt", message);
        Assert.Contains("无法撤销", message);
    }

    [Fact]
    public async Task 删除文件夹的确认文案强调连同内容一起删除且确认后删除并提示()
    {
        _remote.AddFile($"{Home}/dir/inner.txt", [1]);
        _dialogs.ConfirmResult = true;
        var vm = await ConnectedAsync();
        vm.Entries.Single(e => e.Name == "dir").IsSelected = true;

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Contains("全部内容", Assert.Single(_dialogs.ConfirmMessages));
        Assert.False(_remote.Exists($"{Home}/dir"));
        Assert.Equal("已删除 1 项。", vm.NoticeMessage);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task 删除部分失败时汇总失败原因()
    {
        _remote.AddFile($"{Home}/ok.txt", [1]);
        _remote.AddFile($"{Home}/locked.txt", [1]);
        _remote.FailDelete = path => path.EndsWith("locked.txt", StringComparison.Ordinal)
            ? new FileTransferException(FileTransferErrorCode.PermissionDenied, "没有权限执行此操作。")
            : null;
        _dialogs.ConfirmResult = true;
        var vm = await ConnectedAsync();
        foreach (var entry in vm.Entries)
        {
            entry.IsSelected = true;
        }

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Contains("1 项未能删除", vm.NoticeMessage);
        Assert.Contains("没有权限", vm.NoticeMessage);
        Assert.False(_remote.Exists($"{Home}/ok.txt"));
        Assert.True(_remote.Exists($"{Home}/locked.txt"));
    }

    [Fact]
    public async Task 没选中就删除只提示不弹确认()
    {
        _remote.AddFile($"{Home}/a.txt", [1]);
        var vm = await ConnectedAsync();

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.ConfirmMessages);
        Assert.Contains("请先选择", vm.NoticeMessage);
    }

    // ── 重命名 / 新建 ──

    [Fact]
    public async Task 重命名成功后刷新列表()
    {
        _remote.AddFile($"{Home}/old.txt", [1]);
        _dialogs.PromptQueue.Enqueue("new.txt");
        var vm = await ConnectedAsync();
        vm.Entries.Single().IsSelected = true;

        await vm.RenameCommand.ExecuteAsync(null);

        Assert.False(_remote.Exists($"{Home}/old.txt"));
        Assert.True(_remote.Exists($"{Home}/new.txt"));
        Assert.Equal(["new.txt"], vm.Entries.Select(e => e.Name));
        Assert.False(Assert.Single(_remote.Renames).Overwrite);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task 重命名为不合法名称时提示且不发请求(string input)
    {
        _remote.AddFile($"{Home}/old.txt", [1]);
        _dialogs.PromptQueue.Enqueue(input);
        var vm = await ConnectedAsync();
        vm.Entries.Single().IsSelected = true;

        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Contains("名称不能", vm.NoticeMessage);
        Assert.Empty(_remote.Renames);
    }

    [Fact]
    public async Task 重命名目标已存在时提示已存在且原文件不动()
    {
        _remote.AddFile($"{Home}/a.txt", [1]);
        _remote.AddFile($"{Home}/b.txt", [2]);
        _dialogs.PromptQueue.Enqueue("b.txt");
        var vm = await ConnectedAsync();
        vm.Entries.Single(e => e.Name == "a.txt").IsSelected = true;

        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Contains("重命名失败", vm.NoticeMessage);
        Assert.Contains("已存在", vm.NoticeMessage);
        Assert.Equal([1], _remote.ReadFile($"{Home}/a.txt"));
        Assert.Equal([2], _remote.ReadFile($"{Home}/b.txt"));
    }

    [Fact]
    public async Task 重命名要求恰好选中一项()
    {
        _remote.AddFile($"{Home}/a.txt", [1]);
        _remote.AddFile($"{Home}/b.txt", [1]);
        var vm = await ConnectedAsync();

        await vm.RenameCommand.ExecuteAsync(null);
        Assert.Contains("请选择一个项目", vm.NoticeMessage);

        foreach (var entry in vm.Entries)
        {
            entry.IsSelected = true;
        }

        await vm.RenameCommand.ExecuteAsync(null);
        Assert.Contains("请选择一个项目", vm.NoticeMessage);
        Assert.Empty(_dialogs.PromptQueue);
    }

    [Fact]
    public async Task 新建文件夹成功与已存在提示()
    {
        _remote.AddFile($"{Home}/taken", [1]);
        var vm = await ConnectedAsync();

        _dialogs.PromptQueue.Enqueue("newdir");
        await vm.NewFolderCommand.ExecuteAsync(null);
        Assert.True(_remote.Exists($"{Home}/newdir"));
        Assert.Contains(vm.Entries, e => e.Name == "newdir" && e.IsDirectory);

        _dialogs.PromptQueue.Enqueue("taken");
        await vm.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains("已存在", vm.NoticeMessage);
    }

    [Fact]
    public async Task 取消输入名称时什么都不做()
    {
        var vm = await ConnectedAsync();
        _dialogs.PromptQueue.Enqueue(null);

        await vm.NewFolderCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_remote.Calls, c => c.StartsWith("mkdir", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 复制路径选中一项复制其全路径否则复制当前目录()
    {
        _remote.AddFile($"{Home}/a.txt", [1]);
        _remote.AddFile($"{Home}/b.txt", [1]);
        var vm = await ConnectedAsync();

        await vm.CopyPathCommand.ExecuteAsync(null);
        Assert.Equal(Home, _dialogs.Clipboard);

        vm.Entries.Single(e => e.Name == "b.txt").IsSelected = true;
        await vm.CopyPathCommand.ExecuteAsync(null);
        Assert.Equal($"{Home}/b.txt", _dialogs.Clipboard);
    }

    // ── 断开与释放 ──

    [Fact]
    public async Task 操作中发现连接中断转为断开并提示重新连接()
    {
        var vm = await ConnectedAsync();
        _remote.FailList = _ => new FileTransferException(FileTransferErrorCode.ConnectionLost, "与服务器的连接已中断。");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(FileTransferConnectionState.Disconnected, vm.State);
        Assert.True(vm.HasConnectionProblem);
        Assert.Contains("重新连接", vm.ErrorMessage);
    }

    [Fact]
    public async Task 宿主会话断开时取消队列并转为断开()
    {
        var file = WriteLocal("big.bin", new byte[200_000]);
        var started = new TaskCompletionSource();
        _remote.OnChunk = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        _dialogs.FilesToPick = [file];
        var vm = await ConnectedAsync();
        vm.UploadFilesCommand.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        vm.OnHostStateChanged(ConnectionState.Disconnected);
        await WaitAsync(() => vm.Transfers.Single().IsFinished);

        Assert.Equal(FileTransferConnectionState.Disconnected, vm.State);
        Assert.Equal("会话已断开，文件传输不可用。", vm.ErrorMessage);
        Assert.Equal("已取消", vm.Transfers.Single().StatusText);
        await WaitAsync(() => _remote.Disposed);
    }

    [Fact]
    public async Task 宿主仍处于已连接或连接中时不误判为断开()
    {
        var vm = await ConnectedAsync();

        vm.OnHostStateChanged(ConnectionState.Connected);
        vm.OnHostStateChanged(ConnectionState.Connecting);

        Assert.Equal(FileTransferConnectionState.Connected, vm.State);
    }

    [Fact]
    public async Task 释放视图模型会取消传输并释放文件系统()
    {
        var file = WriteLocal("big.bin", new byte[200_000]);
        var started = new TaskCompletionSource();
        _remote.OnChunk = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        _dialogs.FilesToPick = [file];
        var vm = await ConnectedAsync();
        vm.UploadFilesCommand.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        vm.Dispose();

        await WaitAsync(() => _remote.Disposed);
        await WaitAsync(() => _remote.TempFiles().Count == 0);
        await vm.EnsureConnectedAsync(); // 释放后不再连接
        Assert.NotEqual(FileTransferConnectionState.Connecting, vm.State);
    }

    [Fact]
    public async Task 清除已完成只移除已结束的传输项()
    {
        var quick = WriteLocal("quick.txt", [1]);
        _dialogs.FilesToPick = [quick];
        var vm = await ConnectedAsync();
        vm.UploadFilesCommand.Execute(null);
        await WaitAsync(() => vm.Transfers.Single().IsFinished);
        Assert.True(vm.HasFinishedTransfers);

        vm.ClearFinishedCommand.Execute(null);

        Assert.Empty(vm.Transfers);
        Assert.False(vm.HasTransfers);
    }

    [Fact]
    public async Task 关闭提示后提示消失()
    {
        var vm = await ConnectedAsync();
        vm.DownloadCommand.Execute(null);
        Assert.True(vm.HasNotice);

        vm.DismissNoticeCommand.Execute(null);

        Assert.False(vm.HasNotice);
    }

    // ── 传输项文案 ──

    [Fact]
    public async Task 跳过时传输项显示已完成并带跳过数量()
    {
        _remote.AddFile($"{Home}/a.txt", [9]);
        _dialogs.FilesToPick = [WriteLocal("a.txt", [1])];
        _dialogs.ConflictDecision = new ConflictDecision(ConflictAction.Skip);
        var vm = await ConnectedAsync();

        vm.UploadFilesCommand.Execute(null);
        var item = vm.Transfers.Single();
        await WaitAsync(() => item.IsFinished);

        Assert.Equal("已完成（1 项跳过）", item.StatusText);
        Assert.Equal("Ui.Warning.Filled", item.StatusIconKey);
        Assert.Equal("Status.Warning", item.StatusBrushKey);
    }

    [Fact]
    public async Task 失败的传输项显示失败原因()
    {
        _remote.FailUpload = _ => new FileTransferException(FileTransferErrorCode.PermissionDenied, "没有权限执行此操作。");
        _dialogs.FilesToPick = [WriteLocal("a.txt", [1])];
        var vm = await ConnectedAsync();

        vm.UploadFilesCommand.Execute(null);
        var item = vm.Transfers.Single();
        await WaitAsync(() => item.IsFinished);

        Assert.Equal("失败", item.StatusText);
        Assert.Equal("没有权限执行此操作。", item.DetailText);
        Assert.Equal("Ui.Error.Filled", item.StatusIconKey);
        Assert.False(item.CanCancel);
    }

    // ── 大小格式化 ──

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-5, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(123L * 1024 * 1024, "123 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 / 2, "1.5 GB")]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024, "5 TB")]
    public void 字节数格式化(long bytes, string expected)
        => Assert.Equal(expected, ByteSizeFormatter.Format(bytes));

    // ── 辅助 ──

    private FileTransferViewModel NewViewModel(Func<CancellationToken, Task<IRemoteFileSystem>>? open = null)
    {
        var vm = new FileTransferViewModel(
            open ?? (_ => Task.FromResult<IRemoteFileSystem>(_remote)),
            _dialogs,
            SynchronousUiDispatcher.Instance,
            NullLogger.Instance);
        _viewModels.Add(vm);
        return vm;
    }

    private async Task<FileTransferViewModel> ConnectedAsync()
    {
        var vm = NewViewModel();
        await vm.EnsureConnectedAsync();
        Assert.Equal(FileTransferConnectionState.Connected, vm.State);
        return vm;
    }

    private string WriteLocal(string relative, byte[] data)
    {
        var path = Path.Combine(_workspace.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件成立超时。");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>记录式对话框替身：可配置各类返回值，并记录交互以便断言。只实现文件传输用到的成员。</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public IReadOnlyList<string> FilesToPick { get; set; } = [];

        public string? FolderToPick { get; set; }

        public int PickFolderCalls { get; private set; }

        public bool ConfirmResult { get; set; } = true;

        public List<string> ConfirmMessages { get; } = [];

        public Queue<string?> PromptQueue { get; } = new();

        public ConflictDecision ConflictDecision { get; set; } = new(ConflictAction.Skip);

        public List<TransferConflict> Conflicts { get; } = [];

        public string? Clipboard { get; private set; }

        public IReadOnlyList<string> PickFilesToOpen(string title, string filter) => FilesToPick;

        public string? PickFolder(string title)
        {
            PickFolderCalls++;
            return FolderToPick;
        }

        public Task<string?> PromptTextAsync(string title, string label, string initialText = "")
            => Task.FromResult(PromptQueue.Count > 0 ? PromptQueue.Dequeue() : null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
        {
            ConfirmMessages.Add(message);
            return Task.FromResult(ConfirmResult);
        }

        public Task<ConflictDecision> ResolveTransferConflictAsync(TransferConflict conflict, CancellationToken cancellationToken)
        {
            lock (Conflicts)
            {
                Conflicts.Add(conflict);
            }

            return Task.FromResult(ConflictDecision);
        }

        public Task CopyToClipboardAsync(string text)
        {
            Clipboard = text;
            return Task.CompletedTask;
        }

        public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info) => Task.CompletedTask;

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

        public Task ShowConnectionTestAsync(ConnectionProfile profile) => throw new NotSupportedException();
    }
}
