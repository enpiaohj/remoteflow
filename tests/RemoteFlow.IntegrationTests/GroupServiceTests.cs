using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 分组模型的端到端验证（UI 分组优化提示词 §25）：真实建库 + 迁移 + GroupService。
/// </summary>
public sealed class GroupServiceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;
    private readonly SqliteGroupRepository _groups;
    private readonly SqliteConnectionRepository _connections;
    private readonly GroupService _service;

    public GroupServiceTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();
        _groups = new SqliteGroupRepository(_database);
        _connections = new SqliteConnectionRepository(_database);
        _service = new GroupService(_groups, _connections);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    private async Task<Guid> AddConnectionAsync(string name, Guid? groupId)
    {
        var profile = new ConnectionProfile
        {
            Name = name,
            Host = "10.0.0.1",
            Port = 3389,
            Protocol = ProtocolType.Rdp,
            GroupId = groupId,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        await _connections.AddAsync(profile);
        return profile.Id;
    }

    [Fact]
    public async Task 迁移后未分组系统分组存在且不可删不可改()
    {
        var all = await _groups.GetAllAsync();
        var ungrouped = Assert.Single(all, g => g.Id == ConnectionGroup.UngroupedId);
        Assert.True(ungrouped.IsSystem);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(ConnectionGroup.UngroupedId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RenameAsync(ConnectionGroup.UngroupedId, "改个名"));
    }

    [Fact]
    public async Task 首次种子在无用户分组时创建我的设备()
    {
        var defaultId = await _service.EnsureSeedAsync();
        Assert.NotNull(defaultId);

        var all = await _groups.GetAllAsync();
        var mine = Assert.Single(all, g => !g.IsSystem);
        Assert.Equal(GroupService.DefaultGroupName, mine.Name);
        Assert.Equal(mine.Id, defaultId!.Value);
    }

    [Fact]
    public async Task 已有用户分组时首次种子仍创建我的设备且不动现有分组()
    {
        var company = await _service.CreateAsync("公司", null);

        var defaultId = await _service.EnsureSeedAsync();

        var all = await _groups.GetAllAsync();
        var mine = Assert.Single(all, g => g.Name == GroupService.DefaultGroupName);
        Assert.Equal(mine.Id, defaultId);
        Assert.True(mine.IsDefault);
        Assert.True(mine.IsProtected);

        // 现有「公司」原封不动。
        var untouched = all.Single(g => g.Id == company.Id);
        Assert.False(untouched.IsDefault);
        Assert.False(untouched.IsProtected);

        // 「我的设备」排在「公司」之前。
        Assert.True(mine.SortOrder < untouched.SortOrder);
    }

    [Fact]
    public async Task 已有同名我的设备普通组时首次种子复用它而非新建()
    {
        var existing = await _service.CreateAsync(GroupService.DefaultGroupName, null);

        var defaultId = await _service.EnsureSeedAsync();

        var all = await _groups.GetAllAsync();
        var mine = Assert.Single(all, g => g.Name == GroupService.DefaultGroupName);
        Assert.Equal(existing.Id, mine.Id);
        Assert.Equal(existing.Id, defaultId);
        Assert.True(mine.IsDefault);
        Assert.True(mine.IsProtected);
    }

    [Fact]
    public async Task 创建分组与子分组()
    {
        var parent = await _service.CreateAsync("公司", null);
        var child = await _service.CreateAsync("生产", parent.Id);

        Assert.Equal(parent.Id, child.ParentId);

        var tree = await _service.GetTreeAsync();
        var parentNode = Assert.Single(tree, n => n.Group.Id == parent.Id);
        Assert.Single(parentNode.Children, n => n.Group.Id == child.Id);
    }

    [Fact]
    public async Task 同级同名分组被拒绝()
    {
        await _service.CreateAsync("公司", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(" 公司 ", null));
    }

    [Fact]
    public async Task 空名分组被拒绝()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync("   ", null));
    }

    [Fact]
    public async Task 重命名分组保留Id与子分组()
    {
        var parent = await _service.CreateAsync("公司", null);
        var child = await _service.CreateAsync("生产", parent.Id);

        await _service.RenameAsync(parent.Id, "总部");

        var all = await _groups.GetAllAsync();
        Assert.Equal("总部", all.Single(g => g.Id == parent.Id).Name);
        Assert.Equal(parent.Id, all.Single(g => g.Id == child.Id).ParentId);
    }

    [Fact]
    public async Task 删除空分组()
    {
        var group = await _service.CreateAsync("临时", null);
        await _service.DeleteAsync(group.Id);

        Assert.DoesNotContain(await _groups.GetAllAsync(), g => g.Id == group.Id);
    }

    [Fact]
    public async Task 删除有连接的分组_连接进入未分组()
    {
        var group = await _service.CreateAsync("公司", null);
        var connId = await AddConnectionAsync("DC01", group.Id);

        await _service.DeleteAsync(group.Id);

        var conn = await _connections.GetByIdAsync(connId);
        Assert.NotNull(conn);
        Assert.Null(conn!.GroupId); // 未分组以 null 表示
    }

    [Fact]
    public async Task 删除父分组_子分组提升到父级的父级()
    {
        var company = await _service.CreateAsync("公司", null);
        var prod = await _service.CreateAsync("生产", company.Id);
        var dc = await _service.CreateAsync("机房", prod.Id);

        await _service.DeleteAsync(prod.Id); // 删「生产」

        var all = await _groups.GetAllAsync();
        // 「机房」应提升到「公司」（被删分组的父级）
        Assert.Equal(company.Id, all.Single(g => g.Id == dc.Id).ParentId);
    }

    [Fact]
    public async Task 移动分组到自己的子分组下被拒绝()
    {
        var a = await _service.CreateAsync("A", null);
        var b = await _service.CreateAsync("B", a.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.MoveAsync(a.Id, b.Id));
    }

    [Fact]
    public async Task 移动连接到分组并落库()
    {
        var g1 = await _service.CreateAsync("G1", null);
        var g2 = await _service.CreateAsync("G2", null);
        var connId = await AddConnectionAsync("srv", g1.Id);

        await _service.MoveConnectionAsync(connId, g2.Id);
        Assert.Equal(g2.Id, (await _connections.GetByIdAsync(connId))!.GroupId);

        await _service.MoveConnectionAsync(connId, ConnectionGroup.UngroupedId);
        Assert.Null((await _connections.GetByIdAsync(connId))!.GroupId);
    }

    [Fact]
    public async Task 迁移不丢既有分组与连接()
    {
        // 直接以 v2 建库已在构造函数完成；这里验证既有普通分组升级后可被服务操作。
        var legacy = new ConnectionGroup { Name = "Windows", SortOrder = 0 };
        await _groups.AddAsync(legacy);
        var connId = await AddConnectionAsync("legacy-conn", legacy.Id);

        await _service.RenameAsync(legacy.Id, "我的 Windows");
        await _service.DeleteAsync(legacy.Id);

        Assert.NotNull(await _connections.GetByIdAsync(connId));
        Assert.Null((await _connections.GetByIdAsync(connId))!.GroupId);
    }

    [Fact]
    public async Task 首次种子创建默认组并带默认与保护()
    {
        var defaultId = await _service.EnsureSeedAsync(createIfEmpty: true);
        Assert.NotNull(defaultId);
        var def = (await _groups.GetAllAsync()).Single(g => !g.IsSystem);
        Assert.True(def.IsDefault);
        Assert.True(def.IsProtected);
        Assert.Equal(def.Id, defaultId!.Value);
    }

    [Fact]
    public async Task 种子标记已种时删光分组不再复活()
    {
        var first = await _service.EnsureSeedAsync(createIfEmpty: true);
        Assert.NotNull(first);
        await _service.SetDefaultProtectionAsync(false);
        await _service.DeleteAsync(first!.Value);

        var again = await _service.EnsureSeedAsync(createIfEmpty: false);
        Assert.Null(again);
        Assert.DoesNotContain(await _groups.GetAllAsync(), g => !g.IsSystem);
    }

    [Fact]
    public async Task 已种过且有普通分组但无默认时不回填返回null()
    {
        // 已种过（createIfEmpty:false）代表用户曾经历过种子：此时无默认组 = 用户自己删了默认组。
        // 不借用现有分组顶上，也不复活「我的设备」，返回 null（新连接回落未分组）。
        await _service.CreateAsync("首个分组", null);
        await _service.CreateAsync("第二个分组", null);

        var defaultId = await _service.EnsureSeedAsync(createIfEmpty: false);

        Assert.Null(defaultId);
        Assert.DoesNotContain(await _groups.GetAllAsync(), g => !g.IsSystem && g.IsDefault);
    }

    [Fact]
    public async Task 受保护默认组拒绝改名删移()
    {
        var defaultId = (await _service.EnsureSeedAsync())!.Value;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RenameAsync(defaultId, "新名"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(defaultId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.MoveAsync(defaultId, null));
    }

    [Fact]
    public async Task 关保护后可改默认并删除()
    {
        var defaultId = (await _service.EnsureSeedAsync())!.Value;
        await _service.SetDefaultProtectionAsync(false);

        var other = (await _service.CreateAsync("服务器", null)).Id;
        await _service.SetDefaultAsync(other);

        var def = (await _groups.GetAllAsync()).Single(g => !g.IsSystem && g.IsDefault);
        Assert.Equal(other, def.Id);
        Assert.False(def.IsProtected);

        await _service.DeleteAsync(other);
        Assert.Null(await _service.GetDefaultGroupAsync());
    }

    [Fact]
    public async Task 受保护默认组存在时SetDefault被拒绝()
    {
        var defaultId = (await _service.EnsureSeedAsync())!.Value;
        var other = (await _service.CreateAsync("服务器", null)).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetDefaultAsync(other));
        var all = await _groups.GetAllAsync();
        Assert.True(all.Single(g => g.Id == defaultId).IsDefault);
    }
}
