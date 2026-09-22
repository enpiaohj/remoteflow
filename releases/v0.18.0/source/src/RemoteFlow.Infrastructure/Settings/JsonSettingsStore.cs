using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Settings;

/// <summary>
/// 应用设置的 JSON 持久化。
/// 写入采用「临时文件 + 原子替换」，避免进程异常退出导致配置文件损坏。
/// </summary>
public sealed class JsonSettingsStore(string settingsPath, ILogger<JsonSettingsStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 枚举以字符串形式落盘，配置文件可读，且后续调整枚举数值不会破坏已有配置。
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _mutex = new(1, 1);

    public AppSettings Load()
    {
        if (!File.Exists(settingsPath))
        {
            logger.LogInformation("未找到设置文件，使用默认设置：{Path}", settingsPath);
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 设置损坏不应阻止应用启动：回退到默认值并记录，用户仍可正常使用与重新配置。
            logger.LogError(ex, "设置文件读取失败，已回退到默认设置：{Path}", settingsPath);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var tempPath = settingsPath + ".tmp";

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(settingsPath))
            {
                File.Replace(tempPath, settingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, settingsPath);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }
}
