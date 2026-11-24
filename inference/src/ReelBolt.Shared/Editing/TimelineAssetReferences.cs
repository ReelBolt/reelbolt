namespace ReelBolt.Shared.Editing;

/// <summary>P7/P8: the asset references a document carries — media sources (root, nested and multicam angles) and LUT files.</summary>
public static class TimelineAssetReferences
{
    /// <summary>Every LUT file a grade in the document names — on clips, colour groups and adjustment clips, in the root tracks and in every sequence.</summary>
    public static IEnumerable<AssetRef> LutFiles(EditTimeline document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (Track track in AllTracks(document))
        {
            foreach (Item item in track.Items ?? [])
            {
                GradeStack? stack = item switch
                {
                    MediaClip clip => clip.Grade,
                    AdjustmentClip adjustment => adjustment.Grade,
                    _ => null
                };
                foreach (AssetRef lut in LutsOf(stack)) yield return lut;
            }
        }

        foreach (ColorGroup group in document.ColorGroups ?? [])
        {
            foreach (AssetRef lut in LutsOf(group.Grade)) yield return lut;
        }
    }

    /// <summary>
    /// P8: every media source the document can play — media clips and insert assets on the root
    /// tracks, the same inside every sequence, and every multicam angle — so ownership validation
    /// and asset staging see what the flattened document will read.
    /// </summary>
    public static IEnumerable<AssetRef> MediaSources(EditTimeline document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (Track track in AllTracks(document))
        {
            foreach (Item item in track.Items ?? [])
            {
                switch (item)
                {
                    case MediaClip clip:
                        yield return clip.AssetRef;
                        break;
                    case InsertClip { AssetRef: { } insertRef }:
                        yield return insertRef;
                        break;
                    case MulticamClip multicam:
                        foreach (MulticamAngle? angle in multicam.Angles ?? [])
                        {
                            if (angle?.AssetRef is { } angleRef) yield return angleRef;
                        }
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Every step result an item's provenance names (the step that decided a clip seeded from a
    /// run), across the root tracks and every sequence. The validator resolves these through the
    /// same ownership map as <see cref="AssetRefKind.StepResult"/> sources, so every caller that
    /// builds that map (the API's PATCH validation and the engine's render-time re-validation) must
    /// include them; a missing one reads as "unknown to this project".
    /// </summary>
    public static IEnumerable<Guid> ProvenanceStepResultIds(EditTimeline document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (Track track in AllTracks(document))
        {
            foreach (Item? item in track.Items ?? [])
            {
                if (item?.Provenance?.StepResultId is { } id) yield return id;
            }
        }
    }

    /// <summary>
    /// The distinct VideoAnalyze step-result ids named by segmentation masks (<c>mask.kind == "segmentation"</c>)
    /// on media clips, across the root tracks and every sequence. Feeds <see cref="SegmentationTrackIdLookup"/>.
    /// </summary>
    public static IReadOnlyList<Guid> SegmentationMaskStepResultIds(EditTimeline document)
    {
        ArgumentNullException.ThrowIfNull(document);
        HashSet<Guid> ids = [];
        foreach (Track track in AllTracks(document))
        {
            foreach (Item? item in track.Items ?? [])
            {
                if (item is not MediaClip clip) continue;
                if (clip.Mask is { } mask && mask.IsSegmentationMask() && mask.AnalysisStepResultId is { } id && id != Guid.Empty)
                {
                    ids.Add(id);
                }

                foreach (RegionClipEffect? effect in clip.RegionEffects ?? [])
                {
                    if (effect?.Mask is { } m && m.IsSegmentationMask() && m.AnalysisStepResultId is { } rid && rid != Guid.Empty)
                    {
                        ids.Add(rid);
                    }
                }
            }
        }

        return ids.ToList();
    }

    /// <summary>True when any media clip (root or inside a sequence) carries a segmentation mask.</summary>
    public static bool HasSegmentationMask(EditTimeline document) =>
        AllTracks(document).Any(t => (t.Items ?? []).Any(i => i is MediaClip { Mask: { } m } && m.IsSegmentationMask()));

    /// <summary>The root tracks followed by every sequence's tracks (nulls skipped).</summary>
    public static IEnumerable<Track> AllTracks(EditTimeline document)
    {
        foreach (Track? track in document.Tracks ?? [])
        {
            if (track is not null) yield return track;
        }

        foreach (EditSequence? sequence in document.Sequences ?? [])
        {
            foreach (Track? track in sequence?.Tracks ?? [])
            {
                if (track is not null) yield return track;
            }
        }
    }

    private static IEnumerable<AssetRef> LutsOf(GradeStack? stack)
    {
        if (stack?.Nodes is not { } nodes) yield break;
        foreach (GradeNode node in nodes)
        {
            if (node.Lut is { } lut) yield return lut.File;
        }
    }
}
