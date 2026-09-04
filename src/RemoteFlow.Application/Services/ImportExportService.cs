using System.Text;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 连接资产的 CSV 导入 / 导出。
/// <para>
/// <b>安全约束：导出文件绝不包含密码或私钥。</b>
/// CredentialName 列只写凭据名称，用于人工核对与再次关联，
/// 导入时按名称匹配已有凭据，匹配不到则留空，由用户后续手工指定。
/// </para>
/// </summary>
public sealed class ImportExportService(
    IConnectionRepository connections,
    IGroupRepository groups,
    ITagRepository tags,
    ICredentialRepository credentials,
    ILogger<ImportExportService> logger)
{
    private static readonly string[] Header =
        ["Name", "Host", "Port", "Protocol", "Group", "Tags", "CredentialName", "Notes"];

    /// <summary>导出全部连接为 CSV。使用 UTF-8 BOM，保证 Excel 直接打开中文不乱码。</summary>
    public async Task ExportAsync(string filePath, CancellationToken ct = default)
    {
        var allConnections = await connections.GetAllAsync(ct);
        var groupNames = (await groups.GetAllAsync(ct)).ToDictionary(g => g.Id, g => g.Name);
        var tagNames = (await tags.GetAllAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        var credentialNames = (await credentials.GetAllAsync(ct)).ToDictionary(c => c.Id, c => c.Name);

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Header));

        foreach (var profile in allConnections)
        {
            var groupName = profile.GroupId is { } gid && groupNames.TryGetValue(gid, out var g) ? g : string.Empty;
            var credentialName = profile.CredentialId is { } cid && credentialNames.TryGetValue(cid, out var c) ? c : string.Empty;
            var tagList = string.Join(';', profile.TagIds
                .Where(tagNames.ContainsKey)
                .Select(id => tagNames[id]));

            builder.AppendLine(string.Join(',',
                Escape(profile.Name),
                Escape(profile.Host),
                profile.Port.ToString(),
                Escape(profile.Protocol.ToString()),
                Escape(groupName),
                Escape(tagList),
                Escape(credentialName),
                Escape(profile.Notes)));
        }

        await File.WriteAllTextAsync(filePath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
        logger.LogInformation("已导出 {Count} 条连接到 CSV", allConnections.Count);
    }

    /// <summary>
    /// 从 CSV 导入连接。缺失的分组与标签会自动创建；
    /// 单行解析失败不会中断整体导入，失败原因汇总在结果中返回。
    /// </summary>
    public async Task<ImportResult> ImportAsync(string filePath, CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var rows = CsvParser.Parse(content);

        if (rows.Count == 0)
        {
            return new ImportResult(0, 0, ["CSV 文件为空。"]);
        }

        var columns = rows[0].Select(h => h.Trim()).ToList();
        var nameIndex = columns.FindIndex(h => h.Equals("Name", StringComparison.OrdinalIgnoreCase));
        var hostIndex = columns.FindIndex(h => h.Equals("Host", StringComparison.OrdinalIgnoreCase));

        if (nameIndex < 0 || hostIndex < 0)
        {
            return new ImportResult(0, 0, ["CSV 缺少必需的 Name 或 Host 列。"]);
        }

        var portIndex = columns.FindIndex(h => h.Equals("Port", StringComparison.OrdinalIgnoreCase));
        var protocolIndex = columns.FindIndex(h => h.Equals("Protocol", StringComparison.OrdinalIgnoreCase));
        var groupIndex = columns.FindIndex(h => h.Equals("Group", StringComparison.OrdinalIgnoreCase));
        var tagsIndex = columns.FindIndex(h => h.Equals("Tags", StringComparison.OrdinalIgnoreCase));
        var credentialIndex = columns.FindIndex(h => h.Equals("CredentialName", StringComparison.OrdinalIgnoreCase));
        var notesIndex = columns.FindIndex(h => h.Equals("Notes", StringComparison.OrdinalIgnoreCase));

        var existingGroups = (await groups.GetAllAsync(ct))
            .ToDictionary(g => g.Name, g => g.Id, StringComparer.CurrentCultureIgnoreCase);
        var existingTags = (await tags.GetAllAsync(ct))
            .ToDictionary(t => t.Name, t => t.Id, StringComparer.CurrentCultureIgnoreCase);
        var existingCredentials = (await credentials.GetAllAsync(ct))
            .ToDictionary(c => c.Name, c => c.Id, StringComparer.CurrentCultureIgnoreCase);

        // 去重：名称 + 主机 + 端口 + 协议完全相同视为同一条连接，跳过。
        // 同时把本次导入已写入的键也计入，处理 CSV 内部自带的重复行。
        var seenConnections = (await connections.GetAllAsync(ct))
            .Select(ConnectionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            ct.ThrowIfCancellationRequested();

            var row = rows[rowIndex];
            // CSV 行号从 1 开始计（含表头），与用户在 Excel 中看到的行号一致。
            var displayRow = rowIndex + 1;

            try
            {
                var name = Field(row, nameIndex);
                var host = Field(row, hostIndex);

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host))
                {
                    skipped++;
                    errors.Add($"第 {displayRow} 行：Name 或 Host 为空，已跳过。");
                    continue;
                }

                var protocol = ParseProtocol(Field(row, protocolIndex));
                var port = ParsePort(Field(row, portIndex), protocol);

                if (!seenConnections.Add(ConnectionKey(name, host, port, protocol)))
                {
                    skipped++;
                    errors.Add($"第 {displayRow} 行：连接「{name}」（{host}:{port}）已存在，已跳过。");
                    continue;
                }

                var profile = new ConnectionProfile
                {
                    Name = name,
                    Host = host,
                    Port = port,
                    Protocol = protocol,
                    Notes = Field(row, notesIndex)
                };

                var groupName = Field(row, groupIndex);
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    profile.GroupId = await EnsureGroupAsync(groupName, existingGroups, ct);
                }

                var tagNames = Field(row, tagsIndex);
                if (!string.IsNullOrWhiteSpace(tagNames))
                {
                    foreach (var tagName in tagNames.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        profile.TagIds.Add(await EnsureTagAsync(tagName, existingTags, ct));
                    }
                }

                // 凭据只按名称关联已有项，绝不凭 CSV 内容创建凭据（CSV 中没有也不应有 Secret）。
                var credentialName = Field(row, credentialIndex);
                if (!string.IsNullOrWhiteSpace(credentialName)
                    && existingCredentials.TryGetValue(credentialName, out var credentialId))
                {
                    profile.CredentialId = credentialId;
                }

                await connections.AddAsync(profile, ct);
                imported++;
            }
            catch (Exception ex)
            {
                skipped++;
                errors.Add($"第 {displayRow} 行导入失败：{ex.Message}");
            }
        }

        logger.LogInformation("CSV 导入完成：成功 {Imported} 条，跳过 {Skipped} 条", imported, skipped);
        return new ImportResult(imported, skipped, errors);
    }

    /// <summary>连接去重键：名称 + 主机 + 端口 + 协议。</summary>
    private static string ConnectionKey(ConnectionProfile p) => ConnectionKey(p.Name, p.Host, p.Port, p.Protocol);

    private static string ConnectionKey(string name, string host, int port, ProtocolType protocol)
        => $"{name.Trim()}|{host.Trim()}|{port}|{protocol}";

    private async Task<Guid> EnsureGroupAsync(string name, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(name, out var id))
        {
            return id;
        }

        var group = new ConnectionGroup { Name = name };
        await groups.AddAsync(group, ct);
        cache[name] = group.Id;
        return group.Id;
    }

    private async Task<Guid> EnsureTagAsync(string name, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(name, out var id))
        {
            return id;
        }

        var tag = new Tag { Name = name };
        await tags.AddAsync(tag, ct);
        cache[name] = tag.Id;
        return tag.Id;
    }

    private static string Field(IReadOnlyList<string> row, int index)
        => index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;

    private static ProtocolType ParseProtocol(string value)
        => Enum.TryParse<ProtocolType>(value, ignoreCase: true, out var protocol) ? protocol : ProtocolType.Rdp;

    private static int ParsePort(string value, ProtocolType protocol)
        => int.TryParse(value, out var port) && port is > 0 and <= 65535
            ? port
            : ConnectionProfile.GetDefaultPort(protocol);

    /// <summary>按 RFC 4180 转义 CSV 字段。</summary>
    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        return value;
    }
}

/// <summary>导入结果汇总。</summary>
public sealed record ImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// 最小 CSV 解析器。支持带引号字段、字段内逗号与换行、双引号转义。
/// 只为导入连接清单服务，不追求覆盖 CSV 的全部方言。
/// </summary>
internal static class CsvParser
{
    public static List<List<string>> Parse(string content)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // 连续两个引号表示一个字面量引号。
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    currentRow.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    // 交给随后的 \n 统一处理；单独的 \r 也视为换行。
                    if (i + 1 >= content.Length || content[i + 1] != '\n')
                    {
                        goto case '\n';
                    }
                    break;

                case '\n':
                    currentRow.Add(field.ToString());
                    field.Clear();
                    rows.Add(currentRow);
                    currentRow = [];
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        // 收尾：文件末尾没有换行时，最后一行也要保留。
        if (field.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(field.ToString());
            rows.Add(currentRow);
        }

        // 去掉可能存在的 UTF-8 BOM，避免第一列列名匹配失败。
        if (rows.Count > 0 && rows[0].Count > 0)
        {
            rows[0][0] = rows[0][0].TrimStart('﻿');
        }

        return rows;
    }
}
