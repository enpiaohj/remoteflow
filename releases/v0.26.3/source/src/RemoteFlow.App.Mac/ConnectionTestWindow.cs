using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 「测试连接」对话框：打开即自动跑 DNS → Ping → TCP 三步诊断（<see cref="ConnectionTestService"/>），
/// 逐行推进状态，最后给结论 + 可折叠的详情。App-Modal。
///
/// 版式与 Windows 版 <c>TestConnectionDialog</c> 对齐：图标 + 连接名 / Host:Port ·
/// 协议、三步行、结论行、可能原因、ICMP 附注、折叠详情、底部按钮条（测试中「取消」，
/// 结束「重新测试 / 关闭」）。
/// </summary>
public sealed class ConnectionTestWindow : NSWindowController
{
    /// <summary>面板宽度固定，与 Windows 版一致；高度贴合内容。</summary>
    private static readonly nfloat PanelWidth = 520;
    private static readonly nfloat ContentInsetX = 28;
    /// <summary>结论 / 可能原因 / ICMP 附注的缩进（Windows 版是 28）。</summary>
    private static readonly nfloat NoteIndent = 28;

    private readonly ConnectionProfile _profile;
    private readonly NSStackView _stack;

    private readonly StatusLine _dns;
    private readonly StatusLine _ping;
    private readonly StatusLine _tcp;

    /// <summary>缩进包装视图 —— Hidden 必须设在**它**上。设在内部子视图上没用：Hidden 只是
    /// 不绘制，Auto Layout 照样按内容给出尺寸，包装视图仍占高度，对话框里就空出一块。</summary>
    private readonly NSView _conclusionHost;
    private readonly NSView _causesHost;
    private readonly NSView _noteHost;
    private readonly NSView _detailsHost;

    private readonly NSView _conclusionRow;
    private readonly NSImageView _conclusionIcon;
    private readonly NSTextField _conclusionText;

    private readonly NSStackView _causesPanel;
    private readonly NSStackView _causesList;
    private readonly NSTextField _pingNote;

    private readonly NSButton _detailsToggle;
    private readonly NSImageView _detailsChevron;
    private readonly NSView _detailsBox;
    private readonly NSTextField _detailsText;

    private readonly NSButton _cancel;
    private readonly NSButton _retry;
    private readonly NSButton _close;

    private CancellationTokenSource? _cts;
    private bool _detailsOpen;

    public ConnectionTestWindow(ConnectionProfile profile)
        : base(NewPanel())
    {
        _profile = profile;
        Window.Title = "测试连接";

        // ── 头部：协议图标 + 连接名 / Host:Port · 协议 ──
        var header = Header(profile);

        // ── 三步单行诊断 ──
        _dns = new StatusLine("DNS 解析");
        _ping = new StatusLine("Ping");
        _tcp = new StatusLine($"TCP 端口 {profile.Port}");

        // ── 结论行：图标 + 文本（SemiBold）──
        _conclusionIcon = Symbol("checkmark.circle", 14, NSColor.SystemGreen);
        _conclusionText = Wrapped(string.Empty, 13, NSFontWeight.Semibold, NSColor.Label);
        _conclusionRow = HStack(8, _conclusionIcon, _conclusionText);

        // ── 可能原因（仅失败时出现）──
        _causesPanel = VStack(spacing: 4);
        _causesPanel.AddArrangedSubview(Caption("可能原因", NSColor.SecondaryLabel));
        _causesList = VStack(spacing: 3);
        _causesPanel.AddArrangedSubview(_causesList);

        // ── ICMP 附注：TCP 可达但 Ping 超时时附带 ──
        _pingNote = Wrapped(string.Empty, 12, NSFontWeight.Regular, NSColor.SecondaryLabel);

        // ── 折叠详情 ──
        _detailsChevron = Symbol("chevron.right", 10, NSColor.SecondaryLabel);
        _detailsToggle = NSButton.CreateButton(string.Empty, ToggleDetails);
        _detailsToggle.Bordered = false;
        _detailsToggle.TranslatesAutoresizingMaskIntoConstraints = false;
        var toggleLabel = Caption("查看详细信息", NSColor.SecondaryLabel);
        var toggleRow = HStack(6, _detailsChevron, toggleLabel);
        toggleRow.TranslatesAutoresizingMaskIntoConstraints = false;
        _detailsToggle.AddSubview(toggleRow);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            toggleRow.LeadingAnchor.ConstraintEqualTo(_detailsToggle.LeadingAnchor),
            toggleRow.TrailingAnchor.ConstraintEqualTo(_detailsToggle.TrailingAnchor),
            toggleRow.TopAnchor.ConstraintEqualTo(_detailsToggle.TopAnchor, 4),
            toggleRow.BottomAnchor.ConstraintEqualTo(_detailsToggle.BottomAnchor, -4),
        });

        _detailsText = Wrapped(string.Empty, 11, NSFontWeight.Regular, NSColor.SecondaryLabel);
        _detailsText.Font = NSFont.FromFontName("Menlo", 11) ?? NSFont.UserFixedPitchFontOfSize(11);
        _detailsBox = Box(_detailsText);

        // ── 组装 ──
        _stack = VStack(spacing: 0);
        _stack.EdgeInsets = new NSEdgeInsets(26, ContentInsetX, 0, ContentInsetX);
        _stack.AddArrangedSubview(header);
        _stack.AddArrangedSubview(Gap(20));
        foreach (var row in new[] { _dns, _ping, _tcp })
        {
            _stack.AddArrangedSubview(row);
            _stack.AddArrangedSubview(Gap(10));
        }
        _stack.AddArrangedSubview(Gap(4));
        _conclusionHost = Indent(_conclusionRow);
        _causesHost = Indent(_causesPanel);
        _noteHost = Indent(_pingNote);
        _detailsHost = Indent(_detailsBox);
        _stack.AddArrangedSubview(_conclusionHost);
        _stack.AddArrangedSubview(_causesHost);
        _stack.AddArrangedSubview(_noteHost);
        _stack.AddArrangedSubview(Gap(10));
        _stack.AddArrangedSubview(_detailsToggle);
        _stack.AddArrangedSubview(_detailsHost);

        // ── 底部按钮条 ──
        _cancel = NSButton.CreateButton("取消", Cancel);
        _cancel.BezelStyle = NSBezelStyle.Rounded;
        _retry = NSButton.CreateButton("重新测试", () => _ = RunDiagnosticsAsync());
        _retry.BezelStyle = NSBezelStyle.Rounded;
        _close = NSButton.CreateButton("关闭", () => Finish());
        _close.BezelStyle = NSBezelStyle.Rounded;
        _close.KeyEquivalent = "\r";

        var buttons = HStack(8, _cancel, _retry, _close);
        buttons.Alignment = NSLayoutAttribute.CenterY;
        buttons.TranslatesAutoresizingMaskIntoConstraints = false;

        // macOS 原生对话框的按钮区不是一块染色的条，而是「内容 | 一条分隔线 | 按钮」。
        var separator = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true };
        Palette.With(separator, () =>
            separator.Layer!.BackgroundColor = NSColor.Separator.CGColor);

        var footer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        footer.AddSubview(separator);
        footer.AddSubview(buttons);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            separator.TopAnchor.ConstraintEqualTo(footer.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(footer.LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(footer.TrailingAnchor),
            separator.HeightAnchor.ConstraintEqualTo(1),
        });

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(_stack);
        root.AddSubview(footer);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            root.WidthAnchor.ConstraintEqualTo(PanelWidth),
            _stack.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            _stack.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _stack.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),

            footer.TopAnchor.ConstraintEqualTo(_stack.BottomAnchor, 22),
            footer.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            footer.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            footer.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),

            buttons.TopAnchor.ConstraintEqualTo(footer.TopAnchor, 16),
            buttons.BottomAnchor.ConstraintEqualTo(footer.BottomAnchor, -16),
            buttons.TrailingAnchor.ConstraintEqualTo(footer.TrailingAnchor, -24),
        });
        Window.ContentView = root;

        // 初始态与 BeginRun 一致：结论 / 可能原因 / ICMP 附注 / 详情全部收起。
        _conclusionHost.Hidden = true;
        _causesHost.Hidden = true;
        _noteHost.Hidden = true;
        _detailsHost.Hidden = true;

        SetRunning(true);
        FitToContent();
    }

    public void Run()
    {
        FitToContent();
        Window.Center();
        Window.MakeKeyAndOrderFront(null);
        _ = RunDiagnosticsAsync();
        NSApplication.SharedApplication.RunModalForWindow(Window);
    }

    private async Task RunDiagnosticsAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        BeginRun();
        var service = new ConnectionTestService();
        try
        {
            var report = await service.RunAsync(
                _profile,
                ConnectionTestService.DefaultTimeout,
                _cts.Token,
                progress: step => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => Apply(step)));

            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => ShowReport(report));
        }
        catch (OperationCanceledException)
        {
            NSApplication.SharedApplication.BeginInvokeOnMainThread(ShowCancelled);
        }
        catch (Exception ex)
        {
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                ShowCancelled();
                _conclusionText.StringValue = $"诊断出错：{ex.Message}";
                _conclusionIcon.Image = NSImage.GetSystemSymbol("xmark.circle", null);
                _conclusionIcon.ContentTintColor = NSColor.SystemRed;
            });
        }
    }

    // ── 状态推进 ──────────────────────────────────────────────

    /// <summary>新一轮测试开始：三行回到等待，第一行立即转「正在测试」。</summary>
    private void BeginRun()
    {
        _dns.MarkWaiting();
        _ping.MarkWaiting();
        _tcp.MarkWaiting();
        _dns.MarkRunning();

        _conclusionHost.Hidden = true;
        _causesHost.Hidden = true;
        ClearStack(_causesList);
        _noteHost.Hidden = true;
        _detailsHost.Hidden = true;
        _detailsOpen = false;
        _detailsChevron.Image = NSImage.GetSystemSymbol("chevron.right", null);

        SetRunning(true);
        FitToContent();
    }

    /// <summary>诊断完成：三行落到最终状态，展示结论、可能原因与 ICMP 附注。</summary>
    private void ShowReport(ConnectionTestReport report)
    {
        _dns.Apply(report.Dns);
        _ping.Apply(report.Ping);
        _tcp.Apply(report.Tcp);

        _conclusionHost.Hidden = false;
        var ok = report.TcpReachable;
        _conclusionIcon.Image = NSImage.GetSystemSymbol(ok ? "checkmark.circle" : "xmark.circle", null);
        _conclusionIcon.ContentTintColor = ok ? NSColor.SystemGreen : NSColor.SystemRed;
        _conclusionText.StringValue = $"{report.Conclusion} · {report.TotalMs} ms";

        ClearStack(_causesList);
        foreach (var cause in report.Causes)
        {
            _causesList.AddArrangedSubview(CauseRow(cause));
        }
        _causesHost.Hidden = report.Causes.Count == 0;

        // TCP 可达但 Ping 是「警告」（常见于禁 ICMP）：附一句说明，免得用户以为连接有问题。
        var showNote = ok && report.Ping.Status == StepStatus.Warning;
        _pingNote.StringValue = showNote ? "可能禁用了 ICMP，不影响连接。" : string.Empty;
        _noteHost.Hidden = !showNote;

        _detailsText.StringValue = BuildDetails(report, ConnectionTestService.DefaultTimeout);
        SetRunning(false);
        FitToContent();
    }

    /// <summary>用户取消：仍在跑的行收回等待态，结论置为已取消。</summary>
    private void ShowCancelled()
    {
        _dns.StopRunning();
        _ping.StopRunning();
        _tcp.StopRunning();

        _conclusionHost.Hidden = false;
        _conclusionIcon.Image = NSImage.GetSystemSymbol("xmark.circle", null);
        _conclusionIcon.ContentTintColor = NSColor.SystemRed;
        _conclusionText.StringValue = "诊断已取消或超时。";

        _causesHost.Hidden = true;
        _noteHost.Hidden = true;
        SetRunning(false);
        FitToContent();
    }

    /// <summary>把一步的进度落到对应行上。</summary>
    private void Apply(StepResult step)
    {
        switch (step.Label)
        {
            case "DNS 解析":
                _dns.Apply(step);
                _ping.MarkRunning();
                break;
            case "Ping":
                _ping.Apply(step);
                _tcp.MarkRunning();
                break;
            default:
                _tcp.Apply(step);
                break;
        }
    }

    /// <summary>测试中只留「取消」；结束后换成「重新测试 / 关闭」。</summary>
    private void SetRunning(bool running)
    {
        _cancel.Hidden = !running;
        _retry.Hidden = running;
        _close.Hidden = running;
    }

    private void ToggleDetails()
    {
        _detailsOpen = !_detailsOpen;
        _detailsHost.Hidden = !_detailsOpen;
        _detailsChevron.Image = NSImage.GetSystemSymbol(_detailsOpen ? "chevron.down" : "chevron.right", null);
        FitToContent();
    }

    private void Cancel() => _cts?.Cancel();

    private void Finish()
    {
        _cts?.Cancel();
        NSApplication.SharedApplication.StopModal();
        Window.OrderOut(null);
    }

    /// <summary>面板高度贴合内容。结论 / 原因 / 详情都是异步长出来的，每次更新后都要重算。</summary>
    private void FitToContent()
    {
        Window.ContentView?.LayoutSubtreeIfNeeded();
        var height = Window.ContentView?.FittingSize.Height ?? 0;
        if (height < 180)
        {
            height = 180;
        }
        Window.SetContentSize(new CGSize(PanelWidth, height));
    }

    /// <summary>折叠面板正文：目标 / 协议 / 端口 / TCP 超时 + 三步明细（对齐 Windows 版）。</summary>
    private static string BuildDetails(ConnectionTestReport report, TimeSpan timeout)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("目标     ").AppendLine(report.Host);
        sb.Append("协议     ").AppendLine(report.ProtocolName);
        sb.Append("端口     ").AppendLine(report.Port.ToString());
        sb.Append("TCP 超时 ").AppendLine($"{(int)timeout.TotalMilliseconds} ms");
        sb.AppendLine();
        foreach (var step in new[] { report.Dns, report.Ping, report.Tcp })
        {
            sb.Append(step.Label).Append(": ").AppendLine(step.Text);
        }
        return sb.ToString().TrimEnd();
    }

    // ── 构件 ──────────────────────────────────────────────────

    private static NSView Header(ConnectionProfile profile)
    {
        var tint = ProtocolStyle.Tint(profile.Protocol);
        var tile = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true };
        tile.Layer!.CornerRadius = 8;
        Palette.With(tile, () =>
            tile.Layer.BackgroundColor = tint.ColorWithAlphaComponent(0.12f).CGColor);

        var glyph = new NSImageView
        {
            Image = ProtocolStyle.Symbol(profile.Protocol),
            ContentTintColor = tint,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(19, NSFontWeight.Regular),
        };
        tile.AddSubview(glyph);

        var title = new NSTextField
        {
            StringValue = profile.Name,
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold),
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var sub = new NSTextField
        {
            StringValue = $"{profile.Host}:{profile.Port}  ·  {ProtoName(profile.Protocol)}",
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            TextColor = NSColor.SecondaryLabel,
            Font = NSFont.SystemFontOfSize(12),
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var texts = VStack(spacing: 2);
        texts.AddArrangedSubview(title);
        texts.AddArrangedSubview(sub);

        var row = HStack(12, tile, texts);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            tile.WidthAnchor.ConstraintEqualTo(44),
            tile.HeightAnchor.ConstraintEqualTo(44),
            glyph.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor),
            glyph.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor),
        });
        return row;
    }

    /// <summary>可能原因的一行：缩进的「·」+ 文本。</summary>
    private static NSView CauseRow(string text)
    {
        var dot = Caption("·", NSColor.TertiaryLabel);
        var body = Wrapped(text, 12, NSFontWeight.Regular, NSColor.SecondaryLabel);
        var row = HStack(7, dot, body);
        return row;
    }

    /// <summary>详情面板的容器：铺一层与左侧栏**同材质**的毛玻璃底（圆角 6、不描边），
    /// 里面只做缩进与内边距 —— 与全应用其它「框」保持同一种材质。</summary>
    private static NSView Box(NSView content)
    {
        var box = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var blur = new NSVisualEffectView
        {
            Material = NSVisualEffectMaterial.Sidebar,      // 与左侧栏同材质
            BlendingMode = NSVisualEffectBlendingMode.WithinWindow,
            State = NSVisualEffectState.FollowsWindowActiveState,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        blur.WantsLayer = true;
        blur.Layer!.CornerRadius = 6;
        blur.Layer.MasksToBounds = true;
        box.AddSubview(blur, NSWindowOrderingMode.Below, null);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            blur.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor),
            blur.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor),
            blur.TopAnchor.ConstraintEqualTo(box.TopAnchor),
            blur.BottomAnchor.ConstraintEqualTo(box.BottomAnchor),
        });

        box.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            content.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor, 12),
            content.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor, -12),
            content.TopAnchor.ConstraintEqualTo(box.TopAnchor, 10),
            content.BottomAnchor.ConstraintEqualTo(box.BottomAnchor, -10),
        });
        return box;
    }

    /// <summary>把子视图按 Windows 版的缩进量（28）右移。</summary>
    private static NSView Indent(NSView child)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(child);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            child.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor, NoteIndent),
            child.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor),
            child.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            child.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
        });
        return host;
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

    private static NSStackView VStack(nfloat spacing) => new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Vertical,
        Alignment = NSLayoutAttribute.Leading,
        Spacing = spacing,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSStackView HStack(nfloat spacing, params NSView[] views)
    {
        var s = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = spacing,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var v in views)
        {
            s.AddArrangedSubview(v);
        }
        return s;
    }

    /// <summary>清空 stack 的 arranged 子视图（NSStackView 没有 Clear）。</summary>
    private static void ClearStack(NSStackView stack)
    {
        foreach (var v in stack.ArrangedSubviews)
        {
            stack.RemoveArrangedSubview(v);
            v.RemoveFromSuperview();
        }
    }

    private static NSTextField Caption(string text, NSColor color) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        TextColor = color,
        Font = NSFont.SystemFontOfSize(12),
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSTextField Wrapped(string text, nfloat size, nfloat weight, NSColor color) => new()
    {
        StringValue = text,
        Bordered = false,
        Editable = false,
        Selectable = true,
        DrawsBackground = false,
        TextColor = color,
        Font = NSFont.SystemFontOfSize(size, weight),
        LineBreakMode = NSLineBreakMode.ByWordWrapping,
        TranslatesAutoresizingMaskIntoConstraints = false,
        PreferredMaxLayoutWidth = PanelWidth - ContentInsetX * 2,
    };

    private static NSImageView Symbol(string name, nfloat size, NSColor color) => new()
    {
        Image = NSImage.GetSystemSymbol(name, null),
        ContentTintColor = color,
        TranslatesAutoresizingMaskIntoConstraints = false,
        SymbolConfiguration = NSImageSymbolConfiguration.Create(size, NSFontWeight.Medium),
    };

    private static NSView Gap(nfloat h)
    {
        var v = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        v.HeightAnchor.ConstraintEqualTo(h).Active = true;
        return v;
    }

    /// <summary>一行诊断状态：图标（随状态变色换形）+ 固定宽标签 + 值。列宽对齐 Windows（18 / 88）。</summary>
    private sealed class StatusLine : NSStackView
    {
        private readonly NSImageView _icon;
        private readonly NSTextField _label;
        private readonly NSTextField _value;
        private StepStatus _status = StepStatus.Waiting;

        public StatusLine(string label)
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
            Alignment = NSLayoutAttribute.CenterY;
            Spacing = 8;
            TranslatesAutoresizingMaskIntoConstraints = false;

            _icon = Symbol("circle", 12, NSColor.TertiaryLabel);
            _label = new NSTextField
            {
                StringValue = label,
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                TextColor = NSColor.SecondaryLabel,
                Font = NSFont.SystemFontOfSize(12),
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            _value = new NSTextField
            {
                StringValue = "等待中…",
                Bordered = false,
                Editable = false,
                Selectable = true,
                DrawsBackground = false,
                TextColor = NSColor.SecondaryLabel,
                Font = NSFont.SystemFontOfSize(12),
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            AddArrangedSubview(_icon);
            AddArrangedSubview(_label);
            AddArrangedSubview(_value);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _icon.WidthAnchor.ConstraintEqualTo(18),
                _label.WidthAnchor.ConstraintEqualTo(88),
            });
        }

        public void MarkWaiting() => Set(StepStatus.Waiting, "等待中…");

        public void MarkRunning() => Set(StepStatus.Running, "正在测试…");

        public void StopRunning()
        {
            if (_status == StepStatus.Running)
            {
                Set(StepStatus.Waiting, "等待中…");
            }
        }

        public void Apply(StepResult step) => Set(step.Status, step.Text);

        private void Set(StepStatus status, string text)
        {
            _status = status;
            _icon.Image = NSImage.GetSystemSymbol(status switch
            {
                StepStatus.Success => "checkmark.circle",
                StepStatus.Warning => "exclamationmark.triangle",
                StepStatus.Failed => "xmark.circle",
                StepStatus.Running => "circle.dotted",
                _ => "circle",
            }, null);

            _icon.ContentTintColor = status switch
            {
                StepStatus.Success => NSColor.SystemGreen,
                StepStatus.Warning => NSColor.SystemYellow,
                StepStatus.Failed => NSColor.SystemRed,
                _ => NSColor.TertiaryLabel,
            };

            _value.StringValue = text;
            _value.TextColor = status == StepStatus.Waiting ? NSColor.SecondaryLabel : NSColor.Label;
        }
    }
}
