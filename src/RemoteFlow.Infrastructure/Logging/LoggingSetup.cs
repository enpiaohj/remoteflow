using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RemoteFlow.Infrastructure.Logging;

/// <summary>
/// 结构化日志初始化。
/// <para>
/// <b>脱敏原则（产品设计文档 §13.4）：</b><br/>
/// 允许记录：主机名/IP、协议、连接阶段、标准化错误码、耗时。<br/>
/// 禁止记录：Password、Private Key 正文、API Token、完整认证报文、剪贴板内容。
/// </para>
/// <para>
/// 第一道防线是调用方不传 Secret；<see cref="SecretRedactingEnricher"/> 作为
/// 第二道防线，对疑似 Secret 的日志内容做兜底遮蔽。
/// </para>
/// </summary>
public static class LoggingSetup
{
    /// <summary>运行时可调的最低日志级别：设置里改日志级别后立即生效，无需重启。</summary>
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    /// <summary>切换调试 / 信息级别（立即生效）。</summary>
    public static void SetDebug(bool debug)
        => LevelSwitch.MinimumLevel = debug ? LogEventLevel.Debug : LogEventLevel.Information;

    public static ILoggerFactory Create(string logFileTemplate, bool verbose = false)
    {
        SetDebug(verbose);
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.With(new SecretRedactingEnricher())
            .WriteTo.File(
                path: logFileTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Logger = serilogLogger;

        return LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(serilogLogger, dispose: true);
            // 过滤交给 Serilog 的 LevelSwitch：这里放开到 Debug，运行时切级别才不会被上游挡住。
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    public static void Shutdown() => Log.CloseAndFlush();
}

/// <summary>
/// 日志脱敏兜底。扫描日志属性，对名称疑似敏感的属性直接替换为遮蔽标记。
/// <para>
/// 这不是纵容上层传 Secret，而是防止某次改动无意间把凭据带进日志导致长期泄露。
/// </para>
/// </summary>
public sealed class SecretRedactingEnricher : ILogEventEnricher
{
    private const string RedactedMarker = "***REDACTED***";

    /// <summary>属性名包含以下任一片段即视为敏感（不区分大小写）。</summary>
    private static readonly string[] SensitiveNameFragments =
    [
        "password", "passwd", "pwd", "secret", "token", "privatekey",
        "private_key", "passphrase", "credential", "apikey", "api_key", "clipboard"
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var property in logEvent.Properties.ToArray())
        {
            if (IsSensitiveName(property.Key))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(RedactedMarker)));
            }
        }
    }

    private static bool IsSensitiveName(string name)
    {
        foreach (var fragment in SensitiveNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
