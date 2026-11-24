namespace ReelBolt.Shared.Inference;

/// <summary>
/// Packs a <see cref="VideoEnhanceJobHandle"/> into the one string the ledger row (<c>external_generation_jobs.remote_job_id</c>,
/// at most 200 characters) can hold, and back, so a restarted attempt POLLS the vendor's existing request instead of submitting
/// again. The form is <c>{requestId}|{statusUrl}</c>; the response URL is the status URL without its trailing <c>/status</c>
/// (the queue contract), and is carried in full as a third part only when it is not. A string that does not decode (a plain id
/// from another writer, a non-https URL) answers null: never a guessed handle.
/// </summary>
public static class VideoEnhanceHandleCodec
{
    /// <summary>The longest string <see cref="Encode"/> may answer (the ledger column's width).</summary>
    public const int MaxLength = 200;

    private const char Separator = '|';
    private const string StatusSuffix = "/status";

    /// <summary>The ledger string for <paramref name="handle"/>, or null when it does not fit or carries no usable URL.</summary>
    public static string? Encode(VideoEnhanceJobHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (string.IsNullOrWhiteSpace(handle.RequestId) || handle.RequestId.Contains(Separator)
            || !IsHttps(handle.StatusUrl) || !IsHttps(handle.ResponseUrl))
            return null;

        string derived = handle.StatusUrl.EndsWith(StatusSuffix, StringComparison.Ordinal)
            ? handle.StatusUrl[..^StatusSuffix.Length]
            : string.Empty;
        string encoded = string.Equals(derived, handle.ResponseUrl, StringComparison.Ordinal)
            ? $"{handle.RequestId}{Separator}{handle.StatusUrl}"
            : $"{handle.RequestId}{Separator}{handle.StatusUrl}{Separator}{handle.ResponseUrl}";
        return encoded.Length <= MaxLength ? encoded : null;
    }

    /// <summary>The handle <paramref name="stored"/> encodes, or null.</summary>
    public static VideoEnhanceJobHandle? Decode(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        string[] parts = stored.Split(Separator);
        if (parts.Length is not (2 or 3) || string.IsNullOrWhiteSpace(parts[0]) || !IsHttps(parts[1]))
            return null;

        string response;
        if (parts.Length == 3)
        {
            response = parts[2];
        }
        else if (parts[1].EndsWith(StatusSuffix, StringComparison.Ordinal))
        {
            response = parts[1][..^StatusSuffix.Length];
        }
        else
        {
            return null;
        }

        return IsHttps(response) ? new VideoEnhanceJobHandle(parts[0], parts[1], response) : null;
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;
}
