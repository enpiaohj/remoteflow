using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 连接 CSV 导入 / 导出的端到端验证。重点：导入去重——
/// 名称 + 主机 + 端口 + 协议相同的行不重复写入。
/// </summary>
public sealed class ImportExportServiceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;
    private readonly SqliteConnectionRepository _connections;
    private readonly ImportExportService _service;

    public ImportExportServiceTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();

        _connections = new SqliteConnectionRepository(_database);
        _service = new ImportExportService(
            _connections,
            new SqliteGroupRepository(_database),
            new SqliteTagRepository(_database),
            new SqliteCredentialRepository(_database),
            NullLogger<ImportExportService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    private async Task<string> WriteCsvAsync(string content)
    {
        var path = Path.Combine(_workspace.Root, $"{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private const string Header = "Name,Host,Port,Protocol,Group,Tags,CredentialName,Notes";

    [Fact]
    public async Task 同一个CSV里的重复行只导入一次()
    {
        var csv = string.Join('\n',
            Header,
            "Web-01,10.0.0.1,22,Ssh,,,,",
            "Web-01,10.0.0.1,22,Ssh,,,,",   // 完全重复
            "Web-02,10.0.0.2,3389,Rdp,,,,");

        var result = await _service.ImportAsync(await WriteCsvAsync(csv));

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(result.Errors, e => e.Contains("Web-01") && e.Contains("已存在"));
        Assert.Equal(2, (await _connections.GetAllAsync()).Count);
    }

    [Fact]
    public async Task 重复导入同一个文件不产生副本()
    {
        var csv = string.Join('\n',
            Header,
            "DC,192.0.2.11,3389,Rdp,,,,",
            "App,app.example.com,22,Ssh,,,,");

        var path = await WriteCsvAsync(csv);

        var first = await _service.ImportAsync(path);
        Assert.Equal(2, first.Imported);

        var second = await _service.ImportAsync(path);
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.Skipped);

        Assert.Equal(2, (await _connections.GetAllAsync()).Count);
    }

    [Fact]
    public async Task 名称相同但主机或端口不同视为不同连接()
    {
        await _service.ImportAsync(await WriteCsvAsync(string.Join('\n',
            Header, "srv,10.0.0.1,22,Ssh,,,,")));

        var csv = string.Join('\n',
            Header,
            "srv,10.0.0.1,22,Ssh,,,,",     // 同一条 → 跳过
            "srv,10.0.0.2,22,Ssh,,,,",     // 主机不同 → 新增
            "srv,10.0.0.1,2222,Ssh,,,,");  // 端口不同 → 新增
        var result = await _service.ImportAsync(await WriteCsvAsync(csv));

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(3, (await _connections.GetAllAsync()).Count);
    }

    [Fact]
    public async Task 导出再导入是幂等的_不重复()
    {
        await _service.ImportAsync(await WriteCsvAsync(string.Join('\n',
            Header,
            "one,1.1.1.1,22,Ssh,,,,",
            "two,2.2.2.2,5900,Vnc,,,,")));

        var exportPath = Path.Combine(_workspace.Root, "out.csv");
        await _service.ExportAsync(exportPath);

        var reimport = await _service.ImportAsync(exportPath);

        Assert.Equal(0, reimport.Imported);
        Assert.Equal(2, (await _connections.GetAllAsync()).Count);
    }
}
