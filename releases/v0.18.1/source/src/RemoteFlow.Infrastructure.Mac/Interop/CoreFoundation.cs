using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteFlow.Infrastructure.Mac.Interop;

/// <summary>
/// CoreFoundation 最小互操作层。
/// <para>
/// Keychain 的 <c>SecItem*</c> 系列 API 全部以 CF 对象（CFString / CFData / CFDictionary）
/// 传参与返回，因此需要这一层来构造与释放它们。
/// </para>
/// <para>
/// <b>内存规则（Create Rule）：</b>名字里带 <c>Create</c> 或 <c>Copy</c> 的函数返回的对象
/// 由调用方负责 <see cref="CFRelease"/>。本层统一用 <see cref="CFObject"/> 包装以保证释放。
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class CoreFoundation
{
    private const string Library = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>CFAllocatorRef 传 null 表示使用默认分配器。</summary>
    internal static readonly nint DefaultAllocator = nint.Zero;

    [LibraryImport(Library)]
    internal static partial void CFRelease(nint cf);

    // 数组一律以指针传递：LibraryImport 源生成器不对托管数组做隐式封送
    // （SYSLIB1051），而 CF 的原生签名本来就是指针，直接对应更清晰。

    /// <summary>chars 为 UTF-16 码元序列（CF 的 UniChar 即 UInt16）。</summary>
    [LibraryImport(Library)]
    internal static unsafe partial nint CFStringCreateWithCharacters(nint alloc, ushort* chars, nint numChars);

    [LibraryImport(Library)]
    internal static unsafe partial nint CFDataCreate(nint alloc, byte* bytes, nint length);

    [LibraryImport(Library)]
    internal static partial nint CFDataGetBytePtr(nint theData);

    [LibraryImport(Library)]
    internal static partial nint CFDataGetLength(nint theData);

    [LibraryImport(Library)]
    internal static unsafe partial nint CFDictionaryCreate(
        nint allocator,
        nint* keys,
        nint* values,
        nint numValues,
        nint keyCallBacks,
        nint valueCallBacks);

    /// <summary>CFDictionary 的标准 key 回调集（保留 / 释放 / 比较）。</summary>
    internal static nint KeyCallBacks => GetGlobal("kCFTypeDictionaryKeyCallBacks");

    /// <summary>CFDictionary 的标准 value 回调集。</summary>
    internal static nint ValueCallBacks => GetGlobal("kCFTypeDictionaryValueCallBacks");

    private static readonly nint LibraryHandle = NativeLibrary.Load(Library);

    /// <summary>
    /// 取导出的全局变量地址本身（不解引用）。
    /// <c>kCFTypeDictionaryKeyCallBacks</c> 是结构体而非指针，需要的是它的地址。
    /// </summary>
    private static nint GetGlobal(string name) => NativeLibrary.GetExport(LibraryHandle, name);

    /// <summary>
    /// CF 对象的 RAII 包装。CF 的 Create/Copy 语义要求调用方释放，
    /// 手工 <c>CFRelease</c> 极易在异常路径上漏掉，统一交给 <c>using</c>。
    /// </summary>
    internal readonly struct CFObject : IDisposable
    {
        public CFObject(nint handle) => Handle = handle;

        public nint Handle { get; }

        public bool IsValid => Handle != nint.Zero;

        public void Dispose()
        {
            if (Handle != nint.Zero)
            {
                CFRelease(Handle);
            }
        }
    }

    /// <summary>把 .NET 字符串转成 CFString（UTF-16，CF 的原生表示）。</summary>
    internal static unsafe CFObject CreateString(string value)
    {
        fixed (char* chars = value)
        {
            return new CFObject(CFStringCreateWithCharacters(DefaultAllocator, (ushort*)chars, value.Length));
        }
    }

    /// <summary>把字节数组转成 CFData（CF 会复制内容，调用方缓冲可随后清零）。</summary>
    internal static unsafe CFObject CreateData(byte[] bytes)
    {
        fixed (byte* pointer = bytes)
        {
            return new CFObject(CFDataCreate(DefaultAllocator, pointer, bytes.Length));
        }
    }

    /// <summary>把 CFData 读回托管字节数组。传入句柄的所有权不转移。</summary>
    internal static byte[] ReadData(nint cfData)
    {
        if (cfData == nint.Zero)
        {
            return [];
        }

        var length = (int)CFDataGetLength(cfData);
        if (length <= 0)
        {
            return [];
        }

        var pointer = CFDataGetBytePtr(cfData);
        var buffer = new byte[length];
        Marshal.Copy(pointer, buffer, 0, length);
        return buffer;
    }

    /// <summary>用给定键值对构造 CFDictionary。键值句柄的所有权不转移。</summary>
    internal static unsafe CFObject CreateDictionary(nint[] keys, nint[] values)
    {
        if (keys.Length != values.Length)
        {
            throw new ArgumentException("键与值的数量必须一致。", nameof(values));
        }

        fixed (nint* keyPointer = keys)
        fixed (nint* valuePointer = values)
        {
            return new CFObject(CFDictionaryCreate(
                DefaultAllocator, keyPointer, valuePointer, keys.Length, KeyCallBacks, ValueCallBacks));
        }
    }
}
