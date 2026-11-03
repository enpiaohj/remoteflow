using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接编辑器凭据下拉：第一行「＋ 新建凭据…」，建好后自动选中；取消则不改变选择。</summary>
public sealed class ConnectionEditorCredentialEntryTests
{
    private static readonly Credential Existing = new() { Name = "已有凭据", Type = CredentialType.SshPassword, Username = "root" };

    private static ConnectionEditorViewModel Create(Func<Task<Credential?>>? creator)
        => new(
            existing: null,
            credentials: [Existing],
            groups: [],
            tags: [],
            defaults: new AppSettings(),
            createCredentialAsync: creator);

    [Fact]
    public void 有新建能力时下拉第一行是新建凭据_其后是未指定和已有凭据()
    {
        var vm = Create(() => Task.FromResult<Credential?>(null));

        Assert.Same(CredentialOption.NewCredentialEntry, vm.CredentialChoices[0]);
        Assert.Equal(["＋ 新建凭据…", "未指定", "已有凭据"], vm.CredentialChoices.Select(c => c.Name).ToArray());

        // 操作项不会混进 macOS 编辑器使用的列表，也不是连接的选中值。
        Assert.DoesNotContain(vm.AvailableCredentials, c => c.IsNewCredentialEntry);
        Assert.Equal("未指定", vm.SelectedCredential.Name);
    }

    [Fact]
    public void 平台没有新建能力时不出现新建凭据入口()
    {
        var vm = Create(null);

        Assert.DoesNotContain(vm.CredentialChoices, c => c.IsNewCredentialEntry);
        Assert.Equal(vm.AvailableCredentials.Select(c => c.Name), vm.CredentialChoices.Select(c => c.Name));
    }

    [Fact]
    public async Task 选择新建凭据后建好的凭据自动被选中并写入连接()
    {
        var created = new Credential { Name = "新建的", Type = CredentialType.LocalPassword, Username = "admin" };
        var vm = Create(() => Task.FromResult<Credential?>(created));
        vm.Name = "新主机";
        vm.Host = "host";

        vm.SelectedCredentialChoice = CredentialOption.NewCredentialEntry;
        await WaitUntilAsync(() => vm.SelectedCredential.Id == created.Id);

        Assert.Equal("新建的", vm.SelectedCredentialChoice.Name);
        Assert.Contains(vm.CredentialChoices, c => c.Id == created.Id);
        Assert.Equal(created.Id, vm.Build()?.CredentialId);
    }

    [Fact]
    public async Task 取消新建凭据时保持原来的选择()
    {
        var vm = Create(() => Task.FromResult<Credential?>(null));
        vm.SelectedCredentialChoice = vm.CredentialChoices.Single(c => c.Name == "已有凭据");
        var notified = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionEditorViewModel.SelectedCredentialChoice))
            {
                notified++;
            }
        };

        vm.SelectedCredentialChoice = CredentialOption.NewCredentialEntry;
        await WaitUntilAsync(() => notified >= 2);

        Assert.Equal("已有凭据", vm.SelectedCredential.Name);
        Assert.Equal("已有凭据", vm.SelectedCredentialChoice.Name);
        Assert.Equal(3, vm.CredentialChoices.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "等待条件超时。");
    }
}
