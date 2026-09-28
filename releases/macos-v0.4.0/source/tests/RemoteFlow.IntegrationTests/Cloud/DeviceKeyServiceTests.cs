using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class DeviceKeyServiceTests
{
    [Fact]
    public async Task EnsureDeviceKey_creates_the_key_once_and_returns_a_stable_public_key()
    {
        var store = new InMemoryVaultKeyStore();
        var service = new DeviceKeyService(store);

        var first = await service.EnsureDeviceKeyAsync();
        var second = await service.EnsureDeviceKeyAsync();

        Assert.Equal(first, second);
        Assert.Equal(1, store.SetPrivateKeyCalls);
    }

    [Fact]
    public async Task Unwrap_uses_the_stored_device_key()
    {
        var store = new InMemoryVaultKeyStore();
        var service = new DeviceKeyService(store);
        var publicKey = await service.EnsureDeviceKeyAsync();
        var master = VaultCryptography.NewMasterKey();

        var envelope = service.WrapMasterKeyFor(publicKey, master);
        var recovered = await service.UnwrapMasterKeyAsync(envelope);

        Assert.Equal(master, recovered);
    }

    [Fact]
    public async Task Unwrap_without_a_stored_key_throws()
    {
        var service = new DeviceKeyService(new InMemoryVaultKeyStore());
        var envelope = VaultCryptography.WrapForDevice(
            VaultCryptography.NewDeviceKeyPair().PublicKey, VaultCryptography.NewMasterKey());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UnwrapMasterKeyAsync(envelope));
    }
}
