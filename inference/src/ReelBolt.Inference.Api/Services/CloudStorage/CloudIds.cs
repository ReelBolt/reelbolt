namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// Validation of the provider ids a request names (<c>driveId</c>, <c>folderId</c>) before anything is
/// asked of a provider (docs/design/cloud-drive-sync.md "HTTP API"): at most <see cref="MaxLength"/>
/// characters and no control characters. Providers percent-escape whatever they put into a URL; this is
/// the edge check in front of that.
/// </summary>
public static class CloudIds
{
    public const int MaxLength = 1024;

    public const string InvalidError = "invalid_id";

    /// <summary>Null, empty or whitespace normalizes to null (the default space / the root). False when the id is unacceptable.</summary>
    public static bool TryNormalize(string? raw, out string? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        string value = raw.Trim();
        if (value.Length > MaxLength) return false;
        foreach (char c in value)
        {
            if (char.IsControl(c)) return false;
        }

        id = value;
        return true;
    }
}
