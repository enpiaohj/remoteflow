using System.Runtime.InteropServices;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 把 <see cref="IFrameSource"/> 的 BGRA32 帧按显示帧率贴到一个 <see cref="CALayer"/>。
/// VNC / RDP 两个会话视图共用。
///
/// <para><b>为什么单独抽出来做优化：</b>原先每帧都在
/// <c>GCHandle.Alloc(Pinned)</c> 钉住整块帧缓冲（1080p ≈ 8MB）、
/// <c>CGColorSpace.CreateDeviceRGB()</c> 新建色彩空间、
/// <c>new CGDataProvider(byte[])</c> 把整帧再拷进 native —— 30fps 下这些都是纯浪费。
/// 这里：色彩空间静态缓存；用两块常驻 native 缓冲乒乓，
/// <c>CGDataProvider</c> 直接引用缓冲不拷贝；不可见时暂停。</para>
///
/// <para><b>乒乓缓冲的正确性：</b>写 inactive 那块 → 建 CGImage 直接引用它 →
/// <c>layer.Contents = img</c>。上一帧的 CGImage 在这一步被 CoreAnimation 释放，
/// 而我们要再写回那块缓冲至少是下一帧（≥33ms 后）的事，绝不会边显示边覆写。</para>
/// </summary>
internal sealed class LayerFramePump : IDisposable
{
    private const double FrameIntervalMs = 33; // ≈30fps

    // 设备 RGB 色彩空间是不可变的，全进程一份即可。
    private static readonly CGColorSpace DeviceRgb = CGColorSpace.CreateDeviceRGB();

    private readonly IFrameSource _frames;
    private readonly CALayer _target;
    private readonly Action _onFirstFrame;

    private NSTimer? _timer;
    private FrameSize _pendingSize;
    private bool _firstFrameSeen;

    private readonly nint[] _buf = new nint[2];
    private int _bufBytes;
    private int _write;
    private int _w;
    private int _h;

    /// <summary>当前帧尺寸（像素）。会话视图做坐标换算用。</summary>
    public int Width => _w;

    public int Height => _h;

    public LayerFramePump(IFrameSource frames, CALayer target, Action onFirstFrame)
    {
        _frames = frames;
        _target = target;
        _onFirstFrame = onFirstFrame;
        _pendingSize = frames.FrameSize;
        _frames.FrameSizeChanged += OnFrameSizeChanged;
    }

    /// <summary>视图不可见（切到别的 Tab、窗口最小化、被完全遮挡）时置 true —— 定时器仍转但直接跳过。</summary>
    public bool Paused { get; set; }

    public void Start()
    {
        Stop();
        _timer = NSTimer.CreateRepeatingScheduledTimer(
            TimeSpan.FromMilliseconds(FrameIntervalMs), _ => Tick());
        NSRunLoop.Main.AddTimer(_timer, NSRunLoopMode.Common);
    }

    public void Stop()
    {
        _timer?.Invalidate();
        _timer = null;
    }

    private void OnFrameSizeChanged(object? sender, FrameSize size) => _pendingSize = size;

    private void Realloc(int w, int h)
    {
        var bytes = checked(w * h * 4);
        for (var i = 0; i < 2; i++)
        {
            if (_buf[i] != 0)
            {
                Marshal.FreeHGlobal(_buf[i]);
            }

            _buf[i] = Marshal.AllocHGlobal(bytes);
        }

        _bufBytes = bytes;
        _w = w;
        _h = h;
        _write = 0;
    }

    private void Tick()
    {
        if (Paused)
        {
            return;
        }

        if (!_pendingSize.IsEmpty)
        {
            if (_pendingSize.Width > 0 && _pendingSize.Height > 0)
            {
                Realloc(_pendingSize.Width, _pendingSize.Height);
            }

            _pendingSize = FrameSize.Empty;
        }

        if (_bufBytes == 0)
        {
            return;
        }

        var dst = _buf[_write];
        // IFrameSource 约定：无新帧直接返回 false，不拷贝。
        if (!_frames.TryCopyLatestFrame(dst, _bufBytes, _w * 4, _w, _h))
        {
            return;
        }

        // provider 直接引用 dst（不拷贝、不接管所有权）。
        using var provider = new CGDataProvider(dst, _bufBytes);
        var image = new CGImage(
            _w, _h, 8, 32, _w * 4, DeviceRgb,
            CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.NoneSkipFirst,
            provider, null, false, CGColorRenderingIntent.Default);
        _target.Contents = image; // CoreAnimation retain 新帧、释放上一帧
        image.Dispose();          // 本侧不再持有；图层已 retain
        _write ^= 1;

        if (!_firstFrameSeen)
        {
            _firstFrameSeen = true;
            _onFirstFrame();
        }
    }

    public void Dispose()
    {
        Stop();
        _frames.FrameSizeChanged -= OnFrameSizeChanged;

        // 先摘掉图层持有的最后一帧，再释放缓冲。用 BeginInvokeOnMainThread 让释放
        // 排到当前 CoreAnimation 事务提交之后，避免合成线程还在读这块内存。
        _target.Contents = null;
        var toFree = new[] { _buf[0], _buf[1] };
        _buf[0] = _buf[1] = 0;
        _bufBytes = 0;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            foreach (var p in toFree)
            {
                if (p != 0)
                {
                    Marshal.FreeHGlobal(p);
                }
            }
        });
    }
}
