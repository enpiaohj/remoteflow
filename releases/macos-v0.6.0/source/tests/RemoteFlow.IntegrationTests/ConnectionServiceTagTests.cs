using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 标签管理端到端验证：真实建库 + 迁移 + <see cref="ConnectionService"/> 的标签 CRUD。
/// 仓储层（SqliteTagRepository）早就支持任意名称 / 颜色，这里验证的是服务层新增的
/// 校验规则（名称去空白、不能为空、不能重名、颜色必须 #RRGGBB）。
/// </summary>
public sealed class ConnectionServiceTagTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly ConnectionService _service;

    public ConnectionServiceTagTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        _service = new ConnectionService(
            new SqliteConnectionRepository(database),
            new SqliteGroupRepository(database),
            new SqliteTagRepository(database));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task 新建标签_名称去空白_颜色写入成功()
    {
        var tag = await _service.CreateTagAsync("  生产  ", "#C4342A", "  生产环境  ");

        Assert.Equal("生产", tag.Name);
        Assert.Equal("#C4342A", tag.Color);
        Assert.Equal("生产环境", tag.Description);

        var all = await _service.GetTagsAsync();
        Assert.Single(all, t => t.Id == tag.Id);
    }

    [Fact]
    public async Task 新建标签_名称为空_抛异常()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateTagAsync("   ", "#0F6CBD", ""));
    }

    [Fact]
    public async Task 新建标签_颜色格式不对_抛异常()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateTagAsync("测试", "red", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateTagAsync("测试", "#FFF", ""));
    }

    [Fact]
    public async Task 新建标签_同名大小写不敏感_抛异常()
    {
        await _service.CreateTagAsync("Prod", "#0F6CBD", "");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateTagAsync("PROD", "#985900", ""));
    }

    [Fact]
    public async Task 更新标签_改名改色成功()
    {
        var tag = await _service.CreateTagAsync("旧名字", "#0F6CBD", "");

        await _service.UpdateTagAsync(tag.Id, "新名字", "#0E7C57", "改过的描述");

        var updated = Assert.Single(await _service.GetTagsAsync());
        Assert.Equal("新名字", updated.Name);
        Assert.Equal("#0E7C57", updated.Color);
        Assert.Equal("改过的描述", updated.Description);
    }

    [Fact]
    public async Task 更新标签_改成已存在的同名_抛异常且不影响原值()
    {
        var a = await _service.CreateTagAsync("A", "#0F6CBD", "");
        await _service.CreateTagAsync("B", "#985900", "");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateTagAsync(a.Id, "B", "#0F6CBD", ""));

        var stillA = (await _service.GetTagsAsync()).Single(t => t.Id == a.Id);
        Assert.Equal("A", stillA.Name);
    }

    [Fact]
    public async Task 更新标签_不存在的Id_抛异常()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateTagAsync(Guid.NewGuid(), "任意名字", "#0F6CBD", ""));
    }

    [Fact]
    public async Task 删除标签_成功后列表不再包含()
    {
        var tag = await _service.CreateTagAsync("待删除", "#0F6CBD", "");

        await _service.DeleteTagAsync(tag.Id);

        Assert.Empty(await _service.GetTagsAsync());
    }
}
