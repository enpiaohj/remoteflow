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
    private CancellationTokenSource? _cts;

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
            PreferredMaxLayoutWidth = 380,
        };

        _close = NSButton.CreateButton("完成", () => Finish());
        _close.BezelStyle = NSBezelStyle.Rounded;
        _close.KeyEquivalent = "\r";

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 10,
            EdgeInsets = new NSEdgeInsets(22, 24, 20, 24),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.AddArrangedSubview(heading);
        stack.AddArrangedSubview(Gap(4));
        stack.AddArrangedSubview(Labeled("DNS 解析", _dns));
        stack.AddArrangedSubview(Labeled("Ping", _ping));
        stack.AddArrangedSubview(Labeled("TCP 端口", _tcp));
        stack.AddArrangedSubview(Gap(6));
        stack.AddArrangedSubview(_conclusion);

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(stack);
        root.AddSubview(_close);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            stack.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _close.TopAnchor.ConstraintEqualTo(stack.BottomAnchor, 16),
            _close.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -24),
            _close.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -18),
        });
        Window.ContentView = root;
    }

    public void Run()
    {
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
            });
        }
        catch (OperationCanceledException)
        {
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => _conclusion.StringValue = "诊断已取消或超时。");
        }
        catch (Exception ex)
        {
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => _conclusion.StringValue = $"诊断出错：{ex.Message}");
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
        new CGRect(0, 0, 440, 320),
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
