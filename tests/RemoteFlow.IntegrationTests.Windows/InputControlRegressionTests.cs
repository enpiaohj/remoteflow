using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteFlow.App.Views;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>输入框样式回归：文字缩进、密码框大写锁定提示。</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class InputControlRegressionTests
{
    /// <summary>
    /// 样式里的 Padding 只能生效一次。WPF 已把 Control.Padding 自动应用到 PART_ContentHost，
    /// 模板再给它绑 Margin=Padding，文字就缩进到 3 + 2×Padding（Padding=10 时 ~22px，
    /// 看起来像每个文本框开头多了空格）。
    /// </summary>
    [Theory]
    [InlineData("Input.TextBox", 0)]
    [InlineData("Input.TextBox", 10)]
    [InlineData("Input.PasswordBox", 10)]
    public void TextIndent_AppliesPaddingOnce(string styleKey, double padding)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                Control box = styleKey == "Input.PasswordBox"
                    ? new PasswordBox { Password = "WWW" }
                    : new TextBox { Text = "WWW" };
                box.Style = (Style)System.Windows.Application.Current!.Resources[styleKey];
                box.Padding = new Thickness(padding, 0, padding, 0);
                box.Width = 300;

                var left = FirstDarkColumn(box);

                // 单次应用：3（边框 + 文字固有偏移）+ Padding；双次应用会是 3 + 2×Padding。
                Assert.InRange(left, 3 + padding - 1, 3 + padding + 1);
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }
        });
    }

    [Fact]
    public void PasswordBox_ShowsCapsLockHint_OnlyWhenFocusedAndCapsLockOn()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            var original = CapsLockIndicator.CapsLockProbe;
            try
            {
                var box = new PasswordBox
                {
                    Style = (Style)System.Windows.Application.Current!.Resources["Input.PasswordBox"],
                    Width = 300,
                };
                Layout(box);

                Assert.True(CapsLockIndicator.GetIsMonitored(box));
                var hint = (FrameworkElement)box.Template.FindName("CapsLockHint", box);
                var host = (FrameworkElement)box.Template.FindName("PART_ContentHost", box);
                Assert.Equal(Visibility.Collapsed, hint.Visibility);

                // Caps Lock 开 + 有焦点 → 显示，并给内容宿主让出右侧空间。
                CapsLockIndicator.CapsLockProbe = () => true;
                CapsLockIndicator.Refresh(box, hasKeyboardFocus: true);
                Layout(box);
                Assert.True(CapsLockIndicator.GetIsActive(box));
                Assert.Equal(Visibility.Visible, hint.Visibility);
                Assert.True(host.Margin.Right > 0);

                // 失去焦点：同页其它密码框不应同时亮提示。
                CapsLockIndicator.Refresh(box, hasKeyboardFocus: false);
                Assert.False(CapsLockIndicator.GetIsActive(box));
                Layout(box);
                Assert.Equal(Visibility.Collapsed, hint.Visibility);
                Assert.Equal(0, host.Margin.Right);

                // Caps Lock 关：有焦点也不显示。
                CapsLockIndicator.CapsLockProbe = () => false;
                CapsLockIndicator.Refresh(box, hasKeyboardFocus: true);
                Assert.False(CapsLockIndicator.GetIsActive(box));
            }
            finally
            {
                CapsLockIndicator.CapsLockProbe = original;
                UiTestHost.RestoreAppResources();
            }
        });
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(300, 40));
        element.Arrange(new Rect(0, 0, 300, 40));
        element.UpdateLayout();
    }

    /// <summary>离屏渲染控件，返回第一列含深色文字像素的 x。</summary>
    private static int FirstDarkColumn(FrameworkElement element)
    {
        Layout(element);
        var bitmap = new RenderTargetBitmap(300, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[300 * 40 * 4];
        bitmap.CopyPixels(pixels, 300 * 4, 0);

        for (var x = 3; x < 300; x++)
        {
            for (var y = 6; y < 34; y++)
            {
                var i = (y * 300 + x) * 4;
                // 只认不透明的深色像素：控件范围之外的透明像素 RGB 也是 0。
                if (pixels[i + 3] == 255 && pixels[i] < 110 && pixels[i + 1] < 110 && pixels[i + 2] < 110)
                {
                    return x;
                }
            }
        }

        return -1;
    }
}
