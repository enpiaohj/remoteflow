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

    /// <summary>
    /// C 回调：<paramref name="src"/> 是整块 primary_buffer（stride 可能 &gt; width*4），
    /// <c>(dirtyX,dirtyY,dirtyW,dirtyH)</c> 是本帧的变化矩形——只回写这一块，不全拷。
    /// 在 FreeRDP 线程上调用。
    /// </summary>
    public void Ingest(nint src, int width, int height, int stride,
        int dirtyX, int dirtyY, int dirtyW, int dirtyH)
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
                // 新缓冲全是 0，脏区必须覆盖整幅，否则边缘留黑。
                dirtyX = 0;
                dirtyY = 0;
                dirtyW = width;
                dirtyH = height;
            }

            // 夹到画面范围。
            if (dirtyX < 0) { dirtyW += dirtyX; dirtyX = 0; }
            if (dirtyY < 0) { dirtyH += dirtyY; dirtyY = 0; }
            if (dirtyX + dirtyW > width) { dirtyW = width - dirtyX; }
            if (dirtyY + dirtyH > height) { dirtyH = height - dirtyY; }
            if (dirtyW <= 0 || dirtyH <= 0)
            {
                return;
            }

            var rowBytes = width * 4;
            var colOffset = dirtyX * 4;
            var copyBytes = dirtyW * 4;
            unsafe
            {
                var s = (byte*)src;
                fixed (byte* d = _buffer)
                {
                    for (var y = dirtyY; y < dirtyY + dirtyH; y++)
                    {
                        Buffer.MemoryCopy(
                            s + (long)y * stride + colOffset,
                            d + (long)y * rowBytes + colOffset,
                            copyBytes, copyBytes);
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
