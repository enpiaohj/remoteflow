using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class VaultCryptographyTests
{
    private static PayloadContext Context(string entityType = "connection", string entityId = "conn-1") =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "com.appscloud.remoteflow",
            entityType, entityId, SchemaVersion: 1, KeyVersion: 1);

    [Fact]
    public void Device_envelope_round_trips_the_master_key_for_the_same_device()
    {
        var (privateKey, publicKey) = VaultCryptography.NewDeviceKeyPair();
        var master = VaultCryptography.NewMasterKey();

        var envelope = VaultCryptography.WrapForDevice(publicKey, master);
        var recovered = VaultCryptography.UnwrapFromDevice(privateKey, envelope);

        Assert.Equal(master, recovered);
        Assert.Equal(VaultAlgorithms.DeviceEnvelope, envelope.Algorithm);
    }

    [Fact]
    public void Approved_device_unwraps_an_envelope_wrapped_by_another_device()
    {
        var (bPrivate, bPublic) = VaultCryptography.NewDeviceKeyPair();
        var master = VaultCryptography.NewMasterKey();

        // 设备 A 用设备 B 的公钥包装 VMK（批准流程）
        var envelope = VaultCryptography.WrapForDevice(bPublic, master);
        var recovered = VaultCryptography.UnwrapFromDevice(bPrivate, envelope);

        Assert.Equal(master, recovered);
    }

    [Fact]
    public void A_different_device_key_cannot_unwrap_the_envelope()
    {
        var (_, targetPublic) = VaultCryptography.NewDeviceKeyPair();
        var (otherPrivate, _) = VaultCryptography.NewDeviceKeyPair();
        var envelope = VaultCryptography.WrapForDevice(targetPublic, VaultCryptography.NewMasterKey());

        Assert.ThrowsAny<CryptographicException>(
            () => VaultCryptography.UnwrapFromDevice(otherPrivate, envelope));
    }

    [Fact]
    public void Recovery_envelope_round_trips_the_master_key()
    {
        var master = VaultCryptography.NewMasterKey();
        var recoveryKey = RandomNumberGenerator.GetBytes(32);

        var envelope = VaultCryptography.WrapForRecovery(recoveryKey, master);
        var recovered = VaultCryptography.UnwrapFromRecovery(recoveryKey, envelope);

        Assert.Equal(master, recovered);
        Assert.Equal(VaultAlgorithms.RecoveryEnvelope, envelope.Algorithm);
    }

    [Fact]
    public void A_wrong_recovery_key_cannot_unwrap()
    {
        var envelope = VaultCryptography.WrapForRecovery(
            RandomNumberGenerator.GetBytes(32), VaultCryptography.NewMasterKey());

        Assert.ThrowsAny<CryptographicException>(
            () => VaultCryptography.UnwrapFromRecovery(RandomNumberGenerator.GetBytes(32), envelope));
    }

    [Fact]
    public void Payload_round_trips_when_the_context_matches()
    {
        var master = VaultCryptography.NewMasterKey();
        var plaintext = Encoding.UTF8.GetBytes("""{"host":"10.0.0.1","user":"admin"}""");
        var context = Context();

        var payload = VaultCryptography.EncryptPayload(master, context, plaintext);
        var recovered = VaultCryptography.DecryptPayload(master, context, payload);

        Assert.Equal(plaintext, recovered);
        Assert.Equal(1, payload.KeyVersion);
        Assert.Equal(1, payload.SchemaVersion);
    }

    [Fact]
    public void Tampering_with_the_ciphertext_fails_decryption()
    {
        var master = VaultCryptography.NewMasterKey();
        var context = Context();
        var payload = VaultCryptography.EncryptPayload(master, context, Encoding.UTF8.GetBytes("secret"));
        payload.Ciphertext[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(
            () => VaultCryptography.DecryptPayload(master, context, payload));
    }

    [Fact]
    public void A_payload_cannot_be_replayed_under_a_different_entity_context()
    {
        var master = VaultCryptography.NewMasterKey();
        var payload = VaultCryptography.EncryptPayload(
            master, Context(entityId: "conn-1"), Encoding.UTF8.GetBytes("secret"));

        // 同 VMK、同 EntityType，但 EntityId 不同 —— AAD 不匹配，必须认证失败而不是返回乱码
        Assert.ThrowsAny<CryptographicException>(
            () => VaultCryptography.DecryptPayload(master, Context(entityId: "conn-2"), payload));
    }

    [Fact]
    public void A_payload_cannot_be_read_across_accounts()
    {
        var master = VaultCryptography.NewMasterKey();
        var mine = Context();
        var theirs = mine with { UserId = Guid.Parse("22222222-2222-2222-2222-222222222222") };
        var payload = VaultCryptography.EncryptPayload(master, mine, Encoding.UTF8.GetBytes("secret"));

        Assert.ThrowsAny<CryptographicException>(
            () => VaultCryptography.DecryptPayload(master, theirs, payload));
    }

    [Fact]
    public void The_same_plaintext_encrypts_to_a_different_ciphertext_each_time()
    {
        var master = VaultCryptography.NewMasterKey();
        var context = Context();
        var plaintext = Encoding.UTF8.GetBytes("same input");

        var a = VaultCryptography.EncryptPayload(master, context, plaintext);
        var b = VaultCryptography.EncryptPayload(master, context, plaintext);

        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
    }
}
