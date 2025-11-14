namespace TiYf.Engine.Core;

public static class NewsSourceTypeHelper
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "file";
        }

        return raw.Trim().ToLowerInvariant();
    }
}
