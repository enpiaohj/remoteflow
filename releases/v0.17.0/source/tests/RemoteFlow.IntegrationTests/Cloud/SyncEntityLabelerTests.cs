using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Sync;
using RemoteFlow.Infrastructure.Sync.Sources;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// 冲突标签：本地已删除、只能靠云端密文识别时，能从 Payload 解出名称 ——
/// 避免冲突对话框只显示 GUID（如「(已删除的分组) 9f5fd131-…」）。
/// </summary>
public sealed class SyncEntityLabelerTests
{
    [Fact]
    public void Connection_payload_yields_name_and_host()
    {
        var profile = new ConnectionProfile { Name = "DC01", Host = "10.0.0.1" };
        var payload = SyncSerializer.Serialize(profile, 1);

        Assert.Equal("DC01（10.0.0.1）", SyncEntityLabeler.DescribePayload("connection", payload));
    }

    [Fact]
    public void Group_payload_yields_the_name()
    {
        var group = new ConnectionGroup { Name = "生产环境" };
        var payload = SyncSerializer.Serialize(group, 1);

        Assert.Equal("生产环境", SyncEntityLabeler.DescribePayload("group", payload));
    }

    [Fact]
    public void Tag_payload_yields_the_name()
    {
        var tag = new Tag { Name = "prod" };
        var payload = SyncSerializer.Serialize(tag, 1);

        Assert.Equal("prod", SyncEntityLabeler.DescribePayload("tag", payload));
    }

    [Fact]
    public void Credential_payload_uses_the_metadata_shape()
    {
        var metadata = new CredentialSyncSource.CredentialMetadata(
            "DomainAdmin", CredentialType.WindowsDomain, "admin", "CORP", "", DateTimeOffset.Now, DateTimeOffset.Now);
        var payload = SyncSerializer.Serialize(metadata, 1);

        Assert.Equal("DomainAdmin", SyncEntityLabeler.DescribePayload("credential", payload));
    }

    [Fact]
    public void Unknown_or_broken_payload_returns_null_instead_of_throwing()
    {
        Assert.Null(SyncEntityLabeler.DescribePayload("connection", "not json"u8.ToArray()));
        Assert.Null(SyncEntityLabeler.DescribePayload("credential-secret", "{}"u8.ToArray()));
    }
}
