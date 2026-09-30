using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RemoteFlow.App.Services;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 连接质量详情 Flyout（轻量弹出窗）。
/// <para>
/// 以独立顶层窗口呈现：既能盖在 RDP ActiveX airspace 之上（同全屏药丸使用 Popup 的原因），
/// 又不会退出完全全屏。窗口无边框、AllowsTransparency、Topmost、不可调整大小，且不设置
/// 原生 Owner，避免激活切换改写主窗口状态；圆角与投影随主题（DynamicResource）。
/// 同一会话 Tab 只允许一个实例，由
/// <see cref="SessionHostView"/> 统一打开 / 定位 / 关闭。
/// </para>
/// <para>
/// 交互：点击外部（Deactivated）或按 Esc 关闭；用户主动关闭（Esc / 关闭按钮）时把
/// <see cref="CloseByUserIntent"/> 置真，宿主据此把键盘焦点还给会话画面（RDP ActiveX）。
/// </para>
/// </summary>
public partial class ConnectionQualityFlyout : Window
{
    private bool _activatedOnce;
    private bool _closed;

    /// <summary>本次关闭是否由用户主动触发（Esc / 关闭按钮）。点击外部关闭时保持 false。</summary>
    public bool CloseByUserIntent { get; private set; }

    public ConnectionQualityFlyout()
    {
        InitializeComponent();
        WindowBackdrop.PrepareDialog(this);

        PreviewKeyDown += OnPreviewKeyDown;
        Activated += (_, _) => _activatedOnce = true;
        Deactivated += (_, _) =>
        {
            // 点击窗口以外（含 RDP ActiveX / 标题栏工具条）会令本窗失活 → 视为关闭。
            // 本窗不设置原生 Owner，避免激活切换影响主窗口的 WindowState。
            if (_activatedOnce && !_closed)
            {
                Close();
            }
        };
        Closed += (_, _) => _closed = true;
    }

    /// <summary>关窗即标记，避免 Deactivated → Close → Deactivated 重入。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _closed = true;
        base.OnClosing(e);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseByUserIntent = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseByUserIntent = true;
        Close();
    }

    private void OnTitleDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标尚未真正按下 / 窗口状态不允许拖动：忽略。
        }
    }
}
