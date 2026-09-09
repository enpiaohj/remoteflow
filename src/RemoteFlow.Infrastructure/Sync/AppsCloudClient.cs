using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// AppsCloud REST 客户端。<see cref="HttpClient.BaseAddress"/> 由组合根设置为
/// <c>https://host/appscloud/</c>（含 PathBase，末尾斜杠）。
/// </summary>
public sealed class AppsCloudClient(
    HttpClient http,
    CloudEndpoint endpoint,
    ICloudTokenStore tokenStore,
    ILogger<AppsCloudClient> logger) : ICloudClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _authGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry = DateTimeOffset.MinValue;

    public async Task<bool> HasSessionAsync(CancellationToken ct = default) =>
        await tokenStore.GetAsync(ct) is not null;

    public async Task<Guid> GetUserIdAsync(CancellationToken ct = default) =>
        (await tokenStore.GetAsync(ct) ?? throw new CloudAuthRequiredException("Not signed in.")).UserId;

    public async Task<Guid> GetCurrentDeviceIdAsync(CancellationToken ct = default)
    {
        var session = await tokenStore.GetAsync(ct) ?? throw new CloudAuthRequiredException("Not signed in.");
        using var response = await SendAsync(HttpMethod.Get, "api/v1/devices", null, authenticated: true, ct);
        response.EnsureSuccessStatusCode();
        var devices = await ReadAsync<List<DeviceListItemDto>>(response, ct);
        var match = devices.Find(d => d.ClientDeviceId == session.ClientDeviceId)
            ?? throw new CloudApiException(404, "Current device is not registered.");
        return match.Id;
    }

    // ── 账号 ────────────────────────────────────────────────────

    public async Task<CloudRegisterOutcome> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post, "api/v1/auth/register", new { email, password }, authenticated: false, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.Created => CloudRegisterOutcome.Created,
            HttpStatusCode.Conflict => CloudRegisterOutcome.AlreadyExists,
            _ => throw await ApiError(response),
        };
    }

    public async Task LoginAsync(
        string email, string password, CloudDeviceInfo device, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/auth/login", new
        {
            email,
            password,
            appId = device.AppId,
            clientDeviceId = device.ClientDeviceId,
            deviceName = device.DeviceName,
            platform = device.Platform,
            publicKey = device.PublicKey,
        }, authenticated: false, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiError(response);
        }

        var tokens = await ReadAsync<TokenDto>(response, ct);
        var userId = JwtReader.ReadSubject(tokens.AccessToken);
        await tokenStore.SetAsync(
            new CloudSession(tokens.RefreshToken, userId, device.AppId, device.ClientDeviceId), ct);
        SetAccessToken(tokens);
        logger.LogInformation("已登录 AppsCloud，UserId {UserId}", userId);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var session = await tokenStore.GetAsync(ct);
        if (session is not null)
        {
            try
            {
                using var _ = await SendAsync(
                    HttpMethod.Post, "api/v1/auth/logout",
                    new { refreshToken = session.RefreshToken }, authenticated: false, ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "云端登出请求失败，仅清除本地会话");
            }
        }

        await tokenStore.ClearAsync(ct);
        _accessToken = null;
        _accessTokenExpiry = DateTimeOffset.MinValue;
    }

    // ── Vault ───────────────────────────────────────────────────

    public async Task<CloudVaultStatus> GetVaultStatusAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/vault/status", null, authenticated: true, ct);
        response.EnsureSuccessStatusCode();
        var dto = await ReadAsync<VaultStatusDto>(response, ct);
        return new CloudVaultStatus(dto.Exists, dto.CurrentKeyVersion, dto.ThisDeviceAuthorized, dto.HasRecoveryEnvelope);
    }

    public async Task<bool> BootstrapVaultAsync(
        DeviceKeyEnvelope deviceEnvelope, RecoveryKeyEnvelope recoveryEnvelope, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/vault/bootstrap", new
        {
            deviceEnvelope = ToWire(deviceEnvelope),
            recoveryEnvelope = ToWire(recoveryEnvelope),
        }, authenticated: true, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.Created => true,
            HttpStatusCode.Conflict => false,
            _ => throw await ApiError(response),
        };
    }

    public async Task<DeviceKeyEnvelope?> GetDeviceEnvelopeAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get, "api/v1/vault/device-envelope", null, authenticated: true, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await ReadAsync<DeviceEnvelopeDto>(response, ct);
        return new DeviceKeyEnvelope(
            dto.Algorithm, B64(dto.WrappedKey), B64(dto.Nonce), B64(dto.EphemeralPublicKey));
    }

    public async Task<IReadOnlyList<CloudPendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get, "api/v1/vault/pending-devices", null, authenticated: true, ct);
        response.EnsureSuccessStatusCode();
        var dtos = await ReadAsync<List<PendingDeviceDto>>(response, ct);
        return dtos.ConvertAll(x => new CloudPendingDevice(
            x.DeviceId, x.ClientDeviceId, x.DisplayName, x.Platform, B64(x.PublicKey), x.LastSeenAt));
    }

    public async Task AddDeviceEnvelopeAsync(
        Guid targetDeviceId, DeviceKeyEnvelope envelope, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/vault/device-envelopes", new
        {
            targetDeviceId,
            envelope = ToWire(envelope),
        }, authenticated: true, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiError(response);
        }
    }

    public async Task<RecoveryKeyEnvelope?> GetRecoveryEnvelopeAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get, "api/v1/vault/recovery-envelope", null, authenticated: true, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await ReadAsync<RecoveryEnvelopeDto>(response, ct);
        return new RecoveryKeyEnvelope(dto.Algorithm, B64(dto.WrappedKey), B64(dto.Nonce), B64(dto.Salt));
    }

    public async Task PutRecoveryEnvelopeAsync(RecoveryKeyEnvelope envelope, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put, "api/v1/vault/recovery-envelope", ToWire(envelope), authenticated: true, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiError(response);
        }
    }

    // ── Sync ────────────────────────────────────────────────────

    public async Task<SyncPushResponse> PushAsync(
        IReadOnlyList<SyncPushOperation> operations, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/sync/push", new
        {
            operations = operations.Select(op => new
            {
                operationId = op.OperationId,
                operationType = op.OperationType.ToString(),
                entityType = op.EntityType,
                entityId = op.EntityId,
                baseVersion = op.BaseVersion,
                keyVersion = op.KeyVersion,
                schemaVersion = op.SchemaVersion,
                ciphertext = op.Ciphertext is null ? null : Convert.ToBase64String(op.Ciphertext),
                nonce = op.Nonce is null ? null : Convert.ToBase64String(op.Nonce),
            }).ToList(),
        }, authenticated: true, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiError(response);
        }

        var dto = await ReadAsync<PushResponseDto>(response, ct);
        return new SyncPushResponse(
            dto.StreamRevision,
            dto.Results.ConvertAll(r => new SyncPushOperationResult(
                r.OperationId,
                Enum.Parse<SyncPushStatus>(r.Status),
                r.EntityType,
                r.EntityId,
                r.Version,
                r.Revision,
                r.Server is null
                    ? null
                    : new SyncServerEntity(
                        r.Server.EntityType, r.Server.EntityId, r.Server.Version, r.Server.Revision,
                        r.Server.KeyVersion, r.Server.SchemaVersion, r.Server.Deleted,
                        NullableB64(r.Server.Ciphertext), NullableB64(r.Server.Nonce)))));
    }

    public async Task<SyncPullPage> PullAsync(long cursor, int limit, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"api/v1/sync/pull?cursor={cursor}&limit={limit}", null, authenticated: true, ct);
        response.EnsureSuccessStatusCode();
        var dto = await ReadAsync<PullPageDto>(response, ct);
        return new SyncPullPage(
            dto.Changes.ConvertAll(c => new SyncPulledChange(
                c.EntityType, c.EntityId, c.Version, c.Revision, c.KeyVersion, c.SchemaVersion,
                c.Deleted, NullableB64(c.Ciphertext), NullableB64(c.Nonce))),
            dto.NextCursor,
            dto.HasMore);
    }

    // ── Token 生命周期 ─────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        var response = await SendOnceAsync(method, path, body, authenticated, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated)
        {
            response.Dispose();
            await RefreshAccessTokenAsync(force: true, ct);
            response = await SendOnceAsync(method, path, body, authenticated: true, ct);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint.RequireBase(), path));
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        if (authenticated)
        {
            var token = await GetAccessTokenAsync(ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http.SendAsync(request, ct);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && _accessTokenExpiry > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return _accessToken;
        }

        return await RefreshAccessTokenAsync(force: false, ct);
    }

    private async Task<string> RefreshAccessTokenAsync(bool force, CancellationToken ct)
    {
        await _authGate.WaitAsync(ct);
        try
        {
            if (!force && _accessToken is not null && _accessTokenExpiry > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                return _accessToken;
            }

            var session = await tokenStore.GetAsync(ct)
                ?? throw new CloudAuthRequiredException("No stored AppsCloud session.");

            using var response = await SendOnceAsync(
                HttpMethod.Post, "api/v1/auth/refresh",
                new { refreshToken = session.RefreshToken }, authenticated: false, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await tokenStore.ClearAsync(ct);
                throw new CloudAuthRequiredException("AppsCloud refresh token was rejected.");
            }

            response.EnsureSuccessStatusCode();
            var tokens = await ReadAsync<TokenDto>(response, ct);
            await tokenStore.SetAsync(session with { RefreshToken = tokens.RefreshToken }, ct);
            SetAccessToken(tokens);
            return _accessToken!;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private void SetAccessToken(TokenDto tokens)
    {
        _accessToken = tokens.AccessToken;
        _accessTokenExpiry = tokens.ExpiresAt;
    }

    // ── 辅助 ────────────────────────────────────────────────────

    private static object ToWire(DeviceKeyEnvelope e) => new
    {
        algorithm = e.Algorithm,
        wrappedKey = Convert.ToBase64String(e.WrappedKey),
        nonce = Convert.ToBase64String(e.Nonce),
        ephemeralPublicKey = Convert.ToBase64String(e.EphemeralPublicKey),
    };

    private static object ToWire(RecoveryKeyEnvelope e) => new
    {
        algorithm = e.Algorithm,
        wrappedKey = Convert.ToBase64String(e.WrappedKey),
        nonce = Convert.ToBase64String(e.Nonce),
        salt = Convert.ToBase64String(e.Salt),
    };

    private static byte[] B64(string value) => Convert.FromBase64String(value);

    private static byte[]? NullableB64(string? value) =>
        string.IsNullOrEmpty(value) ? null : Convert.FromBase64String(value);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, Json)
            ?? throw new CloudApiException((int)response.StatusCode, "Empty response body.");
    }

    private static async Task<CloudApiException> ApiError(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return new CloudApiException(
            (int)response.StatusCode, ReadProblemMessage(body) ?? Truncate(body, 200));
    }

    /// <summary>
    /// 从 RFC 7807 ProblemDetails 里取一句人读的说明：优先第一条字段校验错误，其次 <c>detail</c>，
    /// 再次 <c>title</c>。解析不出则返回 null，由调用方回退到截断的原文。
    /// </summary>
    private static string? ReadProblemMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (doc.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errors.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.Array
                        && field.Value.GetArrayLength() > 0
                        && field.Value[0].GetString() is { Length: > 0 } message)
                    {
                        return message;
                    }
                }
            }

            foreach (var key in (ReadOnlySpan<string>)["detail", "title"])
            {
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON —— 交回调用方按原文处理。
        }

        return null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // ── 线格 DTO ───────────────────────────────────────────────

    private sealed record TokenDto(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
    private sealed record DeviceListItemDto(Guid Id, string ClientDeviceId);
    private sealed record VaultStatusDto(
        bool Exists, int? CurrentKeyVersion, bool ThisDeviceAuthorized, bool HasRecoveryEnvelope);
    private sealed record DeviceEnvelopeDto(
        int KeyVersion, string Algorithm, string WrappedKey, string Nonce, string EphemeralPublicKey);
    private sealed record RecoveryEnvelopeDto(
        int KeyVersion, string Algorithm, string WrappedKey, string Nonce, string Salt);
    private sealed record PendingDeviceDto(
        Guid DeviceId, string ClientDeviceId, string DisplayName, string Platform, string PublicKey,
        DateTimeOffset LastSeenAt);
    private sealed record PushResponseDto(long StreamRevision, List<PushResultDto> Results);
    private sealed record PushResultDto(
        string OperationId, string Status, string EntityType, string EntityId,
        long? Version, long? Revision, ServerEntityDto? Server);
    private sealed record ServerEntityDto(
        string EntityType, string EntityId, long Version, long Revision, int KeyVersion, int SchemaVersion,
        bool Deleted, string? Ciphertext, string? Nonce);
    private sealed record PullPageDto(List<PullChangeDto> Changes, long NextCursor, bool HasMore);
    private sealed record PullChangeDto(
        string EntityType, string EntityId, long Version, long Revision, int KeyVersion, int SchemaVersion,
        bool Deleted, string? Ciphertext, string? Nonce);
}
