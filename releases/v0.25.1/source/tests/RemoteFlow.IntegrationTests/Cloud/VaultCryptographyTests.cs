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

    // ── 认证密钥 ────────────────────────────────────────────────

    [Fact]
    public void Auth_key_is_deterministic_for_the_same_password_and_email()
    {
        var a = VaultCryptography.DeriveAuthKey("cloud-passw0rd", "Me@Example.com");
        var b = VaultCryptography.DeriveAuthKey("cloud-passw0rd", "me@example.com  ");

        Assert.Equal(a, b); // 邮箱大小写与空白已规范化
        Assert.Equal(32, Convert.FromBase64String(a).Length);
    }

    [Fact]
    public void Auth_key_differs_by_password_and_by_email()
    {
        var baseKey = VaultCryptography.DeriveAuthKey("cloud-passw0rd", "me@example.com");

        Assert.NotEqual(baseKey, VaultCryptography.DeriveAuthKey("cloud-passw0rE", "me@example.com"));
        Assert.NotEqual(baseKey, VaultCryptography.DeriveAuthKey("cloud-passw0rd", "you@example.com"));
    }

    // ── 口令 / Recovery 信封 ────────────────────────────────────

    [Fact]
    public void Password_envelope_round_trips_the_master_key()
    {
        var master = VaultCryptography.NewMasterKey();

        var envelope = VaultCryptography.WrapWithSecret(VaultEnvelopeKinds.Password, "master-passw0rd", master);
        var recovered = VaultCryptography.UnwrapWithSecret("master-passw0rd", envelope);

        Assert.Equal(master, recovered);
        Assert.Equal(VaultEnvelopeKinds.Password, envelope.Kind);
        Assert.Equal(VaultAlgorithms.SecretEnvelope, envelope.Algorithm);
        Assert.Equal(VaultCryptography.Pbkdf2Iterations, envelope.Iterations);
        Assert.NotEmpty(envelope.Salt);
    }

    [Fact]
    public void Recovery_envelope_round_trips_the_master_key()
    {
        var master = VaultCryptography.NewMasterKey();
        var recoveryKey = RecoveryKey.Generate().ToDisplayString();

        var envelope = VaultCryptography.WrapWithSecret(VaultEnvelopeKinds.Recovery, recoveryKey, master);
        var recovered = VaultCryptography.UnwrapWithSecret(recoveryKey, envelope);

        Assert.Equal(master, recovered);
        Assert.Equal(VaultEnvelopeKinds.Recovery, envelope.Kind);
    }

    [Fact]
    public void A_wrong_secret_cannot_unwrap_the_envelope()
    {
        var envelope = VaultCryptography.WrapWithSecret(
            VaultEnvelopeKinds.Password, "the-real-passw0rd", VaultCryptography.NewMasterKey());

        Assert.ThrowsAny<CryptographicException>(
            () => VaultCryptography.UnwrapWithSecret("a-different-passw0rd", envelope));
    }

    [Fact]
    public void Each_wrap_uses_a_fresh_salt_and_nonce()
    {
        var master = VaultCryptography.NewMasterKey();

        var a = VaultCryptography.WrapWithSecret(VaultEnvelopeKinds.Password, "pw", master);
        var b = VaultCryptography.WrapWithSecret(VaultEnvelopeKinds.Password, "pw", master);

        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.WrappedKey, b.WrappedKey);
    }

    // ── 载荷 ────────────────────────────────────────────────────

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
