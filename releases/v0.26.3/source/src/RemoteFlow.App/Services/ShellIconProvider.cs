using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RemoteFlow.App.Services;

/// <summary>
/// 取 Windows 资源管理器同款的文件 / 文件夹图标（按扩展名，不需要文件真实存在）。
/// 远端文件只有名字，没有本地路径：用 <c>SHGFI_USEFILEATTRIBUTES</c> 按「虚拟文件属性」查询系统的文件类型图标。
/// 结果按扩展名缓存并冻结，可跨线程使用；查询失败返回 null，由调用方回退到矢量图标。
/// </summary>
public static class ShellIconProvider
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;

    private const string DirectoryKey = "<dir>";
    private const string NoExtensionKey = "<file>";

    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? Get(string name, bool isDirectory)
    {
        var key = isDirectory ? DirectoryKey : CacheKey(name);
        return Cache.GetOrAdd(key, static (k, state) => Load(state.Name, state.IsDirectory), (Name: name, IsDirectory: isDirectory));
    }

    private static string CacheKey(string name)
    {
        var extension = Path.GetExtension(name);
        return string.IsNullOrEmpty(extension) ? NoExtensionKey : extension;
    }

    private static BitmapSource? Load(string name, bool isDirectory)
    {
        // 目录与无扩展名文件用固定占位名，避免远端文件名里的特殊字符影响查询。
        var probe = isDirectory ? "folder" : Path.GetExtension(name) is { Length: > 0 } ext ? "file" + ext : "file";
        var info = new ShFileInfo();
        var result = SHGetFileInfo(
            probe,
            isDirectory ? FileAttributeDirectory : FileAttributeNormal,
            ref info,
            (uint)Marshal.SizeOf<ShFileInfo>(),
            ShgfiIcon | ShgfiLargeIcon | ShgfiUseFileAttributes);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
