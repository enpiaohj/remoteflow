using System.Runtime.Versioning;
using Avalonia;

namespace RemoteFlow.App.Mac;

[SupportedOSPlatform("macos")]
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
