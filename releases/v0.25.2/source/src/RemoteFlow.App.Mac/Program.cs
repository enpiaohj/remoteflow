using System.Runtime.Versioning;
using AppKit;

[assembly: SupportedOSPlatform("macos13.0")]

namespace RemoteFlow.App.Mac;

internal static class Program
{
    private static void Main(string[] args)
    {
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new AppDelegate();
        NSApplication.SharedApplication.Run();
    }
}
