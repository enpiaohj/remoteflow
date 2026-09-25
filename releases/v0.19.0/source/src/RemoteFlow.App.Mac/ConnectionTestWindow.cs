using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 「测试连接」对话框：打开即自动跑 DNS → Ping → TCP 三步诊断（<see cref="ConnectionTestService"/>），
/// 逐行推进状态，最后给结论。App-Modal。
/// </summary>
public sealed class ConnectionTestWindow : NSWindowController
{
    private readonly ConnectionProfile _profile;
    private readonly NSTextField _dns = Row();
    private readonly NSTextField _ping = Row();
    private readonly NSTextField _tcp = Row();
    private readonly NSTextField _conclusion;
    private readonly NSButton _close;
    private readonly NSStackView _stack;
    private CancellationTokenSource? _cts;

    /// <summary>面板宽度固定，高度贴合内容。与 <see cref="_conclusion"/> 的换行宽度是同一个数
    /// （440 - 左右各 24 的内边距），改一处要一起改。</summary>
    private static readonly nfloat PanelWidth = 440;
    private static readonly nfloat ContentInsetX = 24;

    public ConnectionTestWindow(ConnectionProfile profile)
        : base(NewPanel())
    {
        _profile = profile;
        Window.Title = "测试连接";

        var heading = Big($"{ProtoName(profile.Protocol)}   ·   {profile.Host}:{profile.Port}");

        _conclusion = new NSTextField
        {
            StringValue = "正在诊断…",
            Bordered = false,
            Editable = false,
            Selectable = true,
            DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium),
            TranslatesAutoresizingMaskIntoConstraints = false,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            PreferredMaxLayoutWidth = PanelWidth - ContentInsetX * 2,
        };

        _close = NSButton.CreateButton("完成", () => Finish());
        _close.BezelStyle = NSBezelStyle.Rounded;
        _close.KeyEquivalent = "\r";

        _stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 10,
            EdgeInsets = new NSEdgeInsets(22, ContentInsetX, 20, ContentInsetX),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _stack.AddArrangedSubview(heading);
        _stack.AddArrangedSubview(Gap(4));
        _stack.AddArrangedSubview(Labeled("DNS 解析", _dns));
        _stack.AddArrangedSubview(Labeled("Ping", _ping));
        _stack.AddArrangedSubview(Labeled("TCP 端口", _tcp));
        _stack.AddArrangedSubview(Gap(6));
        _stack.AddArrangedSubview(_conclusion);

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(_stack);
        root.AddSubview(_close);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            root.WidthAnchor.ConstraintEqualTo(PanelWidth),   // 固定宽，高度由内容定（见 FitToContent）
            _stack.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            _stack.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _stack.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _close.TopAnchor.ConstraintEqualTo(_stack.BottomAnchor, 16),
            _close.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -ContentInsetX),
            _close.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -18),
        });
        Window.ContentView = root;
        FitToContent();
    }

    /// <summary>把面板高度贴合内容。原来固定 440×320：内容矮时 Auto Layout 只能把几行拉开
    /// 填满那 320，看着松散；结论带「可能原因」多行时又顶出面板底部 —— 两种都表现为尺寸不合适。
    /// 结论是异步逐行填进来的，所以每次更新后都要重算一次。</summary>
    private void FitToContent()
    {
        Window.ContentView?.LayoutSubtreeIfNeeded();
        var height = _stack.FittingSize.Height + 16 + _close.FittingSize.Height + 18;
        if (height < 160)
        {
            height = 160;   // 兜底下限，避免内容异常为空时面板塌成一条
        }
        Window.SetContentSize(new CGSize(PanelWidth, height));
    }

    public void Run()
    {
        FitToContent();   // 显示前按已就位的内容定稿高度
        Window.Center();
        Window.MakeKeyAndOrderFront(null);
        _ = RunDiagnosticsAsync();
        NSApplication.SharedApplication.RunModalForWindow(Window);
    }

    private async Task RunDiagnosticsAsync()
    {
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var service = new ConnectionTestService();
        try
        {
            var report = await service.RunAsync(
                _profile,
                ConnectionTestService.DefaultTimeout,
                _cts.Token,
                progress: step => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => Apply(step)));

            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                _conclusion.StringValue = report.Causes.Count > 0
                    ? report.Conclusion + "\n\n可能原因：\n· " + string.Join("\n· ", report.Causes)
                    : report.Conclusion;
                _conclusion.TextColor = report.TcpReachable ? NSColor.SystemGreen : NSColor.SystemOrange;
                FitToContent();   // 结论行数变了，面板高度跟着贴合
            });
        }
        catch (OperationCanceledException)
        {
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                _conclusion.StringValue = "诊断已取消或超时。";
                FitToContent();
            });
        }
        catch (Exception ex)
        {
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                _conclusion.StringValue = $"诊断出错：{ex.Message}";
                FitToContent();
            });
        }
    }

    private void Apply(StepResult step)
    {
        var target = step.Label switch
        {
            "DNS 解析" => _dns,
            "Ping" => _ping,
            _ => _tcp,
        };
        target.StringValue = step.Text;
        target.TextColor = step.Status switch
        {
            StepStatus.Success => NSColor.SystemGreen,
            StepStatus.Warning => NSColor.SystemYellow,
            StepStatus.Failed => NSColor.SystemRed,
            _ => NSColor.SecondaryLabel,
        };
    }

    private void Finish()
    {
        _cts?.Cancel();
        NSApplication.SharedApplication.StopModal();
        Window.OrderOut(null);
    }

    private static NSWindow NewPanel() => new NSPanel(
        new CGRect(0, 0, PanelWidth, 240),   // 初值；构造末尾 FitToContent 会按内容校正高度
        NSWindowStyle.Titled | NSWindowStyle.Closable,
        NSBackingStore.Buffered,
        deferCreation: false);

    private static string ProtoName(ProtocolType p) => p switch
    {
        ProtocolType.Rdp => "RDP",
        ProtocolType.Ssh => "SSH",
        _ => "VNC",
    };

    private static NSView Labeled(string label, NSTextField value)
    {
        var l = new NSTextField
        {
            StringValue = label,
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Alignment = NSTextAlignment.Right,
            TextColor = NSColor.SecondaryLabel,
            Font = NSFont.SystemFontOfSize(12),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        l.WidthAnchor.ConstraintEqualTo(72).Active = true;

        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 10,
            Alignment = NSLayoutAttribute.FirstBaseline,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(l);
        row.AddArrangedSubview(value);
        return row;
    }

    private static NSTextField Row() => new()
    {
        StringValue = "等待中…",
        Bordered = false,
        Editable = false,
        Selectable = true,
        DrawsBackground = false,
        TextColor = NSColor.SecondaryLabel,
        Font = NSFont.SystemFontOfSize(13),
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSTextField Big(string text) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = true,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold),
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSView Gap(nfloat h)
    {
        var v = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        v.HeightAnchor.ConstraintEqualTo(h).Active = true;
        return v;
    }
}
