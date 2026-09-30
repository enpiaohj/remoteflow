namespace RemoteFlow.Core.FileTransfer;

/// <summary>面包屑里的一段：显示名与到该段为止的完整路径。</summary>
public sealed record RemotePathSegment(string Name, string FullPath);

/// <summary>
/// 远端虚拟路径的纯字符串处理（POSIX 风格，<c>/</c> 分隔，根为 <c>/</c>）。
/// 各通道的文件系统都使用同一套路径，SFTP 直通，SMB 把首段当共享名映射为 UNC。
/// <para>
/// 这里的每个方法都<b>不会返回越过根的路径</b>：<c>..</c> 到根为止，绝不上溯——
/// 路径规范化是防路径穿越的第一道防线（第二道在各文件系统实现内部）。
/// </para>
/// </summary>
public static class RemotePath
{
    public const string Root = "/";

    /// <summary>规范化：补前导 <c>/</c>、折叠重复分隔符、去掉 <c>.</c>、按 <c>..</c> 回退（到根为止）、去掉尾部 <c>/</c>。</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Root;
        }

        var stack = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(part);
        }

        return stack.Count == 0 ? Root : "/" + string.Join('/', stack);
    }

    /// <summary>
    /// 在 <paramref name="parent"/> 下拼接一个<b>单层名称</b>。名称必须是合法条目名
    /// （见 <see cref="IsValidEntryName"/>），否则抛 <see cref="ArgumentException"/>——
    /// 调用方拿远端返回的名字或用户输入拼路径时，不会被 <c>../</c> 之类带出目录。
    /// </summary>
    public static string Combine(string parent, string name)
    {
        if (!IsValidEntryName(name))
        {
            throw new ArgumentException("名称不是合法的单层条目名。", nameof(name));
        }

        var basePath = Normalize(parent);
        return basePath == Root ? "/" + name : basePath + "/" + name;
    }

    /// <summary>上一级目录；根的上一级仍是根。</summary>
    public static string GetParent(string path)
    {
        var normalized = Normalize(path);
        if (normalized == Root)
        {
            return Root;
        }

        var index = normalized.LastIndexOf('/');
        return index <= 0 ? Root : normalized[..index];
    }

    /// <summary>末段名称；根返回空字符串。</summary>
    public static string GetName(string path)
    {
        var normalized = Normalize(path);
        return normalized == Root ? string.Empty : normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// 面包屑分段，第一段固定是根（名称 <c>/</c>）。
    /// 例：<c>/a/b</c> → [<c>/</c>, <c>a</c> (/a), <c>b</c> (/a/b)]。
    /// </summary>
    public static IReadOnlyList<RemotePathSegment> GetSegments(string path)
    {
        var normalized = Normalize(path);
        var result = new List<RemotePathSegment> { new(Root, Root) };
        if (normalized == Root)
        {
            return result;
        }

        var current = string.Empty;
        foreach (var part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            result.Add(new RemotePathSegment(part, current));
        }

        return result;
    }

    /// <summary><paramref name="path"/> 是否等于 <paramref name="root"/> 或位于其下（按路径段比较，<c>/ab</c> 不算在 <c>/a</c> 下）。</summary>
    public static bool IsSameOrUnder(string root, string path)
    {
        var normalizedRoot = Normalize(root);
        var normalizedPath = Normalize(path);
        if (normalizedRoot == Root)
        {
            return true;
        }

        return normalizedPath == normalizedRoot
               || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// 是否为合法的单层条目名：非空、不是 <c>.</c> / <c>..</c>、不含 <c>/</c> 与 NUL。
    /// （POSIX 下反斜杠是合法文件名字符，这里不禁止；落到本机时由 <see cref="LocalNameSanitizer"/> 处理。）
    /// </summary>
    public static bool IsValidEntryName(string? name)
        => !string.IsNullOrEmpty(name)
           && name != "."
           && name != ".."
           && name.IndexOf('/') < 0
           && name.IndexOf('\0') < 0;
}
