using System.IO;
using System.Xml.Linq;
using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>玻璃外观（系统材质 + 半透明覆盖层）的结构回归。</summary>
public sealed class GlassAppearanceTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>纯色调色板必须定义、玻璃覆盖层必须覆盖的「面」语义键。</summary>
    private static readonly string[] SurfaceKeys =
    [
        "Bg.Base", "Bg.Sidebar", "Bg.Chrome", "Bg.Workspace", "Bg.Layer", "Bg.Popup",
        "Bg.Subtle", "Bg.Hover", "Bg.Pressed", "Bg.Selected", "Bg.RowSelected",
        "Stroke.Subtle", "Stroke.Default", "Stroke.Strong",
    ];

    [Theory]
    [InlineData("Theme.Light.xaml")]
    [InlineData("Theme.Dark.xaml")]
    public void 纯色调色板定义全部面语义键且新增键与纸张同色(string file)
    {
        var keys = Keys(file);
        Assert.All(SurfaceKeys, key => Assert.True(keys.ContainsKey(key), $"{file} 缺少 {key}"));

        // 纯色外观下新增的三个键必须与纸张同色，保证未启用玻璃时外观完全不变。
        Assert.Equal(keys["Bg.Layer"], keys["Bg.Chrome"]);
        Assert.Equal(keys["Bg.Layer"], keys["Bg.Workspace"]);
        Assert.Equal(keys["Bg.Layer"], keys["Bg.Popup"]);
    }

    [Theory]
    [InlineData("Glass.Light.xaml")]
    [InlineData("Glass.Dark.xaml")]
    public void 玻璃覆盖层覆盖全部面语义键且弹出层近乎不透明(string file)
    {
        var keys = Keys(file);
        Assert.All(SurfaceKeys, key => Assert.True(keys.ContainsKey(key), $"{file} 缺少 {key}"));

        // 弹出层没有自己的模糊：不透明度不低于 0xF0，避免透出下方文字。
        var popupAlpha = Convert.ToInt32(keys["Bg.Popup"].Substring(1, 2), 16);
        Assert.True(popupAlpha >= 0xF0, $"{file} 的 Bg.Popup 不透明度过低：{keys["Bg.Popup"]}");

        // 窗口底必须半透明（否则看不到系统材质），但不能完全透明（需要亮度薄涂保证文字对比度）。
        var baseAlpha = Convert.ToInt32(keys["Bg.Base"].Substring(1, 2), 16);
        Assert.InRange(baseAlpha, 0x40, 0xE0);
    }

    [Fact]
    public void 所有无边框对话框与浮层都接入玻璃外观()
    {
        var views = FindDirectory("src", "RemoteFlow.App", "Views");
        var windows = Directory.GetFiles(Path.Combine(views, "Dialogs"), "*.xaml.cs")
            .Append(Path.Combine(views, "Sessions", "ConnectionQualityFlyout.xaml.cs"))
            .ToArray();

        Assert.NotEmpty(windows);
        Assert.All(windows, path =>
            Assert.Contains("WindowBackdrop.PrepareDialog(this);", File.ReadAllText(path), StringComparison.Ordinal));
    }

    [Fact]
    public void 主窗口接入玻璃外观且退出无边框全屏后重新套用()
    {
        var code = File.ReadAllText(Path.Combine(FindDirectory("src", "RemoteFlow.App", "Views"), "MainWindow.xaml.cs"));
        Assert.Contains("WindowBackdrop.Attach(this, isMainWindow: true);", code, StringComparison.Ordinal);
        Assert.Contains("WindowBackdrop.Refresh(this, isMainWindow: true);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 窗口材质默认亚克力且设置页读写该项()
    {
        Assert.Equal(WindowMaterial.Acrylic, new AppSettings().WindowMaterial);

        var vm = File.ReadAllText(Path.Combine(
            FindDirectory("src", "RemoteFlow.Presentation", "ViewModels"), "SettingsPageViewModel.cs"));
        Assert.Contains("SelectedWindowMaterial = _settings.WindowMaterial;", vm, StringComparison.Ordinal);
        Assert.Contains("_settings.WindowMaterial = SelectedWindowMaterial;", vm, StringComparison.Ordinal);
        Assert.Contains("partial void OnSelectedWindowMaterialChanged", vm, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Keys(string file)
    {
        var doc = XDocument.Load(Path.Combine(FindDirectory("src", "RemoteFlow.App", "Themes"), file));
        return doc.Root!.Elements()
            .Where(e => e.Attribute(X + "Key") is not null && e.Attribute("Color") is not null)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => NormalizeColor((string)e.Attribute("Color")!));
    }

    /// <summary>#RRGGBB 补成 #FFRRGGBB，Transparent 记为 #00000000，便于比较与读取 Alpha。</summary>
    private static string NormalizeColor(string value) => value switch
    {
        "Transparent" => "#00000000",
        { Length: 7 } => "#FF" + value[1..].ToUpperInvariant(),
        _ => value.ToUpperInvariant(),
    };

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
