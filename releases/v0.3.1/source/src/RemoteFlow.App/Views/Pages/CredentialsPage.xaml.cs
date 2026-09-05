using System.Windows;
using System.Windows.Controls;
using RemoteFlow.App.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 「凭据」页面。只管理凭据元数据，界面从不显示任何 Secret 明文。
/// </summary>
public partial class CredentialsPage : UserControl
{
    public CredentialsPage() => InitializeComponent();

    private CredentialsPageViewModel? ViewModel => DataContext as CredentialsPageViewModel;

    private async void OnEditMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.EditCommand.ExecuteAsync(item);
        }
    }

    private async void OnDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.DeleteCommand.ExecuteAsync(item);
        }
    }

    /// <summary>菜单项的 DataContext 继承自弹出菜单的 PlacementTarget，即所在行。</summary>
    private static CredentialItemViewModel? ResolveItem(object sender)
        => (sender as MenuItem)?.DataContext as CredentialItemViewModel;
}
