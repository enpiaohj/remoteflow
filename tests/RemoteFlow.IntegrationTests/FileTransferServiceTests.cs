using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Core.FileTransfer;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 传输队列的行为：往返一致、临时文件与原子替换、冲突处理、取消 / 失败清理、恶意文件名、
/// 符号链接与递归上限、并发上限、递归删除。用内存假文件系统 + 本机临时目录，不需要真实服务器。
/// </summary>
public sealed class FileTransferServiceTests : IDisposable
{
    private const string Home = "/home/user";

    private readonly TempWorkspace _workspace = new();
    private readonly string _local;
    private readonly FakeRemoteFileSystem _remote = new();
    private readonly List<FileTransferService> _services = [];

    public FileTransferServiceTests()
    {
        _local = Path.Combine(_workspace.Root, "local");
        Directory.CreateDirectory(_local);
        _remote.AddDirectory(Home);
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _workspace.Dispose();
    }

    // ── 上传 ──

    [Fact]
    public async Task 上传单文件内容一致进度走满且不留临时文件()
    {
        var data = Bytes(100_000);
        var file = WriteLocal("a.bin", data);
        var service = NewService();

        var job = Assert.Single(service.EnqueueUploads([file], Home, Never()));
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.Equal(data, _remote.ReadFile($"{Home}/a.bin"));
        Assert.Empty(_remote.TempFiles());
        Assert.Equal(data.Length, job.BytesTotal);
        Assert.Equal(data.Length, job.BytesDone);
        Assert.Equal(1, job.FilesDone);
        Assert.Equal(1d, job.Progress);
        Assert.False(job.HasIssues);
    }

    [Fact]
    public async Task 上传先写临时名再改名到目标无冲突时不覆盖()
    {
        var file = WriteLocal("a.txt", Bytes(10));

        var job = Assert.Single(NewService().EnqueueUploads([file], Home, Never()));
        await FinishedAsync(job);

        var (from, to, overwrite) = Assert.Single(_remote.Renames);
        Assert.EndsWith(".rfpart", from);
        Assert.StartsWith($"{Home}/.a.txt.", from);
        Assert.Equal($"{Home}/a.txt", to);
        Assert.False(overwrite);
    }

    [Fact]
    public async Task 上传文件夹树结构与内容一致含空目录()
    {
        var a = Bytes(50);
        var b = Bytes(40_000);
        var c = Bytes(7);
        WriteLocal(Path.Combine("proj", "a.txt"), a);
        WriteLocal(Path.Combine("proj", "sub", "b.bin"), b);
        WriteLocal(Path.Combine("proj", "sub", "deep", "c.txt"), c);
        Directory.CreateDirectory(Path.Combine(_local, "proj", "empty"));

        var job = Assert.Single(NewService().EnqueueUploads([Path.Combine(_local, "proj")], Home, Never()));
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.Equal(a, _remote.ReadFile($"{Home}/proj/a.txt"));
        Assert.Equal(b, _remote.ReadFile($"{Home}/proj/sub/b.bin"));
        Assert.Equal(c, _remote.ReadFile($"{Home}/proj/sub/deep/c.txt"));
        Assert.True(_remote.Exists($"{Home}/proj/empty"));
        Assert.Equal(3, job.FilesTotal);
        Assert.Equal(3, job.FilesDone);
        Assert.Equal(a.Length + b.Length + c.Length, job.BytesTotal);
        Assert.Equal(job.BytesTotal, job.BytesDone);
        Assert.Empty(_remote.TempFiles());
    }

    [Fact]
    public async Task 远端已有同名目录时上传文件夹是合并而不是冲突()
    {
        _remote.AddFile($"{Home}/proj/old.txt", Bytes(5));
        WriteLocal(Path.Combine("proj", "new.txt"), Bytes(6));
        var prompts = new List<TransferConflict>();

        var job = Assert.Single(NewService().EnqueueUploads([Path.Combine(_local, "proj")], Home, Record(prompts)));
        await FinishedAsync(job);

        Assert.Empty(prompts);
        Assert.True(_remote.Exists($"{Home}/proj/old.txt"));
        Assert.True(_remote.Exists($"{Home}/proj/new.txt"));
    }

    // ── 冲突 ──

    [Fact]
    public async Task 上传冲突选覆盖时用原子替换换成新内容()
    {
        _remote.AddFile($"{Home}/a.txt", Encoding("OLD"));
        var file = WriteLocal("a.txt", Encoding("NEW-CONTENT"));
        var prompts = new List<TransferConflict>();

        var job = Assert.Single(NewService().EnqueueUploads([file], Home, Record(prompts, ConflictAction.Overwrite)));
        await FinishedAsync(job);

        var prompt = Assert.Single(prompts);
        Assert.Equal("a.txt", prompt.Name);
        Assert.Equal(3, prompt.ExistingSize);
        Assert.Equal(11, prompt.IncomingSize);
        Assert.True(prompt.CanOverwrite);
        Assert.Equal(Encoding("NEW-CONTENT"), _remote.ReadFile($"{Home}/a.txt"));
        Assert.True(Assert.Single(_remote.Renames).Overwrite);
    }

    [Fact]
    public async Task 上传冲突选跳过时原文件保持不变()
    {
        _remote.AddFile($"{Home}/a.txt", Encoding("OLD"));
        var file = WriteLocal("a.txt", Encoding("NEW"));

        var job = Assert.Single(NewService().EnqueueUploads([file], Home, Always(ConflictAction.Skip)));
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.Equal(1, job.SkippedCount);
        Assert.True(job.HasIssues);
        Assert.Equal(Encoding("OLD"), _remote.ReadFile($"{Home}/a.txt"));
        Assert.Empty(_remote.TempFiles());
    }

    [Fact]
    public async Task 上传冲突选改名时保留两者且序号递增()
    {
        _remote.AddFile($"{Home}/a.txt", Encoding("OLD"));
        _remote.AddFile($"{Home}/a (1).txt", Encoding("OLD1"));
        _remote.AddFile($"{Home}/.bashrc", Encoding("RC"));
        var file = WriteLocal("a.txt", Encoding("NEW"));
        var dot = WriteLocal(".bashrc", Encoding("NEWRC"));

        var jobs = NewService().EnqueueUploads([file, dot], Home, Always(ConflictAction.Rename));
        await Task.WhenAll(jobs.Select(FinishedAsync));

        Assert.Equal(Encoding("OLD"), _remote.ReadFile($"{Home}/a.txt"));
        Assert.Equal(Encoding("NEW"), _remote.ReadFile($"{Home}/a (2).txt"));
        Assert.Equal(Encoding("NEWRC"), _remote.ReadFile($"{Home}/.bashrc (1)"));
    }

    [Fact]
    public async Task 对全部应用只询问一次()
    {
        for (var i = 0; i < 3; i++)
        {
            _remote.AddFile($"{Home}/f{i}.txt", Encoding("OLD"));
        }

        var files = Enumerable.Range(0, 3).Select(i => WriteLocal($"f{i}.txt", Encoding("NEW"))).ToList();
        var prompts = new List<TransferConflict>();

        var jobs = NewService().EnqueueUploads(files, Home, Record(prompts, ConflictAction.Skip, applyToAll: true));
        await Task.WhenAll(jobs.Select(FinishedAsync));

        Assert.Single(prompts);
        Assert.All(jobs, j => Assert.Equal(1, j.SkippedCount));
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(Encoding("OLD"), _remote.ReadFile($"{Home}/f{i}.txt")));
    }

    [Fact]
    public async Task 选取消会取消同一批的全部项目()
    {
        var files = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            _remote.AddFile($"{Home}/f{i}.txt", Encoding("OLD"));
            files.Add(WriteLocal($"f{i}.txt", Encoding("NEW")));
        }

        var jobs = NewService().EnqueueUploads(files, Home, Always(ConflictAction.Cancel));
        await Task.WhenAll(jobs.Select(FinishedAsync));

        Assert.All(jobs, j => Assert.Equal(TransferStatus.Cancelled, j.Status));
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(Encoding("OLD"), _remote.ReadFile($"{Home}/f{i}.txt")));
        Assert.Empty(_remote.TempFiles());
    }

    [Fact]
    public async Task 文件与文件夹同名时覆盖退化为跳过不破坏目录()
    {
        _remote.AddFile($"{Home}/a.txt/inside.txt", Encoding("KEEP"));
        var file = WriteLocal("a.txt", Encoding("NEW"));
        var prompts = new List<TransferConflict>();

        var job = Assert.Single(NewService().EnqueueUploads([file], Home, Record(prompts, ConflictAction.Overwrite)));
        await FinishedAsync(job);

        Assert.False(Assert.Single(prompts).CanOverwrite);
        Assert.Equal(1, job.SkippedCount);
        Assert.Equal(Encoding("KEEP"), _remote.ReadFile($"{Home}/a.txt/inside.txt"));
    }

    [Fact]
    public async Task 询问期间被单独取消的项不会牵连同批其它项目()
    {
        _remote.AddFile($"{Home}/a.txt", Encoding("OLD-A"));
        _remote.AddFile($"{Home}/b.txt", Encoding("OLD-B"));
        var a = WriteLocal("a.txt", Encoding("NEW-A"));
        var b = WriteLocal("b.txt", Encoding("NEW-B"));
        var service = NewService();
        TransferJob? jobA = null;
        var release = new TaskCompletionSource();

        // 只在 a.txt 的询问里：先取消 a 这一项（模拟用户点了它的取消，界面随之关闭对话框），再返回「取消整批」。
        ConflictResolver resolver = async (conflict, _) =>
        {
            if (conflict.Name == "a.txt")
            {
                await release.Task;
                service.Cancel(jobA!);
                return new ConflictDecision(ConflictAction.Cancel);
            }

            return new ConflictDecision(ConflictAction.Skip);
        };

        var jobs = service.EnqueueUploads([a, b], Home, resolver);
        jobA = jobs[0];
        release.SetResult();
        await Task.WhenAll(jobs.Select(FinishedAsync));

        Assert.Equal(TransferStatus.Cancelled, jobs[0].Status);
        Assert.Equal(TransferStatus.Completed, jobs[1].Status); // b 没被牵连，按 Skip 正常结束
        Assert.Equal(1, jobs[1].SkippedCount);
    }
    // ── 取消 / 失败 ──

    [Fact]
    public async Task 上传中途取消不留临时文件也不产生目标文件()
    {
        var file = WriteLocal("big.bin", Bytes(200_000));
        var service = NewService();
        var started = new TaskCompletionSource();
        _remote.OnChunk = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // 卡在传输中，等取消打断
        };

        var job = Assert.Single(service.EnqueueUploads([file], Home, Never()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Cancel(job);
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Cancelled, job.Status);
        Assert.Empty(_remote.TempFiles());
        Assert.False(_remote.Exists($"{Home}/big.bin"));
    }

    [Fact]
    public async Task 覆盖上传中途取消时原文件仍完好()
    {
        _remote.AddFile($"{Home}/doc.txt", Encoding("ORIGINAL"));
        var file = WriteLocal("doc.txt", Bytes(200_000));
        var service = NewService();
        var started = new TaskCompletionSource();
        _remote.OnChunk = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var job = Assert.Single(service.EnqueueUploads([file], Home, Always(ConflictAction.Overwrite)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Cancel(job);
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Cancelled, job.Status);
        Assert.Equal(Encoding("ORIGINAL"), _remote.ReadFile($"{Home}/doc.txt"));
        Assert.Empty(_remote.TempFiles());
    }

    [Fact]
    public async Task 单文件上传失败整项判为失败并清理临时文件()
    {
        var file = WriteLocal("a.txt", Bytes(50_000));
        _remote.FailUpload = _ => new FileTransferException(FileTransferErrorCode.PermissionDenied, "没有权限执行此操作。");

        var job = Assert.Single(NewService().EnqueueUploads([file], Home, Never()));
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Failed, job.Status);
        Assert.Contains("没有权限", job.Message);
        Assert.Empty(_remote.TempFiles());
        Assert.False(_remote.Exists($"{Home}/a.txt"));
    }

    [Fact]
    public async Task 文件夹里个别文件失败其余继续并汇总()
    {
        WriteLocal(Path.Combine("d", "ok1.txt"), Bytes(10));
        WriteLocal(Path.Combine("d", "bad.txt"), Bytes(10));
        WriteLocal(Path.Combine("d", "ok2.txt"), Bytes(10));
        _remote.FailUpload = path => path.Contains("bad.txt")
            ? new FileTransferException(FileTransferErrorCode.PermissionDenied, "没有权限执行此操作。")
            : null;

        var job = Assert.Single(NewService().EnqueueUploads([Path.Combine(_local, "d")], Home, Never()));
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.True(job.HasIssues);
        Assert.Equal(1, job.FailedCount);
        Assert.Equal(2, job.FilesDone);
        Assert.Contains("1 个文件失败", job.Message);
        Assert.True(_remote.Exists($"{Home}/d/ok1.txt"));
        Assert.True(_remote.Exists($"{Home}/d/ok2.txt"));
        Assert.False(_remote.Exists($"{Home}/d/bad.txt"));
    }

    [Fact]
    public async Task 文件夹里全部失败时整项判为失败()
    {
        WriteLocal(Path.Combine("d", "a.txt"), Bytes(10));
        WriteLocal(Path.Combine("d", "b.txt"), Bytes(10));
        _remote.FailUpload = _ => new FileTransferException(FileTransferErrorCode.DiskFull, "磁盘空间不足。");

        var job = Assert.Single(NewService().EnqueueUploads([Path.Combine(_local, "d")], Home, Never()));
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Failed, job.Status);
        Assert.Equal(2, job.FailedCount);
    }

    [Fact]
    public async Task 本机源文件不存在时该项失败且不影响同批其它项()
    {
        var good = WriteLocal("good.txt", Bytes(10));
        var missing = Path.Combine(_local, "missing.txt");

        var jobs = NewService().EnqueueUploads([missing, good], Home, Never());
        await Task.WhenAll(jobs.Select(FinishedAsync));

        Assert.Equal(TransferStatus.Failed, jobs[0].Status);
        Assert.Contains("不存在", jobs[0].Message);
        Assert.Equal(TransferStatus.Completed, jobs[1].Status);
        Assert.True(_remote.Exists($"{Home}/good.txt"));
    }

    // ── 下载 ──

    [Fact]
    public async Task 下载单文件内容一致并保留修改时间且不留临时文件()
    {
        var data = Bytes(100_000);
        var modified = new DateTimeOffset(2024, 3, 5, 10, 20, 30, TimeSpan.Zero);
        _remote.AddFile($"{Home}/data.bin", data, modified);
        var entry = Entry($"{Home}/data.bin");

        var job = Assert.Single(NewService().EnqueueDownloads([entry], _local, Never()));
        await FinishedAsync(job);

        var target = Path.Combine(_local, "data.bin");
        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.Equal(data, File.ReadAllBytes(target));
        Assert.Equal(modified.UtcDateTime, File.GetLastWriteTimeUtc(target));
        Assert.Empty(Directory.GetFiles(_local, "*.rfpart", SearchOption.AllDirectories));
        Assert.Equal(data.Length, job.BytesDone);
    }

    [Fact]
    public async Task 下载冲突覆盖跳过改名各自生效()
    {
        _remote.AddFile($"{Home}/a.txt", Encoding("REMOTE"));
        File.WriteAllText(Path.Combine(_local, "a.txt"), "LOCAL");
        var entry = Entry($"{Home}/a.txt");

        var skip = Assert.Single(NewService().EnqueueDownloads([entry], _local, Always(ConflictAction.Skip)));
        await FinishedAsync(skip);
        Assert.Equal("LOCAL", File.ReadAllText(Path.Combine(_local, "a.txt")));

        var rename = Assert.Single(NewService().EnqueueDownloads([entry], _local, Always(ConflictAction.Rename)));
        await FinishedAsync(rename);
        Assert.Equal("LOCAL", File.ReadAllText(Path.Combine(_local, "a.txt")));
        Assert.Equal("REMOTE", File.ReadAllText(Path.Combine(_local, "a (1).txt")));

        var overwrite = Assert.Single(NewService().EnqueueDownloads([entry], _local, Always(ConflictAction.Overwrite)));
        await FinishedAsync(overwrite);
        Assert.Equal("REMOTE", File.ReadAllText(Path.Combine(_local, "a.txt")));
    }

    [Fact]
    public async Task 下载中途取消本机无临时文件且已有目标不变()
    {
        File.WriteAllText(Path.Combine(_local, "big.bin"), "ORIGINAL");
        _remote.AddFile($"{Home}/big.bin", Bytes(200_000));
        var service = NewService();
        var started = new TaskCompletionSource();
        _remote.OnChunk = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var job = Assert.Single(service.EnqueueDownloads([Entry($"{Home}/big.bin")], _local, Always(ConflictAction.Overwrite)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Cancel(job);
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Cancelled, job.Status);
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_local, "big.bin")));
        Assert.Empty(Directory.GetFiles(_local, "*.rfpart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 下载文件夹树结构一致且符号链接不被跟随()
    {
        _remote.AddFile($"{Home}/tree/a.txt", Encoding("A"));
        _remote.AddFile($"{Home}/tree/sub/b.txt", Encoding("B"));
        _remote.AddDirectory($"{Home}/tree/empty");
        _remote.AddSymlink($"{Home}/tree/linkdir", pointsToDirectory: true);
        _remote.AddSymlink($"{Home}/tree/linkfile", pointsToDirectory: false);
        var entry = Entry($"{Home}/tree");

        var job = Assert.Single(NewService().EnqueueDownloads([entry], _local, Never()));
        await FinishedAsync(job);

        var root = Path.Combine(_local, "tree");
        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.Equal("A", File.ReadAllText(Path.Combine(root, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(root, "sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(root, "empty")));
        Assert.False(Path.Exists(Path.Combine(root, "linkdir")));
        Assert.False(Path.Exists(Path.Combine(root, "linkfile")));
        Assert.Equal(2, job.SkippedCount);
        Assert.True(job.HasIssues);
        Assert.DoesNotContain(_remote.Calls, c => c == $"list {Home}/tree/linkdir");
    }

    [Fact]
    public async Task 明确选中符号链接目录也不递归()
    {
        _remote.AddSymlink($"{Home}/link", pointsToDirectory: true);
        var entry = Entry($"{Home}/link");

        var job = Assert.Single(NewService().EnqueueDownloads([entry], _local, Never()));
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.Contains("符号链接目录", job.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_local));
        Assert.DoesNotContain(_remote.Calls, c => c.StartsWith("list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 恶意文件名被清洗后落在目标目录内绝不越界()
    {
        _remote.AddDirectory($"{Home}/evil");
        _remote.AddRawEntry($"{Home}/evil", @"..\..\evil.txt", Encoding("X1"));
        _remote.AddRawEntry($"{Home}/evil", "CON", Encoding("X2"));
        _remote.AddRawEntry($"{Home}/evil", "a:b", Encoding("X3"));
        _remote.AddRawEntry($"{Home}/evil", "trail.", Encoding("X4"));
        var target = Path.Combine(_workspace.Root, "dl");
        Directory.CreateDirectory(target);
        var before = Directory.GetFileSystemEntries(_workspace.Root).Order().ToArray();

        var job = Assert.Single(NewService().EnqueueDownloads([Entry($"{Home}/evil")], target, Never()));
        await FinishedAsync(job);

        var evilRoot = Path.Combine(target, "evil");
        var written = Directory.GetFiles(evilRoot).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "_CON", "a_b", "trail_", ".._.._evil.txt" }.Order(), written);
        Assert.Equal(4, job.FilesDone);
        Assert.Contains("自动改名", job.Message);
        // 目标目录之外（工作区里）不多出任何东西。
        Assert.Equal(before, Directory.GetFileSystemEntries(_workspace.Root).Order().ToArray());
        Assert.Equal(["evil"], Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
    }

    [Fact]
    public async Task 超过深度上限的层级被跳过而不是崩溃()
    {
        var path = $"{Home}/deep";
        _remote.AddDirectory(path);
        for (var i = 0; i < FileTransferService.MaxDepth + 6; i++)
        {
            path += "/d";
            _remote.AddDirectory(path);
        }

        _remote.AddFile(path + "/bottom.txt", Encoding("X"));
        _remote.AddFile($"{Home}/deep/top.txt", Encoding("T"));

        var job = Assert.Single(NewService().EnqueueDownloads([Entry($"{Home}/deep")], _local, Never()));
        await FinishedAsync(job);

        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.Equal("T", File.ReadAllText(Path.Combine(_local, "deep", "top.txt")));
        Assert.True(job.SkippedCount > 0);
        Assert.Equal(1, job.FilesDone);
    }

    // ── 队列 ──

    [Fact]
    public async Task 并发传输数不超过上限且其余排队()
    {
        var gate = new TaskCompletionSource();
        var enteredTwo = new TaskCompletionSource();
        var entered = 0;
        _remote.OnChunk = async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref entered) >= 2)
            {
                enteredTwo.TrySetResult();
            }

            await gate.Task.WaitAsync(ct);
        };
        var files = Enumerable.Range(0, 4).Select(i => WriteLocal($"f{i}.bin", Bytes(40_000))).ToList();
        var service = NewService(maxConcurrency: 2);

        var jobs = service.EnqueueUploads(files, Home, Never());
        await enteredTwo.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(150); // 给第 3、4 个一点机会（若并发控制失效，它们会闯进来）

        Assert.Equal(2, jobs.Count(j => j.Status == TransferStatus.Running));
        Assert.Equal(2, jobs.Count(j => j.Status == TransferStatus.Queued));
        Assert.True(service.HasActiveJobs);

        gate.SetResult();
        await Task.WhenAll(jobs.Select(FinishedAsync));

        Assert.Equal(2, _remote.MaxActiveTransfers);
        Assert.All(jobs, j => Assert.Equal(TransferStatus.Completed, j.Status));
        Assert.False(service.HasActiveJobs);
    }

    [Fact]
    public async Task 取消排队中的项直接出队不占用通道()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        _remote.OnChunk = async (_, _, ct) =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
        };
        var files = new[] { WriteLocal("a.bin", Bytes(40_000)), WriteLocal("b.bin", Bytes(40_000)) };
        var service = NewService(maxConcurrency: 1);

        var jobs = service.EnqueueUploads(files, Home, Never());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TransferStatus.Queued, jobs[1].Status);

        service.Cancel(jobs[1]);
        await FinishedAsync(jobs[1]);
        Assert.Equal(TransferStatus.Cancelled, jobs[1].Status);

        gate.SetResult();
        await FinishedAsync(jobs[0]);
        Assert.Equal(TransferStatus.Completed, jobs[0].Status);
        Assert.False(_remote.Exists($"{Home}/b.bin"));
    }

    [Fact]
    public async Task 进度单调不倒退且变化事件被触发()
    {
        var file = WriteLocal("a.bin", Bytes(300_000));
        var service = NewService();
        var seen = new List<long>();
        var changed = 0;
        service.JobAdded += (_, job) => job.Changed += (_, _) =>
        {
            Interlocked.Increment(ref changed);
            lock (seen)
            {
                seen.Add(job.BytesDone);
            }
        };

        var job = Assert.Single(service.EnqueueUploads([file], Home, Never()));
        await FinishedAsync(job);

        Assert.True(changed >= 2, "至少应有开始与完成两次变化通知");
        lock (seen)
        {
            Assert.Equal(seen.Order(), seen);
        }
    }

    [Fact]
    public async Task 清除已完成只移除已结束的项并触发移除事件()
    {
        var gate = new TaskCompletionSource();
        _remote.OnChunk = async (path, _, ct) =>
        {
            if (path.Contains("slow"))
            {
                await gate.Task.WaitAsync(ct);
            }
        };
        var fast = WriteLocal("fast.bin", Bytes(20_000));
        var slow = WriteLocal("slow.bin", Bytes(20_000));
        var service = NewService();
        var removed = new List<TransferJob>();
        service.JobRemoved += (_, job) => removed.Add(job);

        var jobs = service.EnqueueUploads([fast, slow], Home, Never());
        await FinishedAsync(jobs[0]);

        service.ClearFinished();

        Assert.Equal([jobs[0]], removed);
        Assert.Equal([jobs[1]], service.Jobs);
        gate.SetResult();
        await FinishedAsync(jobs[1]);
    }

    [Fact]
    public async Task 释放服务会取消在途传输并清理临时文件()
    {
        var file = WriteLocal("a.bin", Bytes(200_000));
        var started = new TaskCompletionSource();
        _remote.OnChunk = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var service = new FileTransferService(_remote, NullLogger.Instance);

        var job = Assert.Single(service.EnqueueUploads([file], Home, Never()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.DisposeAsync();

        Assert.Equal(TransferStatus.Cancelled, job.Status);
        Assert.Empty(_remote.TempFiles());
        Assert.Throws<ObjectDisposedException>(() => service.EnqueueUploads([file], Home, Never()));
    }

    // ── 递归删除 ──

    [Fact]
    public async Task 递归删除清空目录树且不进入符号链接()
    {
        _remote.AddFile($"{Home}/t/a.txt", Encoding("A"));
        _remote.AddFile($"{Home}/t/sub/b.txt", Encoding("B"));
        _remote.AddSymlink($"{Home}/t/link", pointsToDirectory: true);
        _remote.AddFile($"{Home}/outside/keep.txt", Encoding("KEEP"));

        var summary = await NewService().DeleteAsync([Entry($"{Home}/t")], CancellationToken.None);

        Assert.Equal(0, summary.Failed);
        Assert.Equal(5, summary.Deleted); // a、b、sub、link、t
        Assert.False(_remote.Exists($"{Home}/t"));
        Assert.True(_remote.Exists($"{Home}/outside/keep.txt"));
        Assert.DoesNotContain(_remote.Calls, c => c == $"list {Home}/t/link");
    }

    [Fact]
    public async Task 删除某项失败时其余选中项照常删除并汇总()
    {
        _remote.AddFile($"{Home}/t/sub/b.txt", Encoding("B"));
        _remote.AddFile($"{Home}/other.txt", Encoding("O"));
        _remote.FailDelete = path => path.EndsWith("b.txt", StringComparison.Ordinal)
            ? new FileTransferException(FileTransferErrorCode.PermissionDenied, "没有权限执行此操作。")
            : null;

        var summary = await NewService().DeleteAsync([Entry($"{Home}/t"), Entry($"{Home}/other.txt")], CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Deleted);
        Assert.Contains("没有权限", summary.FirstError);
        Assert.True(_remote.Exists($"{Home}/t/sub/b.txt"));
        Assert.False(_remote.Exists($"{Home}/other.txt"));
    }

    [Fact]
    public async Task 删除可被取消()
    {
        _remote.AddFile($"{Home}/t/a.txt", Encoding("A"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewService().DeleteAsync([Entry($"{Home}/t")], cts.Token));
        Assert.True(_remote.Exists($"{Home}/t/a.txt"));
    }

    // ── 辅助 ──

    private FileTransferService NewService(int maxConcurrency = 2)
    {
        var service = new FileTransferService(_remote, NullLogger.Instance, maxConcurrency);
        _services.Add(service);
        return service;
    }

    private RemoteFileEntry Entry(string path)
        => _remote.StatAsync(path, CancellationToken.None).GetAwaiter().GetResult()
           ?? throw new InvalidOperationException($"测试数据缺少 {path}");

    private string WriteLocal(string relative, byte[] data)
    {
        var path = Path.Combine(_local, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] Bytes(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    private static byte[] Encoding(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static async Task FinishedAsync(TransferJob job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!job.IsFinished)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"传输项 {job.Name} 未在 15 秒内结束，状态 {job.Status}。");
            }

            await Task.Delay(10);
        }
    }

    private static ConflictResolver Never()
        => (_, _) => throw new InvalidOperationException("不应发生冲突询问。");

    private static ConflictResolver Always(ConflictAction action)
        => (_, _) => Task.FromResult(new ConflictDecision(action));

    private static ConflictResolver Record(
        List<TransferConflict> sink, ConflictAction action = ConflictAction.Skip, bool applyToAll = false)
        => (conflict, _) =>
        {
            lock (sink)
            {
                sink.Add(conflict);
            }

            return Task.FromResult(new ConflictDecision(action, applyToAll));
        };
}
