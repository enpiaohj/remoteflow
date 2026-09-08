using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MarcusW.VncClient;
using MarcusW.VncClient.Rendering;
using RemoteFlow.Core.Sessions;
using VncSize = MarcusW.VncClient.Size;

namespace RemoteFlow.Protocol.Vnc;

/// <summary>
/// VNC 帧缓冲渲染目标。同时是协议库的 <see cref="IRenderTarget"/> 与
/// RemoteFlow 的 <see cref="IFrameSource"/>。
/// <para>
/// <b>线程模型（这是 VNC 内嵌最关键的一点）：</b>
/// RFB 协议在后台线程解码并写入画面，而 UI 框架的位图只能在 UI 线程访问。
/// 因此这里以一块非托管缓冲区作为中转：协议线程直接写缓冲区，
/// UI 线程按显示帧率把缓冲区拷贝进自己的位图。
/// 两侧通过锁与脏标记协调，既不阻塞协议解码，也不违反 UI 框架的线程约束。
/// </para>
/// <para>
/// 本类<b>不引用任何 UI 框架</b>：具体位图类型由各 UI 工程通过
/// <see cref="IFrameSource.TryCopyLatestFrame"/> 自行承接（技术方案 §6.5）。
/// </para>
/// </summary>
public sealed class VncRenderTarget : IRenderTarget, IFrameSource, IDisposable
{
    /// <summary>
    /// 帧缓冲像素格式：内存中依次为 B、G、R、A，在小端平台上等价于 0xAARRGGBB。
    /// 与 WPF 的 <c>PixelFormats.Bgra32</c>、Avalonia 的 <c>PixelFormat.Bgra8888</c> 逐字节对应，
    /// 直接用目标格式接收，避免每帧再做一次像素转换。
    /// </summary>
    public static readonly PixelFormat FramebufferFormat = new(
        name: "BGRA32",
        bitsPerPixel: 32,
        depth: 32,
        bigEndian: false,
        trueColor: true,
        hasAlpha: true,
        redMax: 255, greenMax: 255, blueMax: 255, alphaMax: 255,
        redShift: 16, greenShift: 8, blueShift: 0, alphaShift: 24);

    private readonly Lock _sync = new();

    private nint _buffer;
    private int _bufferByteCount;
    private VncSize _size = VncSize.Zero;
    private bool _dirty;
    private bool _disposed;

    /// <summary>远端桌面尺寸发生变化。UI 需要据此重建自己的位图。</summary>
    public event EventHandler<FrameSize>? FrameSizeChanged;

    /// <summary>当前帧缓冲尺寸。</summary>
    public VncSize Size
    {
        get
        {
            lock (_sync)
            {
                return _size;
            }
        }
    }

    /// <inheritdoc />
    public FrameSize FrameSize
    {
        get
        {
            lock (_sync)
            {
                return new FrameSize(_size.Width, _size.Height);
            }
        }
    }

    /// <summary>
    /// 由协议线程调用，取得可写的帧缓冲引用。
    /// 尺寸变化时重新分配缓冲区，并通知 UI 重建位图。
    /// </summary>
    public IFramebufferReference GrabFramebufferReference(VncSize size, IImmutableSet<Screen> layout)
    {
        var sizeChanged = false;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (size != _size || _buffer == nint.Zero)
            {
                var required = size.Width * size.Height * FramebufferFormat.BytesPerPixel;

                if (_buffer != nint.Zero)
                {
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = nint.Zero;
                }

                _buffer = Marshal.AllocHGlobal(required);
                _bufferByteCount = required;
                _size = size;
                sizeChanged = true;

                // 新缓冲区内容未定义，先清零避免首帧出现花屏。
                unsafe
                {
                    new Span<byte>((void*)_buffer, required).Clear();
                }
            }
        }

        if (sizeChanged)
        {
            FrameSizeChanged?.Invoke(this, new FrameSize(size.Width, size.Height));
        }

        return new FramebufferReference(this);
    }

    /// <inheritdoc />
    public bool TryCopyLatestFrame(
        nint destination,
        long destinationCapacityBytes,
        int destinationStride,
        int expectedWidth,
        int expectedHeight)
    {
        lock (_sync)
        {
            if (_disposed || _buffer == nint.Zero || !_dirty)
            {
                return false;
            }

            // 尺寸不一致说明 UI 尚未按新尺寸重建位图，跳过本帧等待下一轮。
            if (expectedWidth != _size.Width || expectedHeight != _size.Height)
            {
                return false;
            }

            var sourceStride = _size.Width * FramebufferFormat.BytesPerPixel;

            if (destinationStride < sourceStride ||
                destinationCapacityBytes < (long)destinationStride * _size.Height)
            {
                // 目标缓冲区放不下，拷贝会越界——宁可丢一帧也不能写坏内存。
                return false;
            }

            unsafe
            {
                var source = (byte*)_buffer;
                var target = (byte*)destination;

                // 逐行拷贝并遵循目标 stride：目标位图可能有行对齐填充，
                // 整块拷贝会让每一行逐渐错位（原实现的潜在缺陷）。
                for (var y = 0; y < _size.Height; y++)
                {
                    Buffer.MemoryCopy(
                        source: source + (long)y * sourceStride,
                        destination: target + (long)y * destinationStride,
                        destinationSizeInBytes: destinationCapacityBytes - (long)y * destinationStride,
                        sourceBytesToCopy: sourceStride);
                }
            }

            _dirty = false;
            return true;
        }
    }

    private void MarkDirty()
    {
        lock (_sync)
        {
            _dirty = true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_buffer != nint.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = nint.Zero;
                _bufferByteCount = 0;
            }
        }
    }

    /// <summary>
    /// 帧缓冲引用。协议层在其生命周期内直接向 <see cref="Address"/> 写像素，
    /// 释放时标记脏数据，通知 UI 有新帧可取。
    /// </summary>
    private sealed class FramebufferReference(VncRenderTarget target) : IFramebufferReference
    {
        public nint Address => target._buffer;

        public VncSize Size => target._size;

        public PixelFormat Format => FramebufferFormat;

        // 保持 96 DPI，缩放交给 WPF 的布局系统处理，避免双重缩放。
        public double HorizontalDpi => 96.0;

        public double VerticalDpi => 96.0;

        public void Dispose() => target.MarkDirty();
    }
}
