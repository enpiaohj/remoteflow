namespace RemoteFlow.Core.Models;

/// <summary>
/// RemoteFlow 支持的远程连接协议。V0.1 覆盖 RDP / SSH / VNC 三种。
/// </summary>
public enum ProtocolType
{
    Rdp = 0,
    Ssh = 1,
    Vnc = 2
}

/// <summary>
/// 凭据类型。决定 Vault 中实际存放的 Secret 形态与协议层的使用方式。
/// </summary>
public enum CredentialType
{
    /// <summary>Windows 域账号（Domain\Username + Password）。</summary>
    WindowsDomain = 0,

    /// <summary>本机账号（Username + Password）。</summary>
    LocalPassword = 1,

    /// <summary>SSH 口令登录。</summary>
    SshPassword = 2,

    /// <summary>SSH 私钥登录（私钥内容存 Vault，可选 Passphrase）。</summary>
    SshPrivateKey = 3,

    /// <summary>VNC 口令（RFB 协议仅需密码，无用户名）。</summary>
    VncPassword = 4
}

/// <summary>
/// 会话连接状态。
/// <para>
/// 正常推进：Idle → Connecting → Connected（连接成功后自动重连期间进入 Reconnecting）
/// → Disconnecting → Disconnected → Closed（终态）。任意连接阶段失败进入 Failed，
/// 失败后经统一关闭模板收敛到 Disconnected / Closed。
/// </para>
/// <para>会话为单次使用：已 Disconnected / Closed 后不允许再回到 Connected（重连 = 建新会话）。</para>
/// </summary>
public enum ConnectionState
{
    Idle = 0,
    Connecting = 1,
    Connected = 2,
    Disconnecting = 3,
    Disconnected = 4,
    Failed = 5,

    /// <summary>自动重连中（如 RDP 库断线重连）。可回到 Connected，也可被用户中断进入关闭流程。</summary>
    Reconnecting = 6,

    /// <summary>会话已终结（终态，不再变化）。供 SessionManager 从活动集合移除后标记，UI 据此删除 Tab。</summary>
    Closed = 7
}

/// <summary>连接历史的最终结果。</summary>
public enum ConnectionResult
{
    Success = 0,
    Failed = 1,
    Cancelled = 2
}

/// <summary>
/// 标准化连接错误码。协议层必须把底层异常映射为其中之一，
/// UI 才能给出可理解的中文提示，而不是直接抛出原始异常。
/// </summary>
public enum ConnectionErrorCode
{
    None = 0,

    /// <summary>无法解析主机名。</summary>
    HostNotFound,

    /// <summary>网络不可达 / 目标端口拒绝连接。</summary>
    NetworkUnreachable,

    /// <summary>连接超时。</summary>
    Timeout,

    /// <summary>认证失败（用户名、密码、私钥或域不正确）。</summary>
    AuthenticationFailed,

    /// <summary>连接配置引用的凭据不存在或已被删除。</summary>
    CredentialMissing,

    /// <summary>SSH Host Key 与已记录指纹不一致，可能存在中间人攻击。</summary>
    HostKeyMismatch,

    /// <summary>用户拒绝接受 Host Key。</summary>
    HostKeyRejected,

    /// <summary>协议协商失败（版本、加密套件、编码不兼容）。</summary>
    ProtocolNegotiationFailed,

    /// <summary>远端主动关闭会话。</summary>
    RemoteClosed,

    /// <summary>用户取消。</summary>
    Cancelled,

    /// <summary>本机缺少必要组件（如 RDP ActiveX 控件不可用）。</summary>
    ComponentUnavailable,

    /// <summary>未归类错误。</summary>
    Unknown
}

/// <summary>
/// RDP 显示模式。
/// </summary>
public enum RdpDisplayMode
{
    /// <summary>跟随窗口尺寸动态调整远程分辨率。</summary>
    FitToWindow = 0,

    /// <summary>使用固定分辨率，超出部分显示滚动条。</summary>
    FixedResolution = 1
}

/// <summary>
/// 编辑连接里的 RDP 分辨率预设。仅用于编辑器 UI 的组织与映射，
/// 不单独持久化——选择结果写回 <see cref="RdpOptions.DisplayMode"/> 与
/// <see cref="RdpOptions.DesktopWidth"/> / <see cref="RdpOptions.DesktopHeight"/>。
/// <para>
/// 不包含「自动」项：适应窗口是独立显示模式（<see cref="RdpDisplayMode.FitToWindow"/>），
/// 不再混入分辨率下拉（历史上 <c>Auto</c> 已被移除，无需为其保留成员）。
/// </para>
/// </summary>
public enum RdpDisplayResolution
{
    Res1280x720 = 1,
    Res1366x768 = 2,
    Res1600x900 = 3,
    Res1920x1080 = 4,
    Res2560x1440 = 5,

    /// <summary>自定义：由用户输入宽高。</summary>
    Custom = 6
}

/// <summary>
/// RDP「体验」里的连接质量预设。
/// <para>
/// 选择结果映射到 RDP ActiveX 的 <c>NetworkConnectionType</c>
/// （<c>IMsRdpClientAdvancedSettings7</c>），由远端据此调整体验参数。
/// <c>Auto</c> 表示不主动写入，交由控件与系统自动处理。
/// </para>
/// </summary>
public enum RdpConnectionQuality
{
    /// <summary>自动：不写入连接类型，交由控件 / 系统决定。</summary>
    Auto = 0,

    /// <summary>局域网（LAN）：对应 <c>CONNECTION_TYPE_LAN</c>（6）。</summary>
    Lan = 1,

    /// <summary>高速宽带：对应 <c>CONNECTION_TYPE_BROADBAND_HIGH</c>（4）。</summary>
    HighSpeed = 2,

    /// <summary>低带宽：对应 <c>CONNECTION_TYPE_BROADBAND_LOW</c>（2），提示远端降低体验开销。</summary>
    LowBandwidth = 3
}

/// <summary>
/// VNC 缩放模式。
/// </summary>
public enum VncScaleMode
{
    /// <summary>缩放画面以适应窗口。</summary>
    FitToWindow = 0,

    /// <summary>1:1 原始像素显示。</summary>
    Original = 1,

    /// <summary>拉伸填满窗口：等比不保留，横纵都拉满（比例不同会变形）。</summary>
    Fill = 2
}

/// <summary>
/// 启动后默认打开的页面。对应左侧导航的一级入口子集。
/// </summary>
public enum LandingPage
{
    Home = 0,
    Connections = 1,
    Favorites = 2,
    Recent = 3,
    Credentials = 4
}

/// <summary>
/// SSH 终端配色主题。作为「设置 → SSH」的全局默认，新建会话时套用。
/// </summary>
public enum SshTerminalTheme
{
    /// <summary>跟随应用主题（System / Light / Dark 的当前实际生效主题）。</summary>
    FollowApp = 0,

    /// <summary>Dark Gray（深灰，默认）：Background #1E1E1E，Foreground #D4D4D4。</summary>
    DarkGray = 1,

    /// <summary>纯黑背景。</summary>
    Black = 2,

    /// <summary>Navy（深蓝）。</summary>
    Navy = 3,

    /// <summary>Solarized Dark。</summary>
    SolarizedDark = 4,

    /// <summary>浅色。</summary>
    Light = 5
}
