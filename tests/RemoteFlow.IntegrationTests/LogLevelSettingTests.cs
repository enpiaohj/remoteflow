using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Logging;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>日志级别设置：默认信息，可切到调试并立即生效（不用重启）。</summary>
[Collection("GlobalLogging")]
public sealed class LogLevelSettingTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "rf-loglevel-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响结论。
        }
    }

    [Fact]
    public void 默认日志级别是信息()
    {
        Assert.Equal(AppLogLevel.Information, new AppSettings().LogLevel);
    }

    [Fact]
    public void 日志级别运行时切换_调试内容只在调试级别写入()
    {
        var template = Path.Combine(_dir, "t-.log");
        using (var factory = LoggingSetup.Create(template))
        {
            var logger = factory.CreateLogger("LogLevelTest");

            logger.LogDebug("调试-默认级别不应出现");
            logger.LogInformation("信息-始终出现");

            LoggingSetup.SetDebug(true);
            logger.LogDebug("调试-切换后应出现");

            LoggingSetup.SetDebug(false);
            logger.LogDebug("调试-切回信息后不应出现");
        }

        LoggingSetup.Shutdown();

        var text = string.Concat(Directory.GetFiles(_dir, "t-*.log").Select(f =>
        {
            using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));

        Assert.Contains("信息-始终出现", text);
        Assert.Contains("调试-切换后应出现", text);
        Assert.DoesNotContain("调试-默认级别不应出现", text);
        Assert.DoesNotContain("调试-切回信息后不应出现", text);
    }
}
