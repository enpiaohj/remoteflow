using RemoteFlow.Core.FileTransfer;

namespace RemoteFlow.Infrastructure.Smb;

/// <summary>
/// 把统一的虚拟路径映射为 UNC 物理路径：<c>/</c> → 共享列表；<c>/C$/Users/x</c> → <c>{服务器根}\C$\Users\x</c>。
/// <para>
/// 生产环境的「服务器根」是 <c>\\?\UNC\主机</c>——带 <c>\\?\</c> 前缀后 Win32 不再做路径规范化，
/// 结尾的点 / 空格不会被吞、<c>CON</c>、<c>NUL</c> 之类的旧设备名不会被当成设备、也不受 260 字符限制；
/// 代价是 <c>..</c> 与 <c>.</c> 不再被系统解析，所以<b>本类必须自己拒绝它们</b>——这是路径穿越的关键防线。
/// 测试时把「服务器根」换成本机临时目录，即可用本地磁盘完整验证下游文件操作逻辑。
/// </para>
/// </summary>
internal sealed class SmbPathMapper
{
    private static readonly char[] ForbiddenInSegment = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly string _serverRoot;

    public SmbPathMapper(string serverRoot)
    {
        _serverRoot = serverRoot.TrimEnd('\\');
    }

    /// <summary>虚拟路径的层数：根为 0，共享根为 1，共享内的条目 ≥ 2。</summary>
    public static int Depth(string virtualPath)
    {
        var normalized = RemotePath.Normalize(virtualPath);
        return normalized == RemotePath.Root ? 0 : normalized.Count(c => c == '/');
    }

    /// <summary>
    /// 解析为物理路径。根返回服务器根本身。任何一段不安全（<c>.</c> / <c>..</c> / 含反斜杠冒号等）
    /// 都抛 <see cref="FileTransferException"/>（<see cref="FileTransferErrorCode.InvalidName"/>）。
    /// </summary>
    public string ToPhysical(string virtualPath)
    {
        var normalized = RemotePath.Normalize(virtualPath);
        if (normalized == RemotePath.Root)
        {
            return _serverRoot;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            EnsureSafeSegment(segment);
        }

        return _serverRoot + "\\" + string.Join('\\', segments);
    }

    /// <summary>
    /// 校验单层名称。<c>RemotePath.Normalize</c> 已消化了 <c>..</c>，但反斜杠在 POSIX 路径里是普通字符，
    /// 到了 UNC 就是分隔符——<c>a\..\..\b</c> 这类单段名必须在这里拦住。
    /// </summary>
    public static void EnsureSafeSegment(string segment)
    {
        if (!RemotePath.IsValidEntryName(segment)
            || segment.IndexOfAny(ForbiddenInSegment) >= 0
            || segment.Any(char.IsControl))
        {
            throw new FileTransferException(FileTransferErrorCode.InvalidName, "名称包含不允许的字符，操作已拒绝。");
        }
    }
}
