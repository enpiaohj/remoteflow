using System.IO;
using Microsoft.Web.WebView2.Core;

namespace RemoteFlow.App.Services;

/// <summary>
/// 进程内共享的 WebView2 核心环境（<see cref="CoreWebView2Environment"/>）。
/// <para>
/// WebView2 运行时按“环境”复用浏览器子进程：多个 WebView 共享同一个环境时，
/// 它们落到同一批 msedgewebview2 进程，而不是每个会话各自拉起一套。
/// 本类用 Acquire/Release 引用计数管理共享环境的获取与归还：会话视图初始化时
/// <see cref="AcquireAsync"/>（计数 +1），释放自己持有的 WebView2 后
/// <see cref="Release"/>（计数 -1）。应用退出时调用 <see cref="Shutdown"/> 收尾，
/// 不按进程名强杀浏览器进程——所有 WebView/Controller 释放后由运行时自行退出。
/// </para>
/// </summary>
public sealed class SharedWebView2Environment
{
    /// <summary>进程级单例。会话视图与应用退出清理都经由它访问共享环境。</summary>
    public static SharedWebView2Environment Instance { get; } = new();

    private readonly object _gate = new();

    /// <summary>共享环境的惰性创建任务；同一任务被所有获取者共同等待。</summary>
    private Task<CoreWebView2Environment>? _environmentTask;

    /// <summary>共享环境首次创建时使用的用户数据目录，后续获取必须一致。</summary>
    private string? _environmentUserDataFolder;

    /// <summary>尚未归还的获取次数。</summary>
    private int _acquireCount;

    /// <summary>已调用 <see cref="Shutdown"/>，此后不再接受新的获取。</summary>
    private bool _shutdown;

    private SharedWebView2Environment()
    {
    }

    /// <summary>
    /// 获取共享环境，引用计数 +1。首次调用时按 <paramref name="userDataFolder"/>
    /// 惰性创建（沿用 <c>%LOCALAPPDATA%\RemoteFlow\WebView2</c>）。
    /// 调用方释放自己持有的 WebView2 后必须调用 <see cref="Release"/> 归还引用。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 已用不同用户数据目录建立共享环境，或环境已随 <see cref="Shutdown"/> 关闭。
    /// </exception>
    public async Task<CoreWebView2Environment> AcquireAsync(string userDataFolder)
    {
        Task<CoreWebView2Environment> task;

        lock (_gate)
        {
            if (_shutdown)
            {
                throw new InvalidOperationException("WebView2 共享环境已随应用退出关闭，不能再获取。");
            }

            if (_environmentUserDataFolder is not null
                && !string.Equals(_environmentUserDataFolder, userDataFolder, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"共享 WebView2 环境已绑定用户数据目录 {_environmentUserDataFolder}，" +
                    $"不能用 {userDataFolder} 再次获取。");
            }

            if (_environmentTask is null)
            {
                Directory.CreateDirectory(userDataFolder);
                _environmentUserDataFolder = userDataFolder;
                _environmentTask = CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);
            }

            // 只有真正注册/复用了共享 task 的获取才计入引用计数：
            // 若 Directory.CreateDirectory / CreateAsync 同步抛错，异常会在计数前离开 lock，不会留下无人归还的计数。
            _acquireCount++;
            task = _environmentTask;
        }

        try
        {
            return await task;
        }
        catch
        {
            // 创建失败时归还本次计数，并清掉失败任务，允许下一次获取重新尝试。
            Release();

            lock (_gate)
            {
                if (ReferenceEquals(_environmentTask, task))
                {
                    _environmentTask = null;
                    _environmentUserDataFolder = null;
                }
            }

            throw;
        }
    }

    /// <summary>归还一次共享环境引用，引用计数 -1。幂等，计数不会降到 0 以下。</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_acquireCount > 0)
            {
                _acquireCount--;
            }
        }
    }

    /// <summary>
    /// 应用退出收尾时调用：标记关闭、丢弃共享环境引用并清零计数，阻止后续获取。
    /// 不按进程名强杀浏览器进程；会话关闭时各视图已 Dispose 并 Release 自己的
    /// WebView2/Controller，浏览器进程随后由 WebView2 运行时自行退出。幂等。
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _shutdown = true;
            _environmentTask = null;
            _environmentUserDataFolder = null;
            _acquireCount = 0;
        }
    }
}
