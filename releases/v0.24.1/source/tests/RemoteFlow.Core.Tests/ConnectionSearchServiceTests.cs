using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.Core.Tests;

/// <summary>
/// 全局搜索的行为验证。搜索是「服务器多时比层层点目录更重要」的核心能力，
/// 因此匹配范围与相关度排序都需要被固定下来。
/// </summary>
public class ConnectionSearchServiceTests
{
    private readonly ConnectionSearchService _search = new();

    private static readonly Guid ProductionGroupId = Guid.NewGuid();
    private static readonly Guid AdTagId = Guid.NewGuid();

    private readonly IReadOnlyDictionary<Guid, string> _groups =
        new Dictionary<Guid, string> { [ProductionGroupId] = "生产环境" };

    private readonly IReadOnlyDictionary<Guid, string> _tags =
        new Dictionary<Guid, string> { [AdTagId] = "AD" };

    private static ConnectionProfile Profile(
        string name, string host, ProtocolType protocol = ProtocolType.Rdp,
        Guid? groupId = null, Guid? tagId = null, string notes = "") => new()
    {
        Name = name,
        Host = host,
        Port = ConnectionProfile.GetDefaultPort(protocol),
        Protocol = protocol,
        GroupId = groupId,
        TagIds = tagId is { } id ? [id] : [],
        Notes = notes
    };

    [Fact]
    public void 搜索词为空时返回全部连接()
    {
        var source = new[] { Profile("DC01", "10.10.1.10"), Profile("nginx01", "10.10.2.20") };

        var result = _search.Search(source, "", _groups, _tags);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void 可以按名称匹配()
    {
        var source = new[] { Profile("DC01", "10.10.1.10"), Profile("nginx01", "10.10.2.20") };

        var result = _search.Search(source, "dc", _groups, _tags);

        Assert.Single(result);
        Assert.Equal("DC01", result[0].Name);
    }

    [Fact]
    public void 可以按IP匹配()
    {
        var source = new[] { Profile("DC01", "10.10.1.10"), Profile("nginx01", "10.20.2.20") };

        var result = _search.Search(source, "10.20", _groups, _tags);

        Assert.Single(result);
        Assert.Equal("nginx01", result[0].Name);
    }

    [Fact]
    public void 可以按标签匹配()
    {
        var source = new[]
        {
            Profile("DC01", "10.10.1.10", tagId: AdTagId),
            Profile("web01", "10.10.3.30")
        };

        var result = _search.Search(source, "AD", _groups, _tags);

        Assert.Single(result);
        Assert.Equal("DC01", result[0].Name);
    }

    [Fact]
    public void 可以按分组匹配()
    {
        var source = new[]
        {
            Profile("DC01", "10.10.1.10", groupId: ProductionGroupId),
            Profile("web01", "10.10.3.30")
        };

        var result = _search.Search(source, "生产", _groups, _tags);

        Assert.Single(result);
        Assert.Equal("DC01", result[0].Name);
    }

    [Fact]
    public void 可以按备注匹配()
    {
        var source = new[]
        {
            Profile("srv-a", "10.10.1.10", notes: "主域控制器"),
            Profile("srv-b", "10.10.3.30")
        };

        var result = _search.Search(source, "域控", _groups, _tags);

        Assert.Single(result);
        Assert.Equal("srv-a", result[0].Name);
    }

    [Fact]
    public void 可以按协议名筛选()
    {
        var source = new[]
        {
            Profile("DC01", "10.10.1.10", ProtocolType.Rdp),
            Profile("nginx01", "10.10.2.20", ProtocolType.Ssh)
        };

        var result = _search.Search(source, "ssh", _groups, _tags);

        Assert.Single(result);
        Assert.Equal("nginx01", result[0].Name);
    }

    [Fact]
    public void 名称前缀匹配的相关度高于备注匹配()
    {
        var source = new[]
        {
            Profile("other", "10.0.0.2", notes: "这台是 DC01 的备机"),
            Profile("DC01", "10.0.0.1")
        };

        var result = _search.Search(source, "DC01", _groups, _tags);

        Assert.Equal(2, result.Count);
        Assert.Equal("DC01", result[0].Name);
    }

    [Fact]
    public void 相关度相同时最近连接过的排在前面()
    {
        var older = Profile("srv-a", "10.0.0.1");
        older.LastConnectedAt = DateTimeOffset.Now.AddDays(-5);

        var newer = Profile("srv-b", "10.0.0.2");
        newer.LastConnectedAt = DateTimeOffset.Now.AddMinutes(-5);

        var result = _search.Search([older, newer], "srv", _groups, _tags);

        Assert.Equal("srv-b", result[0].Name);
    }

    [Fact]
    public void 搜索不区分大小写()
    {
        var source = new[] { Profile("NGINX01", "10.10.2.20") };

        Assert.Single(_search.Search(source, "nginx", _groups, _tags));
        Assert.Single(_search.Search(source, "NGINX", _groups, _tags));
    }

    [Fact]
    public void 无匹配时返回空集合()
    {
        var source = new[] { Profile("DC01", "10.10.1.10") };

        Assert.Empty(_search.Search(source, "不存在的主机", _groups, _tags));
    }
}
