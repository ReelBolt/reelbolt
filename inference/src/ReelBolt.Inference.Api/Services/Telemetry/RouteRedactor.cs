namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// Reduces a client-supplied route to a bare, opaque-free pathname (alpha-launch T-018). The client
/// is untrusted: this is the enforcement point, and the browser's <c>routePath</c> is only a mirror
/// of it. Both sides are pinned to the same contract by
/// <c>Services/Telemetry/route-redaction.corpus.json</c>.
/// </summary>
/// <remarks>
/// The rule is a known-shape allowlist: a segment is kept only when it is one of the app's static
/// route words; every other segment — a project id, a document id, an invite token — becomes
/// <c>:id</c>. Nothing else can leak, because an unrecognised word is never kept verbatim. The query
/// string and fragment are dropped, and anything that is not a bare pathname (a full URL, a
/// protocol-relative URL, a <c>javascript:</c> URL, a relative path) is refused outright.
/// </remarks>
public static class RouteRedactor
{
    /// <summary>The placeholder an opaque segment is replaced with.</summary>
    public const string Placeholder = ":id";

    /// <summary>Longest route kept; the tail is dropped beyond this.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// The static route words of the app. Pinned to the corpus's <c>staticSegments</c> by
    /// <c>RouteRedactorTests</c>, so a word added on one side only fails a test rather than shipping.
    /// </summary>
    public static readonly IReadOnlySet<string> StaticSegments = new HashSet<string>(StringComparer.Ordinal)
    {
        "accept-terms", "admin", "agents", "app", "avatar-library", "billing", "brand-libraries", "calibration",
        "change-password", "checkout", "confirm", "dashboard", "data", "editor", "editor-coming-soon",
        "executions", "files", "forgot-password", "inference-providers", "invite", "links", "login",
        "marketing", "me", "media", "members", "new", "notifications", "offline", "org", "pair", "preview",
        "projects", "promotions", "reset-password", "review", "runners", "settings", "signup", "skills",
        "spike", "styles", "thumbnails", "timeline", "timelines", "unsubscribe", "usage", "users",
        "verify-email", "welcome", "workflows"
    };

    /// <summary>
    /// The redacted pathname, or the empty string when the input is not a bare pathname. Never throws.
    /// </summary>
    public static string Redact(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return string.Empty;

        string raw = route.Trim();

        int hash = raw.IndexOf('#');
        if (hash >= 0) raw = raw[..hash];
        int query = raw.IndexOf('?');
        if (query >= 0) raw = raw[..query];

        // A bare pathname only: no scheme, no host, no protocol-relative form, no relative path.
        if (!raw.StartsWith('/') || raw.StartsWith("//", StringComparison.Ordinal)) return string.Empty;

        string[] segments = raw.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0 && !StaticSegments.Contains(segments[i])) segments[i] = Placeholder;
        }

        string redacted = string.Join('/', segments);
        return redacted.Length <= MaxLength ? redacted : redacted[..MaxLength];
    }
}
