namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 让远端终端的行列数始终等于页面<b>最后一次</b>上报的值。
/// <para>
/// 页面在就绪时报一次尺寸，随后套用字体 / 字号又会让行列数变化并再报一次。宿主若在套用之后或连接完成之后
/// 拿就绪时的旧值重发，就会把正确尺寸覆盖掉：远端以为屏幕比实际大，<c>clear</c> 等按行定位的输出
/// 随之光标错位、多出一段滚动区。因此需要重发时一律用这里记录的最新值。
/// </para>
/// </summary>
internal sealed class TerminalSizeSync(Action<int, int, int, int> resize)
{
    private (int Columns, int Rows, int Width, int Height)? _latest;

    /// <summary>页面上报尺寸（就绪消息与之后的每次变化）：记录并立即下发。</summary>
    public void Report(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        _latest = (columns, rows, pixelWidth, pixelHeight);
        resize(columns, rows, pixelWidth, pixelHeight);
    }

    /// <summary>需要重发时（例如连接刚建立、此前的下发还没有通道可用）下发最新值。</summary>
    public void Reapply()
    {
        if (_latest is { } size)
        {
            resize(size.Columns, size.Rows, size.Width, size.Height);
        }
    }
}
