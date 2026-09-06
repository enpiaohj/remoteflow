using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteFlow.Protocol.Rdp.Interop;

/// <summary>
/// 承载 Microsoft RDP Client ActiveX 控件（mstscax.dll）的 WinForms 宿主。
/// <para>
/// .NET (Core) 没有 <c>aximp.exe</c> 生成 Interop 程序集的等价工具，
/// 因此这里直接派生 <see cref="AxHost"/> 并通过 IDispatch 后期绑定访问控件成员，
/// 避免手写数千行 COM 接口声明，同时保留完整的控件能力。
/// </para>
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class RdpAxHost(string clsid) : AxHost(clsid)
{
    /// <summary>
    /// 取得底层 ActiveX 对象。<b>句柄创建完成前返回 null</b>，
    /// 因此调用方必须等控件进入可视树后再访问。
    /// </summary>
    public object? ActiveXInstance => IsHandleCreated ? GetOcx() : null;
}

public enum RdpRemoteSessionAction
{
    TaskManager = 6
}

public readonly record struct RdpKeyStroke(int KeyData, bool IsKeyUp);

public static class RdpKeyboardSequence
{
    public static IReadOnlyList<RdpKeyStroke> TaskManager { get; } =
    [
        new(0x001D0001, IsKeyUp: false),
        new(0x002A0001, IsKeyUp: false),
        new(0x00010001, IsKeyUp: false),
        new(unchecked((int)0xC0010001), IsKeyUp: true),
        new(unchecked((int)0xC02A0001), IsKeyUp: true),
        new(unchecked((int)0xC01D0001), IsKeyUp: true)
    ];
}

/// <summary>mstscax 的 IMsTscNonScriptable 基础接口。</summary>
[ComImport]
[Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    void put_ClearTextPassword([In, MarshalAs(UnmanagedType.BStr)] string value);
    void put_PortablePassword([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_PortablePassword();

    void put_PortableSalt([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_PortableSalt();

    void put_BinaryPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_BinaryPassword();

    void put_BinarySalt([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_BinarySalt();

    void ResetPassword();
}

/// <summary>mstscax 的协议级远端键盘输入接口。</summary>
[ComImport]
[Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable : IMsTscNonScriptable
{
    new void put_ClearTextPassword([In, MarshalAs(UnmanagedType.BStr)] string value);
    new void put_PortablePassword([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    new string get_PortablePassword();

    new void put_PortableSalt([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    new string get_PortableSalt();

    new void put_BinaryPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    new string get_BinaryPassword();

    new void put_BinarySalt([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    new string get_BinarySalt();

    new void ResetPassword();
    void NotifyRedirectDeviceChange(UIntPtr wParam, nint lParam);
    void SendKeys(int numKeys, nint pbArrayKeyUp, nint plKeyData);
}

/// <summary>
/// RDP 客户端控件的事件接收接口（dispinterface <c>IMsTscAxEvents</c>）。
/// <para>
/// 只声明本产品关心的事件。COM 通过 <c>IDispatch::Invoke</c> 按 DISPID 分发，
/// 未声明的事件会返回 DISP_E_MEMBERNOTFOUND 并被控件忽略，这是受支持的用法。
/// </para>
/// </summary>
[ComVisible(true)]
[Guid(EventsInterfaceId)]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    /// <summary>
    /// 事件接收接口的 IID。
    /// <para>
    /// 该值经实机枚举控件连接点确认（<c>IConnectionPointContainer::EnumConnectionPoints</c>）。
    /// 注意它与文档中常被误引的 <c>...-C7C0DA05A9F8</c> 只有后半段不同，
    /// 用错会在 <c>FindConnectionPoint</c> 阶段得到 <c>CONNECT_E_NOCONNECTION (0x80040200)</c>，
    /// 表现为「控件能创建但收不到任何事件」。
    /// </para>
    /// </summary>
    public const string EventsInterfaceId = "336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6";

    /// <summary>开始建立连接。</summary>
    [DispId(1)]
    void OnConnecting();

    /// <summary>传输层已连接（尚未完成登录）。</summary>
    [DispId(2)]
    void OnConnected();

    /// <summary>登录完成，远程桌面可用。</summary>
    [DispId(3)]
    void OnLoginComplete();

    /// <summary>会话断开。<paramref name="discReason"/> 为 RDP 断开原因码。</summary>
    [DispId(4)]
    void OnDisconnected(int discReason);

    /// <summary>控件内部致命错误。</summary>
    [DispId(10)]
    void OnFatalError(int errorCode);

    /// <summary>登录失败。</summary>
    [DispId(23)]
    void OnLogonError(int lError);

    /// <summary>正在自动重连。</summary>
    [DispId(17)]
    void OnAutoReconnecting(int disconnectReason, int attemptCount, ref bool continueReconnecting);
}

/// <summary>
/// 本机 RDP ActiveX 控件版本探测。
/// <para>
/// Windows 各版本自带的 mstscax.dll 提供的最高控件版本不同，
/// 因此从高到低探测，取第一个已注册的版本，而不是硬编码某个 CLSID。
/// </para>
/// </summary>
public static class RdpControlLocator
{
    /// <summary>
    /// 候选 ProgID，按版本从高到低排列。
    /// <c>MsTscAx.MsTscAx.N</c> 是非脚本安全版本，允许在连接前设置明文密码，
    /// 这是应用内嵌 RDP 会话所必需的。
    /// </summary>
    private static readonly string[] CandidateProgIds =
    [
        "MsTscAx.MsTscAx.13",
        "MsTscAx.MsTscAx.12",
        "MsTscAx.MsTscAx.11",
        "MsTscAx.MsTscAx.10",
        "MsTscAx.MsTscAx.9",
        "MsTscAx.MsTscAx.8",
        "MsTscAx.MsTscAx.7"
    ];

    private static (string ProgId, Guid Clsid)? _cached;
    private static bool _probed;

    /// <summary>
    /// 探测并缓存本机<b>真正可用</b>的最高版本 RDP 控件。
    /// <para>
    /// 注意：注册表中存在 ProgID <b>不等于</b>该控件可以创建。
    /// 实测 Windows 11 (26200) 注册了 <c>MsTscAx.MsTscAx.13</c>，
    /// 但其类工厂返回 <c>CLASS_E_CLASSNOTAVAILABLE</c>，属于预留/残留注册。
    /// 若只按注册表挑选最高版本，会在控件进入可视树时才崩溃，且异常被
    /// WinForms 的线程异常对话框吞掉、极难定位。
    /// 因此这里对每个候选实际执行一次实例化验证，只接受能真正创建的版本。
    /// </para>
    /// </summary>
    public static (string ProgId, Guid Clsid)? Locate()
    {
        if (_probed)
        {
            return _cached;
        }

        _probed = true;

        foreach (var progId in CandidateProgIds)
        {
            var type = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (type?.GUID is not { } clsid || clsid == Guid.Empty)
            {
                continue;
            }

            object? probe = null;
            try
            {
                probe = Activator.CreateInstance(type);
            }
            catch (Exception)
            {
                // 该版本不可用，继续尝试下一个较低版本。
                continue;
            }
            finally
            {
                if (probe is not null)
                {
                    Marshal.ReleaseComObject(probe);
                }
            }

            _cached = (progId, clsid);
            return _cached;
        }

        _cached = null;
        return null;
    }
}
