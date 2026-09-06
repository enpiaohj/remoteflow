namespace RemoteFlow.Core.Models;

/// <summary>
/// RDP 协议专项参数。对应「新建连接 → 高级设置」中的 RDP 分组。
/// </summary>
public sealed class RdpOptions
{
    /// <summary>登录域。留空表示不指定域。</summary>
    public string Domain { get; set; } = string.Empty;

    public RdpDisplayMode DisplayMode { get; set; } = RdpDisplayMode.FitToWindow;

    /// <summary>固定分辨率宽度，仅 <see cref="RdpDisplayMode.FixedResolution"/> 时生效。</summary>
    public int DesktopWidth { get; set; } = 1920;

    /// <summary>固定分辨率高度，仅 <see cref="RdpDisplayMode.FixedResolution"/> 时生效。</summary>
    public int DesktopHeight { get; set; } = 1080;

    /// <summary>颜色深度（bpp）。</summary>
    public int ColorDepth { get; set; } = 32;

    /// <summary>剪贴板重定向。</summary>
    public bool RedirectClipboard { get; set; } = true;

    /// <summary>音频重定向到本机。</summary>
    public bool RedirectAudio { get; set; }

    /// <summary>
    /// 麦克风重定向（把本机麦克风交给远端录制）。依赖本机 / 远端对 RDP 8+ 音频捕获的支持；
    /// 连接时按 <c>IMsRdpClientAdvancedSettings7.AudioCaptureRedirectionMode</c> 尽力设置，
    /// 控件版本过低或不支持时跳过（TrySet 容错），不会让连接失败。
    /// </summary>
    public bool RedirectMicrophone { get; set; }

    /// <summary>打印机重定向。</summary>
    public bool RedirectPrinters { get; set; }

    /// <summary>本地驱动器重定向。默认关闭，避免无意暴露本机文件。</summary>
    public bool RedirectDrives { get; set; }

    /// <summary>使用全部显示器（多显示器增强项）。</summary>
    public bool UseMultimon { get; set; }

    /// <summary>连接成功后自动进入应用级全屏。默认关闭。</summary>
    public bool StartFullScreen { get; set; }

    /// <summary>启用网络级别身份验证（NLA）。</summary>
    public bool EnableNla { get; set; } = true;

    /// <summary>
    /// 连接质量预设（体验）。映射到 ActiveX <c>NetworkConnectionType</c>，
    /// 由远端按所选网络类型调整体验参数。<c>Auto</c> 为默认，不主动写入。
    /// </summary>
    public RdpConnectionQuality ConnectionQuality { get; set; } = RdpConnectionQuality.Auto;

    public RdpOptions Clone() => (RdpOptions)MemberwiseClone();
}

/// <summary>
/// SSH 协议专项参数。
/// </summary>
public sealed class SshOptions
{
    /// <summary>终端类型，影响远端 TERM 环境变量。</summary>
    public string TerminalType { get; set; } = "xterm-256color";

    /// <summary>字符编码。默认 UTF-8，保证中文正常显示。</summary>
    public string Encoding { get; set; } = "UTF-8";

    /// <summary>KeepAlive 间隔（秒）。0 表示关闭。</summary>
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>连接超时（秒）。</summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>登录后自动执行的命令，留空表示不执行。</summary>
    public string InitialCommand { get; set; } = string.Empty;

    public SshOptions Clone() => (SshOptions)MemberwiseClone();
}

/// <summary>
/// VNC / macOS Screen Sharing 协议专项参数。
/// </summary>
public sealed class VncOptions
{
    public VncScaleMode ScaleMode { get; set; } = VncScaleMode.FitToWindow;

    /// <summary>只读模式：不向远端发送键鼠输入。</summary>
    public bool ViewOnly { get; set; }

    /// <summary>共享连接：允许其他客户端同时连接，不踢掉已有会话。</summary>
    public bool SharedConnection { get; set; } = true;

    /// <summary>
    /// 剪贴板同步（远端 → 本机）：接收远端复制的内容并写入本机剪贴板。
    /// 仅覆盖协议官方承诺的 server → client 方向；本机 → 远端发送暂不支持。
    /// <para>
    /// 类型默认 <c>false</c> 是存量保守：老连接反序列化时缺字段取类型默认，
    /// 不会因升级而静默开启“远端 → 本机剪贴板覆盖”。新建连接由连接编辑器从
    /// <see cref="AppSettings.VncDefaultClipboardToLocal"/> 显式赋值（全局默认 true）。
    /// </para>
    /// </summary>
    public bool ClipboardToLocal { get; set; } = false;

    /// <summary>连接超时（秒）。</summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    public VncOptions Clone() => (VncOptions)MemberwiseClone();
}
