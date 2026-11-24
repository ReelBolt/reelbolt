using ReelBolt.Shared.Editing.Color;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// log-footage-color T-013: per-source camera-profile conversion for the VideoCompile and VideoAnalyze steps.
/// A source's profile rides the same per-source "kind" string the HDR tone-map uses, so every place that
/// already prepends the HDR stage prepends this one instead (the two are mutually exclusive; HDR wins).
/// </summary>
public static class SourceCameraProfile
{
    /// <summary>The config word meaning "apply only when the detector is High".</summary>
    public const string Auto = "auto";

    /// <summary>The warning code for a resolved profile that was not applied (VideoAnalyze).</summary>
    public const string NotAppliedWarning = "camera_profile_not_applied";

    public const string SourceExplicit = "explicit";
    public const string SourceAuto = "auto";

    private const string KindPrefix = "camera-profile|";

    /// <summary>The ignore warning's plain-language text (the code is <see cref="CameraProfileStage.IgnoredHdrWarning"/>).</summary>
    public const string IgnoredHdrMessage = CameraProfileStage.IgnoredHdrMessage;

    /// <summary>One resolved per-source choice.</summary>
    public sealed record Choice(string ProfileId, string Source);

    /// <summary>
    /// The profile to apply for a source, or null. Null when the config names none for it, the source is audio-only,
    /// an <c>auto</c> request is not High confidence, or the id is unknown. HDR handling is the caller's job.
    /// </summary>
    public static Choice? Pick(string? requested, MediaProbeResult? probe)
    {
        if (string.IsNullOrWhiteSpace(requested) || probe is null) return null;
        if (probe.Width <= 0 || probe.Height <= 0 || probe.VideoCodec is null) return null;

        if (string.Equals(requested, Auto, StringComparison.Ordinal))
        {
            CameraProfileHint hint = CameraProfileDetector.Detect(FactsOf(probe));
            return hint.Confidence == DetectionConfidence.High && CameraProfiles.IsKnown(hint.ProfileId)
                ? new Choice(hint.ProfileId!, SourceAuto)
                : null;
        }

        return CameraProfiles.IsKnown(requested) ? new Choice(requested, SourceExplicit) : null;
    }

    public static CameraFacts FactsOf(MediaProbeResult probe) => new(
        Transfer: probe.ColorTransfer,
        Primaries: probe.ColorPrimaries,
        Matrix: probe.ColorSpace,
        Range: probe.ColorRange,
        PixFmt: probe.PixFmt,
        BitDepth: probe.BitDepth,
        VideoCodec: probe.VideoCodec,
        Container: probe.Container,
        Make: probe.Make,
        Model: probe.Model,
        Software: probe.Software);

    /// <summary>The per-source kind string for a profile: carries the cube path and whether zscale is available.</summary>
    public static string EncodeKind(string profileId, string cubePath, bool useZscale) =>
        $"{KindPrefix}{profileId}|{(useZscale ? "z" : "s")}|{cubePath}";

    public static bool IsKind(string? kind) => kind is not null && kind.StartsWith(KindPrefix, StringComparison.Ordinal);

    private static (string ProfileId, bool Zscale, string CubePath) Decode(string kind)
    {
        string[] parts = kind[KindPrefix.Length..].Split('|', 3);
        return (parts[0], parts[1] == "z", parts[2]);
    }

    /// <summary>The stage for a kind: the camera-profile filter or the HDR tone-map.</summary>
    public static string Filter(string kind)
    {
        if (!IsKind(kind)) return HdrToneMap.Filter(kind);
        (string id, bool z, string cube) = Decode(kind);
        return CameraProfileStage.Filter(id, cube, z);
    }

    /// <summary>The cheap analysis counterpart: the profile stage ends in yuv420p already, so it is the same filter.</summary>
    public static string AnalysisFilter(string kind) => IsKind(kind) ? Filter(kind) : HdrToneMap.AnalysisFilter(kind);

    /// <summary>The recipe token for a profile kind, or null for an HDR kind.</summary>
    public static string? RecipeTokenOf(string? kind) => IsKind(kind) ? CameraProfileStage.RecipeToken(Decode(kind!).ProfileId) : null;

    /// <summary>
    /// Step-cache-key component for a config's <c>SourceInputProfiles</c>, or null when it names none (so such a step's
    /// key stays byte-identical). Folds the stage recipe and table versions, and the detector rule version only when any
    /// value is "auto", so a formula or rule change cannot replay a stale result.
    /// </summary>
    public static string? CacheKeyComponent(IReadOnlyDictionary<int, string>? profiles)
    {
        if (profiles is not { Count: > 0 }) return null;
        string entries = string.Join(",", profiles.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
        string component = $"{CameraProfileStage.RecipeVersion}|{CameraProfileTable.TableVersion}|{entries}";
        if (profiles.Values.Any(v => string.Equals(v, Auto, StringComparison.Ordinal)))
            component += $"|{CameraProfileDetector.RuleVersion}";
        return component;
    }

    /// <summary>The component for a step's raw config JSON (VideoCompile or VideoAnalyze); null when none or unparsable.</summary>
    public static string? CacheKeyComponentFromJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson) || !configJson.Contains("ourceInputProfiles", StringComparison.Ordinal))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(configJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "sourceInputProfiles", StringComparison.OrdinalIgnoreCase) ||
                    prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                var map = new Dictionary<int, string>();
                foreach (var e in prop.Value.EnumerateObject())
                    if (int.TryParse(e.Name, out int idx) && e.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        map[idx] = e.Value.GetString()!;
                return CacheKeyComponent(map);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }
}
