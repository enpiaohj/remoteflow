using RemoteFlow.Core.Sessions;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 开发用 Host Key 策略：接受并记录指纹（变化时也放行）。
/// <b>仅用于可运行骨架</b>——正式确认对话框（首次提示 / 变化强警告）属后续里程碑，
/// 见技术方案 §7.4。指纹被记录供日志回溯，不阻断连接。
/// </summary>
public sealed class DevTrustHostKeyPolicy : ISshHostKeyPolicy
{
    private string? _remembered;

    public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context) => new()
    {
        Host = context.Host,
        Port = context.Port,
        KeyAlgorithm = context.KeyAlgorithm,
        Fingerprint = context.Fingerprint,
        KnownFingerprint = _remembered,
    };

    public Task<bool> ConfirmAndRememberAsync(SshHostKeyVerificationContext context, CancellationToken ct)
    {
        _remembered = context.Fingerprint;
        return Task.FromResult(true);
    }
}
