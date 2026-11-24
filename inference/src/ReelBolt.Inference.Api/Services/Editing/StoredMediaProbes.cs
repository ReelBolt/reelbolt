using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing.Color;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// Reads the probes the engine's derivative run stored on <c>media_derivatives.source_probe_json</c>
/// (probe at ingest, R05.1): one probe per source storage key, from this project's own live rows.
/// </summary>
/// <remarks>
/// One bounded query for a whole set of keys, never one per source. Every row of one source content
/// carries the same probe, so the newest readable one per key is taken; a row whose JSON cannot be
/// read is skipped rather than failing the caller (the media pool is a listing).
/// </remarks>
public static class StoredMediaProbes
{
    public static async Task<Dictionary<string, MediaSourceProbe>> LoadAsync(
        InferenceApiDbContext db,
        Guid projectId,
        IReadOnlyCollection<string> storageKeys,
        CancellationToken ct)
    {
        Dictionary<string, MediaSourceProbe> byKey = new(StringComparer.Ordinal);
        if (storageKeys.Count == 0) return byKey;

        List<string> keys = storageKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();

        var rows = await db.MediaDerivatives
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId &&
                        d.SourceProbeJson != null &&
                        keys.Contains(d.SourceStorageKey))
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.SourceStorageKey, d.SourceProbeJson })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (byKey.ContainsKey(row.SourceStorageKey)) continue;
            if (MediaSourceProbe.TryParse(row.SourceProbeJson) is { } probe)
                byKey[row.SourceStorageKey] = probe;
        }

        return byKey;
    }

    /// <summary>The pool's wire shape for a stored probe. Every field maps one-for-one.</summary>
    public static MediaProbeResponse ToResponse(MediaSourceProbe probe) => new(
        probe.DurationSec,
        probe.FpsNum,
        probe.FpsDen,
        probe.Width,
        probe.Height,
        string.IsNullOrWhiteSpace(probe.VideoCodec) ? null : probe.VideoCodec,
        string.IsNullOrWhiteSpace(probe.AudioCodec) ? null : probe.AudioCodec,
        string.IsNullOrWhiteSpace(probe.Container) ? null : probe.Container,
        probe.AudioChannels,
        probe.AudioSampleRateHz,
        probe.BitrateBps,
        probe.HasAudio,
        probe.ColorTransfer,
        probe.ColorPrimaries,
        probe.Hdr,
        probe.ColorSpace,
        probe.ColorRange,
        probe.PixFmt,
        probe.BitDepth,
        probe.Make,
        probe.Model,
        HintFor(probe));

    /// <summary>
    /// For the seeders: the camera profile id for a source asset whose stored probe the detector is High on, else null.
    /// Medium hints and sources with no stored probe give nothing, so an unhinted timeline is seeded exactly as before.
    /// </summary>
    public static Func<ReelBolt.Shared.Editing.AssetRef, string?> InputProfileResolver(
        IReadOnlyDictionary<string, MediaSourceProbe> probesByKey, Func<string, ReelBolt.Shared.Editing.AssetRef?> resolveKey)
    {
        Dictionary<ReelBolt.Shared.Editing.AssetRef, string> byAsset = [];
        foreach ((string key, MediaSourceProbe probe) in probesByKey)
        {
            if (HighProfileOf(probe) is { } profile && resolveKey(key) is { } asset)
                byAsset[asset] = profile;
        }

        return asset => byAsset.GetValueOrDefault(asset);
    }

    /// <summary>The profile id when the detector is High confidence for this probe, else null.</summary>
    public static string? HighProfileOf(MediaSourceProbe probe) =>
        HintFor(probe) is { Confidence: "high", Profile: { Length: > 0 } id } ? id : null;

    /// <summary>The camera-profile hint for a stored probe, or null when the detector finds nothing.</summary>
    public static CameraProfileHintResponse? HintFor(MediaSourceProbe probe)
    {
        CameraProfileHint hint = CameraProfileDetector.Detect(new CameraFacts(
            Transfer: probe.ColorTransfer,
            Primaries: probe.ColorPrimaries,
            Matrix: probe.ColorSpace,
            Range: probe.ColorRange,
            PixFmt: probe.PixFmt,
            BitDepth: probe.BitDepth,
            VideoCodec: probe.VideoCodec,
            Container: probe.Container,
            Make: probe.Make,
            Model: probe.Model));
        return hint.Confidence switch
        {
            DetectionConfidence.High => new CameraProfileHintResponse(hint.ProfileId, "high", hint.ReasonCode),
            DetectionConfidence.Medium => new CameraProfileHintResponse(hint.ProfileId, "medium", hint.ReasonCode),
            _ => null,
        };
    }
}
