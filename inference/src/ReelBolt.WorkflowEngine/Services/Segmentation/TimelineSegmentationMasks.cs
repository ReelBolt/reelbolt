using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Services.Segmentation;

/// <summary>
/// T-015: turns the segmentation masks a timeline names into verified local files for the renderer. A mask is
/// opened ONLY through <see cref="SegmentationMaskStore"/>'s key scope and hash re-verification: its storage key
/// must lie under this project's segmentation prefix, its bytes must hash to the content hash the analyze step
/// recorded, and its frame size must be its source's. Anything else is a refusal - the render never falls back
/// to the unmasked picture.
/// </summary>
public static class TimelineSegmentationMasks
{
    public sealed record Resolution(IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask> Masks, string? Refusal);

    /// <param name="document">The FLATTENED document (compound/multicam expanded).</param>
    /// <param name="assets">The clips' staged sources; their probe carries the frame size a mask must match.</param>
    /// <param name="readArtifact">Parses the analysis artifact at a key already scope-checked by <see cref="SegmentationTrackIdLookup"/>.</param>
    /// <param name="download">Downloads a project storage key to a local path.</param>
    /// <param name="probe">Probes a local mask video.</param>
    /// <param name="localPathFor">A scratch path for the n-th mask.</param>
    public static async Task<Resolution> ResolveAsync(
        EditTimeline document,
        Guid projectId,
        IReadOnlyDictionary<AssetRef, LocalAsset> assets,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyList<(Guid Id, string? ArtifactKey)>>> loadArtifactKeys,
        Func<string, CancellationToken, Task<VideoAnalysisArtifact?>> readArtifact,
        Func<string, string, CancellationToken, Task> download,
        Func<string, CancellationToken, Task<MediaProbeResult>> probe,
        Func<int, string> localPathFor,
        CancellationToken ct)
    {
        Dictionary<SegmentationMaskRef, LocalSegmentationMask> resolved = [];
        List<MediaClip> clips = TimelineAssetReferences.AllTracks(document)
            .SelectMany(t => t.Items ?? [])
            .OfType<MediaClip>()
            .Where(c => (c.Mask is { } m && m.IsSegmentationMask()) || c.RegionEffects?.Any(e => e is { Enabled: true }) == true)
            .ToList();
        if (clips.Count == 0)
            return new Resolution(resolved, null);

        IReadOnlyList<Guid> wanted = TimelineAssetReferences.SegmentationMaskStepResultIds(document);
        string prefix = SegmentationTrackIdLookup.ArtifactPrefix(projectId);
        Dictionary<Guid, VideoAnalysisArtifact> artifacts = [];
        foreach ((Guid id, string? key) in await loadArtifactKeys(wanted.Take(SegmentationTrackIdLookup.MaxStepResults).ToList(), ct))
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith(prefix, StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal))
                continue;
            try
            {
                if (await readArtifact(key, ct) is { } artifact)
                    artifacts[id] = artifact;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Unreadable analysis: its masks are unverifiable below.
            }
        }

        int index = 0;
        foreach (MediaClip clip in clips)
        {
            // The clip's own segmentation mask plus every enabled region effect's mask: one verification path, and
            // an identical (analysis, track) pair is downloaded once (the `resolved` dictionary).
            List<ShapeMask> masks = [];
            if (clip.Mask is { } own && own.IsSegmentationMask())
                masks.Add(own);
            masks.AddRange(RegionFilterGraphBuilder.EnabledRegionEffects(clip).Select(e => e.Mask));
            foreach (ShapeMask mask in masks)
            {
                if (!mask.IsSegmentationMask())
                    return Refuse(resolved, clip, "a region effect needs a segmentation mask");
                if (mask.AnalysisStepResultId is not { } analysisId || !artifacts.TryGetValue(analysisId, out VideoAnalysisArtifact? analysis))
                    return Refuse(resolved, clip, "its video analysis could not be read");
                if (clip.Stabilize)
                    return Refuse(resolved, clip, "a segmentation mask cannot be combined with stabilisation");
                if (!assets.TryGetValue(clip.AssetRef, out LocalAsset? source))
                    return Refuse(resolved, clip, "its source was not staged");

                foreach (string trackId in mask.TrackIds ?? [])
                {
                    SegmentationMaskRef reference = new(analysisId, trackId);
                    VideoSegmentationTrack? track = (analysis.SegmentationTracks ?? []).FirstOrDefault(t => string.Equals(t.Id, trackId, StringComparison.Ordinal));
                    if (track is null)
                        return Refuse(resolved, clip, $"the analysis has no track {trackId}");
                    // The shared binding rule (also run by the API validator): this clip's source, the whole source,
                    // and every source frame the clip plays - plus the #776 upright-geometry rule, which only the
                    // render can apply because it needs the staged source's probe.
                    double? fps = document.Settings is { Rate: { Den: > 0 } rate } ? (double)rate.Num / rate.Den : null;
                    if (!SegmentationTrackFacts.FromArtifact(analysis).TryGetValue(trackId, out SegmentationTrackFacts? facts))
                        return Refuse(resolved, clip, $"the analysis has no track {trackId}");
                    if (SegmentationMaskBinding.Check(clip, fps, facts, source.Probe?.AltersPixelsOnDecode) is { } unbound)
                        return Refuse(resolved, clip, unbound);
                    if (!SegmentationMaskStore.IsProjectMaskKey(projectId, track.MaskStorageKey))
                        return Refuse(resolved, clip, $"track {trackId} points outside this project's segmentation store");

                    if (!resolved.TryGetValue(reference, out LocalSegmentationMask? local))
                    {
                        string path = localPathFor(index++);
                        try
                        {
                            await download(track.MaskStorageKey, path, ct);
                        }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                            return Refuse(resolved, clip, $"track {trackId} could not be downloaded");
                        }

                        if (!await SegmentationMaskStore.VerifyHashAsync(path, track.MaskContentHash, ct))
                            return Refuse(resolved, clip, $"track {trackId} does not match the hash the analysis recorded");
                        local = new LocalSegmentationMask(path, track.MaskContentHash.Trim().ToLowerInvariant());
                        resolved[reference] = local;
                    }

                    MediaProbeResult maskProbe = await probe(local.LocalPath, ct);
                    if (maskProbe.Width != source.Probe.Width || maskProbe.Height != source.Probe.Height)
                        return Refuse(resolved, clip, $"track {trackId} is {maskProbe.Width}x{maskProbe.Height} but its source is {source.Probe.Width}x{source.Probe.Height}");
                }
            }
        }

        return new Resolution(resolved, null);
    }

    private static Resolution Refuse(IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask> resolved, MediaClip clip, string reason) =>
        new(resolved, $"Segmentation mask on clip {clip.Id} cannot be verified: {reason}. Refusing to render the clip unmasked.");
}
