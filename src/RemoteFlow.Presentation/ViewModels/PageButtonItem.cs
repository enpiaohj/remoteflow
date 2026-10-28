namespace RemoteFlow.Presentation.ViewModels;

/// <summary>分页条里的一个页码按钮；<see cref="Number"/> 为 0 表示省略号（不可点）。</summary>
public sealed class PageButtonItem(int number, bool isCurrent)
{
    public int Number { get; } = number;

    public bool IsCurrent { get; } = isCurrent;

    public bool IsEllipsis => Number == 0;

    public string Label => IsEllipsis ? "…" : Number.ToString();
}

/// <summary>每页条数下拉项。<see cref="Value"/>：正数 = 固定条数；0 = 不分页（全部）。</summary>
public sealed record PageSizeOption(int Value, string Text)
{
    public static IReadOnlyList<PageSizeOption> Defaults { get; } =
    [
        new(10, "10 条/页"),
        new(20, "20 条/页"),
        new(50, "50 条/页"),
        new(100, "100 条/页"),
        new(0, "全部"),
    ];
}
