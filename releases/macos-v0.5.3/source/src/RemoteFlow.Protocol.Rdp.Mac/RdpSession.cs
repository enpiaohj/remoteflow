using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Rdp.Mac;

/// <summary>
/// macOS 应用内嵌入式 RDP 会话。P/Invoke libremoteflow_rdp（FreeRDP 3.x 封装）。
/// 画面经 <see cref="Frames"/>（<see cref="IFrameSource"/>）交给 UI 层；输入经
/// <see cref="SendPointer"/> / <see cref="SendKey"/> 回传。
/// </summary>
public sealed class RdpSession : RemoteSessionBase
{
    private readonly SessionRequest _request;
    private readonly ILogger _logger;
    private readonly IHostKeyRepository _hostKeys;
    private readonly RdpFrameBuffer _frames = new();

    // 委托实例必须存字段（防 GC；C 侧长期持有函数指针）。
    private readonly NativeRdp.FrameCallback _frameCb;
    private readonly NativeRdp.StateCallback _stateCb;
    private readonly NativeRdp.CertCallback _certCb;
    private readonly NativeRdp.CursorCallback _cursorCb;
    private readonly nint _frameCbPtr;
    private readonly nint _stateCbPtr;
    private readonly nint _certCbPtr;
    private readonly nint _cursorCbPtr;

    /// <summary>远端光标形状变化（在 FreeRDP 线程上触发，处理方需自行封送 UI 线程）。</summary>
    public event Action<RdpCursor>? CursorChanged;

    private nint _handle;
    private ResolvedCredential? _credential;
    private int _lastX;
    private int _lastY;
    private int _buttons;
    private TaskCompletionSource<bool>? _connectGate;

    /// <summary>「适应窗口」模式下请求的桌面像素尺寸。UI 层在 <see cref="ConnectAsync"/> 前按显示区设。</summary>
    public (int Width, int Height)? PreferredSize { get; set; }

    /// <summary>连接后视图尺寸变化时调用（FreeRDP DynamicResolutionUpdate）。</summary>
    public void Resize(int width, int height)
    {
        if (_handle != nint.Zero && width > 0 && height > 0)
        {
            NativeRdp.rf_rdp_resize(_handle, width, height);
        }
    }

    public RdpSession(SessionRequest request, ILoggerFactory loggerFactory, IHostKeyRepository hostKeys)
    {
        _request = request;
        _logger = loggerFactory.CreateLogger<RdpSession>();
        _hostKeys = hostKeys;
        _credential = request.Credential;

        _frameCb = OnFrame;
        _stateCb = OnState;
        _certCb = OnCert;
        _cursorCb = OnCursor;
        _frameCbPtr = Marshal.GetFunctionPointerForDelegate(_frameCb);
        _stateCbPtr = Marshal.GetFunctionPointerForDelegate(_stateCb);
        _certCbPtr = Marshal.GetFunctionPointerForDelegate(_certCb);
        _cursorCbPtr = Marshal.GetFunctionPointerForDelegate(_cursorCb);
    }

    public override ProtocolType Protocol => ProtocolType.Rdp;

    public override ConnectionProfile Profile => _request.Profile;

    public IFrameSource Frames => _frames;

    // ── 连接 ────────────────────────────────────────────────────
    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Connecting);
        _connectGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _handle = NativeRdp.rf_rdp_create(nint.Zero, _frameCbPtr, _stateCbPtr, _certCbPtr, _cursorCbPtr);
            if (_handle == nint.Zero)
            {
                Fail(ConnectionErrorCode.ComponentUnavailable, "RDP 组件初始化失败（libremoteflow_rdp / FreeRDP 缺失？）");
                return;
            }

            var p = _request.Profile;
            int width, height;
            if (p.Rdp.DisplayMode == RdpDisplayMode.FixedResolution)
            {
                width = p.Rdp.DesktopWidth;
                height = p.Rdp.DesktopHeight;
            }
            else if (PreferredSize is { } pref)
            {
                // 适应窗口：按显示区实际像素请求桌面尺寸，消除上下黑边。
                width = pref.Width;
                height = pref.Height;
            }
            else
            {
                width = 1920;
                height = 1080;
            }

            var domain = string.IsNullOrEmpty(_credential?.Domain) ? p.Rdp.Domain : _credential!.Domain;
            var rc = NativeRdp.rf_rdp_connect(
                _handle, p.Host, p.Port,
                _credential?.Username, domain, _credential?.Password,
                width, height);

            // 凭据已交给 native，托管侧立即释放明文引用。
            _credential?.Dispose();
            _credential = null;

            if (rc != 0)
            {
                Fail(ConnectionErrorCode.Unknown, "RDP 连接线程启动失败");
                return;
            }

            // 等 native 状态回调把结果送来（连上 / 失败），或调用方取消，或兜底超时
            // （FreeRDP 自带 TCP/NLA 超时，正常都会先回调；此处仅防线程卡死）。
            using var reg = cancellationToken.Register(() => _connectGate?.TrySetResult(false));
            var completed = await Task.WhenAny(_connectGate.Task, Task.Delay(TimeSpan.FromSeconds(45))).ConfigureAwait(false);
            if (completed != _connectGate.Task)
            {
                _connectGate.TrySetResult(false);
            }

            if (State != ConnectionState.Connected && ErrorCode == ConnectionErrorCode.None)
            {
                Fail(ConnectionErrorCode.Timeout, "RDP 连接超时");
            }
        }
        catch (DllNotFoundException)
        {
            Fail(ConnectionErrorCode.ComponentUnavailable, "未找到 libremoteflow_rdp.dylib，请先 bash native/rdp/build.sh");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RDP 会话 {SessionId} 连接异常", SessionId);
            Fail(ConnectionErrorCode.Unknown, ex.Message);
        }
    }

    protected override ValueTask PerformTeardownAsync()
    {
        var h = Interlocked.Exchange(ref _handle, nint.Zero);
        if (h != nint.Zero)
        {
            try
            {
                NativeRdp.rf_rdp_destroy(h);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RDP 会话 {SessionId} teardown", SessionId);
            }
        }

        _frames.Dispose();
        _credential?.Dispose();
        _credential = null;
        return ValueTask.CompletedTask;
    }

    // ── 输入 ────────────────────────────────────────────────────
    /// <summary>指针事件。<paramref name="leftDown"/> / <paramref name="rightDown"/> /
    /// <paramref name="middleDown"/> 为当前按下状态，本方法据变化发 DOWN / release。</summary>
    public void SendPointer(int x, int y, bool leftDown, bool rightDown, bool middleDown)
    {
        if (_handle == nint.Zero || Profile.Rdp is null)
        {
            return;
        }

        _lastX = x;
        _lastY = y;

        // 移动
        NativeRdp.rf_rdp_send_pointer(_handle, x, y, NativeRdp.PtrFlagsMove);

        SendButton(NativeRdp.PtrFlagsButton1, leftDown, x, y);
        SendButton(NativeRdp.PtrFlagsButton2, rightDown, x, y);
        SendButton(NativeRdp.PtrFlagsButton3, middleDown, x, y);
    }

    private void SendButton(int buttonFlag, bool down, int x, int y)
    {
        var was = (_buttons & buttonFlag) != 0;
        if (was == down)
        {
            return;
        }

        if (down)
        {
            _buttons |= buttonFlag;
        }
        else
        {
            _buttons &= ~buttonFlag;
        }

        var flags = buttonFlag | (down ? NativeRdp.PtrFlagsDown : 0);
        NativeRdp.rf_rdp_send_pointer(_handle, x, y, flags);
    }

    public void SendWheel(int delta)
    {
        if (_handle != nint.Zero)
        {
            NativeRdp.rf_rdp_send_wheel(_handle, _lastX, _lastY, delta);
        }
    }

    /// <summary>RDP scancode（PC set 1）。<paramref name="extended"/> 对应 0xE0 前缀键。</summary>
    public void SendKey(int scancode, bool down, bool extended)
    {
        if (_handle != nint.Zero)
        {
            NativeRdp.rf_rdp_send_key(_handle, scancode, down ? 1 : 0, extended ? 1 : 0);
        }
    }

    public void SendUnicode(char c, bool down)
    {
        if (_handle != nint.Zero)
        {
            NativeRdp.rf_rdp_send_unicode(_handle, c, down ? 1 : 0);
        }
    }

    // ── C 回调 ──────────────────────────────────────────────────
    private void OnFrame(nint user, nint bgrx, int width, int height, int stride,
        int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight)
        => _frames.Ingest(bgrx, width, height, stride, dirtyX, dirtyY, dirtyWidth, dirtyHeight);

    private void OnCursor(nint user, nint rgba, int w, int h, int hotX, int hotY)
    {
        // 在 FreeRDP 线程上：rgba 缓冲随即可能被 C 侧 Pointer_Free 释放，必须同步拷出。
        RdpCursor cursor;
        if (rgba == nint.Zero)
        {
            cursor = w < 0 ? RdpCursor.Default : RdpCursor.Hidden;
        }
        else
        {
            var bytes = new byte[w * h * 4];
            Marshal.Copy(rgba, bytes, 0, bytes.Length);
            cursor = new RdpCursor(bytes, w, h, hotX, hotY);
        }

        try
        {
            CursorChanged?.Invoke(cursor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "光标回调处理失败");
        }
    }

    private void OnState(nint user, int state, nint message)
    {
        switch (state)
        {
            case 1:
                SetState(ConnectionState.Connected);
                _connectGate?.TrySetResult(true);
                break;
            case 2:
                SetState(ConnectionState.Disconnected);
                _connectGate?.TrySetResult(false);
                break;
            case 3:
                // 证书校验已把 ErrorCode 设为 HostKeyMismatch 时不覆盖（否则会丢失中间人警告语义）。
                if (ErrorCode == ConnectionErrorCode.None)
                {
                    var msg = message == nint.Zero ? "RDP 连接失败" : Marshal.PtrToStringUTF8(message) ?? "RDP 连接失败";
                    Fail(ConnectionErrorCode.Unknown, msg);
                }
                _connectGate?.TrySetResult(false);
                break;
        }
    }

    /// <summary>
    /// FreeRDP 证书校验回调（协议线程同步调用）。规则同 SSH Host Key（§7.4）：
    /// 首次见到 → TOFU 记录并放行；指纹一致 → 静默放行；<b>指纹变化 → 强拒绝，绝不静默接受</b>。
    /// SSH 走两步式弹窗；RDP 首版 TOFU（贴近 mstsc 默认信任并缓存的行为），变化仍硬失败。
    /// </summary>
    private int OnCert(nint user, nint hostPtr, int port, nint cnPtr, nint fpPtr, int changed)
    {
        var host = Marshal.PtrToStringUTF8(hostPtr);
        if (string.IsNullOrEmpty(host))
        {
            host = _request.Profile.Host;
        }

        var fingerprint = Marshal.PtrToStringUTF8(fpPtr) ?? string.Empty;

        try
        {
            var known = Task.Run(() => _hostKeys.GetAsync(host, port)).GetAwaiter().GetResult();

            if (known is null && changed == 0)
            {
                Task.Run(() => _hostKeys.SaveAsync(new SshHostKeyRecord
                {
                    HostKey = SshHostKeyRecord.BuildHostKey(host, port),
                    KeyAlgorithm = "RDP-TLS",
                    Fingerprint = fingerprint,
                    TrustedAt = DateTimeOffset.Now,
                })).GetAwaiter().GetResult();
                _logger.LogInformation("RDP 证书首次信任 {Host}:{Port}", host, port);
                return 1;
            }

            if (known is not null &&
                string.Equals(known.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return 1; // 一致 —— 静默放行
            }

            _logger.LogWarning(
                "RDP 服务器证书指纹与记录不一致 {Host}:{Port}（changed={Changed}）", host, port, changed);
            Fail(ConnectionErrorCode.HostKeyMismatch,
                $"{host}:{port} 的 RDP 服务器证书指纹与此前记录不一致，可能存在中间人攻击，连接已中止。");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RDP 证书校验异常 {Host}:{Port}", host, port);
            return 0;
        }
    }

    private void Fail(ConnectionErrorCode code, string message)
    {
        ErrorCode = code;
        ErrorMessage = message;
        SetState(ConnectionState.Failed);
    }
}
