using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
/// <para>
/// <b>真实 Bug（2026-09-05 用户反馈）：</b>这个对话框是在握手中止后异步弹出的，
/// 弹出时机紧跟在「终端刚就绪、用户习惯性按一下回车/点一下鼠标看看有没有反应」之类的
/// 操作后面。第一轮修复只处理了键盘：之前 RejectButton 同时挂了 <c>IsCancel</c> 和
/// <c>IsDefault</c>，残留在消息队列里的回车/Esc 一送到这个新窗口就会立刻触发
/// 「取消连接」；去掉 IsDefault 并在窗口刚加载的一小段宽限期内整体禁用两个按钮后，
/// 键盘这条路径已经关闭。
/// </para>
/// <para>
/// <b>第二轮修复（同一份反馈复现，仍然一闪而过）：</b>排查发现窗口本身还挂了一个
/// <c>MouseLeftButtonDown</c> 处理器，用于无边框窗口的拖动——它直接绑在 Window 上，
/// 完全不受两个按钮的禁用状态影响，且禁用态的按钮在 WPF 里不吃鼠标命中测试，事件会
/// 直接穿透到 Window 层。任何残留/时序错位的鼠标按下事件（例如触发「连接」的那次点击、
/// 双击的第一下）打到这个刚创建的窗口上都会调用 <see cref="DragMove"/>，而
/// <see cref="DragMove"/> 要求调用时鼠标左键必须确实按住，否则会抛
/// <see cref="InvalidOperationException"/>——这个异常会被 App 级
/// <c>DispatcherUnhandledException</c> 兜底，但 <c>ShowDialog()</c> 的嵌套消息循环
/// 已经被打断，窗口随之瞬间关闭且 <c>DialogResult</c> 从未被设置为 true，表现和「一闪而过」
/// 完全一致。修复：拖动这条路径同样纳入宽限期保护（宽限期内忽略拖动），并给
/// <see cref="DragMove"/> 加防御性 try/catch，即使宽限期之后仍然发生时序错位也不会
/// 再抛出未处理异常打断弹窗。
/// </para>
/// </summary>
public partial class HostKeyDialog : Window
{
    /// <summary>刚打开后的宽限期：这段时间内两个按钮都不响应，也不响应拖动，吸收残留的键盘/鼠标事件。</summary>
    private static readonly TimeSpan InputGracePeriod = TimeSpan.FromMilliseconds(400);

    /// <summary>宽限期是否已结束，输入是否已经真正生效——拖动处理器与两个按钮共用这一个开关。</summary>
    private bool _inputArmed;

    private HostKeyDialog()
    {
        InitializeComponent();

        MouseLeftButtonDown += (_, e) =>
        {
            // 宽限期内忽略：残留/时序错位的鼠标按下不应该触发拖动。
            if (!_inputArmed || e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove 要求调用时鼠标左键确实按住；时序错位（事件里报告 Pressed，
                // 但实际按键已经释放）会导致这里抛异常。吞掉即可——不拖动，不影响弹窗本身。
            }
        };

        Loaded += OnLoadedArmButtonsAfterGracePeriod;
    }

    /// <summary>
    /// 宽限期结束后需要落焦点在「取消连接」上（高风险场景）——不能在按钮还被
    /// 禁用时调用 <c>Focus()</c>（WPF 不允许禁用控件获得键盘焦点，会被静默忽略），
    /// 所以焦点也要跟着挪到宽限期结束之后一起设置。
    /// </summary>
    private bool _focusRejectAfterGracePeriod;

    private void OnLoadedArmButtonsAfterGracePeriod(object sender, RoutedEventArgs e)
    {
        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;

        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = InputGracePeriod };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _inputArmed = true;
            AcceptButton.IsEnabled = true;
            RejectButton.IsEnabled = true;

            if (_focusRejectAfterGracePeriod)
            {
                RejectButton.Focus();
            }
        };
        timer.Start();
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

            // 高风险场景：信任按钮改用警示色，宽限期结束后焦点落在「取消连接」。
            dialog.AcceptButton.Style = (Style)System.Windows.Application.Current.FindResource("Button.Danger");
            dialog.AcceptButton.Content = "仍然信任";
            dialog._focusRejectAfterGracePeriod = true;
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
