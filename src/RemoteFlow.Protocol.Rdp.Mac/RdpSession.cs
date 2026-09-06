using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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
    private readonly RdpFrameBuffer _frames = new();

    // 委托实例必须存字段（防 GC；C 侧长期持有函数指针）。
    private readonly NativeRdp.FrameCallback _frameCb;
    private readonly NativeRdp.StateCallback _stateCb;
    private readonly nint _frameCbPtr;
    private readonly nint _stateCbPtr;

    private nint _handle;
    private ResolvedCredential? _credential;
    private int _lastX;
    private int _lastY;
    private int _buttons;
    private TaskCompletionSource<bool>? _connectGate;

    public RdpSession(SessionRequest request, ILoggerFactory loggerFactory)
    {
        _request = request;
        _logger = loggerFactory.CreateLogger<RdpSession>();
        _credential = request.Credential;

        _frameCb = OnFrame;
        _stateCb = OnState;
        _frameCbPtr = Marshal.GetFunctionPointerForDelegate(_frameCb);
        _stateCbPtr = Marshal.GetFunctionPointerForDelegate(_stateCb);
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
            _handle = NativeRdp.rf_rdp_create(nint.Zero, _frameCbPtr, _stateCbPtr);
            if (_handle == nint.Zero)
            {
                Fail(ConnectionErrorCode.ComponentUnavailable, "RDP 组件初始化失败（libremoteflow_rdp / FreeRDP 缺失？）");
                return;
            }

            var p = _request.Profile;
            var width = p.Rdp.DisplayMode == RdpDisplayMode.FixedResolution ? p.Rdp.DesktopWidth : 1920;
            var height = p.Rdp.DisplayMode == RdpDisplayMode.FixedResolution ? p.Rdp.DesktopHeight : 1080;

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

            // 等 native 状态回调把结果送来（连上 / 失败），或调用方取消 / 超时。
            using var reg = cancellationToken.Register(() => _connectGate?.TrySetResult(false));
            await _connectGate.Task.ConfigureAwait(false);

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
    private void OnFrame(nint user, nint bgrx, int width, int height, int stride)
        => _frames.Ingest(bgrx, width, height, stride);

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
                var msg = message == nint.Zero ? "RDP 连接失败" : Marshal.PtrToStringUTF8(message) ?? "RDP 连接失败";
                Fail(ConnectionErrorCode.Unknown, msg);
                _connectGate?.TrySetResult(false);
                break;
        }
    }

    private void Fail(ConnectionErrorCode code, string message)
    {
        ErrorCode = code;
        ErrorMessage = message;
        SetState(ConnectionState.Failed);
    }
}
