using AppKit;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Settings;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac.Host;

/// <summary>
/// <see cref="IDialogService"/> 的 AppKit 实现。
/// <para>
/// 确认 / 提示 / Host Key 用 <see cref="NSAlert"/>；连接编辑用 App-Modal 的
/// <see cref="ConnectionEditorSheet"/> 绑共享 VM。凭据 / 标签 / 文件选择器逐步补齐。
/// </para>
/// </summary>
public sealed class AppKitDialogService : IDialogService
{
    private readonly ILogger<AppKitDialogService> _logger;
    private readonly ConnectionService _connections;
    private readonly DefaultGroupResolver _defaultGroup;
    private readonly ICredentialRepository _credentials;
    private readonly AppSettings _settings;

    public AppKitDialogService(
        ILogger<AppKitDialogService> logger,
        ConnectionService connections,
        DefaultGroupResolver defaultGroup,
        ICredentialRepository credentials,
        AppSettings settings)
    {
        _logger = logger;
        _connections = connections;
        _defaultGroup = defaultGroup;
        _credentials = credentials;
        _settings = settings;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool isDanger = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var alert = new NSAlert
            {
                MessageText = title,
                InformativeText = message,
                AlertStyle = isDanger ? NSAlertStyle.Critical : NSAlertStyle.Informational,
            };
            alert.AddButton(confirmText);
            alert.AddButton("取消");
            tcs.SetResult(alert.RunModal() == (nint)NSAlertButtonReturn.First);
        });
        return tcs.Task;
    }

    public Task ShowMessageAsync(string title, string message, DialogKind kind = DialogKind.Info)
    {
        var tcs = new TaskCompletionSource();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            new NSAlert
            {
                MessageText = title,
                InformativeText = message,
                AlertStyle = kind switch
                {
                    DialogKind.Error => NSAlertStyle.Critical,
                    DialogKind.Warning => NSAlertStyle.Warning,
                    _ => NSAlertStyle.Informational,
                },
            }.RunModal();
            tcs.SetResult();
        });
        return tcs.Task;
    }

    public Task<bool> ConfirmHostKeyAsync(SshHostKeyVerificationContext context)
    {
        var changed = context.IsMismatch;
        var title = changed ? "⚠️ 主机密钥已变化" : "首次连接：确认主机密钥";
        var body = changed
            ? $"{context.Host}:{context.Port} 的密钥指纹与此前记录不一致，可能存在中间人攻击。\n\n" +
              $"此前：{context.KnownFingerprint}\n本次：{context.Fingerprint}\n\n仍要信任并连接吗？"
            : $"{context.Host}:{context.Port}\n算法 {context.KeyAlgorithm}\n指纹 {context.Fingerprint}\n\n信任该主机并记录指纹？";
        return ConfirmAsync(title, body, changed ? "仍然信任" : "信任", isDanger: changed);
    }

    public async Task<ConnectionEditorResult?> EditConnectionAsync(
        ConnectionProfile? existing, ProtocolType? preselectedProtocol = null)
    {
        // 下拉数据源先在后台取齐，再切主线程开对话框。
        var credentialList = await _credentials.GetAllAsync();
        var groups = await _connections.GetGroupsAsync();
        var tags = await _connections.GetTagsAsync();
        var defaultGroupId = existing is null ? await _defaultGroup.ResolveDefaultAsync() : (Guid?)null;

        var tcs = new TaskCompletionSource<ConnectionEditorResult?>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                var vm = new ConnectionEditorViewModel(
                    existing, credentialList, groups, tags, _settings, defaultGroupId, preselectedProtocol);
                var sheet = new ConnectionEditorSheet(vm);
                tcs.SetResult(sheet.Run());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "连接编辑器打开失败");
                tcs.SetResult(null);
            }
        });
        return await tcs.Task;
    }

    // ── 逐步补齐 ─────────────────────────────────────────────────

    public Task<CredentialEditorResult?> EditCredentialAsync(Credential? existing)
    {
        var tcs = new TaskCompletionSource<CredentialEditorResult?>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                var vm = new CredentialEditorViewModel(existing);
                var sheet = new CredentialEditorSheet(vm);
                tcs.SetResult(sheet.Run());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "凭据编辑器打开失败");
                tcs.SetResult(null);
            }
        });
        return tcs.Task;
    }

    public Task<string?> EditGroupNameAsync(GroupNamePrompt prompt)
    {
        var tcs = new TaskCompletionSource<string?>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var input = new AppKit.NSTextField(new CoreGraphics.CGRect(0, 0, 260, 24))
            {
                StringValue = prompt.InitialName,
            };
            var alert = new NSAlert
            {
                MessageText = prompt.Title,
                InformativeText = prompt.ParentName is { Length: > 0 } p ? $"上级分组：{p}" : string.Empty,
                AccessoryView = input,
            };
            alert.AddButton("确定");
            alert.AddButton("取消");
            alert.Window.InitialFirstResponder = input;
            var name = alert.RunModal() == (nint)NSAlertButtonReturn.First ? input.StringValue.Trim() : null;
            tcs.SetResult(string.IsNullOrEmpty(name) ? null : name);
        });
        return tcs.Task;
    }

    public Task<DefaultGroupOption?> PickDefaultGroupAsync(
        string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options)
    {
        var tcs = new TaskCompletionSource<DefaultGroupOption?>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            if (options.Count == 0)
            {
                tcs.SetResult(null);
                return;
            }

            var popup = new AppKit.NSPopUpButton(new CoreGraphics.CGRect(0, 0, 260, 26), pullsDown: false);
            foreach (var o in options)
            {
                popup.AddItem(o.Name);
            }
            popup.SelectItem(0);

            var alert = new NSAlert
            {
                MessageText = "选择新的默认分组",
                InformativeText = $"「{deletedDefaultName}」是当前默认新建连接分组，删除后新连接将进入所选分组。",
                AccessoryView = popup,
            };
            alert.AddButton("确定");
            alert.AddButton("取消");
            tcs.SetResult(alert.RunModal() == (nint)NSAlertButtonReturn.First
                ? options[(int)popup.IndexOfSelectedItem]
                : null);
        });
        return tcs.Task;
    }

    public Task<TagEditorResult?> EditTagAsync(TagEditorPrompt prompt) => NotYet<TagEditorResult?>("EditTagAsync");

    public Task<IReadOnlyList<Tag>> ManageTagsAsync() => NotYet<IReadOnlyList<Tag>>("ManageTagsAsync");

    public Task<string?> PromptPasswordAsync(string title, string message, bool confirm)
        => NotYet<string?>("PromptPasswordAsync");

    public string? PickFileToOpen(string title, string filter)
    {
        string? result = null;
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var panel = NSOpenPanel.OpenPanel;
            panel.Title = title;
            panel.CanChooseFiles = true;
            panel.CanChooseDirectories = false;
            panel.AllowsMultipleSelection = false;
            if (panel.RunModal() == 1)
            {
                result = panel.Url?.Path;
            }
        });
        return result;
    }

    public string? PickFileToSave(string title, string filter, string defaultFileName)
    {
        string? result = null;
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var panel = NSSavePanel.SavePanel;
            panel.Title = title;
            panel.NameFieldStringValue = defaultFileName;
            if (panel.RunModal() == 1)
            {
                result = panel.Url?.Path;
            }
        });
        return result;
    }

    public string? PickFolder(string title)
    {
        string? result = null;
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var panel = NSOpenPanel.OpenPanel;
            panel.Title = title;
            panel.CanChooseFiles = false;
            panel.CanChooseDirectories = true;
            panel.AllowsMultipleSelection = false;
            if (panel.RunModal() == 1)
            {
                result = panel.Url?.Path;
            }
        });
        return result;
    }

    public Task ShowAboutAsync() => ShowMessageAsync("RemoteFlow", "统一远程连接工作台（macOS）");

    public Task ShowConnectionTestAsync(ConnectionProfile profile) => NotYet<object?>("ShowConnectionTestAsync");

    private Task<T> NotYet<T>(string what)
    {
        _logger.LogWarning("IDialogService.{What} 尚未实现", what);
        return Task.FromResult<T>(default!);
    }
}
