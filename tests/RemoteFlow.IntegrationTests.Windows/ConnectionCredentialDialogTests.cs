using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteFlow.App.Views.Dialogs;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接前输入账号 / 密码的对话框：三种场景的字段显隐、明暗主题前景、密码框不绑定。</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class ConnectionCredentialDialogTests
{
    private static Window Create(ConnectionCredentialRequirement requirement)
        => (Window)Activator.CreateInstance(
            typeof(ConnectionCredentialDialog),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [new ConnectionCredentialRequest("生产库", "host:22", requirement)],
            null)!;

    private static T Find<T>(Window window, string name) where T : class => (T)window.FindName(name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 没分配凭据_显示可编辑的用户名与密码框_并使用主题前景(bool dark)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                UiTestHost.UsePalette(dark);
                var dialog = Create(new ConnectionCredentialRequirement(
                    ConnectionCredentialPrompt.UsernameAndPassword, CredentialType.SshPassword));

                Assert.Equal("输入你的凭据", Find<TextBlock>(dialog, "TitleText").Text);
                Assert.Equal("这些凭据将用于连接 host:22。", Find<TextBlock>(dialog, "MessageText").Text);
                Assert.False(Find<CheckBox>(dialog, "RememberCheck").IsChecked);
                Assert.Equal(Visibility.Visible, Find<StackPanel>(dialog, "UsernamePanel").Visibility);
                Assert.False(Find<TextBox>(dialog, "UsernameInput").IsReadOnly);
                Assert.Equal(Visibility.Collapsed, Find<TextBlock>(dialog, "AccountText").Visibility);
                Assert.IsType<PasswordBox>(dialog.FindName("PasswordInput"));
                Assert.Equal(
                    ((SolidColorBrush)dialog.FindResource("Text.Primary")).Color,
                    ((SolidColorBrush)dialog.GetValue(System.Windows.Documents.TextElement.ForegroundProperty)).Color);
                dialog.Close();
            }
            finally { UiTestHost.RestoreAppResources(); }
        });
    }

    [Fact]
    public void 凭据只有账户名_用户名只读并带出域()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var dialog = Create(new ConnectionCredentialRequirement(
                    ConnectionCredentialPrompt.PasswordOnly, CredentialType.WindowsDomain, "admin", "CORP"));

                // 账户名固定显示为文字，不提供可编辑的用户名框。
                var account = Find<TextBlock>(dialog, "AccountText");
                Assert.Equal(Visibility.Visible, account.Visibility);
                // 账号只在密码框上方带标签显示一次（之前上下各一遍、都没有标签）。
                Assert.Equal("用户名：CORP\\admin", account.Text);
                Assert.Equal(Visibility.Collapsed, Find<TextBlock>(dialog, "FullAccountText").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<StackPanel>(dialog, "UsernamePanel").Visibility);
                dialog.Close();
            }
            finally { UiTestHost.RestoreAppResources(); }
        });
    }

    [Fact]
    public void VNC口令没有用户名栏()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var dialog = Create(new ConnectionCredentialRequirement(
                    ConnectionCredentialPrompt.PasswordOnly, CredentialType.VncPassword));

                Assert.Equal(Visibility.Collapsed, Find<StackPanel>(dialog, "UsernamePanel").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<TextBlock>(dialog, "AccountText").Visibility);
                dialog.Close();
            }
            finally { UiTestHost.RestoreAppResources(); }
        });
    }
}
