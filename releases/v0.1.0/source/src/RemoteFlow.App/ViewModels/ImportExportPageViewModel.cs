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
    IDialogService dialogs,
    ILogger<ImportExportPageViewModel> logger) : ObservableObject
{
    private const string CsvFilter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";

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
}
