using System.Runtime.InteropServices;

namespace RemoteFlow.Infrastructure.Smb;

/// <summary>
/// SMB 通道用到的 Win32 / NetAPI 原生调用。全部为系统自带 DLL（mpr / netapi32），无第三方依赖。
/// <para>
/// <b>安全约束：</b>连接一律带 <see cref="ConnectTemporary"/>（不写入用户的持久化映射），
/// <b>绝不</b>带 CONNECT_INTERACTIVE（那会在出错时弹出系统凭据对话框，抢走界面控制权）。
/// </para>
/// </summary>
internal static partial class SmbNative
{
    /// <summary>NETRESOURCE.dwType：任意资源（连接到 IPC$ 只为建立会话认证，不映射盘符）。</summary>
    internal const uint ResourceTypeAny = 0;

    /// <summary>CONNECT_TEMPORARY：不持久化到用户配置，也不记入「记住的网络驱动器」。</summary>
    internal const uint ConnectTemporary = 0x00000004;

    internal const int MaxPreferredLength = -1;

    /// <summary>SHARE_INFO_1.shi1_type 的类型位：0 = 磁盘共享；高位 0x80000000 表示特殊（管理）共享。</summary>
    internal const uint ShareTypeMask = 0x0000FFFF;

    internal const uint ShareTypeDisk = 0;

    /// <summary>
    /// NETRESOURCEW。字符串字段用 <c>nint</c> 而不是 <c>string</c>：<c>[LibraryImport]</c> 不支持按引用传递
    /// 含字符串的结构体，由调用方用 <see cref="Marshal.StringToHGlobalUni(string)"/> 分配并在调用后释放。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public nint LocalName;
        public nint RemoteName;
        public nint Comment;
        public nint Provider;
    }

    /// <summary>SHARE_INFO_1：字符串字段是指向 NetAPI 缓冲区内字符串的指针，需在释放缓冲区前读出。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShareInfo1
    {
        public nint NetName;
        public uint Type;
        public nint Remark;
    }

    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WNetAddConnection2(
        ref NetResource netResource, string? password, string? userName, uint flags);

    [LibraryImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WNetCancelConnection2(
        string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    [LibraryImport("netapi32.dll", EntryPoint = "NetShareEnum", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int NetShareEnum(
        string? serverName,
        int level,
        out nint buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        ref int resumeHandle);

    [LibraryImport("netapi32.dll")]
    internal static partial int NetApiBufferFree(nint buffer);
}
