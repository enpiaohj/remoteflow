using System.Windows.Controls;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 「凭据」页面。只管理凭据元数据，界面从不显示任何 Secret 明文。
/// </summary>
public partial class CredentialsPage : UserControl
{
    public CredentialsPage() => InitializeComponent();
}
