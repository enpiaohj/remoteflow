using System.Text;
using Renci.SshNet;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.Ssh;

/// <summary>
/// 由主机 / 端口 / 凭据构建 SSH.NET 的 <see cref="ConnectionInfo"/>。终端会话与 SFTP 工厂共用，
/// 保证两者的认证方式与编码解析完全一致。
/// <para>构建出的对象自带认证材料的副本，构建完成后调用方即可释放 <see cref="ResolvedCredential"/>。</para>
/// </summary>
internal static class SshConnectionInfoFactory
{
    public static ConnectionInfo Build(
        string host, int port, ResolvedCredential? credential, int connectTimeoutSeconds, string encodingName)
    {
        if (credential is null)
        {
            throw ConnectionException.FromCode(ConnectionErrorCode.CredentialMissing);
        }

        credential.ThrowIfDisposed();

        var username = credential.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ConnectionException(
                ConnectionErrorCode.AuthenticationFailed, "SSH 连接必须指定用户名。");
        }

        AuthenticationMethod authentication = credential.Type switch
        {
            CredentialType.SshPrivateKey => BuildPrivateKeyAuthentication(username, credential),
            _ => new PasswordAuthenticationMethod(username, credential.Password ?? string.Empty)
        };

        return new ConnectionInfo(host, port, username, authentication)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(connectTimeoutSeconds, 5)),
            Encoding = ResolveEncoding(encodingName)
        };
    }

    private static PrivateKeyAuthenticationMethod BuildPrivateKeyAuthentication(string username, ResolvedCredential credential)
    {
        if (string.IsNullOrEmpty(credential.PrivateKey))
        {
            throw new ConnectionException(
                ConnectionErrorCode.CredentialMissing, "该凭据未包含 SSH 私钥，无法使用私钥登录。");
        }

        try
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(credential.PrivateKey));

            // Password 字段在私钥登录场景下承担 Passphrase 的角色。
            var keyFile = string.IsNullOrEmpty(credential.Password)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, credential.Password);

            return new PrivateKeyAuthenticationMethod(username, keyFile);
        }
        catch (Renci.SshNet.Common.SshException ex)
        {
            // 私钥格式错误或 Passphrase 不正确。异常信息不包含私钥内容，可安全传递。
            throw new ConnectionException(
                ConnectionErrorCode.AuthenticationFailed, "SSH 私钥无法加载，请检查私钥格式或 Passphrase 是否正确。", ex);
        }
    }

    internal static Encoding ResolveEncoding(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            // 配置了不认识的编码时退回 UTF-8，而不是让连接直接失败。
            return Encoding.UTF8;
        }
    }
}
