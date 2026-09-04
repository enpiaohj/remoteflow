using System.Windows.Controls;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 「导入 / 导出」页面。V0.1 支持 CSV，导出文件不含任何密码或私钥。
/// </summary>
public partial class ImportExportPage : UserControl
{
    public ImportExportPage() => InitializeComponent();
}
