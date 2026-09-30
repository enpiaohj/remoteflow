using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 图标体系：线性操作图标（Ui.*，Fluent System Icons）+ 彩色身份图标（Id.*）。
/// 覆盖资源完整性、页面不再使用字体字形、许可证披露，以及真实加载全部视图不缺资源。
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed partial class UiIconSystemTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>保留系统字形的图标：窗口标题栏按钮与折叠箭头（系统原生观感，且不承载语义）。</summary>
    private static readonly HashSet<string> KeptGlyphKeys =
    [
        "Icon.WindowMinimize", "Icon.WindowMaximize", "Icon.WindowRestore",
        "Icon.ChevronRight", "Icon.ChevronLeft", "Icon.ChevronDown",
    ];

    [Fact]
    public void 线性图标均为锚定20画布的非空几何()
    {
        var keys = KeysOf("UiIcons.xaml");
        Assert.True(keys.Count >= 60, $"Ui.* 图标数量异常：{keys.Count}");

        var doc = XDocument.Load(ThemeFile("UiIcons.xaml"));
        foreach (var element in doc.Root!.Elements())
        {
            var data = (string?)element.Attribute("Figures") ?? element.Value;
            var geometry = Geometry.Parse(data);
            var key = (string)element.Attribute(X + "Key")!;
            Assert.False(geometry.IsEmpty(), $"{key} 为空几何");
            // 画布锚点保证 Stretch=Uniform 时各图标留白一致。
            Assert.Equal(new Rect(0, 0, 20, 20), geometry.Bounds);
        }
    }

    [Fact]
    public void 代码与标记引用的图标键全部存在()
    {
        var defined = KeysOf("UiIcons.xaml")
            .Concat(KeysOf("IdentityIcons.xaml"))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in IconKeyReference().Matches(text))
            {
                var key = m.Groups["key"].Value;
                // UiIconGeometry 转换器的变体后缀（ConverterParameter=.Filled）不是独立键。
                if (!defined.Contains(key))
                {
                    missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void 导航填充变体齐全()
    {
        var keys = KeysOf("UiIcons.xaml").ToHashSet(StringComparer.Ordinal);
        foreach (var nav in new[] { "Home", "Connections", "Credential", "Settings", "Help", "Info" })
        {
            Assert.Contains($"Ui.{nav}", keys);
            Assert.Contains($"Ui.{nav}.Filled", keys);
        }
    }

    [Fact]
    public void 视图不再使用语义字体字形()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppDirectory("Views"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in LegacyGlyphReference().Matches(text))
            {
                if (!KeptGlyphKeys.Contains(m.Groups["key"].Value))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Groups["key"].Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void 第三方清单披露FluentSystemIcons及许可证全文()
    {
        var notices = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));
        Assert.Contains("Fluent UI System Icons", notices, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2020 Microsoft Corporation", notices, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", notices, StringComparison.Ordinal);

        var header = File.ReadAllText(ThemeFile("UiIcons.xaml"));
        Assert.Contains("MIT", header, StringComparison.Ordinal);
    }

    [Fact]
    public void 真实加载应用资源后全部视图可实例化()
    {
        Exception? error = null;
        var failures = new List<string>();
        var loaded = 0;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    _ = new System.Windows.Application();
                }

                var resources = System.Windows.Application.Current!.Resources;
                var originalDictionaries = resources.MergedDictionaries.ToList();
                var originalKeys = resources.Keys.Cast<object>().ToHashSet();
                try
                {
                    LoadAppResources(resources);
                    loaded = InstantiateViews(failures);
                }
                finally
                {
                    // 还原全局资源：Application 是进程级单例，其它用例（玻璃外观）依赖其原始状态。
                    resources.MergedDictionaries.Clear();
                    originalDictionaries.ForEach(resources.MergedDictionaries.Add);
                    foreach (var key in resources.Keys.Cast<object>().Where(k => !originalKeys.Contains(k)).ToList())
                    {
                        resources.Remove(key);
                    }
                }
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

        Assert.Empty(failures);
        Assert.True(loaded >= 10, $"实际加载的视图过少：{loaded}");
    }

    // ── 辅助 ──

    /// <summary>
    /// 实例化 RemoteFlow.App.Views 下全部视图：取参数最少的构造函数、参数给默认值。
    /// 视图构造先 InitializeComponent，标记里的资源缺失在此即抛 XamlParseException（记为失败）；
    /// 之后因空参数 / 缺运行时服务（DI、主窗口等）引发的异常不属于本用例范围，跳过。
    /// </summary>
    private static int InstantiateViews(List<string> failures)
    {
        var loaded = 0;
        var views = typeof(RemoteFlow.App.Converters.UiIconResources).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("RemoteFlow.App.Views", StringComparison.Ordinal) == true
                        && !t.IsAbstract
                        && typeof(FrameworkElement).IsAssignableFrom(t)
                        && t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0
                        && t.GetMethod("InitializeComponent") is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in views)
        {
            try
            {
                var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .OrderBy(c => c.GetParameters().Length)
                    .First();
                var args = ctor.GetParameters()
                    .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                    .ToArray();
                var view = (FrameworkElement)ctor.Invoke(args);
                loaded++;
                if (view is Window window)
                {
                    window.Close();
                }
            }
            catch (Exception ex) when (IsMissingResource(ex))
            {
                failures.Add($"{type.Name}: {Innermost(ex).Message}");
            }
            catch (Exception)
            {
                // 见方法说明：非资源类异常跳过。
            }
        }

        return loaded;
    }

    /// <summary>按 App.xaml 的顺序合并主题字典并登记全局转换器，与应用启动时的资源环境一致。</summary>
    private static void LoadAppResources(ResourceDictionary target)
    {
        var app = XDocument.Load(AppFile("App.xaml"));
        var assemblyName = typeof(RemoteFlow.App.Converters.UiIconResources).Assembly.GetName().Name;
        var resources = app.Root!.Element(Presentation + "Application.Resources")!.Element(Presentation + "ResourceDictionary")!;

        target.MergedDictionaries.Clear();
        foreach (var dictionary in resources.Element(Presentation + "ResourceDictionary.MergedDictionaries")!.Elements())
        {
            var source = (string)dictionary.Attribute("Source")!;
            target.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/{assemblyName};component/{source}", UriKind.Relative)
            });
        }

        var converterAssembly = typeof(RemoteFlow.App.Converters.UiIconResources).Assembly;
        foreach (var converter in resources.Elements().Where(e => e.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal)))
        {
            var ns = converter.Name.NamespaceName["clr-namespace:".Length..].Split(';')[0];
            var type = converterAssembly.GetType($"{ns}.{converter.Name.LocalName}", throwOnError: true)!;
            target[(string)converter.Attribute(X + "Key")!] = Activator.CreateInstance(type)!;
        }
    }

    private static bool IsMissingResource(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Windows.Markup.XamlParseException xpe
                && (xpe.Message.Contains("StaticResource", StringComparison.Ordinal)
                    || xpe.Message.Contains("资源", StringComparison.Ordinal)
                    || xpe.InnerException is Exception { Message: var m } && m.Contains("resource", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    private static List<string> KeysOf(string themeFile)
        => XDocument.Load(ThemeFile(themeFile)).Root!.Elements()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .Where(k => k is not null && (k.StartsWith("Ui.", StringComparison.Ordinal) || k.StartsWith("Id.", StringComparison.Ordinal)))
            .Select(k => k!)
            .ToList();

    private static IEnumerable<string> SourceFiles()
        => new[] { AppDirectory(), SrcDirectory("RemoteFlow.Presentation") }
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.EndsWith("UiIcons.xaml", StringComparison.OrdinalIgnoreCase)
                        && !f.EndsWith("IdentityIcons.xaml", StringComparison.OrdinalIgnoreCase));

    /// <summary>StaticResource 引用或字符串字面量里的完整键（Ui.X / Ui.X.Filled / Id.X）。</summary>
    [GeneratedRegex(@"(?:StaticResource |"")(?<key>(?:Ui|Id)\.[A-Za-z]+(?:\.Filled)?)(?=[}""])")]
    private static partial Regex IconKeyReference();

    [GeneratedRegex(@"StaticResource (?<key>Icon\.\w+)\}")]
    private static partial Regex LegacyGlyphReference();

    private static string ThemeFile(string name) => Path.Combine(AppDirectory("Themes"), name);

    private static string AppFile(string name) => Path.Combine(AppDirectory(), name);

    private static string AppDirectory(params string[] segments) => Path.Combine([SrcDirectory("RemoteFlow.App"), .. segments]);

    private static string SrcDirectory(string project) => Path.Combine(RepoRoot(), "src", project);

    private static string RepoFile(string name) => Path.Combine(RepoRoot(), name);

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RemoteFlow.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("未找到仓库根目录（RemoteFlow.slnx）。");
    }
}

/// <summary>操作进程级 WPF Application 单例（全局资源）的用例串行执行，互不干扰。</summary>
[CollectionDefinition(Name)]
public sealed class WpfApplicationCollection
{
    public const string Name = "WpfApplication";
}
