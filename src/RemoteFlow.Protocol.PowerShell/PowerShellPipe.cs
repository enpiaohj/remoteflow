using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;

namespace RemoteFlow.Protocol.PowerShell;

/// <summary>
/// 向子进程 <c>powershell.exe</c> 传参的公共部分：固定脚本的编码，以及仅当前用户可访问、
/// 并核对对端进程号的一次性命名管道。主机、账号、密码只经这条管道传递。
/// </summary>
internal static class PowerShellPipe
{
    public static string LoadScript(string name)
    {
        using var stream = typeof(PowerShellPipe).Assembly
            .GetManifestResourceStream("RemoteFlow.Protocol.PowerShell." + name)
            ?? throw new InvalidOperationException("缺少 PowerShell 脚本资源。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 生成 <c>-EncodedCommand</c> 参数：不依赖脚本执行策略，也无需落盘。去掉注释与空行以缩短命令行
    /// （脚本内没有跨行字符串）；唯一的替换项是随机管道名。
    /// </summary>
    public static string Encode(string script, string pipeName)
    {
        var lines = script.ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.Trim() is { Length: > 0 } trimmed && !trimmed.StartsWith('#'));
        var body = string.Join('\n', lines).Replace("__PIPE__", pipeName);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(body));
    }

    public static NamedPipeServerStream CreateServer(string pipeName)
        => new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>CurrentUserOnly 挡住其他用户；再核对对端就是刚启动的子进程，挡住同一用户下抢先连接的其他进程。</summary>
    public static void VerifyPeer(NamedPipeServerStream channel, int expectedProcessId)
    {
        if (!GetNamedPipeClientProcessId(channel.SafePipeHandle, out var processId) || processId != (uint)expectedProcessId)
        {
            throw new ConnectionException(ConnectionErrorCode.Unknown, "PowerShell 启动通道校验失败，已中止。");
        }
    }

    /// <summary>写入若干非机密字段，随后是密码（长度 + 字符）；临时缓冲区用完即清零。</summary>
    public static async Task SendAsync(Stream channel, Action<BinaryWriter> writeFields, string? password, CancellationToken token)
    {
        var secret = (password ?? string.Empty).ToCharArray();
        using var buffer = new MemoryStream();
        try
        {
            using (var writer = new BinaryWriter(buffer, new UTF8Encoding(false), leaveOpen: true))
            {
                writeFields(writer);
                writer.Write(secret.Length);
                writer.Write(secret);
            }

            await channel.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), token);
            await channel.FlushAsync(token);
        }
        finally
        {
            Array.Clear(secret);
            Array.Clear(buffer.GetBuffer());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
