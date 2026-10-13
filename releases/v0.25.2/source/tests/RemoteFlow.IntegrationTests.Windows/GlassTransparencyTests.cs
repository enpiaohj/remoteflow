using System.IO;
using System.Xml.Linq;
using RemoteFlow.App.Services;
using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>玻璃透明度滑块：三层半透明面的 Alpha 插值规则。</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class GlassTransparencyTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(false, "Glass.Light.xaml")]
    [InlineData(true, "Glass.Dark.xaml")]
    public void 默认透明度50与覆盖层文件中的设计值一致(bool dark, string file)
    {
        var doc = XDocument.Load(Path.Combine(FindDirectory("src", "RemoteFlow.App", "Themes"), file));
        foreach (var key in GlassTransparency.ScaledKeys)
        {
            var color = (string)doc.Root!.Elements().Single(e => (string?)e.Attribute(X + "Key") == key).Attribute("Color")!;
            var designed = Convert.ToByte(color.Substring(1, 2), 16);

            Assert.Equal(designed, GlassTransparency.Alpha(key, dark, GlassTransparency.Default));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 透明度越高各层Alpha越低且两端不越界(bool dark)
    {
        foreach (var key in GlassTransparency.ScaledKeys)
        {
            var opaque = GlassTransparency.Alpha(key, dark, 0);
            var middle = GlassTransparency.Alpha(key, dark, 50);
            var clear = GlassTransparency.Alpha(key, dark, 100);

            Assert.True(opaque > middle && middle > clear, $"{key} 不是单调递减：{opaque} / {middle} / {clear}");
            Assert.Equal(opaque, GlassTransparency.Alpha(key, dark, -20));
            Assert.Equal(clear, GlassTransparency.Alpha(key, dark, 150));
            // 最透明也保留一层薄涂：窗口底与纸张完全透明会让文字直接压在任意后方内容上。
            Assert.True(clear >= 0x1A, $"{key} 最透明时 Alpha 过低：{clear}");
        }
    }

    [Fact]
    public void 透明度默认50且设置页读写该项()
    {
        Assert.Equal(GlassTransparency.Default, new AppSettings().WindowTransparency);

        var vm = File.ReadAllText(Path.Combine(
            FindDirectory("src", "RemoteFlow.Presentation", "ViewModels"), "SettingsPageViewModel.cs"));
        Assert.Contains("WindowTransparency = _settings.WindowTransparency;", vm, StringComparison.Ordinal);
        Assert.Contains("_settings.WindowTransparency = WindowTransparency;", vm, StringComparison.Ordinal);
        Assert.Contains("partial void OnWindowTransparencyChanged", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void 运行中调整透明度即时替换全局面画刷()
    {
        if (!WindowBackdrop.IsSupported)
        {
            return; // 低于 Windows 11 22H2：玻璃外观恒为纯色，透明度不生效，无需验证。
        }

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // 真实加载应用的主题文件（ThemeService 用组件 pack URI 指向 App 程序集）。
                if (System.Windows.Application.Current is null)
                {
                    _ = new System.Windows.Application();
                }

                var theme = new ThemeService();
                theme.SetTransparency(GlassTransparency.Default);
                theme.SetMaterial(WindowMaterial.Acrylic);
                theme.Apply(AppTheme.Light);

                byte AlphaOf(string key) =>
                    ((System.Windows.Media.SolidColorBrush)System.Windows.Application.Current!.Resources[key]).Color.A;

                Assert.Equal(GlassTransparency.Alpha("Bg.Layer", false, 50), AlphaOf("Bg.Layer"));

                theme.SetTransparency(0);
                Assert.Equal(GlassTransparency.Alpha("Bg.Base", false, 0), AlphaOf("Bg.Base"));
                Assert.Equal(GlassTransparency.Alpha("Bg.Layer", false, 0), AlphaOf("Bg.Layer"));

                theme.SetTransparency(100);
                Assert.Equal(GlassTransparency.Alpha("Bg.Workspace", false, 100), AlphaOf("Bg.Workspace"));

                // 切回纯色：覆盖层移除，面画刷回到纯色调色板（不透明）。
                theme.SetMaterial(WindowMaterial.Solid);
                Assert.Equal(0xFF, AlphaOf("Bg.Layer"));
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private static string FindDirectory(params string[] segments)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"未找到 {Path.Combine(segments)}。");
    }
}
