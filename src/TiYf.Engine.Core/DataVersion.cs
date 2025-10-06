using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiYf.Engine.Core;

public static class DataVersion
{
    public static string Compute(IEnumerable<string> paths)
    {
        // Concatenate canonicalized bytes (UTF8, LF line endings, trimmed trailing whitespace)
        using var sha = SHA256.Create();
        foreach (var path in paths)
        {
            using var fs = File.OpenRead(path);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.TrimEnd();
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return string.Concat(sha.Hash!.Select(b => b.ToString("X2")));
    }
}