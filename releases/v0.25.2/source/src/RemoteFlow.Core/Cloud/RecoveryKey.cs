using System.Security.Cryptography;
using System.Text;

namespace RemoteFlow.Core.Cloud;

/// <summary>
/// 256-bit Recovery Key。生成时给用户看的是分组 Base32（RFC 4648，去掉易混字符无关——用标准表），
/// 只显示一次，服务端只存 信封，不存明文（安全设计 §8）。
/// </summary>
public sealed class RecoveryKey
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int KeyBytes = 32;
    private const int GroupSize = 4;

    private readonly byte[] _bytes;

    private RecoveryKey(byte[] bytes) => _bytes = bytes;

    public static RecoveryKey Generate() => new(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>从用户输入解析。允许空格 / 连字符 / 大小写混排。格式非法抛 <see cref="FormatException"/>。</summary>
    public static RecoveryKey Parse(string input)
    {
        var cleaned = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch is ' ' or '-' or '\t' or '\r' or '\n')
            {
                continue;
            }

            var upper = char.ToUpperInvariant(ch);
            var value = Alphabet.IndexOf(upper);
            if (value < 0)
            {
                throw new FormatException($"Recovery key contains an invalid character '{ch}'.");
            }

            cleaned.Append(upper);
        }

        var bits = 0;
        var accumulator = 0;
        var output = new List<byte>(KeyBytes);
        foreach (var ch in cleaned.ToString())
        {
            accumulator = (accumulator << 5) | Alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((accumulator >> bits) & 0xFF));
            }
        }

        if (output.Count != KeyBytes)
        {
            throw new FormatException($"Recovery key must decode to {KeyBytes} bytes, got {output.Count}.");
        }

        return new RecoveryKey([.. output]);
    }

    /// <summary>只读原始字节，用于 KDF。</summary>
    public ReadOnlySpan<byte> AsSpan() => _bytes;

    /// <summary>给用户展示 / 保存的分组字符串，例如 <c>ABCD EFGH ... </c>（13 组 4 字符）。</summary>
    public string ToDisplayString()
    {
        var raw = Encode(_bytes);
        var builder = new StringBuilder(raw.Length + raw.Length / GroupSize);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0)
            {
                builder.Append(' ');
            }

            builder.Append(raw[i]);
        }

        return builder.ToString();
    }

    private static string Encode(byte[] data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        var bits = 0;
        var accumulator = 0;
        foreach (var b in data)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(accumulator >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(accumulator << (5 - bits)) & 0x1F]);
        }

        return builder.ToString();
    }
}
