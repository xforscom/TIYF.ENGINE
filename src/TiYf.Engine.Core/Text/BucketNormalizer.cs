namespace TiYf.Engine.Core.Text;

public static class BucketNormalizer
{
    public static string? Normalize(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket)) return null;
        return bucket.Trim().ToLowerInvariant() switch
        {
            "calm" => "Calm",
            "moderate" => "Moderate",
            "volatile" => "Volatile",
            _ => null
        };
    }
}
