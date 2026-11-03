using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 连接前的账号 / 密码输入框，版式与文案复刻 Windows 安全中心（mstsc）的凭据框：
/// 未分配凭据时输入用户名和密码；凭据只有账户名时账户名固定显示、只输入密码；
/// 「记住我的凭据」保存为凭据；「更多选项」可直接选用已保存的凭据。
/// <para>
/// <b>安全设计：</b>密码走 <see cref="PasswordBox"/>，不参与数据绑定，只在用户确认时读取一次；
/// 关闭时立即清空所有输入框。「眼睛」切换时在密文框与明文框之间互传值，明文不进入任何绑定属性。
/// </para>
/// </summary>
public partial class ConnectionCredentialDialog : Window
{
    /// <summary>「更多选项」列表的一行。<see cref="SavedId"/> 为 null 表示默认项（使用其他账户 / 此连接原有账户）。</summary>
    public sealed record MoreItem(string Title, string Subtitle, Guid? SavedId);

    /// <summary>「凭据类型」下拉的一项。</summary>
    public sealed record TypeOption(CredentialType Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ConnectionCredentialRequirement _requirement;
    private bool _syncingPassword;

    /// <summary>用户是否手改过凭据名称：改过之后不再跟随用户名自动填充。</summary>
    private bool _nameEdited;

    private bool _settingName;

    private ConnectionCredentialDialog(ConnectionCredentialRequest request)
    {
        InitializeComponent();
        WindowBackdrop.PrepareDialog(this);

        _requirement = request.Requirement;
        TitleText.Text = "输入你的凭据";
        MessageText.Text = $"这些凭据将用于连接 {request.Target}。";

        string defaultTitle;
        string defaultSubtitle;

        if (!_requirement.NeedsUsername)
        {
            // VNC 口令：只有密码。
            UsernamePanel.Visibility = Visibility.Collapsed;
            defaultTitle = "使用口令";
            defaultSubtitle = request.ConnectionName;
        }
        else if (_requirement.Prompt == ConnectionCredentialPrompt.PasswordOnly)
        {
            // 凭据只保存了账户名：账户名固定显示，密码框下方再给出「域\用户」。
            UsernamePanel.Visibility = Visibility.Collapsed;
            var full = string.IsNullOrEmpty(_requirement.Domain)
                ? _requirement.Username
                : $"{_requirement.Domain}\\{_requirement.Username}";
            AccountText.Text = _requirement.Username;
            AccountText.Visibility = Visibility.Visible;
            FullAccountText.Text = full;
            FullAccountText.Visibility = Visibility.Visible;
            defaultTitle = _requirement.Username;
            defaultSubtitle = full;
        }
        else
        {
            // 与 Windows 安全中心的凭据框一致的标准说法，且不带副标题。
            defaultTitle = "使用其他账户";
            defaultSubtitle = string.Empty;
        }

        // 凭据类型：RDP 可选本地账号 / Windows 域账号；SSH、VNC 只有对应的一种。
        List<TypeOption> typeOptions = _requirement.Type switch
        {
            CredentialType.SshPassword => [new(CredentialType.SshPassword, "SSH 口令")],
            CredentialType.VncPassword => [new(CredentialType.VncPassword, "VNC 口令")],
            _ => [new(CredentialType.LocalPassword, "本地账号"), new(CredentialType.WindowsDomain, "Windows 域账号")],
        };
        CredentialTypeCombo.ItemsSource = typeOptions;
        CredentialTypeCombo.SelectedItem = typeOptions.FirstOrDefault(o => o.Value == _requirement.Type) ?? typeOptions[0];

        // 账户名固定（凭据只有账户名）时，名称默认就是它的全称；其余情况随用户名输入自动填充。
        if (_requirement.Prompt == ConnectionCredentialPrompt.PasswordOnly)
        {
            SetName(string.IsNullOrEmpty(_requirement.Domain)
                ? _requirement.Username
                : $"{_requirement.Domain}\\{_requirement.Username}");
        }

        var items = new List<MoreItem> { new(defaultTitle, defaultSubtitle, null) };
        items.AddRange((request.SavedCredentials ?? [])
            .Select(c => new MoreItem(c.Account, c.Name, c.Id)));
        MoreList.ItemsSource = items;
        MoreList.SelectedIndex = 0;

        // 没有可选的已保存凭据时，不显示「更多选项」。
        if (items.Count < 2)
        {
            MoreOptionsButton.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) =>
        {
            if (UsernamePanel.Visibility == Visibility.Visible)
            {
                UsernameInput.Focus();
            }
            else
            {
                PasswordInput.Focus();
            }
        };
    }

    /// <summary>用户输入的凭据。<c>null</c> 表示取消。</summary>
    public ConnectionCredentialInput? Result { get; private set; }

    public static ConnectionCredentialInput? Prompt(Window? owner, ConnectionCredentialRequest request)
    {
        var dialog = new ConnectionCredentialDialog(request)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };

        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            DragMove();
        }
    }

    // ── 密码框：输入后才出现眼睛图标 ─────────────────────────────

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => UpdateRevealVisibility();

    private void OnPasswordRevealChanged(object sender, TextChangedEventArgs e) => UpdateRevealVisibility();

    private void UpdateRevealVisibility()
    {
        if (_syncingPassword)
        {
            return;
        }

        RevealToggle.Visibility = CurrentPassword.Length > 0 || RevealToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnRevealToggled(object sender, RoutedEventArgs e)
    {
        _syncingPassword = true;
        try
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
        finally
        {
            _syncingPassword = false;
        }

        UpdateRevealVisibility();
    }

    private string CurrentPassword =>
        RevealToggle.IsChecked == true ? PasswordReveal.Text : PasswordInput.Password;

    // ── 更多选项 ─────────────────────────────────────────────────

    private void OnMoreOptionsClick(object sender, RoutedEventArgs e)
    {
        MoreList.Visibility = MoreList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        MoreListLabel.Visibility = MoreList.Visibility;
        UpdateRememberOptions();
    }

    private void OnMoreSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选了已保存的凭据：不需要再输入，输入区置灰；选回默认项则恢复输入。
        var usingSaved = MoreList.SelectedItem is MoreItem { SavedId: not null };
        InputsPanel.IsEnabled = !usingSaved;
        InputsPanel.Opacity = usingSaved ? 0.45 : 1;
        UpdateRememberOptions();
    }

    // ── 记住我的凭据：类型与名称 ─────────────────────────────────

    private void OnRememberToggled(object sender, RoutedEventArgs e) => UpdateRememberOptions();

    /// <summary>
    /// 展开「更多选项」且勾选「记住我的凭据」、并且会新建凭据时，动态出现「凭据类型 / 凭据名称」。
    /// 凭据只有账户名（补存密码）与选用已保存凭据时不新建，不显示。
    /// </summary>
    private void UpdateRememberOptions()
    {
        if (RememberOptionsPanel is null || MoreList is null)
        {
            return;
        }

        var createsNew = _requirement.Prompt == ConnectionCredentialPrompt.UsernameAndPassword
            || !_requirement.NeedsUsername;
        var show = MoreList.Visibility == Visibility.Visible
                   && RememberCheck.IsChecked == true
                   && createsNew
                   && MoreList.SelectedItem is MoreItem { SavedId: null };
        RememberOptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetName(string text)
    {
        _settingName = true;
        try
        {
            CredentialNameInput.Text = text;
        }
        finally
        {
            _settingName = false;
        }
    }

    /// <summary>名称跟随用户名（全称）自动填充，直到用户自己改过名称。</summary>
    private void OnUsernameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_nameEdited && CredentialNameInput is not null)
        {
            SetName(UsernameInput.Text.Trim());
        }
    }

    private void OnCredentialNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingName)
        {
            _nameEdited = true;
        }
    }

    // ── 确认 / 取消 ──────────────────────────────────────────────

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var remember = RememberCheck.IsChecked == true;

        if (MoreList.SelectedItem is MoreItem { SavedId: { } savedId })
        {
            Result = new ConnectionCredentialInput(string.Empty, string.Empty, string.Empty, remember, savedId);
            Finish();
            return;
        }

        var password = CurrentPassword;
        string username;
        string domain;

        if (!_requirement.NeedsUsername)
        {
            username = string.Empty;
            domain = string.Empty;
        }
        else if (_requirement.Prompt == ConnectionCredentialPrompt.PasswordOnly)
        {
            username = _requirement.Username;
            domain = _requirement.Domain;
        }
        else
        {
            username = UsernameInput.Text.Trim();
            domain = string.Empty;
            if (username.Length == 0)
            {
                ShowError("请输入用户名。");
                return;
            }

            // 输入「域\用户」时拆成域和用户名。
            if (username.IndexOf('\\') is var slash and > 0 && slash < username.Length - 1)
            {
                domain = username[..slash];
                username = username[(slash + 1)..];
            }
        }

        if (password.Length == 0 && _requirement.NeedsUsername)
        {
            ShowError("请输入密码。");
            return;
        }

        // 类型 / 名称只在会新建凭据时有意义；名称留空则由服务按用户名全称生成。
        var type = (CredentialTypeCombo.SelectedItem as TypeOption)?.Value;
        if (type == CredentialType.LocalPassword && domain.Length > 0
            && CredentialTypeCombo.Items.OfType<TypeOption>().Any(o => o.Value == CredentialType.WindowsDomain))
        {
            type = CredentialType.WindowsDomain; // 带域的账号按域账号保存
        }

        var name = CredentialNameInput.Text.Trim();
        Result = new ConnectionCredentialInput(
            username, domain, password, remember, null,
            name.Length == 0 ? null : name, type);
        Finish();
    }

    private void Finish()
    {
        ClearInputs();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ClearInputs();
        DialogResult = false;
        Close();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearInputs()
    {
        PasswordInput.Clear();
        PasswordReveal.Clear();
    }
}
