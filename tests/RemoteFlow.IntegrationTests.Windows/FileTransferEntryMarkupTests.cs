using System.IO;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>文件传输入口的界面接线：菜单项、侧栏列与分隔条、编辑器分区（读取源码标记，不启动窗口）。</summary>
public sealed class FileTransferEntryMarkupTests
{
    [Theory]
    [InlineData("Views/Pages/ConnectionsPage.xaml")]
    [InlineData("Views/Pages/HomePage.xaml")]
    public void RowContextMenu_HasFileTransferEntry(string relative)
    {
        var markup = ReadApp(relative);

        Assert.Contains("Tag=\"filetransfer\" Header=\"文件传输…\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionHostView_HostsSidebarWithSplitter()
    {
        var markup = ReadApp("Views/Sessions/SessionHostView.xaml");

        Assert.Contains("<GridSplitter x:Name=\"FileTransferSplitter\"", markup, StringComparison.Ordinal);
        Assert.Contains("<local:FileTransferPanel x:Name=\"FileTransferPanelView\"", markup, StringComparison.Ordinal);
        Assert.Contains("ToggleFileTransferCommand", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_HostsFileTransferAsWorkspaceTab()
    {
        var markup = ReadApp("Views/MainWindow.xaml");

        // 标签头、标题栏工具条占位、内容区各一份模板；内容区直接铺面板。
        var templates = markup.Split("DataType=\"{x:Type vm:FileTransferTabViewModel}\"").Length - 1;
        Assert.Equal(3, templates);
        Assert.Contains("<sessions:FileTransferPanel DataContext=\"{Binding FileTransfer}\"", markup, StringComparison.Ordinal);
        // 背景随主题（由面板自身的 Bg.Layer 提供），不再套会话深色舞台与外框。
        Assert.DoesNotContain("Bg.Session}\" Padding=\"12\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionToolsBar_HasFileTransferToggle()
    {
        Assert.Contains("ToggleFileTransferCommand", ReadApp("Views/Sessions/SessionToolsBar.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionEditor_HasFileTransferSectionForRdp()
    {
        var markup = ReadApp("Views/Dialogs/ConnectionEditorDialog.xaml");

        Assert.Contains("Text=\"文件传输\"", markup, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedFileTransferChannel}\"", markup, StringComparison.Ordinal);
        Assert.Contains("FileTransfer.SftpPort", markup, StringComparison.Ordinal);
    }

    private static string ReadApp(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "RemoteFlow.App", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RemoteFlow.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("未找到仓库根目录。");
    }
}
