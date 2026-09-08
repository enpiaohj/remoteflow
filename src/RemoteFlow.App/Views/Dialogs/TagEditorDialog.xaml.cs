using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>新建 / 编辑标签的小对话框——名称、颜色（预设色板 + 自定义 Hex）、可选描述。</summary>
public partial class TagEditorDialog : Window
{
    /// <summary>预设色板，覆盖常见的标签语义色，同时给一个可以随手点的选项。</summary>
    private static readonly string[] PresetColors =
    [
        "#0F6CBD", "#3352CE", "#0E7C57", "#7A4FC4",
        "#C4342A", "#985900", "#0891B2", "#DB2777",
        "#B5179E", "#6B7280",
    ];

    private static readonly Regex HexColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private readonly List<Border> _swatches = [];
    private string _selectedColor;
    private bool _suppressColorInputSync;

    private TagEditorDialog(TagEditorPrompt prompt)
    {
        InitializeComponent();

        TitleText.Text = prompt.Title;
        NameInput.Text = prompt.InitialName;
        DescriptionInput.Text = prompt.InitialDescription;
        _selectedColor = HexColorPattern.IsMatch(prompt.InitialColor) ? prompt.InitialColor : PresetColors[0];

        BuildSwatches();
        ApplySelectedColor(fromSwatchClick: false);

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    /// <summary>用户填写的结果。<c>null</c> 表示取消。</summary>
    public TagEditorResult? Result { get; private set; }

    public static TagEditorResult? Prompt(Window? owner, TagEditorPrompt prompt)
    {
        var dialog = new TagEditorDialog(prompt)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void BuildSwatches()
    {
        foreach (var hex in PresetColors)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderThickness = new Thickness(2),
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                Tag = hex,
            };
            // 用 MouseLeftButtonDown（而不是 Up）并显式 Handled=true：
            // Border 不像 Button 会自动吞掉这个事件，不拦截的话它会一路冒泡到
            // 窗口级的 MouseLeftButtonDown（那个是用来拖动无边框窗口的），
            // DragMove() 一旦被触发就会整段接管这次按下-释放，色板的点击
            // 永远等不到 MouseUp——这正是「颜色无法选择」的真根因。
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                SelectColor(hex, fromSwatchClick: true);
                e.Handled = true;
            };
            _swatches.Add(swatch);
            SwatchList.Items.Add(swatch);
        }
    }

    private void SelectColor(string hex, bool fromSwatchClick)
    {
        _selectedColor = hex;
        ApplySelectedColor(fromSwatchClick);
    }

    private void ApplySelectedColor(bool fromSwatchClick)
    {
        var accent = (SolidColorBrush)FindResource("Brand.Default");

        foreach (var swatch in _swatches)
        {
            var isSelected = string.Equals((string)swatch.Tag, _selectedColor, StringComparison.OrdinalIgnoreCase);
            swatch.BorderBrush = isSelected ? accent : System.Windows.Media.Brushes.Transparent;
        }

        if (TryParseColor(_selectedColor, out var color))
        {
            PreviewBrush.Color = color;
        }

        // 点色板时同步回填 Hex 输入框；反过来在 OnColorInputChanged 里同步色板，
        // 用 _suppressColorInputSync 避免这两个方向互相触发死循环。
        if (fromSwatchClick || ColorInput.Text.Length == 0)
        {
            _suppressColorInputSync = true;
            ColorInput.Text = _selectedColor;
            _suppressColorInputSync = false;
        }
    }

    private void OnColorInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressColorInputSync)
        {
            return;
        }

        var text = ColorInput.Text.Trim();
        if (!HexColorPattern.IsMatch(text))
        {
            return;
        }

        _selectedColor = text;
        ApplySelectedColor(fromSwatchClick: false);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (FormatException)
        {
            color = Colors.Transparent;
            return false;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "标签名称不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var color = ColorInput.Text.Trim();
        if (!HexColorPattern.IsMatch(color))
        {
            ErrorText.Text = "颜色必须是 #RRGGBB 格式，或直接点选上方色板。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new TagEditorResult(name, color, DescriptionInput.Text.Trim());
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
