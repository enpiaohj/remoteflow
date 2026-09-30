using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 工作区里的「文件传输」Tab：不建立终端 / RDP 会话，直接对某台主机传文件。
/// 与 <see cref="SessionTabViewModel"/> 并列，但不是会话——不参与会话计数、全屏与工具条。
/// </summary>
public sealed partial class FileTransferTabViewModel : WorkspaceTabViewModel, IDisposable
{
    private readonly Func<FileTransferTabViewModel, Task> _closeCallback;

    public FileTransferTabViewModel(
        ConnectionProfile profile,
        FileTransferViewModel fileTransfer,
        Func<FileTransferTabViewModel, Task> closeCallback)
    {
        Profile = profile;
        FileTransfer = fileTransfer;
        _closeCallback = closeCallback;
        Title = $"传输 · {profile.Name}";
    }

    public ConnectionProfile Profile { get; }

    public FileTransferViewModel FileTransfer { get; }

    /// <summary>设备类型矢量图标资源键，与同一主机的会话 Tab 用同一套规则。</summary>
    public string DeviceIconKey =>
        (DeviceTypeCatalog.Resolve(Profile.DeviceType) ?? DeviceTypeCatalog.InferFromProtocol(Profile.Protocol)).IconResourceKey;

    public string TabToolTip => $"文件传输 · {Profile.Name}（{Profile.Host}）";

    [RelayCommand]
    private Task CloseAsync() => _closeCallback(this);

    public void Dispose() => FileTransfer.Dispose();
}
