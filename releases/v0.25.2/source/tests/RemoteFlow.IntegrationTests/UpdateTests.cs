using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Updates;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>应用内更新：发布列表筛选、下载校验、视图模型状态机。全部用假发布源，不访问网络。</summary>
public sealed class UpdateTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // ── 发布列表筛选 ──

    [Fact]
    public void 只取Windows版本线里版本号最高且含本平台产物的正式版()
    {
        var json = $$"""
        [
          {"tag_name":"macos-v0.99.0","draft":false,"prerelease":false,"html_url":"p0","body":"mac",
           "assets":[{"name":"RemoteFlow-macos-v0.99.0-macos-arm64.dmg","browser_download_url":"https://github.com/x/mac.dmg","size":1}]},
          {"tag_name":"v0.30.0","draft":false,"prerelease":true,"html_url":"p1","body":"pre",
           "assets":[{"name":"RemoteFlow-v0.30.0-win-x64.exe","browser_download_url":"https://github.com/x/pre.exe","size":1}]},
          {"tag_name":"v0.29.0","draft":true,"prerelease":false,"html_url":"p2","body":"draft",
           "assets":[{"name":"RemoteFlow-v0.29.0-win-x64.exe","browser_download_url":"https://github.com/x/draft.exe","size":1}]},
          {"tag_name":"v0.28.0","draft":false,"prerelease":false,"html_url":"p3","body":"no asset","assets":[]},
          {"tag_name":"v0.9.0","draft":false,"prerelease":false,"html_url":"p4","body":"old",
           "assets":[{"name":"RemoteFlow-v0.9.0-win-x64.exe","browser_download_url":"https://github.com/x/old.exe","size":9}]},
          {"tag_name":"v0.25.0","draft":false,"prerelease":false,"html_url":"page","body":"notes",
           "assets":[
             {"name":"RemoteFlow-v0.25.0-win-x64.zip","browser_download_url":"https://github.com/x/a.zip","size":5},
             {"name":"RemoteFlow-v0.25.0-win-x64.exe","browser_download_url":"https://github.com/x/a.exe","size":123,"digest":"sha256:{{HashA.ToUpperInvariant()}}"}]}
        ]
        """;

        var release = GitHubReleaseParser.SelectLatest(json, "win-x64.exe");

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 25, 0), release.Version);
        Assert.Equal("RemoteFlow-v0.25.0-win-x64.exe", release.AssetName);
        Assert.Equal("https://github.com/x/a.exe", release.DownloadUrl);
        Assert.Equal(123, release.Size);
        Assert.Equal(HashA, release.Sha256);
        Assert.Equal("page", release.PageUrl);
        Assert.Equal("notes", release.Notes);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"message\":\"rate limited\"}")]
    public void 没有可用版本时返回空(string json)
    {
        Assert.Null(GitHubReleaseParser.SelectLatest(json, "win-x64.exe"));
    }

    [Theory]
    [InlineData("md5:abc")]
    [InlineData("sha256:xyz")]
    [InlineData("")]
    public void 摘要不是合法SHA256时视为没有(string digest)
    {
        var json = $$"""
        [{"tag_name":"v1.0.0","html_url":"p","body":"",
          "assets":[{"name":"RemoteFlow-v1.0.0-win-x64.exe","browser_download_url":"https://github.com/x/a.exe","size":1,"digest":"{{digest}}"}]}]
        """;

        Assert.Null(GitHubReleaseParser.SelectLatest(json, "win-x64.exe")!.Sha256);
    }

    // ── 下载与校验 ──

    [Fact]
    public async Task 下载内容与登记的哈希一致时保留文件()
    {
        var payload = RandomNumberGenerator.GetBytes(200_000);
        var service = new GitHubUpdateService(new HttpClient(new StaticHandler(payload)), "win-x64.exe");
        var reports = new List<double>();

        var path = await service.DownloadAsync(Release(payload.Length, Sha(payload)), new SyncProgress(reports.Add), CancellationToken.None);

        try
        {
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(100d, reports[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 哈希不一致时拒绝并删除已下载的文件()
    {
        var payload = RandomNumberGenerator.GetBytes(50_000);
        var service = new GitHubUpdateService(new HttpClient(new StaticHandler(payload)), "win-x64.exe");
        var release = Release(payload.Length, HashA, "RemoteFlow-v9.9.1-win-x64.exe");

        await Assert.ThrowsAsync<UpdateVerificationException>(() => service.DownloadAsync(release, null, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "RemoteFlow-update", release.AssetName + ".download")));
    }

    [Fact]
    public async Task 大小不一致时拒绝()
    {
        var payload = RandomNumberGenerator.GetBytes(10_000);
        var service = new GitHubUpdateService(new HttpClient(new StaticHandler(payload)), "win-x64.exe");

        await Assert.ThrowsAsync<UpdateVerificationException>(
            () => service.DownloadAsync(Release(payload.Length + 1, Sha(payload), "RemoteFlow-v9.9.2-win-x64.exe"), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(null, "https://github.com/x/a.exe")]
    [InlineData(HashA, "http://github.com/x/a.exe")]
    [InlineData(HashA, "https://example.com/a.exe")]
    public async Task 没有哈希或地址不是GitHub的HTTPS时不发起下载(string? sha, string url)
    {
        var handler = new StaticHandler([1, 2, 3]);
        var service = new GitHubUpdateService(new HttpClient(handler), "win-x64.exe");
        var release = new UpdateRelease(new Version(9, 9, 3), "v9.9.3", "", "p", "RemoteFlow-v9.9.3-win-x64.exe", url, 3, sha);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => service.DownloadAsync(release, null, CancellationToken.None));

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task 下载连接停滞时超时放弃并清掉半截文件()
    {
        var service = new GitHubUpdateService(new HttpClient(new StalledHandler()), "win-x64.exe", TimeSpan.FromMilliseconds(300));
        var release = Release(1000, HashA, "RemoteFlow-v9.9.4-win-x64.exe");

        await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadAsync(release, null, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "RemoteFlow-update", release.AssetName + ".download")));
    }

    [Fact]
    public async Task 用户取消下载时抛出取消而不是超时()
    {
        var service = new GitHubUpdateService(new HttpClient(new StalledHandler()), "win-x64.exe", TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadAsync(Release(1000, HashA, "RemoteFlow-v9.9.5-win-x64.exe"), null, cts.Token));
    }

    // ── 视图模型 ──

    [Fact]
    public async Task 默认不自动检查且启动检查不联网()
    {
        var feed = new FakeFeed { Latest = Release(1, HashA) };
        var vm = NewViewModel(feed, new FakeInstaller(), new AppSettings());

        Assert.False(vm.AutoCheck);
        await vm.CheckOnStartupAsync();

        Assert.Equal(0, feed.CheckCount);
        Assert.Equal(UpdateState.Idle, vm.State);
        Assert.False(vm.HasUpdate);
    }

    [Fact]
    public async Task 开启自动检查后启动时发现新版本只提示不下载()
    {
        var feed = new FakeFeed { Latest = Release(1, HashA) };
        var settings = new AppSettings { AutoCheckUpdates = true };
        var vm = NewViewModel(feed, new FakeInstaller(), settings);

        await vm.CheckOnStartupAsync();

        Assert.Equal(UpdateState.Available, vm.State);
        Assert.True(vm.HasUpdate);
        Assert.Equal(0, feed.DownloadCount);
    }

    [Fact]
    public async Task 自动检查失败不打扰用户_手动检查失败给出说明()
    {
        var feed = new FakeFeed { CheckError = new HttpRequestException("offline") };
        var vm = NewViewModel(feed, new FakeInstaller(), new AppSettings { AutoCheckUpdates = true });

        await vm.CheckOnStartupAsync();
        Assert.Equal(UpdateState.Idle, vm.State);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("GitHub", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.HasUpdate);
    }

    [Fact]
    public async Task 版本不比当前新时显示已是最新()
    {
        var feed = new FakeFeed { Latest = Release(1, HashA, version: new Version(1, 0, 0)) };
        var vm = NewViewModel(feed, new FakeInstaller(), new AppSettings());

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.UpToDate, vm.State);
        Assert.False(vm.HasUpdate);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task 下载并更新成功后等待重启_确认后才重启()
    {
        var feed = new FakeFeed { Latest = Release(1, HashA) };
        var installer = new FakeInstaller();
        var dialogs = FakeDialogs.Create(confirm: false);
        var vm = NewViewModel(feed, installer, new AppSettings(), dialogs);

        await vm.CheckCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.ReadyToRestart, vm.State);
        Assert.Equal("pkg", installer.Applied);
        Assert.True(vm.HasUpdate);
        Assert.False(vm.CheckCommand.CanExecute(null));

        await vm.RestartCommand.ExecuteAsync(null);
        Assert.False(installer.Restarted);

        FakeDialogs.SetConfirm(dialogs, true);
        await vm.RestartCommand.ExecuteAsync(null);
        Assert.True(installer.Restarted);
    }

    [Fact]
    public async Task 校验失败或替换失败时保留打开下载页的退路()
    {
        var feed = new FakeFeed { Latest = Release(1, HashA), DownloadError = new UpdateVerificationException("坏包") };
        var installer = new FakeInstaller();
        var vm = NewViewModel(feed, installer, new AppSettings());

        await vm.CheckCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Null(installer.Applied);
        Assert.True(vm.CanOpenReleasePage);
        Assert.True(vm.InstallCommand.CanExecute(null));

        feed.DownloadError = null;
        installer.ApplyError = new UnauthorizedAccessException();
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("写入权限", vm.StatusText, StringComparison.Ordinal);
        vm.OpenReleasePageCommand.Execute(null);
        Assert.Equal("page", installer.OpenedUrl);
    }

    [Fact]
    public async Task 下载中可以取消_取消后回到可下载状态()
    {
        var feed = new FakeFeed { Latest = Release(1, HashA), HoldDownload = true };
        var installer = new FakeInstaller();
        var vm = NewViewModel(feed, installer, new AppSettings());
        await vm.CheckCommand.ExecuteAsync(null);

        var installing = vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Downloading, vm.State);

        vm.CancelDownloadCommand.Execute(null);
        await installing;

        Assert.Equal(UpdateState.Available, vm.State);
        Assert.Contains("已取消", vm.StatusText, StringComparison.Ordinal);
        Assert.Null(installer.Applied);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("timeout", "长时间没有数据")]
    [InlineData("disk", "磁盘空间")]
    [InlineData("network", "网络中断")]
    public async Task 下载失败按原因给出不同说明(string kind, string expected)
    {
        Exception error = kind switch
        {
            "timeout" => new TimeoutException(),
            "disk" => new IOException("disk full"),
            _ => new HttpRequestException("reset")
        };
        var feed = new FakeFeed { Latest = Release(1, HashA), DownloadError = error };
        var vm = NewViewModel(feed, new FakeInstaller(), new AppSettings());
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains(expected, vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.CanOpenReleasePage);
    }

    [Fact]
    public async Task 被限流时说明是请求过于频繁而不是网络问题()
    {
        var feed = new FakeFeed { CheckError = new HttpRequestException("rate", null, HttpStatusCode.Forbidden) };
        var vm = NewViewModel(feed, new FakeInstaller(), new AppSettings());

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("请求频率", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 当前运行形态不支持自更新时只提供下载页()
    {
        var feed = new FakeFeed { Latest = Release(1, HashA) };
        var vm = NewViewModel(feed, new FakeInstaller { CanSelfUpdate = false }, new AppSettings());

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.HasUpdate);
        Assert.False(vm.CanInstall);
        Assert.True(vm.CanOpenReleasePage);
    }

    [Fact]
    public void 切换自动检查开关会写回设置()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(new FakeFeed(), new FakeInstaller(), settings);

        vm.AutoCheck = true;

        Assert.True(settings.AutoCheckUpdates);
    }

    [Fact]
    public async Task 发布页正文是整份更新日志时只显示该版本的条目()
    {
        var body = """
            # 更新日志

            说明文字

            ---

            ## [9.9.0] — 2026-01-01

            本版内容

            ### 修复
            - 一项

            ---

            ## [9.8.0] — 2025-12-01

            旧版内容
            """;
        var feed = new FakeFeed { Latest = Release(1, HashA) with { Notes = body } };
        var vm = NewViewModel(feed, new FakeInstaller(), new AppSettings());

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.StartsWith("## [9.9.0]", vm.Notes, StringComparison.Ordinal);
        Assert.Contains("- 一项", vm.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("旧版内容", vm.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("更新日志", vm.Notes, StringComparison.Ordinal);
        Assert.False(vm.Notes.EndsWith('-'));
    }

    [Fact]
    public async Task 发布页正文没有版本标题时原样显示()
    {
        var feed = new FakeFeed { Latest = Release(1, HashA) with { Notes = "  只有一句说明  " } };
        var vm = NewViewModel(feed, new FakeInstaller(), new AppSettings());

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal("只有一句说明", vm.Notes);
    }

    // ── 辅助 ──

    private static UpdateViewModel NewViewModel(FakeFeed feed, FakeInstaller installer, AppSettings settings, IDialogService? dialogs = null)
        => new(feed, installer, dialogs ?? FakeDialogs.Create(confirm: true), settings, settingsStore: null,
            SynchronousUiDispatcher.Instance, NullLogger<UpdateViewModel>.Instance, currentVersion: new Version(1, 0, 0));

    private static UpdateRelease Release(long size, string? sha, string asset = "RemoteFlow-v9.9.0-win-x64.exe", Version? version = null)
        => new(version ?? new Version(9, 9, 0), "v9.9.0", "notes", "page", asset, "https://github.com/x/a.exe", size, sha);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class StaticHandler(byte[] payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }

    /// <summary>发出响应头后永远不给数据的连接。</summary>
    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverStream()) });

        private sealed class NeverStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class FakeFeed : IUpdateService
    {
        public UpdateRelease? Latest { get; set; }

        public Exception? CheckError { get; set; }

        public Exception? DownloadError { get; set; }

        public int CheckCount { get; private set; }

        public int DownloadCount { get; private set; }

        public Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            return CheckError is null ? Task.FromResult(Latest) : Task.FromException<UpdateRelease?>(CheckError);
        }

        /// <summary>为 true 时下载一直挂起，直到被取消。</summary>
        public bool HoldDownload { get; set; }

        public async Task<string> DownloadAsync(UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            DownloadCount++;
            if (HoldDownload)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return DownloadError is null ? "pkg" : throw DownloadError;
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public bool CanSelfUpdate { get; set; } = true;

        public string? Applied { get; private set; }

        public Exception? ApplyError { get; set; }

        public bool Restarted { get; private set; }

        public string? OpenedUrl { get; private set; }

        public void Apply(string verifiedPackagePath)
        {
            if (ApplyError is not null)
            {
                throw ApplyError;
            }

            Applied = verifiedPackagePath;
        }

        public void Restart() => Restarted = true;

        public void OpenUrl(string url) => OpenedUrl = url;
    }

    /// <summary>只关心确认对话框的 IDialogService 替身，其余成员返回默认值。</summary>
    public class FakeDialogs : DispatchProxy
    {
        private bool _confirm;

        public static IDialogService Create(bool confirm)
        {
            var proxy = Create<IDialogService, FakeDialogs>();
            ((FakeDialogs)(object)proxy)._confirm = confirm;
            return proxy;
        }

        public static void SetConfirm(IDialogService dialogs, bool confirm) => ((FakeDialogs)(object)dialogs)._confirm = confirm;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ConfirmAsync))
            {
                return Task.FromResult(_confirm);
            }

            var type = targetMethod?.ReturnType;
            if (type == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = type.GetGenericArguments()[0];
                var value = result.IsValueType ? Activator.CreateInstance(result) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
            }

            return type is { IsValueType: true } && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
