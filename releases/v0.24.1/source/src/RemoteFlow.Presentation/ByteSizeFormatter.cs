using System.Globalization;

namespace RemoteFlow.Presentation;

/// <summary>字节数的人类可读格式化（1024 进制：B / KB / MB / GB / TB），固定使用不变区域性，避免小数点随系统语言变化。</summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 三位数以上不留小数（如 123 MB），否则最多一位并去掉多余的 .0（如 1.5 KB、12 KB）。
        var text = value >= 100
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{text} {Units[unit]}";
    }
}
