using System.Security.Cryptography;
using System.Text;

namespace TiYf.Engine.Core;

public static class CsvCanonicalizer
{
    public static byte[] Canonicalize(ReadOnlySpan<byte> rawUtf8)
    {
        // Interpret as UTF8 (allow no BOM). Normalize line endings to \n, trim trailing spaces per line.
        var text = Encoding.UTF8.GetString(rawUtf8);
        var sb = new StringBuilder();
        using var sr = new StringReader(text.Replace("\r\n", "\n").Replace("\r", "\n"));
        string? line;
        bool first = true;
        while ((line = sr.ReadLine()) != null)
        {
            var trimmed = line.TrimEnd(' ', '\t');
            if (!first) sb.Append('\n');
            sb.Append(trimmed);
            first = false;
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static string Sha256Hex(ReadOnlySpan<byte> canonicalBytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(canonicalBytes.ToArray());
        return Convert.ToHexString(hash);
    }
}
