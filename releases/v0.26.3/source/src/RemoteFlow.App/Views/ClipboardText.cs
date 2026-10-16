using System.Windows;

namespace RemoteFlow.App.Views;

/// <summary>菜单里「复制地址 / 复制用户名」等动作共用的剪贴板写入。</summary>
public static class ClipboardText
{
    /// <summary>写入文本；空文本不处理。剪贴板被其它程序占用等偶发失败不打扰用户，返回 false。</summary>
    public static bool TryCopy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
