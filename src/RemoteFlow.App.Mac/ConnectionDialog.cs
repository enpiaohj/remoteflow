using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 新建连接的最小对话框：名称 / 协议 / 主机 / 端口 / 账号 / 口令。
/// 口令经凭据库（Keychain）存取，不落库明文。
/// </summary>
public sealed class ConnectionDialog : Window
{
    private readonly TextBox _nameBox = new() { PlaceholderText = "显示名称", PlaceholderForeground = Brushes.Gray };
    private readonly ComboBox _protoBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _hostBox = new() { PlaceholderText = "主机 / IP" };
    private readonly TextBox _portBox = new() { Text = "22" };
    private readonly TextBox _userBox = new() { PlaceholderText = "账号" };
    private readonly TextBox _passBox = new() { PasswordChar = '●', PlaceholderText = "口令" };

    public ConnectionDialog()
    {
        Title = "新建连接";
        Width = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _protoBox.ItemsSource = new[] { "SSH", "VNC" };
        _protoBox.SelectedIndex = 0;
        _protoBox.SelectionChanged += (_, _) => _portBox.Text = _protoBox.SelectedIndex == 0 ? "22" : "5900";

        var ok = new Button { Content = "创建", HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Finish();
        cancel.Click += (_, _) => Close();
        ok.IsDefault = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, ok },
        };

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        foreach (var (label, control) in new (string, Control)[]
                 {
                     ("名称", _nameBox), ("协议", _protoBox), ("主机", _hostBox),
                     ("端口", _portBox), ("账号", _userBox), ("口令", _passBox),
                 })
        {
            var row = new StackPanel { Spacing = 3 };
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.DimGray });
            row.Children.Add(control);
            panel.Children.Add(row);
        }

        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };

        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    private void Finish()
    {
        var name = _nameBox.Text?.Trim();
        var host = _hostBox.Text?.Trim();
        var user = _userBox.Text?.Trim();
        var pass = _passBox.Text;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(host))
        {
            return;
        }

        if (!int.TryParse(_portBox.Text?.Trim(), out var port) || port <= 0)
        {
            port = _protoBox.SelectedIndex == 0 ? 22 : 5900;
        }

        var proto = _protoBox.SelectedIndex == 0 ? ProtocolType.Ssh : ProtocolType.Vnc;
        Close(new ConnectionDialogResult(name, host, port, proto, user ?? string.Empty, pass ?? string.Empty));
    }
}
