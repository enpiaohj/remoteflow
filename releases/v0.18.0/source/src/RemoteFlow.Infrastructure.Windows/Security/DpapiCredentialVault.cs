using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Security;

/// <summary>
/// 基于 Windows Data Protection API（DPAPI）的凭据保险库。
/// <para>
/// <b>存储模型：</b>
/// <code>
/// SQLite  →  Credential 元数据 + SecretReference（仅引用键）
///                    │
///                    ▼
/// vault.dat  →  DPAPI(CurrentUser) 加密后的 Secret 密文
/// </code>
/// Secret 与业务数据分文件存放，且用当前 Windows 用户账户的主密钥加密。
/// 因此把 <c>remoteflow.db</c> 复制走得不到任何密码；
/// 连 <c>vault.dat</c> 一起复制走，在其他机器或其他 Windows 账户下同样无法解密。
/// </para>
/// <para>
/// <b>本类是全应用唯一接触明文 Secret 的组件</b>，任何方法都不得把 Secret 写入日志。
/// </para>
/// </summary>
public sealed class DpapiCredentialVault : ICredentialVault
{
    /// <summary>
    /// DPAPI 附加熵。与用户主密钥共同参与加解密，
    /// 使密文即便被移动到同一用户的其他应用上下文也无法直接解开。
    /// </summary>
    private static readonly byte[] Entropy = "RemoteFlow.CredentialVault.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _vaultPath;
    private readonly ILogger<DpapiCredentialVault> _logger;

    /// <summary>保护 vault 文件的读改写全过程，避免并发写入互相覆盖。</summary>
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public DpapiCredentialVault(string vaultPath, ILogger<DpapiCredentialVault> logger)
    {
        _vaultPath = vaultPath;
        _logger = logger;

        var directory = Path.GetDirectoryName(vaultPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<string> StoreSecretAsync(string reference, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        await _mutex.WaitAsync(ct);
        try
        {
            var store = await LoadStoreAsync(ct);
            store[reference] = Protect(secret);
            await SaveStoreAsync(store, ct);

            // 只记录引用键，绝不记录 Secret 本身。
            _logger.LogInformation("已保存凭据 Secret，引用键 {Reference}", reference);
            return reference;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<string?> RetrieveSecretAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            var store = await LoadStoreAsync(ct);
            if (!store.TryGetValue(reference, out var cipherText))
            {
                return null;
            }

            return Unprotect(cipherText, reference);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteSecretAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            var store = await LoadStoreAsync(ct);
            if (store.Remove(reference))
            {
                await SaveStoreAsync(store, ct);
                _logger.LogInformation("已删除凭据 Secret，引用键 {Reference}", reference);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ResolvedCredential> ResolveAsync(Credential credential, CancellationToken ct = default)
    {
        string? password = null;
        string? privateKey = null;

        if (!string.IsNullOrEmpty(credential.SecretReference))
        {
            password = await RetrieveSecretAsync(credential.SecretReference, ct);
        }

        if (!string.IsNullOrEmpty(credential.KeyReference))
        {
            privateKey = await RetrieveSecretAsync(credential.KeyReference, ct);
        }

        return new ResolvedCredential
        {
            Type = credential.Type,
            Username = credential.Username,
            Domain = credential.Domain,
            Password = password,
            PrivateKey = privateKey
        };
    }

    /// <summary>生成一个新的引用键。使用 GUID，避免从键名反推凭据用途。</summary>
    public static string CreateReference(string purpose) => $"{purpose}:{Guid.NewGuid():N}";

    // ── DPAPI ─────────────────────────────────────────────────────

    private static string Protect(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        try
        {
            var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipherBytes);
        }
        finally
        {
            // 明文字节数组可控，使用后立即清零，缩短其在内存中的存活窗口。
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private string? Unprotect(string cipherText, string reference)
    {
        byte[]? plainBytes = null;
        try
        {
            var cipherBytes = Convert.FromBase64String(cipherText);
            plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            // 常见原因：vault.dat 来自其他 Windows 用户或其他机器，无法用当前主密钥解密。
            // 只记录引用键与失败事实，不记录任何密文或明文内容。
            _logger.LogError(ex, "无法解密引用键 {Reference} 对应的 Secret，可能来自其他 Windows 账户或机器", reference);
            return null;
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "引用键 {Reference} 对应的 Secret 密文格式损坏", reference);
            return null;
        }
        finally
        {
            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    // ── 文件存取 ──────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> LoadStoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_vaultPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_vaultPath);
            var store = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, ct);
            return store ?? [];
        }
        catch (JsonException ex)
        {
            // 不静默重建：直接失败并让上层提示用户，避免无声丢失全部凭据。
            _logger.LogError(ex, "凭据保险库文件已损坏：{Path}", _vaultPath);
            throw new InvalidOperationException(
                $"凭据保险库文件已损坏：{_vaultPath}。请从备份恢复，或删除该文件后重新录入凭据。", ex);
        }
    }

    /// <summary>
    /// 原子写入：先写临时文件并落盘，再整体替换，
    /// 保证进程在写入过程中被杀死时不会留下半个 vault 文件。
    /// </summary>
    private async Task SaveStoreAsync(Dictionary<string, string> store, CancellationToken ct)
    {
        var tempPath = _vaultPath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, store, JsonOptions, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_vaultPath))
        {
            File.Replace(tempPath, _vaultPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _vaultPath);
        }
    }
}
