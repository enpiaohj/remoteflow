using RemoteFlow.Core.Vmware;

namespace RemoteFlow.IntegrationTests;

/// <summary>VMware 相关测试共用的假连接器 / 假 API：可配置虚拟机与快照，并记录写操作。</summary>
internal sealed class FakeVmwareConnector : IVmwareConnector
{
    public const string Pin = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";

    public VmwareSourceKind Kind { get; set; } = VmwareSourceKind.EsxiHost;
    public bool FailLogin { get; set; }
    public int ConnectCount { get; private set; }
    public int Disposed { get; set; }
    public string? LastUser { get; private set; }
    public string? LastPassword { get; private set; }

    public IReadOnlyList<VsphereVm> Vms { get; set; } =
        [new VsphereVm("vm-1", "VM", "poweredOn", "50-aa-bb")];

    public IReadOnlyList<VmSnapshot> Snapshots { get; set; } = [];
    public string? Reverted { get; set; }
    public List<(string Vm, VmPowerAction Action)> PowerCalls { get; } = [];
    public List<string> CreatedSnapshots { get; } = [];
    public List<(string Vm, int Width, int Height)> Resolutions { get; } = [];
    public bool ToolsUnavailable { get; set; }
    public int? VideoRamKb { get; set; } = 8192;

    public Task<string> FetchCertificatePinAsync(string host, int port, CancellationToken ct = default) => Task.FromResult(Pin);

    public Task<IVSphereConsoleApi> ConnectAsync(string host, int port, string certPinSha256,
        string userName, string password, CancellationToken ct = default)
    {
        ConnectCount++;
        LastUser = userName;
        LastPassword = password;
        if (FailLogin) throw new VsphereApiException("InvalidLogin");
        return Task.FromResult<IVSphereConsoleApi>(new FakeVmwareApi(this));
    }
}

internal sealed class FakeVmwareApi(FakeVmwareConnector owner) : IVSphereConsoleApi
{
    public VmwareServerInfo? ServerInfo { get; } = new(owner.Kind, "Fake", "8.0.3", "1");

    public Task LoginAsync(string userName, string password, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<VsphereVm>> ListVirtualMachinesAsync(CancellationToken ct = default) => Task.FromResult(owner.Vms);

    public Task<WebMksTicket> AcquireWebMksTicketAsync(string vmReference, CancellationToken ct = default)
        => Task.FromResult(new WebMksTicket("h", 443, "t"));

    public Task<VmPowerResult> PowerAsync(string vmReference, VmPowerAction action, CancellationToken ct = default)
    {
        owner.PowerCalls.Add((vmReference, action));
        return Task.FromResult(action is VmPowerAction.ShutdownGuest or VmPowerAction.RebootGuest
            ? VmPowerResult.RequestSent
            : VmPowerResult.Completed);
    }

    public Task<int?> GetVideoMemoryKbAsync(string vmReference, CancellationToken ct = default) => Task.FromResult(owner.VideoRamKb);

    public Task SetScreenResolutionAsync(string vmReference, int width, int height, CancellationToken ct = default)
    {
        if (owner.ToolsUnavailable) throw new VsphereApiException("ToolsUnavailable");
        owner.Resolutions.Add((vmReference, width, height));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VmSnapshot>> ListSnapshotsAsync(string vmReference, CancellationToken ct = default) => Task.FromResult(owner.Snapshots);

    public Task CreateSnapshotAsync(string vmReference, string name, string description, CancellationToken ct = default)
    {
        owner.CreatedSnapshots.Add(name);
        return Task.CompletedTask;
    }

    public Task RevertSnapshotAsync(string snapshotReference, CancellationToken ct = default)
    {
        owner.Reverted = snapshotReference;
        return Task.CompletedTask;
    }

    public Task RemoveSnapshotAsync(string snapshotReference, CancellationToken ct = default) => Task.CompletedTask;

    public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        owner.Disposed++;
        return ValueTask.CompletedTask;
    }
}
