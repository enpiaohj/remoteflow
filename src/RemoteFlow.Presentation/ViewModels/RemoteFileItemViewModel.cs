using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Core.FileTransfer;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>文件浏览器里的一行。<see cref="IsSelected"/> 由列表容器双向绑定，多选状态就存在这里。</summary>
public sealed partial class RemoteFileItemViewModel(RemoteFileEntry entry) : ObservableObject
{
    public RemoteFileEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsSymlink => Entry.IsSymlink;

    /// <summary>图标资源键（<c>Ui.*</c> 线性图标）。</summary>
    public string IconKey => Entry.IsDirectory ? "Ui.Folder" : "Ui.Document";

    /// <summary>文件大小；目录不显示。</summary>
    public string SizeText => Entry.IsDirectory ? string.Empty : ByteSizeFormatter.Format(Entry.Size);

    public string ModifiedText => Entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    public string PermissionsText => Entry.Permissions ?? string.Empty;

    /// <summary>行内第二行：「大小 · 修改时间」。目录只有时间；缺哪项就省略哪项，不留悬空分隔符。</summary>
    public string DetailText => string.Join("  ·  ", new[] { SizeText, ModifiedText }.Where(s => s.Length > 0));

    /// <summary>悬停提示：符号链接特别标注。</summary>
    public string ToolTipText => Entry.IsSymlink ? $"{Entry.Name}（符号链接）" : Entry.Name;

    [ObservableProperty]
    private bool _isSelected;
}
