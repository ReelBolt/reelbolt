using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>The sanitized key of one violation report: the only thing that is ever stored.</summary>
public sealed record CspViolationKey(
    string ReportType, string Disposition, string EffectiveDirective, string BlockedOrigin, string DocumentPath, string UaFamily);

/// <summary>
/// Turns the three browser report shapes into <see cref="CspViolationKey"/>s. Every field is cut to a
/// length cap first, then reduced to a closed vocabulary, a scheme+host, or a path with every id-like
/// segment replaced. Query strings and fragments are never read.
/// </summary>
public static partial class CspReportSanitizer
{
    /// <summary>The largest body read, in bytes.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>Reports taken from one Reporting API array.</summary>
    public const int MaxReportsPerRequest = 10;

    public const string Other = "other";
    public const string Sensitive = "(sensitive)";
    public const string Overflow = "(overflow)";
    public const int MaxOriginLength = 100;
    public const int MaxPathLength = 200;

    private const int MaxInputLength = 2048;

    private static readonly HashSet<string> Directives = new(StringComparer.Ordinal)
    {
        "default-src", "script-src", "script-src-elem", "script-src-attr", "style-src", "style-src-elem", "style-src-attr",
        "img-src", "font-src", "connect-src", "media-src", "object-src", "frame-src", "child-src", "worker-src",
        "manifest-src", "prefetch-src", "base-uri", "form-action", "frame-ancestors", "navigate-to", "sandbox",
        "upgrade-insecure-requests", "require-trusted-types-for", "trusted-types",
    };

    private static readonly HashSet<string> BlockedKeywords = new(StringComparer.Ordinal)
    {
        "inline", "eval", "wasm-eval", "data", "blob", "self",
    };

    private static readonly string[] SensitivePrefixes =
    [
        "/app/review/g", "/api/v1/review-links", "/api/v1/output-links", "/oauth",
    ];

    private static readonly string[] TokenRouteWords = ["reset", "verify", "accept", "invite", "email-change", "confirm"];

    /// <summary>Whether the content type (parameters ignored) is one of the three accepted shapes.</summary>
    public static bool IsAcceptedContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        string type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return type is "application/csp-report" or "application/reports+json" or "application/json";
    }

    /// <summary>Parses a body into keys. Malformed input yields none; never throws.</summary>
    public static IReadOnlyList<CspViolationKey> Parse(ReadOnlySpan<byte> body, string? userAgent)
    {
        string ua = UaFamily(userAgent);
        List<CspViolationKey> keys = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray().Take(MaxReportsPerRequest))
                    AddReportingApi(item, ua, keys);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("csp-report", out JsonElement legacy) && legacy.ValueKind == JsonValueKind.Object)
                    keys.Add(FromBody(legacy, "csp", ua));
                else if (root.TryGetProperty("body", out _) && root.TryGetProperty("type", out _))
                    AddReportingApi(root, ua, keys);
                else if (root.TryGetProperty("blocked-uri", out _) || root.TryGetProperty("blockedURL", out _)
                    || root.TryGetProperty("effective-directive", out _) || root.TryGetProperty("effectiveDirective", out _)
                    || root.TryGetProperty("violated-directive", out _))
                    keys.Add(FromBody(root, "csp", ua));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return keys;
    }

    private static void AddReportingApi(JsonElement item, string ua, List<CspViolationKey> keys)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("body", out JsonElement body) || body.ValueKind != JsonValueKind.Object)
            return;
        string? type = Text(item, "type");
        if (type == "csp-violation") keys.Add(FromBody(body, "csp", ua));
        else if (type == "coep") keys.Add(FromBody(body, "coep", ua));
    }

    private static CspViolationKey FromBody(JsonElement body, string reportType, string ua)
    {
        string directive = reportType == "coep"
            ? "coep"
            : Directive(Text(body, "effective-directive") ?? Text(body, "effectiveDirective")
                ?? Text(body, "violated-directive") ?? Text(body, "violatedDirective"));
        string disposition = (Text(body, "disposition") ?? string.Empty).ToLowerInvariant() switch
        {
            "report" => "report",
            "enforce" => "enforce",
            _ => "unknown",
        };
        return new CspViolationKey(
            reportType,
            disposition,
            directive,
            BlockedOrigin(Text(body, "blocked-uri") ?? Text(body, "blockedURL") ?? Text(body, "blockedUri")),
            DocumentPath(Text(body, "document-uri") ?? Text(body, "documentURL") ?? Text(body, "documentUri")),
            ua);
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? Cut(v.GetString(), MaxInputLength)
            : null;

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    /// <summary>The directive name: the first token (a violated-directive carries its policy), allowlisted.</summary>
    public static string Directive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Other;
        string first = Cut(value.Trim().ToLowerInvariant(), 64)!.Split([' ', '\t'], 2)[0];
        return Directives.Contains(first) ? first : Other;
    }

    /// <summary>Scheme+host(+port) of the blocked resource, or a keyword, or <c>other</c>. Never a path or query.</summary>
    public static string BlockedOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Other;
        string v = Cut(value.Trim(), MaxInputLength)!;
        string lower = v.ToLowerInvariant();
        if (BlockedKeywords.Contains(lower)) return lower;
        if (lower.StartsWith("data:", StringComparison.Ordinal)) return "data";
        if (lower.StartsWith("blob:", StringComparison.Ordinal)) return "blob";
        int sep = lower.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return Other;
        string scheme = lower[..sep];
        if (scheme is not ("http" or "https" or "ws" or "wss")) return Other;
        string rest = lower[(sep + 3)..];
        int end = rest.IndexOfAny(['/', '?', '#']);
        string authority = end < 0 ? rest : rest[..end];
        int at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        if (!HostPattern().IsMatch(authority)) return Other;
        string origin = scheme + "://" + authority;
        return origin.Length > MaxOriginLength ? Other : origin;
    }

    /// <summary>The document path with id-like segments replaced by <c>:id</c>, or <c>(sensitive)</c>, or <c>other</c>.</summary>
    public static string DocumentPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Other;
        string v = Cut(value.Trim(), MaxInputLength)!;
        int sep = v.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return Other;
        string scheme = v[..sep].ToLowerInvariant();
        if (scheme is not ("http" or "https")) return Other;
        string rest = v[(sep + 3)..];
        int slash = rest.IndexOf('/');
        int stop = rest.IndexOfAny(['?', '#']);
        if (slash < 0 || (stop >= 0 && stop < slash)) return "/";
        string path = rest[slash..];
        int cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];

        // nginx normalises doubled slashes and %2F, so a crafted report must be judged the same way.
        path = path.Replace("%2f", "/", StringComparison.OrdinalIgnoreCase).Replace("%5c", "/", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/');
        string lower = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        foreach (string prefix in SensitivePrefixes)
        {
            if (lower == prefix || lower.StartsWith(prefix + "/", StringComparison.Ordinal)) return Sensitive;
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool apiOrApp = lower.StartsWith("/app/", StringComparison.Ordinal) || lower == "/app"
            || lower.StartsWith("/api/", StringComparison.Ordinal);
        if (apiOrApp && segments.Any(IsTokenRouteSegment)) return Sensitive;

        string joined = "/" + string.Join('/', segments.Select(s => LooksLikeId(s) ? ":id" : s.ToLowerInvariant()));
        if (path.EndsWith('/') && joined.Length > 1) joined += "/";
        if (joined.Length > MaxPathLength) joined = joined[..MaxPathLength];
        return joined;
    }

    private static bool IsTokenRouteSegment(string segment)
    {
        string s = segment.ToLowerInvariant();
        foreach (string word in TokenRouteWords)
        {
            if (s == word || s.StartsWith(word + "-", StringComparison.Ordinal) || s.StartsWith(word + "_", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool LooksLikeId(string segment)
    {
        if (segment.Length > 40) return true;
        if (segment.Contains('@') || segment.Contains("%40", StringComparison.OrdinalIgnoreCase)) return true;
        if (DigitsPattern().IsMatch(segment)) return true;
        string hex = segment.Replace("-", string.Empty);
        if (HexPattern().IsMatch(hex) && (hex.Length >= 16 || (hex.Length >= 8 && hex.Any(char.IsDigit)))) return true;
        if (segment.Length >= 20 && TokenPattern().IsMatch(segment)
            && segment.Any(char.IsDigit) && segment.Any(char.IsLetter) && segment.Count(c => c == '-') <= 2)
            return true;
        return false;
    }

    /// <summary>The browser family from the User-Agent header, from a closed list. The raw value is never kept.</summary>
    public static string UaFamily(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return Other;
        string ua = Cut(userAgent, 512)!;
        if (ua.Contains("Edg/", StringComparison.Ordinal) || ua.Contains("EdgA/", StringComparison.Ordinal)
            || ua.Contains("EdgiOS/", StringComparison.Ordinal)) return "edge";
        if (ua.Contains("Firefox/", StringComparison.Ordinal) || ua.Contains("FxiOS/", StringComparison.Ordinal)) return "firefox";
        if (ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal)
            || ua.Contains("Chromium/", StringComparison.Ordinal)) return "chrome";
        if (ua.Contains("Safari/", StringComparison.Ordinal)) return "safari";
        return Other;
    }

    [GeneratedRegex(@"^[a-z0-9.\-]+(:\d{1,5})?$|^\[[0-9a-f:.]+\](:\d{1,5})?$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]+$")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-+=.%]+$")]
    private static partial Regex TokenPattern();
}
