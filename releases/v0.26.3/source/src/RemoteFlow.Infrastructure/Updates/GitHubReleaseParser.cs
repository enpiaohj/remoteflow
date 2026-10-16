using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteFlow.Infrastructure.Updates;

/// <summary>
/// 解析 GitHub Releases 列表。仓库里 Windows 与 macOS 两条版本线的 Release 混在一起
/// （<c>v0.24.0</c> 与 <c>macos-v0.10.0</c>），不能直接用「Latest」，必须按 Tag 形态与产物名筛选。
/// </summary>
public static partial class GitHubReleaseParser
{
    /// <summary>
    /// 从 Releases 列表 JSON 中选出版本号最高、且含指定平台产物的正式版本。
    /// </summary>
    /// <param name="assetSuffix">产物名后缀，如 <c>win-x64.exe</c>；完整产物名为 <c>RemoteFlow-{tag}-{后缀}</c>。</param>
    public static UpdateRelease? SelectLatest(string json, string assetSuffix)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        UpdateRelease? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (GetBool(release, "draft") || GetBool(release, "prerelease"))
            {
                continue;
            }

            var tag = GetString(release, "tag_name");
            var match = WindowsTag().Match(tag);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
            {
                continue;
            }

            if (best is not null && version <= best.Version)
            {
                continue;
            }

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var assetName = $"RemoteFlow-{tag}-{assetSuffix}";
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(GetString(asset, "name"), assetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = GetString(asset, "browser_download_url");
                if (url.Length == 0)
                {
                    break;
                }

                best = new UpdateRelease(
                    version,
                    tag,
                    GetString(release, "body"),
                    GetString(release, "html_url"),
                    assetName,
                    url,
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                    ParseDigest(GetString(asset, "digest")));
                break;
            }
        }

        return best;
    }

    /// <summary><c>sha256:十六进制</c> → 小写十六进制；其它形态一律视为没有。</summary>
    private static string? ParseDigest(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)$")]
    private static partial Regex WindowsTag();
}
