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
    /// 创建「仅本应用可免确认访问」的 SecAccessRef。
    /// <para>
    /// <c>SecAccessCreate(descriptor, trustedlist: NULL, …)</c> 的语义是
    /// <b>只把创建该条目的应用加入信任列表</b>，而不是放开给所有应用——本方法早先叫
    /// <c>CreateOpenAccess</c> 并在注释里写成「所有应用可访问」，与 API 实际行为相反，
    /// 已按 Apple 文档更正（放开给所有应用需要 <c>SecACLSetContents</c> 把
    /// applicationList 置 NULL，本项目刻意不这么做：那等于任何本机程序都能静默读出远程主机密码）。
    /// </para>
    /// <para>
    /// 该 ACL 比对的是应用的 designated requirement。ad-hoc 签名的 DR 含 cdhash，
    /// 每次重新编译都会变，旧授权随即作废、逐条弹系统密码框；配上固定签名证书后
    /// DR 只含 bundle id + 证书主体，跨构建稳定，用户批准一次即长期有效
    /// （见 <c>RemoteFlow.App.Mac.csproj</c> 的 CodesignKey 段）。
    /// </para>
    /// <para>失败（如老 API 不可用）返回 <c>nint.Zero</c>，调用方回落系统默认 ACL。</para>
    /// </summary>
    internal static nint CreateAppOnlyAccess(string label)
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
