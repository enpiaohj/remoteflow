using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RemoteFlow.Infrastructure.Settings;

/// <summary>
/// 会话退出状态快照（session-state.json 的内容）。
/// </summary>
/// <param name="CleanExit">上次退出是否为干净退出（正常 OnExit 收尾完成）。</param>
/// <param name="LastAt">状态写入时间（启动时间或退出时间）。</param>
public sealed record SessionState(bool CleanExit, DateTimeOffset? LastAt);

/// <summary>
/// 会话退出标记文件（session-state.json）的读写，用于崩溃恢复检测。
/// <para>
/// 约定：应用每次启动先 <see cref="Save"/>（cleanExit:false）武装「运行中 / 未干净退出」标记；
/// 应用 OnExit 正常收尾后再写 cleanExit:true。若进程崩溃或被强杀，
/// OnExit 不会执行，标记保持 false，下次启动据此写 recovery 日志（不恢复任何会话，本产品会话
/// 本就不跨重启持久化）。写入采用「临时文件 + 原子替换」（同 <see cref="JsonSettingsStore"/>），
/// 失败尽力而为，不阻断启动 / 退出流程。
/// </para>
/// </summary>
public sealed class SessionStateStore(string dataDirectory, ILogger<SessionStateStore>? logger = null)
{
    /// <summary>标记文件名，固定位于数据目录根下。</summary>
    public const string FileName = "session-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 落盘小驼峰，接近设计文档示例（{ cleanExit, lastAt }）；读取大小写不敏感，兼容历史写法。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path = Path.Combine(dataDirectory, FileName);

    /// <summary>
    /// 读取上次退出状态。文件不存在 / 损坏 / 读取失败时返回 null（视为无上次状态，不阻断启动）。
    /// </summary>
    public SessionState? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(ex, "读取会话状态失败，按无上次状态处理：{Path}", _path);
            return null;
        }
    }

    /// <summary>
    /// 写入退出状态（尽力而为）。临时文件 + 原子替换；磁盘 / 权限类失败只记录不抛出，
    /// 确保启动 / 退出流程不被标记文件打断。
    /// </summary>
    public void Save(bool cleanExit, DateTimeOffset? timestamp = null)
    {
        try
        {
            var state = new SessionState(cleanExit, timestamp ?? DateTimeOffset.Now);
            var tempPath = _path + ".tmp";

            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(tempPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        catch (Exception ex)
        {
            // 标记仅为诊断用途，本方法契约是「尽力而为、永不抛出」：
            // 任何 IO / 权限 / 序列化等失败都只记录，不阻断启动（OnStartup 调用点
            // 不设保护，若抛出会被误判为启动失败）与退出流程。
            logger?.LogWarning(ex, "写入会话状态失败（尽力而为）：{Path}", _path);
        }
    }
}
