using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// The one sanitizer for acquisition attribution fields (product-analytics contract). Every input is
/// lowercased and trimmed first; a value that does not match its shape is DROPPED (null), never
/// rejected and never stored. No query string, port, path-with-query or free text survives.
/// </summary>
public static class AttributionSanitizer
{
    /// <summary>The widest a source label may be (the column width of detail and source_label).</summary>
    public const int MaxLabelLength = 64;

    // Every shape ends with \z, never $: in .NET a $ also matches just before a final line feed, so
    // "/pricing\n" would pass a $ shape with the line feed still in the value. \z is the end of the
    // string and nothing else, which is also what the Go API's sanitizer means by $ (RE2).
    private static readonly Regex UtmShape = new(
        @"^[a-z0-9][a-z0-9._~+-]{0,63}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HostShape = new(
        @"^[a-z0-9][a-z0-9.-]{0,99}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PathShape = new(
        @"^/[a-z0-9/_~.-]{0,119}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DetailShape = new(
        @"^[a-z0-9][a-z0-9:._-]{0,63}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A visitor event's own detail token (the page key of <c>site.page_view</c>, the CTA location of
    /// <c>site.cta_click</c>): <c>^[a-z0-9][a-z0-9:._-]{0,63}$</c>, or null. Unlike the attribution
    /// fields it is compared as sent, with no trimming or lowercasing: the token IS the row's meaning, so
    /// the caller refuses a value that is not one instead of storing a guess.
    /// </summary>
    public static string? DetailToken(string? raw) =>
        raw is { Length: >= 1 and <= MaxLabelLength } && DetailShape.IsMatch(raw) ? raw : null;

    /// <summary>A utm_* value, or null when it does not match.</summary>
    public static string? Utm(string? raw)
    {
        string? v = Normalize(raw);
        return v is not null && UtmShape.IsMatch(v) ? v : null;
    }

    /// <summary>
    /// A utm value that is additionally dropped when it equals one of the site's own hosts (or a subdomain):
    /// the site sends its label in <c>source</c>, and a self-label is not an acquisition source.
    /// </summary>
    public static string? Utm(string? raw, IEnumerable<string>? ownHosts)
    {
        string? v = Utm(raw);
        return v is not null && IsOwnHost(v, ownHosts) ? null : v;
    }

    /// <summary>
    /// A referrer hostname (no scheme, port or path), or null when malformed or when it is one of the
    /// site's own hosts (or a subdomain of one) - a self-referral is not an acquisition source.
    /// </summary>
    public static string? ReferrerHost(string? raw, IEnumerable<string>? ownHosts = null)
    {
        string? v = Normalize(raw);
        if (v is null || !HostShape.IsMatch(v)) return null;
        return IsOwnHost(v, ownHosts) ? null : v;
    }

    private static bool IsOwnHost(string v, IEnumerable<string>? ownHosts)
    {
        if (ownHosts is null) return false;
        foreach (string own in ownHosts)
        {
            string? o = Normalize(own);
            if (o is null) continue;
            if (v == o || v.EndsWith("." + o, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// A landing path with its query and fragment stripped first, or null when it does not match. What is
    /// left after the cut must be the whole shape: white space or a control character that sat in front of
    /// the <c>?</c> (a line feed, say) is not trimmed away a second time, it drops the value, exactly as
    /// the Go API's sanitizer does. The result is therefore never wider than the column (120) and never
    /// carries a character outside the shape.
    /// </summary>
    public static string? LandingPath(string? raw)
    {
        string? v = Normalize(raw);
        if (v is null) return null;
        int cut = v.IndexOfAny(['?', '#']);
        if (cut >= 0) v = v[..cut];
        // A protocol-relative path ("//host") is not a path on this site.
        return PathShape.IsMatch(v) && !v.StartsWith("//", StringComparison.Ordinal) ? v : null;
    }

    /// <summary>
    /// <c>utm_source ?? referrerHost ?? "direct"</c> from already-sanitized values. A referrer host wider
    /// than <see cref="MaxLabelLength"/> cannot be a label and falls through to <c>direct</c>.
    /// </summary>
    public static string SourceLabel(string? utmSource, string? referrerHost)
    {
        if (utmSource is not null) return utmSource;
        if (referrerHost is not null && referrerHost.Length <= MaxLabelLength) return referrerHost;
        return "direct";
    }

    /// <summary>A random UUID v4 in canonical form, or null (any other version, format or nil).</summary>
    public static Guid? VisitorId(string? raw)
    {
        if (raw is null || raw.Length != 36 || !Guid.TryParseExact(raw, "D", out Guid id)) return null;
        // Canonical text: the version nibble is the first char of group 3, the variant the first of group 4.
        return raw[14] == '4' && "89abAB".Contains(raw[19]) ? id : null;
    }

    private static string? Normalize(string? raw)
    {
        if (raw is null) return null;
        string v = raw.Trim().ToLowerInvariant();
        return v.Length == 0 ? null : v;
    }
}
