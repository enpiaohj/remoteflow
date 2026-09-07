using AppKit;
using CoreGraphics;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 详情区顶部的会话 Tab 条。每个活动会话一个 Tab（协议图标 + 名称 + 关闭）。
/// 无会话时由宿主隐藏。
/// </summary>
public sealed class SessionTabBar : NSView
{
    private readonly NSStackView _row = new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
        Spacing = 0,
        Alignment = NSLayoutAttribute.CenterY,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private readonly List<Tab> _tabs = new();
    private Guid _active;

    public event EventHandler<Guid>? TabSelected;
    public event EventHandler<Guid>? TabClosed;

    public int Count => _tabs.Count;

    public SessionTabBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.WindowBackground.CGColor;

        var scroll = new NSScrollView
        {
            DocumentView = _row,
            DrawsBackground = false,
            HasHorizontalScroller = false,
            HasVerticalScroller = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var sep = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };

        AddSubview(scroll);
        AddSubview(sep);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            scroll.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            scroll.TopAnchor.ConstraintEqualTo(TopAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(sep.TopAnchor),
            _row.LeadingAnchor.ConstraintEqualTo(scroll.ContentView.LeadingAnchor, 6),
            _row.TopAnchor.ConstraintEqualTo(scroll.ContentView.TopAnchor),
            _row.BottomAnchor.ConstraintEqualTo(scroll.ContentView.BottomAnchor),
            sep.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            sep.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            sep.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            HeightAnchor.ConstraintEqualTo(34),
        });
    }

    public void AddTab(Guid id, string name, ProtocolType protocol)
    {
        if (_tabs.Any(t => t.Id == id))
        {
            return;
        }

        var tab = new Tab(id, name, protocol, this);
        _tabs.Add(tab);
        _row.AddArrangedSubview(tab.View);
        Select(id);
    }

    public void RemoveTab(Guid id)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
        {
            return;
        }

        _tabs.Remove(tab);
        tab.View.RemoveFromSuperview();

        if (_active == id && _tabs.Count > 0)
        {
            Select(_tabs[^1].Id);
        }
    }

    public void RenameTab(Guid id, string name)
        => _tabs.FirstOrDefault(t => t.Id == id)?.SetTitle(name);

    /// <summary>仅更新高亮，不触发 <see cref="TabSelected"/>（宿主自身切换时用）。</summary>
    public void HighlightOnly(Guid id)
    {
        _active = id;
        foreach (var t in _tabs)
        {
            t.SetActive(t.Id == id);
        }
    }

    public void ClearHighlight()
    {
        _active = Guid.Empty;
        foreach (var t in _tabs)
        {
            t.SetActive(false);
        }
    }

    private void Select(Guid id)
    {
        HighlightOnly(id);
        TabSelected?.Invoke(this, id);
    }

    private void Close(Guid id) => TabClosed?.Invoke(this, id);

    private sealed class Tab
    {
        public Guid Id { get; }
        public NSView View { get; }
        private readonly NSTextField _label;
        private readonly NSView _bg;

        public Tab(Guid id, string name, ProtocolType protocol, SessionTabBar owner)
        {
            Id = id;

            var icon = new NSImageView
            {
                Image = ProtocolStyle.Symbol(protocol),
                ContentTintColor = ProtocolStyle.Tint(protocol),
                TranslatesAutoresizingMaskIntoConstraints = false,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(12, NSFontWeight.Regular),
            };
            _label = new NSTextField
            {
                StringValue = name,
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(12),
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            var close = new NSButton
            {
                Image = NSImage.GetSystemSymbol("xmark", null),
                Bordered = false,
                TranslatesAutoresizingMaskIntoConstraints = false,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(9, NSFontWeight.Bold),
            };
            close.Activated += (_, _) => owner.Close(id);

            _bg = new ClickView(() => owner.Select(id)) { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
            _bg.Layer!.CornerRadius = 6;

            _bg.AddSubview(icon);
            _bg.AddSubview(_label);
            _bg.AddSubview(close);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                icon.LeadingAnchor.ConstraintEqualTo(_bg.LeadingAnchor, 9),
                icon.CenterYAnchor.ConstraintEqualTo(_bg.CenterYAnchor),
                _label.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, 6),
                _label.CenterYAnchor.ConstraintEqualTo(_bg.CenterYAnchor),
                _label.WidthAnchor.ConstraintLessThanOrEqualTo(140),
                close.LeadingAnchor.ConstraintEqualTo(_label.TrailingAnchor, 6),
                close.TrailingAnchor.ConstraintEqualTo(_bg.TrailingAnchor, -8),
                close.CenterYAnchor.ConstraintEqualTo(_bg.CenterYAnchor),
                close.WidthAnchor.ConstraintEqualTo(14),
                _bg.HeightAnchor.ConstraintEqualTo(26),
            });

            var pad = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            pad.AddSubview(_bg);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _bg.LeadingAnchor.ConstraintEqualTo(pad.LeadingAnchor, 3),
                _bg.TrailingAnchor.ConstraintEqualTo(pad.TrailingAnchor, -3),
                _bg.CenterYAnchor.ConstraintEqualTo(pad.CenterYAnchor),
            });
            View = pad;
            SetActive(false);
        }

        public void SetTitle(string name) => _label.StringValue = name;

        public void SetActive(bool active)
            => _bg.Layer!.BackgroundColor = (active ? NSColor.SelectedContentBackground : NSColor.Clear).CGColor;

        private sealed class ClickView : NSView
        {
            private readonly Action _onClick;
            public ClickView(Action onClick) => _onClick = onClick;
            public override void MouseDown(NSEvent theEvent) => _onClick();
        }
    }
}
