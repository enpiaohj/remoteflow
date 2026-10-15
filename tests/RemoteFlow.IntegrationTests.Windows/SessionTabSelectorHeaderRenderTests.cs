using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteFlow.App.Views.Sessions;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 收起态头部的真实渲染留档：按应用资源实际渲染成 PNG，便于人工核对视觉。
/// <para>
/// 覆盖关键状态 × 浅色 / 深色，输出到测试结果目录（不入库）。
/// 与 <see cref="SessionTabSelectorHeaderLayoutTests"/> 的数值断言互补：
/// 那边保证尺寸正确，这边保证看起来正确——深色下「黑底黑字」这类问题只有看图才发现得了。
/// </para>
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class SessionTabSelectorHeaderRenderTests
{
    [Fact]
    public void 渲染收起态头部各状态供人工核对()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var scenarios = new[]
                {
                    ("工作台 · 0 个会话", "Ui.Sessions", "活动连接", null, null),
                    ("工作台 · 5 个会话", "Ui.Sessions", "活动连接", "5", null),
                    ("选中会话 · 1 个（无角标）", "Ui.ProtocolSsh", "北京-跳板机", null, "已连接"),
                    ("选中会话 · 5 个", "Ui.ProtocolSsh", "web-01.prod.internal", "5", "已连接"),
                    ("选中会话 · 12 个（长名截断）", "Ui.ProtocolRdp", "上海-生产-数据库-主节点-01", "12", "连接中"),
                    ("选中会话 · 99+", "Ui.ProtocolVnc", "db-02.staging", "99+", "已连接"),
                    ("文件传输 Tab", "Ui.FileTransfer", "传输 · web-01", "5", null),
                };

                var outputs = new List<string>();
                foreach (var (dark, name) in new[]
                         {
                             (false, "session-tab-selector-header-light.png"),
                             (true, "session-tab-selector-header-dark.png"),
                         })
                {
                    UiTestHost.UsePalette(dark);
                    var output = Path.Combine(AppContext.BaseDirectory, name);
                    Render(output, scenarios);
                    outputs.Add(output);
                }

                // 切主题真的生效了吗？两个主题下品牌色必须不同，
                // 否则「深色留档」其实还是浅色，图会骗人。
                UiTestHost.UsePalette(false);
                var lightBrand = ((SolidColorBrush)System.Windows.Application.Current!.FindResource("Brand.Default")).Color;
                UiTestHost.UsePalette(true);
                var darkBrand = ((SolidColorBrush)System.Windows.Application.Current.FindResource("Brand.Default")).Color;
                Assert.NotEqual(lightBrand, darkBrand);

                foreach (var output in outputs)
                {
                    Assert.True(File.Exists(output), $"未生成渲染图：{output}");
                    Assert.True(new FileInfo(output).Length > 0, $"渲染图为空：{output}");
                }
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }
        });
    }

    private static void Render(string path, (string Label, string Icon, string Title, string? Count, string? State)[] scenarios)
    {
        const double padL = 24;
        const double padT = 44;
        const double rowH = 62;
        const double width = 240;

        var height = padT + scenarios.Length * rowH + 24;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                (Brush)System.Windows.Application.Current!.FindResource("Bg.Base"),
                null,
                new Rect(0, 0, width + padL * 2, height));

            var labelTypeface = new Typeface(
                (FontFamily)System.Windows.Application.Current.FindResource("UiFontDisplay"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            var y = padT;
            foreach (var (label, icon, title, count, state) in scenarios)
            {
                var selector = Build(icon, title, count, state);

                var text = new FormattedText(
                    label,
                    System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight,
                    labelTypeface,
                    11,
                    (Brush)System.Windows.Application.Current.FindResource("Text.Secondary"),
                    1.0);
                dc.DrawText(text, new Point(padL, y - 18));

                // 直接把渲染好的控件画进 DrawingContext（保留主题笔刷与真实排版）。
                dc.DrawRectangle(
                    new VisualBrush(selector) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null,
                    new Rect(padL, y, width, 36));

                y += rowH;
            }
        }

        var bitmap = new RenderTargetBitmap(
            (int)(width + padL * 2), (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>按给定状态构造选择器：直接驱动代码后置的头部刷新路径。</summary>
    private static SessionTabSelector Build(string icon, string title, string? count, string? state)
    {
        var selector = new SessionTabSelector();
        selector.Measure(new Size(240, 36));
        selector.Arrange(new Rect(0, 0, 240, 36));
        selector.UpdateLayout();

        ((System.Windows.Shapes.Path)selector.FindName("HeaderIcon")).Data =
            RemoteFlow.App.Converters.UiIconResources.Find(icon);
        ((TextBlock)selector.FindName("HeaderTitle")).Text = title;

        var badge = (Border)selector.FindName("HeaderCountBadge");
        var close = (Button)selector.FindName("HeaderClose");
        var dot = (System.Windows.Shapes.Ellipse)selector.FindName("HeaderDot");

        if (count is not null)
        {
            ((TextBlock)selector.FindName("HeaderCountText")).Text = count;
            badge.Visibility = Visibility.Visible;
        }

        // 会话态（有状态点）才有关闭按钮；工作台态无。
        if (state is not null)
        {
            dot.Visibility = Visibility.Visible;
            dot.Fill = (Brush)System.Windows.Application.Current!.FindResource(
                state == "已连接" ? "Status.Info" : "Status.Warning");
            close.Visibility = Visibility.Visible;
        }
        else
        {
            dot.Visibility = Visibility.Collapsed;
            close.Visibility = Visibility.Collapsed;
        }

        selector.Measure(new Size(240, 36));
        selector.Arrange(new Rect(0, 0, 240, 36));
        selector.UpdateLayout();
        return selector;
    }
}
