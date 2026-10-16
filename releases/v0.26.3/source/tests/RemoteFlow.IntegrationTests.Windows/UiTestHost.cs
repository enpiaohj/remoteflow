using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// WPF 测试宿主：按 App.xaml 的顺序合并主题字典并登记全局转换器，
/// 让视图能在测试进程里以与应用一致的资源环境被构造与测量。
/// <para>
/// <see cref="Application.Current"/> 是进程级单例，因此使用本类的用例必须归入
/// <see cref="WpfApplicationCollection"/>，串行执行。
/// </para>
/// </summary>
internal static class UiTestHost
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>把应用主题字典装进当前 <see cref="System.Windows.Application"/>（不存在则创建）。</summary>
    internal static void LoadAppResources()
    {
        if (System.Windows.Application.Current is null)
        {
            _ = new System.Windows.Application();
        }

        var resources = System.Windows.Application.Current!.Resources;
        var originalDictionaries = resources.MergedDictionaries.ToList();
        var originalKeys = resources.Keys.Cast<object>().ToHashSet();

        LoadAppResources(resources);

        // 还原全局资源：Application 是进程级单例，其它用例（玻璃外观）依赖其原始状态。
        _restore = () =>
        {
            resources.MergedDictionaries.Clear();
            originalDictionaries.ForEach(resources.MergedDictionaries.Add);
            foreach (var key in resources.Keys.Cast<object>().Where(k => !originalKeys.Contains(k)).ToList())
            {
                resources.Remove(key);
            }
        };
    }

    private static Action? _restore;

    /// <summary>恢复 <see cref="LoadAppResources"/> 之前的全局资源状态。</summary>
    internal static void RestoreAppResources()
    {
        _restore?.Invoke();
        _restore = null;
    }

    /// <summary>
    /// 把当前调色板切到深色 / 浅色：就地替换合并字典里的 Theme.Light/Dark.xaml。
    /// 元素用 DynamicResource 引用语义键，因此替换后新建的控件直接取到新主题的值。
    /// </summary>
    internal static void UsePalette(bool dark)
    {
        var resources = System.Windows.Application.Current!.Resources;
        var target = dark ? "Theme.Dark.xaml" : "Theme.Light.xaml";
        var assemblyName = typeof(RemoteFlow.App.Converters.UiIconResources).Assembly.GetName().Name;

        // 不论当前装的是浅色还是深色调色板，都就地换成目标那一份。
        var index = resources.MergedDictionaries
            .Select((dictionary, i) => (dictionary, i))
            .Where(x => IsPalette(x.dictionary.Source?.OriginalString))
            .Select(x => x.i)
            .DefaultIfEmpty(-1)
            .First();

        if (index < 0)
        {
            throw new InvalidOperationException("未找到调色板字典（Theme.Light.xaml / Theme.Dark.xaml），无法切换主题。");
        }

        resources.MergedDictionaries[index] = new ResourceDictionary
        {
            Source = new Uri($"/{assemblyName};component/Themes/{target}", UriKind.Relative),
        };

        static bool IsPalette(string? source) =>
            source is not null
            && (source.EndsWith("Theme.Light.xaml", StringComparison.Ordinal)
                || source.EndsWith("Theme.Dark.xaml", StringComparison.Ordinal));
    }

    /// <summary>在 STA 线程上执行；异常原样抛回调用线程。</summary>
    internal static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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

    private static void LoadAppResources(ResourceDictionary target)
    {
        var app = XDocument.Load(AppFile("App.xaml"));
        var assemblyName = typeof(RemoteFlow.App.Converters.UiIconResources).Assembly.GetName().Name;
        var resources = app.Root!.Element(Presentation + "Application.Resources")!
            .Element(Presentation + "ResourceDictionary")!;

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
        foreach (var converter in resources.Elements()
                     .Where(e => e.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal)))
        {
            var ns = converter.Name.NamespaceName["clr-namespace:".Length..].Split(';')[0];
            var type = converterAssembly.GetType($"{ns}.{converter.Name.LocalName}", throwOnError: true)!;
            target[(string)converter.Attribute(X + "Key")!] = Activator.CreateInstance(type)!;
        }
    }

    private static string AppFile(string name) => Path.Combine(AppDirectory(), name);

    private static string AppDirectory(params string[] segments)
        => Path.Combine([SrcDirectory("RemoteFlow.App"), .. segments]);

    private static string SrcDirectory(string project) => Path.Combine(RepoRoot(), "src", project);

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
