//------------------------------------------------------------------------------
// 由 Strings.resx 手工维护的强类型访问器（不使用 ResXFileCodeGenerator，
// 保持仓库内代码可读、可 diff）。新增键时在 resx 与本文件同步添加一个属性。
//------------------------------------------------------------------------------
using System.Globalization;
using System.Resources;

namespace RemoteFlow.App.Resources;

/// <summary>
/// 界面文案的强类型访问入口。
/// <para>
/// V0.1 仅内置简体中文（<c>Strings.resx</c> 为中性资源）。后续语言只需新增
/// <c>Strings.en.resx</c> 等附属资源文件，并在启动时按 <see cref="AppSettings.Language"/>
/// 设置 <see cref="CultureInfo.CurrentUICulture"/> 即可，无需改动调用点。
/// </para>
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("RemoteFlow.App.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>按键取文案；缺失时回退为键名本身，便于发现未翻译项。</summary>
    public static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string AppName => Get("App_Name");
    public static string AppTagline => Get("App_Tagline");

    public static string OK => Get("Common_OK");
    public static string Cancel => Get("Common_Cancel");
    public static string Save => Get("Common_Save");
    public static string Delete => Get("Common_Delete");
    public static string Edit => Get("Common_Edit");
    public static string Connect => Get("Common_Connect");
    public static string Close => Get("Common_Close");
    public static string Refresh => Get("Common_Refresh");
    public static string Known => Get("Common_Known");

    public static string NavHome => Get("Nav_Home");
    public static string NavConnections => Get("Nav_Connections");
    public static string NavFavorites => Get("Nav_Favorites");
    public static string NavRecent => Get("Nav_Recent");
    public static string NavCredentials => Get("Nav_Credentials");
    public static string NavImportExport => Get("Nav_ImportExport");
    public static string NavSettings => Get("Nav_Settings");

    public static string ConnectionNew => Get("Connection_New");
    public static string ConnectionEdit => Get("Connection_Edit");
    public static string ConnectionSaveAndConnect => Get("Connection_SaveAndConnect");
    public static string ConnectionSearchPlaceholder => Get("Connection_Search_Placeholder");

    public static string SessionConnecting => Get("Session_Connecting");
    public static string SessionConnected => Get("Session_Connected");
    public static string SessionDisconnected => Get("Session_Disconnected");
    public static string SessionFailed => Get("Session_Failed");
    public static string SessionReconnect => Get("Session_Reconnect");
    public static string SessionFullScreen => Get("Session_FullScreen");
    public static string SessionSendCtrlAltDel => Get("Session_SendCtrlAltDel");

    public static string HostKeyFirstConnectTitle => Get("HostKey_FirstConnect_Title");
    public static string HostKeyChangedTitle => Get("HostKey_Changed_Title");
    public static string HostKeyTrust => Get("HostKey_Trust");
    public static string HostKeyReject => Get("HostKey_Reject");
}
