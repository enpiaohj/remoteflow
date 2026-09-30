using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.Presentation.ViewModels;

public enum FileTransferConnectionState
{
    /// <summary>尚未发起连接（面板还没被打开过）。</summary>
    NotConnected,

    Connecting,

    Connected,

    /// <summary>建立连接失败，可重试。</summary>
    Failed,

    /// <summary>曾经连上，之后断开（会话关闭 / 连接中断）。</summary>
    Disconnected
}

/// <summary>
/// 文件传输面板的视图模型：会话内侧栏与独立窗口共用同一份。
/// 文件系统由工厂委托提供——侧栏来自会话的 <c>IFileTransferSession</c>，独立窗口来自
/// <c>IFileTransferConnector</c>；本类只依赖 <see cref="IRemoteFileSystem"/>，与具体通道无关。
/// <para>
/// 所有成员都在界面线程上调用；后台线程来的变化（传输进度）经 <see cref="IUiDispatcher"/> 切回。
/// 目录列表整体替换（一次属性变更）而不是逐项增删，数万条目的大目录也不会产生通知风暴。
/// </para>
/// </summary>
public sealed partial class FileTransferViewModel : ObservableObject, IDisposable
{
    private readonly Func<CancellationToken, Task<IRemoteFileSystem>> _openFileSystem;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();

    private IRemoteFileSystem? _fileSystem;
    private FileTransferService? _service;
    private Task _connectTask = Task.CompletedTask;
    private CancellationTokenSource? _listCts;
    private int _listVersion;
    private string? _lastDownloadFolder;
    private int _disposed;

    public FileTransferViewModel(
        Func<CancellationToken, Task<IRemoteFileSystem>> openFileSystem,
        IDialogService dialogs,
        IUiDispatcher uiDispatcher,
        ILogger logger,
        string title = "文件传输")
    {
        _openFileSystem = openFileSystem;
        _dialogs = dialogs;
        _ui = uiDispatcher;
        _logger = logger;
        Title = title;

        Transfers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTransfers));
            OnPropertyChanged(nameof(HasFinishedTransfers));
        };
    }

    public string Title { get; }

    public ObservableCollection<TransferItemViewModel> Transfers { get; } = [];

    public bool HasTransfers => Transfers.Count > 0;

    public bool HasFinishedTransfers => Transfers.Any(t => t.IsFinished);

    /// <summary>是否有未结束的传输。关闭窗口 / 退出应用前据此确认。</summary>
    public bool HasActiveTransfers => _service?.HasActiveJobs ?? false;

    // ── 连接状态 ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(HasConnectionProblem))]
    [NotifyPropertyChangedFor(nameof(IsEmptyDirectory))]
    [NotifyPropertyChangedFor(nameof(CanUpload))]
    private FileTransferConnectionState _state = FileTransferConnectionState.NotConnected;

    public bool IsConnected => State == FileTransferConnectionState.Connected;

    public bool IsConnecting => State is FileTransferConnectionState.Connecting or FileTransferConnectionState.NotConnected;

    public bool HasConnectionProblem => State is FileTransferConnectionState.Failed or FileTransferConnectionState.Disconnected;

    /// <summary>连接失败 / 断开时的说明（占据面板主体区域）。</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>当前使用的通道；连接成功后确定。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChannelText))]
    [NotifyPropertyChangedFor(nameof(IsAtShareList))]
    [NotifyPropertyChangedFor(nameof(CanUpload))]
    private FileTransferChannel? _channel;

    public string ChannelText => Channel switch
    {
        FileTransferChannel.Sftp => "SFTP",
        FileTransferChannel.Smb => "SMB 管理共享",
        _ => string.Empty
    };

    /// <summary>列表上方的操作结果 / 提示（可关闭），如「已删除 3 项」「名称不合法」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeMessage = string.Empty;

    public bool HasNotice => NoticeMessage.Length > 0;

    // ── 目录浏览 ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtShareList))]
    [NotifyPropertyChangedFor(nameof(CanUpload))]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    private string _currentPath = RemotePath.Root;

    /// <summary>路径输入框的文本（回车提交）。导航成功后回填规范化路径。</summary>
    [ObservableProperty]
    private string _pathText = RemotePath.Root;

    [ObservableProperty]
    private IReadOnlyList<RemotePathSegment> _segments = [new(RemotePath.Root, RemotePath.Root)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyDirectory))]
    private IReadOnlyList<RemoteFileItemViewModel> _entries = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyDirectory))]
    private bool _isBusy;

    public bool IsEmptyDirectory => IsConnected && !IsBusy && Entries.Count == 0;

    public bool CanGoUp => CurrentPath != RemotePath.Root;

    /// <summary>
    /// SMB 的根是「共享列表」：那里的条目是共享（C$、D$…）而不是文件夹，
    /// 不能上传 / 下载 / 删除——必须先进入某个共享。
    /// </summary>
    public bool IsAtShareList => Channel == FileTransferChannel.Smb && CurrentPath == RemotePath.Root;

    public bool CanUpload => IsConnected && !IsAtShareList;

    // ── 连接与断开 ────────────────────────────────────────────────

    /// <summary>
    /// 确保已连接（首次打开面板 / 窗口时调用）。重复调用安全：进行中返回同一任务，已连接直接返回。
    /// 失败不抛异常，状态置 <see cref="FileTransferConnectionState.Failed"/> 并给出说明。
    /// </summary>
    public Task EnsureConnectedAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || State == FileTransferConnectionState.Connected)
        {
            return Task.CompletedTask;
        }

        if (State == FileTransferConnectionState.Connecting)
        {
            return _connectTask;
        }

        _connectTask = ConnectCoreAsync();
        return _connectTask;
    }

    private async Task ConnectCoreAsync()
    {
        State = FileTransferConnectionState.Connecting;
        ErrorMessage = string.Empty;

        IRemoteFileSystem fileSystem;
        try
        {
            fileSystem = await _openFileSystem(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("文件传输：打开文件系统失败，类型 {ExceptionType}", ex.GetType().Name);
            ErrorMessage = DescribeConnectError(ex);
            State = FileTransferConnectionState.Failed;
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            await fileSystem.DisposeAsync();
            return;
        }

        AttachFileSystem(fileSystem);
        State = FileTransferConnectionState.Connected;
        NotifyDerivedState();

        // 初始目录打不开（如家目录被删）时退回根目录，别让用户面对一片空白。
        if (!await NavigateAsync(fileSystem.InitialPath) && RemotePath.Normalize(fileSystem.InitialPath) != RemotePath.Root)
        {
            await NavigateAsync(RemotePath.Root);
        }
    }

    private void AttachFileSystem(IRemoteFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        Channel = fileSystem.Channel;

        var service = new FileTransferService(fileSystem, _logger);
        service.JobAdded += OnJobAdded;
        _service = service;
    }

    /// <summary>宿主状态变化（会话内侧栏由 <c>SessionTabViewModel</c> 转发）。会话离开已连接即视为断开。</summary>
    public void OnHostStateChanged(ConnectionState hostState)
    {
        if (hostState is ConnectionState.Disconnected or ConnectionState.Failed or ConnectionState.Closed)
        {
            MarkDisconnected("会话已断开，文件传输不可用。");
        }
    }

    private void MarkDisconnected(string message)
    {
        if (Volatile.Read(ref _disposed) != 0 || State is FileTransferConnectionState.Disconnected or FileTransferConnectionState.Failed)
        {
            return;
        }

        var wasConnected = State == FileTransferConnectionState.Connected;
        _service?.CancelAll();
        ErrorMessage = message;
        State = wasConnected ? FileTransferConnectionState.Disconnected : FileTransferConnectionState.Failed;
        NotifyDerivedState();

        // 释放失效的文件系统（后台进行，不让界面等一个已断开的连接）。
        var fileSystem = _fileSystem;
        _fileSystem = null;
        if (fileSystem is not null)
        {
            _ = DisposeQuietlyAsync(fileSystem);
        }
    }

    [RelayCommand]
    private async Task RetryConnectAsync()
    {
        if (State is not (FileTransferConnectionState.Failed or FileTransferConnectionState.Disconnected))
        {
            return;
        }

        DetachService();
        var stale = _fileSystem;
        _fileSystem = null;
        if (stale is not null)
        {
            _ = DisposeQuietlyAsync(stale);
        }

        State = FileTransferConnectionState.NotConnected;
        await EnsureConnectedAsync();
    }

    private static string DescribeConnectError(Exception ex) => ex switch
    {
        ConnectionException connection => connection.Message,
        // 「会话未连接」等由我们自己抛出的状态错误，消息是写给用户看的。
        InvalidOperationException invalid => invalid.Message,
        _ => "无法打开文件传输，详情请查看日志。"
    };

    // ── 导航 ──────────────────────────────────────────────────────

    /// <summary>进入目录。失败时保持在原目录并给出提示；返回是否成功。</summary>
    public async Task<bool> NavigateAsync(string path)
    {
        var fileSystem = _fileSystem;
        if (fileSystem is null || State != FileTransferConnectionState.Connected)
        {
            return false;
        }

        var target = RemotePath.Normalize(path);

        // 新的导航取代进行中的：旧请求取消，结果按版本号丢弃，避免慢请求覆盖后发起的。
        _listCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _listCts = cts;
        var version = ++_listVersion;

        IsBusy = true;
        NoticeMessage = string.Empty;
        try
        {
            var entries = await fileSystem.ListAsync(target, cts.Token);
            if (version != _listVersion)
            {
                return false;
            }

            ApplyListing(target, entries);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (version == _listVersion)
        {
            HandleOperationError(ex, "无法打开该目录");
            PathText = CurrentPath; // 路径框回退，不停留在打不开的路径上
            return false;
        }
        finally
        {
            if (version == _listVersion)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplyListing(string path, IReadOnlyList<RemoteFileEntry> entries)
    {
        CurrentPath = path;
        PathText = path;
        Segments = RemotePath.GetSegments(path);
        Entries =
        [
            .. entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new RemoteFileItemViewModel(e))
        ];
        NotifyDerivedState();
    }

    [RelayCommand]
    private Task RefreshAsync() => NavigateAsync(CurrentPath);

    [RelayCommand]
    private Task GoUpAsync() => NavigateAsync(RemotePath.GetParent(CurrentPath));

    [RelayCommand]
    private Task NavigateToAsync(string? path) => path is null ? Task.CompletedTask : NavigateAsync(path);

    [RelayCommand]
    private Task CommitPathAsync() => NavigateAsync(PathText);

    /// <summary>双击：目录进入；文件不做任何事（避免误触发下载）。</summary>
    [RelayCommand]
    private Task OpenEntryAsync(RemoteFileItemViewModel? item)
        => item is { IsDirectory: true } ? NavigateAsync(item.FullPath) : Task.CompletedTask;

    // ── 上传 / 下载 ───────────────────────────────────────────────

    [RelayCommand]
    private void UploadFiles()
    {
        if (!CanUpload)
        {
            return;
        }

        var files = _dialogs.PickFilesToOpen("选择要上传的文件", "所有文件|*.*");
        if (files.Count > 0)
        {
            EnqueueUploads(files);
        }
    }

    [RelayCommand]
    private void UploadFolder()
    {
        if (!CanUpload)
        {
            return;
        }

        if (_dialogs.PickFolder("选择要上传的文件夹") is { } folder)
        {
            EnqueueUploads([folder]);
        }
    }

    /// <summary>把本机路径上传到当前目录。也是将来拖拽上传的入口。</summary>
    public void EnqueueUploads(IEnumerable<string> localPaths)
    {
        if (_service is not { } service || !CanUpload)
        {
            return;
        }

        service.EnqueueUploads(localPaths, CurrentPath, ResolveConflictAsync);
    }

    [RelayCommand]
    private void Download()
    {
        if (_service is not { } service || !IsConnected)
        {
            return;
        }

        if (IsAtShareList)
        {
            NoticeMessage = "请先进入某个共享（如 C$），再选择要下载的内容。";
            return;
        }

        var selected = SelectedEntries();
        if (selected.Count == 0)
        {
            NoticeMessage = "请先选择要下载的文件或文件夹。";
            return;
        }

        var folder = _dialogs.PickFolder("选择保存位置");
        if (folder is null)
        {
            return;
        }

        _lastDownloadFolder = folder;
        NoticeMessage = string.Empty;
        service.EnqueueDownloads(selected.Select(e => e.Entry), folder, ResolveConflictAsync);
    }

    /// <summary>上次选择的下载目录（本次运行内），供界面在选择对话框里作为起点。</summary>
    public string? LastDownloadFolder => _lastDownloadFolder;

    private Task<ConflictDecision> ResolveConflictAsync(TransferConflict conflict, CancellationToken cancellationToken)
        => _dialogs.ResolveTransferConflictAsync(conflict, cancellationToken);

    // ── 新建 / 重命名 / 删除 / 复制路径 ───────────────────────────

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var fileSystem = _fileSystem;
        if (fileSystem is null || !CanUpload)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("新建文件夹", "文件夹名称", "新建文件夹");
        if (name is null)
        {
            return;
        }

        if (!TryValidateName(name, out var trimmed))
        {
            return;
        }

        try
        {
            var path = RemotePath.Combine(CurrentPath, trimmed);
            if (await fileSystem.StatAsync(path, _lifetime.Token) is not null)
            {
                NoticeMessage = $"「{trimmed}」已存在。";
                return;
            }

            await fileSystem.CreateDirectoryAsync(path, _lifetime.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HandleOperationError(ex, "新建文件夹失败");
            return;
        }

        await NavigateAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var fileSystem = _fileSystem;
        if (fileSystem is null || !IsConnected)
        {
            return;
        }

        if (IsAtShareList)
        {
            NoticeMessage = "共享名称不能修改。";
            return;
        }

        var selected = SelectedEntries();
        if (selected.Count != 1)
        {
            NoticeMessage = "请选择一个项目进行重命名。";
            return;
        }

        var entry = selected[0];
        var input = await _dialogs.PromptTextAsync("重命名", "新名称", entry.Name);
        if (input is null || !TryValidateName(input, out var trimmed) || trimmed == entry.Name)
        {
            return;
        }

        try
        {
            await fileSystem.RenameAsync(
                entry.FullPath, RemotePath.Combine(CurrentPath, trimmed), overwrite: false, _lifetime.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HandleOperationError(ex, "重命名失败");
            return;
        }

        await NavigateAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var service = _service;
        if (service is null || !IsConnected)
        {
            return;
        }

        if (IsAtShareList)
        {
            NoticeMessage = "共享不能删除；请先进入共享再选择要删除的内容。";
            return;
        }

        var selected = SelectedEntries();
        if (selected.Count == 0)
        {
            NoticeMessage = "请先选择要删除的项目。";
            return;
        }

        if (!await _dialogs.ConfirmAsync("删除", BuildDeleteMessage(selected), "删除", isDanger: true))
        {
            return;
        }

        IsBusy = true;
        DeleteSummary summary;
        try
        {
            summary = await service.DeleteAsync(selected.Select(e => e.Entry), _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            IsBusy = false;
            HandleOperationError(ex, "删除失败");
            return;
        }

        IsBusy = false;
        await NavigateAsync(CurrentPath);
        NoticeMessage = summary.Failed == 0
            ? $"已删除 {selected.Count} 项。"
            : $"{summary.Failed} 项未能删除：{summary.FirstError}";
    }

    private static string BuildDeleteMessage(IReadOnlyList<RemoteFileItemViewModel> selected)
    {
        var hasFolder = selected.Any(e => e.IsDirectory);
        var subject = selected.Count == 1
            ? $"「{selected[0].Name}」"
            : $"选中的 {selected.Count} 项（{string.Join("、", selected.Take(3).Select(e => e.Name))}{(selected.Count > 3 ? " 等" : string.Empty)}）";

        return $"确定要永久删除{subject}吗？"
               + (hasFolder ? "\n\n文件夹会连同其中的全部内容一起删除。" : string.Empty)
               + "\n\n此操作不会进入回收站，无法撤销。";
    }

    [RelayCommand]
    private async Task CopyPathAsync()
    {
        var selected = SelectedEntries();
        var text = selected.Count == 1 ? selected[0].FullPath : CurrentPath;
        await _dialogs.CopyToClipboardAsync(text);
        NoticeMessage = "路径已复制。";
    }

    /// <summary>校验用户输入的新名称（重命名 / 新建文件夹）。不合法时给出提示并返回 false。</summary>
    private bool TryValidateName(string input, out string name)
    {
        name = input.Trim();
        if (!RemotePath.IsValidEntryName(name))
        {
            NoticeMessage = "名称不能为空，也不能包含 / 。";
            return false;
        }

        return true;
    }

    private List<RemoteFileItemViewModel> SelectedEntries() => [.. Entries.Where(e => e.IsSelected)];

    // ── 传输队列 ──────────────────────────────────────────────────

    private void OnJobAdded(object? sender, TransferJob job)
    {
        if (_ui.RequeueIfNeeded(() => OnJobAdded(sender, job)))
        {
            return;
        }

        if (_service is not { } service || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var item = new TransferItemViewModel(job, service, _ui);
        item.Finished += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFinishedTransfers));
            OnPropertyChanged(nameof(HasActiveTransfers));
            OnTransferFinished(item);
        };
        Transfers.Add(item);
        OnPropertyChanged(nameof(HasActiveTransfers));
    }

    /// <summary>上传结束且目标目录就是当前目录时自动刷新，让新文件立刻出现在列表里。</summary>
    private void OnTransferFinished(TransferItemViewModel item)
    {
        if (!item.IsFinished
            || item.Job.Direction != TransferDirection.Upload
            || item.Job.Status != TransferStatus.Completed
            || RemotePath.Normalize(item.Job.TargetDirectory) != CurrentPath
            || State != FileTransferConnectionState.Connected)
        {
            return;
        }

        _ = NavigateAsync(CurrentPath);
    }

    [RelayCommand]
    private void CancelAll() => _service?.CancelAll();

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Transfers.Where(t => t.IsFinished).ToList())
        {
            item.Dispose();
            Transfers.Remove(item);
        }

        _service?.ClearFinished();
        OnPropertyChanged(nameof(HasActiveTransfers));
    }

    [RelayCommand]
    private void DismissNotice() => NoticeMessage = string.Empty;

    // ── 错误处理 ──────────────────────────────────────────────────

    private void HandleOperationError(Exception ex, string context)
    {
        switch (ex)
        {
            case FileTransferException { Code: FileTransferErrorCode.ConnectionLost }:
                MarkDisconnected("与服务器的连接已中断，请重新连接。");
                break;

            case FileTransferException transfer:
                NoticeMessage = $"{context}：{transfer.Message}";
                break;

            case ObjectDisposedException:
                break; // 正在关闭，忽略

            default:
                _logger.LogWarning("文件传输：{Context}，类型 {ExceptionType}", context, ex.GetType().Name);
                NoticeMessage = $"{context}，详情请查看日志。";
                break;
        }
    }

    private void NotifyDerivedState()
    {
        // 命令本身不做 CanExecute 限制（各自在执行时给出提示更友好），
        // 这里只刷新依赖当前路径 / 通道的派生属性，供界面绑定。
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(IsAtShareList));
    }

    // ── 释放 ──────────────────────────────────────────────────────

    private void DetachService()
    {
        if (_service is { } service)
        {
            service.JobAdded -= OnJobAdded;
            service.CancelAll();
            _service = null;
            _ = DisposeQuietlyAsync(service);
        }
    }

    private async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("文件传输：释放资源时出现异常，类型 {ExceptionType}", ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _listCts?.Cancel();

        foreach (var item in Transfers)
        {
            item.Dispose();
        }

        DetachService();
        if (_fileSystem is { } fileSystem)
        {
            _fileSystem = null;
            _ = DisposeQuietlyAsync(fileSystem);
        }
    }
}
