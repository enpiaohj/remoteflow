using System.Text;

namespace RemoteFlow.Core.FileTransfer;

/// <summary>清洗后的本机文件名。<see cref="Changed"/> 为 true 表示与远端原名不同，界面据此提示「已重命名」。</summary>
public readonly record struct SanitizedName(string Name, bool Changed);

/// <summary>
/// 把远端返回的名字变成可安全落到本机的文件名，并保证落盘路径不越出目标目录。
/// <para>
/// 这是文件传输里最关键的安全边界：SFTP / SMB 服务端返回的文件名是<b>不可信输入</b>，
/// 恶意或被攻陷的服务器可以构造 <c>..\..\Startup\x.exe</c>、<c>CON</c>、<c>a:evil</c>（NTFS 备用数据流）
/// 之类的名字，让客户端把文件写到目标目录之外或写成系统设备。这里的规则不依赖当前操作系统
/// （macOS 上同样应用），保证同一份远端数据在两个平台行为一致。
/// </para>
/// </summary>
public static class LocalNameSanitizer
{
    /// <summary>名称最大长度。留出 <c>.rfpart</c> 等临时后缀的余量，低于常见文件系统的 255 上限（临时名 = 名称 + 最多 16 个字符）。</summary>
    public const int MaxNameLength = 200;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// 清洗一个远端名称。无法安全落盘（空、<c>.</c>、<c>..</c>）时返回 <c>null</c>，调用方应跳过并报告。
    /// 可修复的问题（非法字符、保留设备名、结尾的点或空格、过长）会被改写，并标记 <see cref="SanitizedName.Changed"/>。
    /// </summary>
    public static SanitizedName? Sanitize(string? remoteName)
    {
        if (string.IsNullOrWhiteSpace(remoteName) || remoteName == "." || remoteName == "..")
        {
            return null;
        }

        var builder = new StringBuilder(remoteName.Length);
        foreach (var ch in remoteName)
        {
            builder.Append(IsForbiddenChar(ch) ? '_' : ch);
        }

        // Windows 会静默去掉名称结尾的点与空格，导致「a.」与「a」串成同一个文件；统一替换掉。
        if (builder.Length > 0 && builder[^1] is '.' or ' ')
        {
            builder[^1] = '_';
        }

        var name = builder.ToString();

        // 保留设备名判断只看第一个点之前的部分：「CON.txt」在 Windows 上同样指向设备。
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        if (ReservedDeviceNames.Contains(stem.TrimEnd(' ')))
        {
            name = "_" + name;
        }

        if (name.Length > MaxNameLength)
        {
            name = Truncate(name);
        }

        return new SanitizedName(name, !string.Equals(name, remoteName, StringComparison.Ordinal));
    }

    /// <summary>
    /// 在 <paramref name="rootDirectory"/> 下拼出落盘完整路径，并校验结果仍在该目录之内。
    /// <paramref name="relativeSegments"/> 里的每一段都必须先经 <see cref="Sanitize"/> 清洗；
    /// 这里再校验一次，是最后一道兜底——即使上游漏了清洗，也不会写到目录之外。
    /// </summary>
    /// <exception cref="FileTransferException">路径越界或含非法段（<see cref="FileTransferErrorCode.InvalidName"/>）。</exception>
    public static string ResolveUnder(string rootDirectory, params string[] relativeSegments)
    {
        var root = Path.GetFullPath(rootDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        foreach (var segment in relativeSegments)
        {
            if (string.IsNullOrEmpty(segment) || segment is "." or "..")
            {
                throw new FileTransferException(FileTransferErrorCode.InvalidName, "文件名不合法，已拒绝写入。");
            }
        }

        var combined = Path.GetFullPath(Path.Combine([root, .. relativeSegments]));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(rootWithSeparator, comparison))
        {
            throw new FileTransferException(FileTransferErrorCode.InvalidName, "文件名试图越出目标目录，已拒绝写入。");
        }

        return combined;
    }

    private static bool IsForbiddenChar(char ch)
        => ch < 0x20
           || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
           || ch == '\u007f';

    /// <summary>超长时保留扩展名（最多 20 个字符）再截断主干，避免把扩展名截没。</summary>
    private static string Truncate(string name)
    {
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= 20 ? name[dot..] : string.Empty;
        var stemLength = MaxNameLength - extension.Length;
        return name[..stemLength] + extension;
    }
}
