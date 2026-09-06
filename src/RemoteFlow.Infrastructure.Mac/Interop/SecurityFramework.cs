using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteFlow.Infrastructure.Mac.Interop;

/// <summary>
/// Security.framework 的 Keychain Services 最小互操作层。
/// <para>
/// 只暴露 RemoteFlow 需要的四个操作：<c>SecItemAdd</c> / <c>SecItemCopyMatching</c> /
/// <c>SecItemUpdate</c> / <c>SecItemDelete</c>，以及构造查询字典所需的常量。
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class SecurityFramework
{
    private const string Library = "/System/Library/Frameworks/Security.framework/Security";

    private static readonly nint LibraryHandle = NativeLibrary.Load(Library);

    // ── OSStatus ──────────────────────────────────────────────────

    internal const int ErrSecSuccess = 0;
    internal const int ErrSecItemNotFound = -25300;
    internal const int ErrSecDuplicateItem = -25299;

    /// <summary>用户在钥匙串授权对话框上点了「拒绝」。</summary>
    internal const int ErrSecAuthFailed = -25293;

    /// <summary>调用方未被授权访问该条目（通常是二进制签名变化导致 ACL 不匹配）。</summary>
    internal const int ErrSecInteractionNotAllowed = -25308;

    // ── 常量（Security.framework 导出的 CFStringRef 全局变量）────
    //
    // 这些是指针类型的全局变量，需要读取变量本身的值（解引用一次），
    // 与 CoreFoundation 里的结构体常量不同。

    internal static nint SecClass => Constant("kSecClass");
    internal static nint SecClassGenericPassword => Constant("kSecClassGenericPassword");
    internal static nint SecAttrService => Constant("kSecAttrService");
    internal static nint SecAttrAccount => Constant("kSecAttrAccount");
    internal static nint SecAttrAccessible => Constant("kSecAttrAccessible");
    internal static nint SecAttrAccessibleAfterFirstUnlock => Constant("kSecAttrAccessibleAfterFirstUnlock");
    internal static nint SecValueData => Constant("kSecValueData");
    internal static nint SecReturnData => Constant("kSecReturnData");
    internal static nint SecMatchLimit => Constant("kSecMatchLimit");
    internal static nint SecMatchLimitOne => Constant("kSecMatchLimitOne");
    internal static nint SecAttrAccess => Constant("kSecAttrAccess");

    private static nint Constant(string name)
        => Marshal.ReadIntPtr(NativeLibrary.GetExport(LibraryHandle, name));

    /// <summary>
    /// 创建「所有应用可访问、不弹授权框」的 SecAccessRef（<c>trustedlist = NULL</c>）。
    /// <para>
    /// 用途：ad-hoc 签名的开发构建每次 hash 变化会让默认「仅创建方」ACL 失效，
    /// 触发钥匙串授权框。桌面单用户应用里「钥匙串本身已解锁」即是安全边界，
    /// 因此新写入的条目放开 app 限制，避免每次构建都要重新授权。
    /// 失败（如权限不足）返回 <c>nint.Zero</c>，调用方回落默认 ACL。
    /// </para>
    /// </summary>
    internal static nint CreateOpenAccess(string label)
    {
        try
        {
            using var descriptor = CoreFoundation.CreateString(label);
            var rc = SecAccessCreate(descriptor.Handle, nint.Zero, out var access);
            return rc == ErrSecSuccess ? access : nint.Zero;
        }
        catch
        {
            // 老 API 不可用等：回落默认 ACL。
            return nint.Zero;
        }
    }

    [LibraryImport(Library)]
    private static partial int SecAccessCreate(nint descriptor, nint trustedList, out nint accessRef);

    // ── CoreFoundation 布尔常量（供 kSecReturnData 使用）─────────

    private static readonly nint CoreFoundationHandle = NativeLibrary.Load(
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");

    internal static nint CFBooleanTrue
        => Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundationHandle, "kCFBooleanTrue"));

    // ── Keychain Services ─────────────────────────────────────────

    [LibraryImport(Library)]
    internal static partial int SecItemAdd(nint attributes, nint result);

    [LibraryImport(Library)]
    internal static partial int SecItemCopyMatching(nint query, out nint result);

    [LibraryImport(Library)]
    internal static partial int SecItemUpdate(nint query, nint attributesToUpdate);

    [LibraryImport(Library)]
    internal static partial int SecItemDelete(nint query);

    /// <summary>
    /// 把 OSStatus 翻译成面向用户的中文说明。
    /// <b>不得包含任何 Secret 内容</b>，仅描述失败原因。
    /// </summary>
    internal static string DescribeStatus(int status) => status switch
    {
        ErrSecSuccess => "成功",
        ErrSecItemNotFound => "钥匙串中不存在该条目",
        ErrSecDuplicateItem => "钥匙串中已存在同名条目",
        ErrSecAuthFailed => "钥匙串授权被拒绝",
        ErrSecInteractionNotAllowed => "当前上下文不允许访问钥匙串（可能钥匙串已锁定或签名不匹配）",
        _ => $"钥匙串操作失败（OSStatus {status}）",
    };
}
