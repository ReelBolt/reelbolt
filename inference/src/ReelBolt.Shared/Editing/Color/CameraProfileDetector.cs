using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Editing.Color;

/// <summary>How sure the detector is. Only <see cref="High"/> may be auto-applied by a client; <see cref="Medium"/> is a suggestion.</summary>
public enum DetectionConfidence
{
    None = 0,
    Medium = 1,
    High = 2,
}

/// <summary>Probe facts the detector looks at. Every field is optional; junk values are tolerated.</summary>
public sealed record CameraFacts(
    string? Transfer = null,
    string? Primaries = null,
    string? Matrix = null,
    string? Range = null,
    string? PixFmt = null,
    int? BitDepth = null,
    string? VideoCodec = null,
    string? Container = null,
    string? Make = null,
    string? Model = null,
    string? Software = null);

/// <summary>The detector's verdict. <see cref="ReasonCode"/> is a stable machine word, never free text or user data.</summary>
public sealed record CameraProfileHint(string? ProfileId, DetectionConfidence Confidence, string ReasonCode);

/// <summary>
/// Conservative, table-driven camera-profile detection. No standard transfer code identifies a log curve, so every rule
/// requires maker evidence (make, and where useful model) plus a consistent colour signature. Adding or tightening a
/// rule once real clips are probed is a data-only change to <see cref="Rules"/>. HLG/PQ footage is never a log profile
/// and plain BT.709/BT.601 footage without maker evidence is never matched.
/// </summary>
public static class CameraProfileDetector
{
    /// <summary>Bumped on any change to <see cref="Rules"/>; folded into the step cache key when a source's profile is "auto".</summary>
    public const string RuleVersion = "camera-detect-v1";

    // Profile ids (kept in step with CameraProfiles; deliberately not referenced from it).
    private const string AppleLog = "appleLog";
    private const string DjiDLogM = "djiDLogM";
    private const string SonySLog3 = "sonySLog3";
    private const string PanasonicVLog = "panasonicVLog";
    private const string CanonCLog3 = "canonCLog3";
    private const string ArriLogC3 = "arriLogC3";
    private const string ArriLogC4 = "arriLogC4";
    private const string FujiFLog = "fujiFLog";
    private const string NikonNLog = "nikonNLog";

    private const string Unspecified = "";

    private static readonly string[] HdrTransfers = ["arib-std-b67", "smpte2084"];
    private static readonly string[] OpenTransfers = [Unspecified, "bt709", "smpte170m", "bt470bg", "iec61966-2-1"];
    private static readonly string[] Prores = ["prores"];
    private static readonly string[] Bt2020Matrices = ["bt2020nc", "bt2020c"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>One detection rule. Null constraint = not checked. Rules are evaluated in order; the first match wins.</summary>
    private sealed record Rule(
        string ReasonCode,
        string ProfileId,
        DetectionConfidence Confidence,
        Regex Make,
        Regex? Model = null,
        string[]? Transfers = null,
        string[]? Primaries = null,
        string[]? Matrices = null,
        string[]? Codecs = null,
        int MinBitDepth = 10);

    private static readonly Rule[] Rules =
    [
        // Apple Log: BT.2020 primaries + matrix, unspecified transfer, ProRes, from an iPhone 15 Pro class model.
        new("apple_make_model_bt2020_prores", AppleLog, DetectionConfidence.High, Rx(@"^apple$"), Rx(@"^iphone (1[5-9]|[2-9]\d)\b.*\bpro\b"),
            Transfers: [Unspecified], Primaries: ["bt2020"], Matrices: Bt2020Matrices, Codecs: Prores),
        // Weaker: HEVC or ProRes with any matrix.
        new("apple_make_model_bt2020", AppleLog, DetectionConfidence.Medium, Rx(@"^apple$"), Rx(@"^iphone (1[5-9]|[2-9]\d)\b.*\bpro\b"),
            Transfers: [Unspecified], Primaries: ["bt2020"], Codecs: ["prores", "hevc"]),
        // No published tag signature for the rest: maker (and model family) plus 10-bit and a non-HDR transfer only, so Medium.
        new("dji_make_10bit", DjiDLogM, DetectionConfidence.Medium, Rx(@"^dji\b"), Transfers: OpenTransfers),
        new("sony_make_model_10bit", SonySLog3, DetectionConfidence.Medium, Rx(@"^sony\b"), Rx(@"^(ilce|ilme|fx|zv|pxw)"), Transfers: OpenTransfers),
        new("panasonic_make_model_10bit", PanasonicVLog, DetectionConfidence.Medium, Rx(@"^panasonic\b"), Rx(@"^dc-(gh|s|bs|bgh)"), Transfers: OpenTransfers),
        new("canon_make_model_10bit", CanonCLog3, DetectionConfidence.Medium, Rx(@"^canon\b"), Rx(@"(eos r(5|6|7|3|1|8|10)?\b|\bc(50|70|80|100|200|300|400|500)\b)"), Transfers: OpenTransfers),
        new("arri_alexa35_make_model", ArriLogC4, DetectionConfidence.Medium, Rx(@"^arri\b"), Rx(@"alexa\s*35"), Transfers: OpenTransfers),
        new("arri_make_10bit", ArriLogC3, DetectionConfidence.Medium, Rx(@"^arri\b"), Transfers: OpenTransfers),
        new("fuji_make_model_10bit", FujiFLog, DetectionConfidence.Medium, Rx(@"^fuji"), Rx(@"^(x-(h2|h2s|t5|s20|t50)|gfx)"), Transfers: OpenTransfers),
        new("nikon_make_model_10bit", NikonNLog, DetectionConfidence.Medium, Rx(@"^nikon\b"), Rx(@"^(nikon\s+)?z\s?(\d|f\b|fc\b)"), Transfers: OpenTransfers),
    ];

    private static readonly CameraProfileHint None = new(null, DetectionConfidence.None, "no_match");

    public static CameraProfileHint Detect(CameraFacts? facts)
    {
        if (facts is null) return None;

        string transfer = Normalise(facts.Transfer);
        if (Array.IndexOf(HdrTransfers, transfer) >= 0) return new(null, DetectionConfidence.None, "hdr_transfer");

        string make = Clean(facts.Make);
        if (make.Length == 0) return None;
        string model = Clean(facts.Model);
        string primaries = Normalise(facts.Primaries);
        string matrix = Normalise(facts.Matrix);
        string codec = Normalise(facts.VideoCodec);
        int depth = facts.BitDepth ?? BitDepthOf(facts.PixFmt) ?? 0;

        foreach (Rule rule in Rules)
        {
            if (depth < rule.MinBitDepth) continue;
            if (!IsMatch(rule.Make, make)) continue;
            if (rule.Model is not null && !IsMatch(rule.Model, model)) continue;
            if (rule.Transfers is not null && Array.IndexOf(rule.Transfers, transfer) < 0) continue;
            if (rule.Primaries is not null && Array.IndexOf(rule.Primaries, primaries) < 0) continue;
            if (rule.Matrices is not null && Array.IndexOf(rule.Matrices, matrix) < 0) continue;
            if (rule.Codecs is not null && Array.IndexOf(rule.Codecs, codec) < 0) continue;
            return new(rule.ProfileId, rule.Confidence, rule.ReasonCode);
        }

        return None;
    }

    private static bool IsMatch(Regex rx, string value)
    {
        try { return rx.IsMatch(value); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    // Lower-cased, trimmed; "unknown"/"unspecified"/null all collapse to the empty string.
    private static string Normalise(string? value)
    {
        string v = Clean(value).ToLowerInvariant();
        return v is "unknown" or "unspecified" or "reserved" ? Unspecified : v;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string v = new(value.Where(c => !char.IsControl(c)).ToArray());
        v = v.Trim();
        return v.Length > 64 ? v[..64] : v;
    }

    private static int? BitDepthOf(string? pixFmt)
    {
        Match m = Regex.Match(pixFmt ?? string.Empty, @"p(9|10|12|14|16)(le|be)?$", RegexOptions.CultureInvariant, RegexTimeout);
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
