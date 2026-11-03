using RemoteFlow.Core.Models;

namespace RemoteFlow.Application.Services;

/// <summary>
/// 连接前弹出的凭据输入框勾选「记住我的凭据」后的落库：
/// 连接没分配凭据 → 新建一条凭据并分配给该连接；凭据只有账户名 → 把密码补存进该凭据。
/// 之后这台连接就不再询问。密码只经 <see cref="CredentialService"/> 进保险库，不落 SQLite。
/// </summary>
public sealed class RememberedCredentialService(CredentialService credentials, ConnectionService connections)
{
    /// <returns>被保存或更新的凭据 Id。</returns>
    public async Task<Guid> RememberAsync(
        ConnectionProfile profile,
        ConnectionCredentialRequirement requirement,
        string username,
        string domain,
        string password,
        string? credentialName = null,
        CredentialType? credentialType = null,
        CancellationToken ct = default)
    {
        if (requirement.Prompt == ConnectionCredentialPrompt.PasswordOnly
            && profile.CredentialId is { } existingId)
        {
            var existing = await credentials.GetByIdAsync(existingId, ct)
                ?? throw new InvalidOperationException("该连接引用的凭据已不存在。");
            await credentials.UpdateAsync(existing, password, null, ct);
            return existing.Id;
        }

        // 名称默认为用户名全称（有域时为「域\用户」），用户在「更多选项」里可改；重名自动编号。
        var fullName = string.IsNullOrEmpty(domain) ? username : $"{domain}\\{username}";
        var baseName = !string.IsNullOrWhiteSpace(credentialName)
            ? credentialName.Trim()
            : !string.IsNullOrEmpty(fullName) ? fullName : profile.Name;
        var name = await UniqueNameAsync(baseName, ct);
        var created = await credentials.CreateAsync(
            new Credential { Name = name, Type = credentialType ?? requirement.Type, Username = username, Domain = domain },
            password,
            null,
            ct);

        // profile 是列表里正在用的同一个对象：同步改内存，界面与会话立即看到新凭据。
        profile.CredentialId = created.Id;
        await connections.UpdateAsync(profile, ct);
        return created.Id;
    }

    /// <summary>把一条已保存的凭据分配给该连接（「更多选项」里选了已有凭据并勾选记住）。</summary>
    public async Task AssignAsync(ConnectionProfile profile, Guid credentialId, CancellationToken ct = default)
    {
        profile.CredentialId = credentialId;
        await connections.UpdateAsync(profile, ct);
    }

    private async Task<string> UniqueNameAsync(string baseName, CancellationToken ct)
    {
        var taken = (await credentials.GetAllAsync(ct)).Select(c => c.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
