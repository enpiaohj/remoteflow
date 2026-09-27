using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>自定义分组编辑器：名称与语义图标一次完成。</summary>
public partial class GroupEditorDialog : Window, INotifyPropertyChanged
{
    private string _groupName = string.Empty;
    private OrganizationIconInfo _selectedIcon = GroupIconCatalog.CustomOptions[0];

    private GroupEditorDialog(GroupEditorPrompt prompt)
    {
        Title = prompt.Title;
        GroupName = prompt.InitialName;
        Subtitle = prompt.ParentName is { Length: > 0 } parent ? $"将创建在「{parent}」下" : string.Empty;
        IconOptions = GroupIconCatalog.CustomOptions;
        SelectedIcon = IconOptions.FirstOrDefault(x => x.Key == GroupIconCatalog.NormalizeCustom(prompt.InitialIcon))
            ?? IconOptions[0];

        InitializeComponent();
        DataContext = this;

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

    public string Subtitle { get; }

    public string GroupName
    {
        get => _groupName;
        set => SetField(ref _groupName, value);
    }

    public IReadOnlyList<OrganizationIconInfo> IconOptions { get; }

    public OrganizationIconInfo SelectedIcon
    {
        get => _selectedIcon;
        set => SetField(ref _selectedIcon, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public GroupEditorResult? Result { get; private set; }

    public static GroupEditorResult? Prompt(Window? owner, GroupEditorPrompt prompt)
    {
        var dialog = new GroupEditorDialog(prompt)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var name = GroupName.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "分组名称不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new GroupEditorResult(name, GroupIconCatalog.NormalizeCustom(SelectedIcon.Key));
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
