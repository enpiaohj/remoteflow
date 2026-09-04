using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// SSH 主机密钥确认对话框。
/// <para>
/// 两种场景在视觉与默认行为上必须明确区分：
/// <list type="bullet">
///   <item><b>首次连接</b>——中性提示，请求用户确认指纹。</item>
///   <item><b>指纹变化</b>——强警告。这可能意味着中间人攻击，
///   因此默认焦点落在「取消连接」，且信任按钮使用警示色，杜绝顺手回车放行。</item>
/// </list>
/// </para>
/// </summary>
public partial class HostKeyDialog : Window
{
    private HostKeyDialog()
    {
        InitializeComponent();

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    /// <summary>显示确认对话框，返回用户是否接受该主机密钥。</summary>
    public static bool Show(Window? owner, SshHostKeyVerificationContext context)
    {
        var dialog = new HostKeyDialog
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };

        dialog.HostText.Text = $"{context.Host}:{context.Port}";
        dialog.AlgorithmText.Text = context.KeyAlgorithm;
        dialog.FingerprintText.Text = $"SHA256:{context.Fingerprint}";

        if (context.IsMismatch)
        {
            dialog.IconGlyph.Text = (string)System.Windows.Application.Current.FindResource("Icon.Warning");
            dialog.IconGlyph.Foreground = (Brush)System.Windows.Application.Current.FindResource("Status.Danger");
            dialog.TitleText.Text = "主机密钥已变更";
            dialog.MessageText.Text =
                "该主机的密钥指纹与此前记录的不一致。这可能是服务器重装或密钥轮换造成的，" +
                "但也可能意味着连接正遭到中间人攻击。";

            dialog.KnownFingerprintRow.Visibility = Visibility.Visible;
            dialog.KnownFingerprintText.Text = $"SHA256:{context.KnownFingerprint}";

            dialog.AdviceText.Text =
                "在通过其他可信渠道（如带外登录服务器执行 ssh-keygen -lf）核实新指纹之前，请不要继续连接。";

            // 高风险场景：信任按钮改用警示色，默认动作是取消。
            dialog.AcceptButton.Style = (Style)System.Windows.Application.Current.FindResource("Button.Danger");
            dialog.AcceptButton.Content = "仍然信任";
            dialog.Loaded += (_, _) => dialog.RejectButton.Focus();
        }
        else
        {
            dialog.IconGlyph.Text = (string)System.Windows.Application.Current.FindResource("Icon.Info");
            dialog.IconGlyph.Foreground = (Brush)System.Windows.Application.Current.FindResource("Status.Info");
            dialog.TitleText.Text = "首次连接该主机";
            dialog.MessageText.Text =
                "这是第一次连接该主机，RemoteFlow 尚未记录它的密钥指纹。请核对下方指纹是否与服务器上的一致。";
            dialog.AdviceText.Text =
                "确认后该指纹会被记录；今后若指纹发生变化，连接会被中止并给出明确警告。";
        }

        return dialog.ShowDialog() == true;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnRejectClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
