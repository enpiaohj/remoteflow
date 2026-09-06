using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Infrastructure;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口（骨架）。当前展示数据目录与共享栈自检结果；
/// 会话视图（SSH 终端 / VNC / RDP）随 Phase 2 / Phase 3 接入。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
    }

    public MainWindow(
        AppPaths paths,
        ICredentialVault vault,
        (string Keychain, string Terminal, string Ssh, string Vnc) checks)
        : this()
    {
        InitializeComponent();

        Title = $"RemoteFlow — {paths.DataDirectory}";

        var checksPanel = this.FindControl<StackPanel>("ChecksPanel")
            ?? throw new InvalidOperationException("MainWindow.axaml 未包含 ChecksPanel，请检查资源是否嵌入。");

        checksPanel.Children.Add(MakeCheck("数据目录", paths.DataDirectory));
        checksPanel.Children.Add(MakeCheck("凭据保险库", checks.Keychain));
        checksPanel.Children.Add(MakeCheck("终端资产", checks.Terminal));
        checksPanel.Children.Add(MakeCheck("SSH", checks.Ssh));
        checksPanel.Children.Add(MakeCheck("VNC", checks.Vnc));
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static StackPanel MakeCheck(string name, string detail)
    {
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = name,
            Width = 120,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = Avalonia.Media.Brushes.Gray,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        return panel;
    }
}