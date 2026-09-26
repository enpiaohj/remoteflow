using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class RecoveryKeyTests
{
    [Fact]
    public void Generated_key_round_trips_through_its_display_string()
    {
        var key = RecoveryKey.Generate();

        var parsed = RecoveryKey.Parse(key.ToDisplayString());

        Assert.Equal(key.AsSpan().ToArray(), parsed.AsSpan().ToArray());
    }

    [Fact]
    public void Parsing_tolerates_spaces_hyphens_and_case()
    {
        var key = RecoveryKey.Generate();
        var display = key.ToDisplayString();
        var messy = display.Replace(" ", "-").ToLowerInvariant() + "  ";

        var parsed = RecoveryKey.Parse(messy);

        Assert.Equal(key.AsSpan().ToArray(), parsed.AsSpan().ToArray());
    }

    [Fact]
    public void Display_string_is_grouped_into_blocks_of_four()
    {
        var display = RecoveryKey.Generate().ToDisplayString();

        var groups = display.Split(' ');
        Assert.All(groups[..^1], g => Assert.Equal(4, g.Length));
        Assert.Equal(52, display.Replace(" ", "").Length); // 32 bytes -> 52 base32 chars
    }

    [Fact]
    public void Parsing_rejects_an_invalid_character()
    {
        Assert.Throws<FormatException>(() => RecoveryKey.Parse("AAAA AAAA 0000 AAAA")); // '0' not in RFC4648 alphabet
    }

    [Fact]
    public void Parsing_rejects_the_wrong_length()
    {
        Assert.Throws<FormatException>(() => RecoveryKey.Parse("ABCD EFGH"));
    }

    [Fact]
    public void A_recovered_key_actually_unwraps_the_vault()
    {
        var service = new RecoveryKeyService();
        var master = VaultCryptography.NewMasterKey();
        var (key, envelope) = service.Create(master);

        var recovered = service.RecoverMasterKey(key.ToDisplayString(), envelope);

        Assert.Equal(master, recovered);
    }
}
