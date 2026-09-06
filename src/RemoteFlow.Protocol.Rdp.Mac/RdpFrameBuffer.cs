using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Rdp.Mac;

/// <summary>
/// RDP 帧缓冲：C 侧（FreeRDP 线程）经回调把 BGRX32 帧写入这里，UI 线程按显示帧率取走。
/// 与 <c>VncRenderTarget</c> 同职责，实现 <see cref="IFrameSource"/>。
/// </summary>
internal sealed class RdpFrameBuffer : IFrameSource, IDisposable
{
    private readonly Lock _sync = new();
    private byte[] _buffer = [];
    private int _width;
    private int _height;
    private bool _dirty;
    private bool _disposed;

    public FrameSize FrameSize
    {
        get
        {
            lock (_sync)
            {
                return new FrameSize(_width, _height);
            }
        }
    }

    public event EventHandler<FrameSize>? FrameSizeChanged;

    /// <summary>C 回调：整帧 BGRX32（stride 可能 &gt; width*4）。在 FreeRDP 线程上调用。</summary>
    public void Ingest(nint src, int width, int height, int stride)
    {
        if (src == nint.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        var sizeChanged = false;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (width != _width || height != _height)
            {
                _width = width;
                _height = height;
                _buffer = new byte[width * height * 4];
                sizeChanged = true;
            }

            var rowBytes = width * 4;
            unsafe
            {
                var s = (byte*)src;
                fixed (byte* d = _buffer)
                {
                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(s + (long)y * stride, d + (long)y * rowBytes, rowBytes, rowBytes);
                    }
                }
            }

            _dirty = true;
        }

        if (sizeChanged)
        {
            FrameSizeChanged?.Invoke(this, new FrameSize(width, height));
        }
    }

    public bool TryCopyLatestFrame(
        nint destination, long destinationCapacityBytes, int destinationStride,
        int expectedWidth, int expectedHeight)
    {
        lock (_sync)
        {
            if (_disposed || !_dirty || _buffer.Length == 0
                || expectedWidth != _width || expectedHeight != _height)
            {
                return false;
            }

            var srcStride = _width * 4;
            if (destinationStride < srcStride
                || destinationCapacityBytes < (long)destinationStride * _height)
            {
                return false;
            }

            unsafe
            {
                var d = (byte*)destination;
                fixed (byte* s = _buffer)
                {
                    for (var y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(
                            s + (long)y * srcStride,
                            d + (long)y * destinationStride,
                            destinationCapacityBytes - (long)y * destinationStride,
                            srcStride);
                    }
                }
            }

            _dirty = false;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _buffer = [];
        }
    }
}
