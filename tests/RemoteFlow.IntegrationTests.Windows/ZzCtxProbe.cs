using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RemoteFlow.App.Views.Sessions;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

[Collection(WpfApplicationCollection.Name)]
public sealed class ZzCtxProbe
{
    [Fact]
    public void Probe()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            var sb = new StringBuilder();
            try
            {
                var window = new Window();
                var sessionMenu = new ContextMenu { Tag = "SESSION_MENU" };
                window.Resources["SessionTabContextMenu"] = sessionMenu;
                window.Resources["FileTransferTabContextMenu"] = new ContextMenu { Tag = "TRANSFER_MENU" };

                var selector = new SessionTabSelector();
                window.Content = selector;

                var list = (ListBox)selector.FindName("SessionsList");

                // 用假的行对象：容器样式与 DataTemplate 选择无关，这里只看容器 ContextMenu。
                list.ItemsSource = new object[] { new object(), new object() };

                window.Measure(new Size(400, 400));
                window.Arrange(new Rect(0, 0, 400, 400));
                window.UpdateLayout();

                var container = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                sb.AppendLine($"container null? {container is null}");
                sb.AppendLine($"container.ContextMenu null? {container?.ContextMenu is null}");
                sb.AppendLine($"container.ContextMenu.Tag = {container?.ContextMenu?.Tag}");
                sb.AppendLine($"is SessionTabContextMenu instance? {ReferenceEquals(container?.ContextMenu, sessionMenu)}");
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }

            File.WriteAllText(@"D:\AIProjects\Remoteflow\_Backup\ctxprobe.txt", sb.ToString());
        });
    }
}
