using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 同步实体的稳定 JSON 序列化。Payload 形如 <c>{"v":1,"d":{...}}</c>——<c>v</c> 是 Schema 版本，
/// 便于旧客户端 Pull 到新 Schema 时识别并迁移（协议设计 §13）。
/// </summary>
public static class SyncSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 枚举写成字符串，跨版本比数字更耐改；属性名保持 PascalCase 与领域模型一致。
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static byte[] Serialize<T>(T entity, int schemaVersion)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", schemaVersion);
            writer.WritePropertyName("d");
            JsonSerializer.Serialize(writer, entity, Options);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static (int SchemaVersion, T Data) Deserialize<T>(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var version = root.TryGetProperty("v", out var v) ? v.GetInt32() : 1;
        var data = root.GetProperty("d").Deserialize<T>(Options)
            ?? throw new JsonException($"Sync payload for {typeof(T).Name} deserialized to null.");
        return (version, data);
    }
}
