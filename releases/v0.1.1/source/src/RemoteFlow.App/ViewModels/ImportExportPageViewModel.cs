using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteFlow.App.Services;
using RemoteFlow.Application.Services;

namespace RemoteFlow.App.ViewModels;

/// <summary>
/// 「导入 / 导出」页面。V0.1 支持 CSV。
/// <para>
/// <b>安全约束：导出文件不包含任何密码或私钥</b>，界面上必须把这一点讲清楚，
/// 避免用户误以为导出文件可以直接用于迁移凭据。
/// </para>
/// </summary>
public sealed partial class ImportExportPageViewModel(
    ImportExportService importExport,
    CredentialBackupService credentialBackup,
    IDialogService dialogs,
    ILogger<ImportExportPageViewModel> logger) : ObservableObject
{
    private const string CsvFilter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";
    private const string BackupFilter = "RemoteFlow 加密备份 (*.rfbackup)|*.rfbackup|所有文件 (*.*)|*.*";

    /// <summary>最近一次导入的逐行错误信息。</summary>
    public ObservableCollection<string> ImportMessages { get; } = [];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasMessages;

    /// <summary>导入完成后需要通知外部刷新连接列表。</summary>
    public event EventHandler? DataImported;

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = dialogs.PickFileToSave(
            "导出连接列表",
            CsvFilter,
            $"RemoteFlow-连接列表-{DateTime.Now:yyyy-MM-dd}.csv");

        if (path is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await importExport.ExportAsync(path);
            StatusMessage = $"已导出到 {path}";
            await dialogs.ShowMessageAsync(
                "导出完成",
                $"连接列表已导出到：\n{path}\n\n出于安全考虑，导出文件不包含任何密码或私钥。",
                DialogKind.Success);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导出连接列表失败");
            StatusMessage = "导出失败。";
            await dialogs.ShowMessageAsync("导出失败", $"无法写入文件：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = dialogs.PickFileToOpen("导入连接列表", CsvFilter);
        if (path is null)
        {
            return;
        }

        IsBusy = true;
        ImportMessages.Clear();

        try
        {
            var result = await importExport.ImportAsync(path);

            foreach (var message in result.Errors)
            {
                ImportMessages.Add(message);
            }

            HasMessages = ImportMessages.Count > 0;
            StatusMessage = $"导入完成：成功 {result.Imported} 条，跳过 {result.Skipped} 条。";

            DataImported?.Invoke(this, EventArgs.Empty);

            await dialogs.ShowMessageAsync(
                "导入完成",
                $"成功导入 {result.Imported} 条连接，跳过 {result.Skipped} 条。\n\n" +
                "CSV 中的 CredentialName 只用于关联已存在的凭据；未匹配到的连接需要手工指定凭据后才能使用。",
                result.Skipped > 0 ? DialogKind.Warning : DialogKind.Success);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导入连接列表失败");
            StatusMessage = "导入失败。";
            await dialogs.ShowMessageAsync("导入失败", $"无法读取文件：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 凭据加密备份 ──────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportCredentialsAsync()
    {
        var password = await dialogs.PromptPasswordAsync(
            "设置备份口令",
            "凭据备份用你设的口令加密。口令弱等于没加密，口令丢了文件就打不开——请用一个只有你知道、且记得住的强口令。",
            confirm: true);

        if (password is null)
        {
            return;
        }

        var path = dialogs.PickFileToSave(
            "导出凭据",
            BackupFilter,
            $"RemoteFlow-凭据-{DateTime.Now:yyyy-MM-dd}.rfbackup");

        if (path is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var count = await credentialBackup.ExportAsync(path, password);
            StatusMessage = $"已导出 {count} 条凭据到 {path}";
            await dialogs.ShowMessageAsync(
                "导出完成",
                $"已把 {count} 条凭据导出到：\n{path}\n\n" +
                "这个文件包含你的密码和私钥，只是用刚才的口令加密。请离线妥善保管，不要随普通文件同步或上传。",
                DialogKind.Success);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导出凭据失败");
            StatusMessage = "导出失败。";
            await dialogs.ShowMessageAsync("导出失败", $"无法写入文件：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportCredentialsAsync()
    {
        var path = dialogs.PickFileToOpen("导入凭据", BackupFilter);
        if (path is null)
        {
            return;
        }

        var password = await dialogs.PromptPasswordAsync(
            "输入备份口令",
            "输入导出这个文件时设置的口令。",
            confirm: false);

        if (password is null)
        {
            return;
        }

        IsBusy = true;
        ImportMessages.Clear();
        try
        {
            var report = await credentialBackup.ImportAsync(path, password);

            if (report.Failure != CredentialBackupImportFailure.None)
            {
                var reason = report.Failure switch
                {
                    CredentialBackupImportFailure.WrongPasswordOrCorrupted => "口令错误，或文件已损坏 / 被篡改。没有导入任何数据。",
                    CredentialBackupImportFailure.UnsupportedVersion => "这个备份文件的版本比当前程序新，无法读取。请升级 RemoteFlow 后再试。",
                    _ => "这不是一个 RemoteFlow 凭据备份文件。",
                };
                StatusMessage = "导入失败。";
                await dialogs.ShowMessageAsync("导入失败", reason, DialogKind.Error);
                return;
            }

            foreach (var name in report.SkippedNames)
            {
                ImportMessages.Add($"「{name}」已存在，已跳过。");
            }

            HasMessages = ImportMessages.Count > 0;
            StatusMessage = $"导入完成：新增 {report.Imported} 条，跳过 {report.SkippedNames.Count} 条。";

            DataImported?.Invoke(this, EventArgs.Empty);

            await dialogs.ShowMessageAsync(
                "导入完成",
                $"新增 {report.Imported} 条凭据，跳过 {report.SkippedNames.Count} 条同名的。",
                report.SkippedNames.Count > 0 ? DialogKind.Warning : DialogKind.Success);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导入凭据失败");
            StatusMessage = "导入失败。";
            await dialogs.ShowMessageAsync("导入失败", $"处理文件时出错：{ex.Message}", DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
