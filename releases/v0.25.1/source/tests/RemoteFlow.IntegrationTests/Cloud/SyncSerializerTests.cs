using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class SyncSerializerTests
{
    [Fact]
    public void Connection_profile_round_trips_with_all_fields()
    {
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = "DC01",
            Host = "10.20.30.40",
            Port = 3389,
            Protocol = ProtocolType.Rdp,
            GroupId = Guid.NewGuid(),
            CredentialId = Guid.NewGuid(),
            Favorite = true,
            Notes = "生产域控",
            TagIds = [Guid.NewGuid(), Guid.NewGuid()],
        };
        profile.Rdp.UseMultimon = true;

        var payload = SyncSerializer.Serialize(profile, schemaVersion: 1);
        var (version, restored) = SyncSerializer.Deserialize<ConnectionProfile>(payload);

        Assert.Equal(1, version);
        Assert.Equal(profile.Name, restored.Name);
        Assert.Equal(profile.Host, restored.Host);
        Assert.Equal(profile.Protocol, restored.Protocol);
        Assert.Equal(profile.Favorite, restored.Favorite);
        Assert.Equal(profile.TagIds, restored.TagIds);
        Assert.True(restored.Rdp.UseMultimon);
    }

    [Fact]
    public void Payload_is_self_describing_about_its_schema_version()
    {
        var payload = SyncSerializer.Serialize(new Tag { Name = "prod" }, schemaVersion: 3);

        var text = System.Text.Encoding.UTF8.GetString(payload);
        Assert.Contains("\"v\":3", text);
        Assert.Equal(3, SyncSerializer.Deserialize<Tag>(payload).SchemaVersion);
    }

    [Fact]
    public void The_same_entity_serializes_deterministically()
    {
        var tag = new Tag { Id = Guid.NewGuid(), Name = "x", Color = "#112233" };

        Assert.Equal(
            SyncSerializer.Serialize(tag, 1),
            SyncSerializer.Serialize(tag, 1));
    }
}
