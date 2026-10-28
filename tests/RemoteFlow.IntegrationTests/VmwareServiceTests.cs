using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Vmware;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>ESXi / vCenter 来源：仓储、类型识别、凭据、稳定会话 Id 与写操作前的安全检查。</summary>
public sealed class VmwareServiceTests : IDisposable
{
    private const string Pin = FakeVmwareConnector.Pin;

    private readonly TempWorkspace _workspace = new();
    private readonly SqliteCredentialRepository _credentialRepository;
    private readonly SqliteVmwareSourceRepository _sources;
    private readonly CredentialService _credentials;
    private readonly FakeVmwareConnector _connector = new();
    private readonly VmwareService _service;

    public VmwareServiceTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        _credentialRepository = new SqliteCredentialRepository(database);
        _sources = new SqliteVmwareSourceRepository(database);
        _credentials = new CredentialService(_credentialRepository, new InMemoryCredentialVault(), NullLogger<CredentialService>.Instance);
        _service = new VmwareService(_connector, _sources, _credentials, NullLogger<VmwareService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task 保存来源会登录验证并由服务器自报类型()
    {
        _connector.Kind = VmwareSourceKind.VCenter;

        var saved = await _service.SaveSourceAsync(
            new VmwareSource { Name = "机房", Host = "https://vc.example.test/ui/", CertPinSha256 = Pin }, "admin@vsphere.local", "secret");

        Assert.Equal(VmwareSourceKind.VCenter, saved.Kind);
        Assert.Equal("vc.example.test", saved.Host);
        Assert.Equal(443, saved.Port);
        Assert.NotNull(saved.CredentialId);
        Assert.Equal("admin@vsphere.local", _connector.LastUser);
        Assert.Equal("secret", _connector.LastPassword);

        var stored = Assert.Single(await _service.GetSourcesAsync());
        Assert.Equal(VmwareSourceKind.VCenter, stored.Kind);
        Assert.Equal(saved.CredentialId, stored.CredentialId);
    }

    [Fact]
    public async Task 登录失败时不保存来源也不创建凭据()
    {
        _connector.FailLogin = true;

        await Assert.ThrowsAsync<VsphereApiException>(() => _service.SaveSourceAsync(
            new VmwareSource { Host = "esxi.example.test", CertPinSha256 = Pin }, "root", "wrong"));

        Assert.Empty(await _service.GetSourcesAsync());
        Assert.Empty(await _credentials.GetAllAsync());
    }

    [Fact]
    public async Task 没有核对证书指纹时拒绝保存()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveSourceAsync(
            new VmwareSource { Host = "esxi.example.test", CertPinSha256 = "" }, "root", "x"));
        Assert.Equal(0, _connector.ConnectCount);
    }

    [Fact]
    public async Task 凭据被删除后来源保留且凭据引用置空()
    {
        var saved = await _service.SaveSourceAsync(
            new VmwareSource { Host = "esxi.example.test", CertPinSha256 = Pin }, "root", "secret");
        await _credentialRepository.DeleteAsync(saved.CredentialId!.Value);

        var stored = Assert.Single(await _service.GetSourcesAsync());
        Assert.Null(stored.CredentialId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ListVirtualMachinesAsync(stored));
    }

    [Fact]
    public async Task 控制台配置不落库且同一虚拟机的会话Id稳定()
    {
        var source = await _service.SaveSourceAsync(
            new VmwareSource { Host = "esxi.example.test", CertPinSha256 = Pin }, "root", "secret");
        var vm = new VsphereVm("1", "WIN-DEMO", "poweredOn", "50-aa-bb");

        var first = VmwareService.BuildConsoleProfile(source, vm);
        var second = VmwareService.BuildConsoleProfile(source, vm with { Name = "改名后" });
        var other = VmwareService.BuildConsoleProfile(source, vm with { InstanceUuid = "50-cc-dd" });

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, other.Id);
        Assert.True(first.Vmware.Enabled);
        Assert.Equal(ProtocolType.Vnc, first.Protocol);
        Assert.Equal(source.CredentialId, first.CredentialId);
        Assert.Equal(Pin, first.Vmware.CertPinSha256);
        Assert.Equal("50-aa-bb", first.Vmware.InstanceUuid);
    }

    [Fact]
    public void 没有UUID的虚拟机不能构造控制台配置()
    {
        var source = new VmwareSource { Host = "h", CertPinSha256 = Pin };
        Assert.Throws<InvalidOperationException>(() =>
            VmwareService.BuildConsoleProfile(source, new VsphereVm("1", "VM", "poweredOn", null)));
    }

    [Fact]
    public async Task 恢复快照前确认快照属于该虚拟机()
    {
        var source = await _service.SaveSourceAsync(
            new VmwareSource { Host = "esxi.example.test", CertPinSha256 = Pin }, "root", "secret");
        _connector.Snapshots = [new VmSnapshot("snap-1", "基线", "", null, true, 0)];

        await _service.RevertSnapshotAsync(source, "vm-1", "snap-1");
        Assert.Equal("snap-1", _connector.Reverted);

        var error = await Assert.ThrowsAsync<VsphereApiException>(() => _service.RevertSnapshotAsync(source, "vm-1", "snap-999"));
        Assert.Equal("ManagedObjectNotFound", error.FaultCode);
        Assert.Equal("snap-1", _connector.Reverted);
    }

    [Fact]
    public async Task 每次操作结束都会注销会话()
    {
        var source = await _service.SaveSourceAsync(
            new VmwareSource { Host = "esxi.example.test", CertPinSha256 = Pin }, "root", "secret");
        var before = _connector.Disposed;

        await _service.ListVirtualMachinesAsync(source);
        await _service.PowerAsync(source, "vm-1", VmPowerAction.ShutdownGuest);

        Assert.Equal(before + 2, _connector.Disposed);
    }

    [Fact]
    public async Task 正常关机只报告请求已发出()
    {
        var source = await _service.SaveSourceAsync(
            new VmwareSource { Host = "esxi.example.test", CertPinSha256 = Pin }, "root", "secret");

        Assert.Equal(VmPowerResult.RequestSent, await _service.PowerAsync(source, "vm-1", VmPowerAction.ShutdownGuest));
        Assert.Equal(VmPowerResult.Completed, await _service.PowerAsync(source, "vm-1", VmPowerAction.PowerOff));
    }

    [Fact]
    public void 错误文案来自固定说明且不回显服务器文本()
    {
        Assert.Contains("密码", new VsphereApiException("InvalidLogin").Message);
        Assert.Contains("权限", new VsphereApiException("NoPermission").Message);
        Assert.Contains("VMware Tools", new VsphereApiException("ToolsUnavailable").Message);
        Assert.DoesNotContain("evil", new VsphereApiException("MissingResponseField:evil").Message.Replace("缺少必要字段", ""));
    }
}
