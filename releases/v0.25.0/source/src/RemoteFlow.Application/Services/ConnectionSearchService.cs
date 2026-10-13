using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 全局搜索。产品原则之一是「搜索优先」——服务器多时，搜索比层层点目录更重要。
/// <para>
/// 搜索范围：Name、Host/IP、Group、Tag、Notes。
/// 在内存中对已加载的连接集合过滤，500 条规模下无需引入全文索引即可做到输入即过滤。
/// </para>
/// </summary>
public sealed class ConnectionSearchService
{
    /// <summary>
    /// 执行搜索并按相关度排序。
    /// </summary>
    /// <param name="source">全部连接。</param>
    /// <param name="query">搜索词。为空时返回原集合。</param>
    /// <param name="groupNames">分组 Id → 名称，用于按分组名搜索。</param>
    /// <param name="tagNames">标签 Id → 名称，用于按标签名搜索。</param>
    public IReadOnlyList<ConnectionProfile> Search(
        IEnumerable<ConnectionProfile> source,
        string? query,
        IReadOnlyDictionary<Guid, string> groupNames,
        IReadOnlyDictionary<Guid, string> tagNames)
    {
        var all = source as IReadOnlyList<ConnectionProfile> ?? source.ToList();

        if (string.IsNullOrWhiteSpace(query))
        {
            return all;
        }

        var term = query.Trim();

        var matches = new List<(ConnectionProfile Profile, int Score)>();
        foreach (var profile in all)
        {
            var score = Score(profile, term, groupNames, tagNames);
            if (score > 0)
            {
                matches.Add((profile, score));
            }
        }

        return matches
            .OrderByDescending(m => m.Score)
            // 同等相关度下，最近连接过的排在前面，贴合「快速继续工作」的使用习惯。
            .ThenByDescending(m => m.Profile.LastConnectedAt ?? DateTimeOffset.MinValue)
            .ThenBy(m => m.Profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => m.Profile)
            .ToList();
    }

    /// <summary>
    /// 相关度打分。名称前缀匹配权重最高，其次是名称包含、主机、标签、分组，最后是备注。
    /// </summary>
    private static int Score(
        ConnectionProfile profile,
        string term,
        IReadOnlyDictionary<Guid, string> groupNames,
        IReadOnlyDictionary<Guid, string> tagNames)
    {
        const StringComparison Ignore = StringComparison.CurrentCultureIgnoreCase;
        var score = 0;

        if (profile.Name.StartsWith(term, Ignore))
        {
            score += 100;
        }
        else if (profile.Name.Contains(term, Ignore))
        {
            score += 60;
        }

        if (profile.Host.StartsWith(term, Ignore))
        {
            score += 50;
        }
        else if (profile.Host.Contains(term, Ignore))
        {
            score += 30;
        }

        foreach (var tagId in profile.TagIds)
        {
            if (tagNames.TryGetValue(tagId, out var tagName) && tagName.Contains(term, Ignore))
            {
                score += 25;
                break;
            }
        }

        if (profile.GroupId is { } groupId
            && groupNames.TryGetValue(groupId, out var groupName)
            && groupName.Contains(term, Ignore))
        {
            score += 20;
        }

        if (!string.IsNullOrEmpty(profile.Notes) && profile.Notes.Contains(term, Ignore))
        {
            score += 10;
        }

        // 协议名也参与匹配，便于输入 "rdp" 快速筛出全部 RDP 连接。
        if (profile.Protocol.ToString().Contains(term, Ignore))
        {
            score += 15;
        }

        return score;
    }
}
