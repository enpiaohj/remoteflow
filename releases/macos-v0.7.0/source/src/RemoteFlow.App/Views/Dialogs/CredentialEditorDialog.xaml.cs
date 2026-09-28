using System.Windows;
using System.Windows.Input;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 新建 / 编辑凭据对话框。
/// <para>
/// <b>安全设计：</b>密码走 <see cref="System.Windows.Controls.PasswordBox"/> 且不参与数据绑定，
/// 只在用户点击保存时读取一次并立即交给 Vault 加密。
/// 编辑已有凭据时不回填任何明文，密码框留空即表示保持原值不变。
/// </para>
/// </summary>
public partial class CredentialEditorDialog : Window
{
    private readonly CredentialEditorViewModel _viewModel;

    public CredentialEditorDialog(CredentialEditorViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public Credential? Result { get; private set; }

    /// <summary>用户输入的密码。null 表示未修改，空字符串表示清除。</summary>
    public string? Password { get; private set; }

    /// <summary>用户输入的私钥。语义同 <see cref="Password"/>。</summary>
    public string? PrivateKey { get; private set; }

    /// <summary>眼睛切换：在密文框与明文框之间互传当前值并互换可见性。明文不进 ViewModel。</summary>
    private void OnRevealToggled(object sender, RoutedEventArgs e)
    {
        if (RevealToggle.IsChecked == true)
        {
            PasswordReveal.Text = PasswordInput.Password;
            PasswordInput.Visibility = Visibility.Collapsed;
            PasswordReveal.Visibility = Visibility.Visible;
            PasswordReveal.Focus();
            PasswordReveal.CaretIndex = PasswordReveal.Text.Length;
        }
        else
        {
            PasswordInput.Password = PasswordReveal.Text;
            PasswordReveal.Clear();
            PasswordReveal.Visibility = Visibility.Collapsed;
            PasswordInput.Visibility = Visibility.Visible;
            PasswordInput.Focus();
        }
    }

    /// <summary>取当前用户实际输入的密码（明文框显示时以它为准）。</summary>
    private string CurrentPassword =>
        RevealToggle.IsChecked == true ? PasswordReveal.Text : PasswordInput.Password;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 私钥字段参与绑定（它不是密码框），需要先回填到 ViewModel 供校验使用。
        var enteredPassword = CurrentPassword;

        if (_viewModel.Build() is not { } credential)
        {
            return;
        }

        Result = credential;

        // 新建时空密码就是「没有密码」；编辑时留空表示保持原值，因此传 null。
        Password = _viewModel.IsNew
            ? enteredPassword
            : string.IsNullOrEmpty(enteredPassword) ? null : enteredPassword;

        PrivateKey = _viewModel.IsNew
            ? _viewModel.EnteredPrivateKey
            : string.IsNullOrWhiteSpace(_viewModel.EnteredPrivateKey) ? null : _viewModel.EnteredPrivateKey;

        // 立即清空输入框，缩短明文在界面控件中的存活时间。
        PasswordInput.Clear();
        PasswordReveal.Clear();

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        PasswordInput.Clear();
        PasswordReveal.Clear();
        DialogResult = false;
        Close();
    }
}
