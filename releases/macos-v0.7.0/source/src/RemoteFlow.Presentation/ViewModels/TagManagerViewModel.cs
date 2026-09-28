using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>标签管理对话框里的一行。纯展示，编辑通过 <see cref="TagManagerViewModel.EditTagCommand"/> 弹子对话框完成。</summary>
public sealed partial class TagRowViewModel(Tag tag) : ObservableObject
{
    public Guid Id { get; } = tag.Id;

    public string Name { get; } = tag.Name;

    public string Color { get; } = tag.Color;

    public string Description { get; } = tag.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

/// <summary>
/// 标签管理对话框的 ViewModel。承担标签的新建 / 编辑 / 删除，
/// 每次操作后重新从数据库拉取，保证列表与其它已打开界面最终一致。
/// </summary>
public sealed partial class TagManagerViewModel : ObservableObject
{
    private readonly ConnectionService _connections;
    private readonly IDialogService _dialogs;

    public TagManagerViewModel(ConnectionService connections, IDialogService dialogs, IReadOnlyList<Tag> initialTags)
    {
        _connections = connections;
        _dialogs = dialogs;
        Tags = new ObservableCollection<TagRowViewModel>(initialTags
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new TagRowViewModel(t)));
        _isEmpty = Tags.Count == 0;
    }

    public ObservableCollection<TagRowViewModel> Tags { get; }

    [ObservableProperty]
    private bool _isEmpty;

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var prompt = new TagEditorPrompt("新建标签");
        if (await _dialogs.EditTagAsync(prompt) is not { } result)
        {
            return;
        }

        try
        {
            await _connections.CreateTagAsync(result.Name, result.Color, result.Description);
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.ShowMessageAsync("无法新建标签", ex.Message, DialogKind.Error);
        }
    }

    [RelayCommand]
    private async Task EditTagAsync(TagRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var prompt = new TagEditorPrompt("编辑标签", row.Name, row.Color, row.Description);
        if (await _dialogs.EditTagAsync(prompt) is not { } result)
        {
            return;
        }

        try
        {
            await _connections.UpdateTagAsync(row.Id, result.Name, result.Color, result.Description);
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.ShowMessageAsync("无法保存标签", ex.Message, DialogKind.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteTagAsync(TagRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除标签",
            $"确定删除标签「{row.Name}」吗？已应用到连接上的这个标签会一并移除，连接本身不受影响。",
            "删除",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await _connections.DeleteTagAsync(row.Id);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var tags = await _connections.GetTagsAsync();
        Tags.Clear();
        foreach (var tag in tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Tags.Add(new TagRowViewModel(tag));
        }
        IsEmpty = Tags.Count == 0;
    }
}
