namespace ReelBolt.WorkflowEngine.Services.Video;

using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Deterministically builds a strongly-typed <see cref="TimelineManifest"/> representing the compiled
/// video edit in Unified Program Mode (Expanded Studio Tracks).
/// Maps cuts, tracked screen inserts, motion graphics overlays, dialogue transcription segments,
/// sound-effect cues, and ducked background-music beds into dedicated timeline tracks.
/// Pure and non-destructive: performs no I/O, no ffmpeg operations, and never throws.
/// </summary>
/// <summary>One generated clip as the finished program places it: the cold open and the end card extend the film, a cutaway replaces picture in place. Times are seconds of the finished film.</summary>
public sealed record TimelineGeneratedClip(string ClipId, string Purpose, double StartSec, double EndSec);

public static class TimelineManifestBuilder
{
    private const int CurrentManifestVersion = 1;

    /// <summary>
    /// Builds a <see cref="TimelineManifest"/> from the resolved elements of a compiled video step.
    /// </summary>
    public static TimelineManifest Build(
        Guid projectId,
        Guid executionId,
        VideoAnalysisArtifact artifact,
        IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> resolvedSpans,
        OutputTimeline timeline,
        string outputStorageKey,
        int width,
        int height,
        double fps,
        IReadOnlyList<ResolvedOverlay>? overlays = null,
        IReadOnlyList<ResolvedScreenInsert>? inserts = null,
        ResolvedMusic? music = null,
        IReadOnlyList<ResolvedSfxCue>? sfx = null,
        ColorGradePlanOutput? colorGrade = null,
        IReadOnlyList<SeamPlan>? seamPlans = null,
        VideoEditDecisionOutput? decision = null,
        TimelineProvenanceContext? provenance = null,
        IReadOnlyList<ResolvedCensorRegion>? censors = null,
        IReadOnlyList<TimelineGeneratedClip>? generatedClips = null,
        double? finalOutputSec = null)
    {
        // 1. Output video metadata. The trial output policy (B8b) rides here: it is metadata about
        // THE FILE, not about any item on a track, so it is stamped once rather than per track. A
        // null context (or a null slot) leaves it absent — the pre-B8b payload, byte for byte.
        var outputVideo = new TimelineOutputVideo(
            Width: width,
            Height: height,
            Fps: fps > 0 ? fps : 30.0,
            DurationSec: Math.Round(finalOutputSec is > 0 ? finalOutputSec.Value : timeline.TotalSec, 3),
            StorageKey: outputStorageKey,
            OutputPolicy: provenance?.OutputPolicy);

        // One clock for every track: finished-film time. The compile lays graphics, censor, inserts, dialogue and
        // sfx on the BODY, then prepends a cold open, so each of them plays bodyShift seconds later in the film.
        double bodyShift = generatedClips?.Where(c => c.Purpose == "coldopen").Sum(c => c.EndSec - c.StartSec) ?? 0;

        // 2. Physical source clips
        IReadOnlyList<TimelineSource> sources = BuildSources(artifact, fps);

        // 3. Studio tracks in Unified Program Mode
        var tracks = new List<TimelineTrack>();
        int trackIndex = 0;

        // Track: video (Cuts)
        var videoTrack = BuildVideoTrack(
            trackIndex++, artifact, resolvedSpans, timeline, decision, colorGrade, seamPlans,
            provenance?.Decision, provenance?.ColorGrade, generatedClips);
        if (videoTrack is not null)
        {
            tracks.Add(videoTrack);
        }

        // Track: inserts (Tracked Screen Inserts)
        var insertsTrack = BuildInsertsTrack(trackIndex, artifact, inserts, width, height, bodyShift);
        if (insertsTrack is not null)
        {
            trackIndex++;
            tracks.Add(insertsTrack);
        }

        // Track: censor (obscured tracked objects)
        if (censors is { Count: > 0 })
        {
            tracks.Add(new TimelineTrack(
                Id: "track-censor",
                Type: "censor",
                Name: "Censored",
                Index: trackIndex++,
                Items: censors.Select((c, i) => new TimelineItem(
                    Id: $"censor-{c.RegionId}-{i}",
                    RegionId: c.RegionId,
                    Label: c.Label,
                    TimelineStartSec: Math.Round(c.OutputStartSec + bodyShift, 3),
                    TimelineEndSec: Math.Round(c.OutputEndSec + bodyShift, 3),
                    Confidence: artifact.InsertRegions?.FirstOrDefault(r => r.Id == c.RegionId) is { } t ? Math.Round(t.Confidence, 3) : null,
                    OutputQuadKeyframes: OutputQuadKeyframes(c.OutputStartSec + bodyShift, c.FpsNum, c.FpsDen, c.Keyframes, width, height),
                    Tuning: artifact.InsertRegions?.FirstOrDefault(r => r.Id == c.RegionId)?.Tuning)).ToList()));
        }

        // Track: graphics (Motion Graphics Overlays)
        var graphicsTrack = BuildGraphicsTrack(trackIndex, overlays, provenance?.Graphics, bodyShift);
        if (graphicsTrack is not null)
        {
            trackIndex++;
            tracks.Add(graphicsTrack);
        }

        // Track: dialogue (Spoken dialogue from transcript segments)
        var dialogueTrack = BuildDialogueTrack(trackIndex, artifact, timeline, bodyShift);
        if (dialogueTrack is not null)
        {
            trackIndex++;
            tracks.Add(dialogueTrack);
        }

        // Track: sfx (Sound effects cues)
        var sfxTrack = BuildSfxTrack(trackIndex, sfx, provenance?.Sfx, bodyShift);
        if (sfxTrack is not null)
        {
            trackIndex++;
            tracks.Add(sfxTrack);
        }

        // Track: music (Background music bed with ducking envelope)
        var musicTrack = BuildMusicTrack(trackIndex, music, provenance?.Music);
        if (musicTrack is not null)
        {
            trackIndex++;
            tracks.Add(musicTrack);
        }

        return new TimelineManifest(
            Version: CurrentManifestVersion,
            ProjectId: projectId,
            ExecutionId: executionId,
            OutputVideo: outputVideo,
            Sources: sources,
            Tracks: tracks);
    }

    private static IReadOnlyList<TimelineSource> BuildSources(VideoAnalysisArtifact artifact, double fallbackFps)
    {
        if (artifact.Sources is { Count: > 0 })
        {
            return artifact.Sources.Select(s => new TimelineSource(
                Index: s.SourceIndex,
                StorageKey: s.StorageKey,
                DurationSec: Math.Round(s.Media.DurationSec, 3),
                Width: s.Media.Width,
                Height: s.Media.Height,
                Fps: s.Media.FpsDen > 0 ? Math.Round((double)s.Media.FpsNum / s.Media.FpsDen, 3) : fallbackFps,
                ProjectFileId: s.ProjectFileId,
                StepResultId: s.StepResultId,
                TrimInSec: s.TrimInSec is > 0 ? Math.Round(s.TrimInSec.Value, 3) : null
            )).ToList();
        }

        double sourceFps = artifact.Media.FpsDen > 0
            ? Math.Round((double)artifact.Media.FpsNum / artifact.Media.FpsDen, 3)
            : fallbackFps;

        return new List<TimelineSource>
        {
            new(
                Index: 0,
                StorageKey: string.Empty,
                DurationSec: Math.Round(artifact.Media.DurationSec, 3),
                Width: artifact.Media.Width,
                Height: artifact.Media.Height,
                Fps: sourceFps)
        };
    }

    /// <summary>
    /// Builds the video (cuts) track.
    ///
    /// <para>
    /// Provenance precedence — the CUT is the item's primary author, so <paramref name="cutsProvenance"/>
    /// wins whenever it resolves. <paramref name="gradeProvenance"/> fills in ONLY when the cuts
    /// provenance is unavailable AND the item really carries a <c>ColorGrade</c> value (see
    /// <paramref name="colorGrade"/>): a colour grader did not author an ungraded cut, so stamping
    /// grade provenance onto one would be a fabricated label — precisely the defect this
    /// provenance exists to remove. The two provenances are never merged into one item; one item
    /// names exactly one author.
    /// </para>
    /// </summary>
    private static TimelineTrack? BuildVideoTrack(
        int trackIndex,
        VideoAnalysisArtifact artifact,
        IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> resolvedSpans,
        OutputTimeline timeline,
        VideoEditDecisionOutput? decision,
        ColorGradePlanOutput? colorGrade,
        IReadOnlyList<SeamPlan>? seamPlans,
        TimelineItemProvenance? cutsProvenance,
        TimelineItemProvenance? gradeProvenance,
        IReadOnlyList<TimelineGeneratedClip>? generatedClips = null)
    {
        if (resolvedSpans.Count == 0)
        {
            return null;
        }

        // A cold open pushes the whole body later in the finished film; the body's placements are body-relative.
        double bodyShift = generatedClips?.Where(c => c.Purpose == "coldopen").Sum(c => c.EndSec - c.StartSec) ?? 0;

        // Color grade summary string
        string? colorGradeStr = null;
        if (colorGrade != null && !string.IsNullOrWhiteSpace(colorGrade.Look) && !string.Equals(colorGrade.Look, "None", StringComparison.OrdinalIgnoreCase))
        {
            colorGradeStr = string.IsNullOrWhiteSpace(colorGrade.Strength)
                ? colorGrade.Look
                : $"{colorGrade.Look} ({colorGrade.Strength})";
        }

        // Rationale join: a resolved span names the FromId(s) of the Keep[i] entries it descends
        // from (see ResolvedSpan.OriginKeepFromIds), so a cut is joined to its OWN reason by id —
        // never by loop index i, which is only valid while the raw Keep list survives the span
        // pipeline 1:1 and it does not (spans are coalesced, dropped by MinSegmentMs and capped by
        // MaxSegments). Built once, outside the loop.
        //
        // Built by explicit ordinal grouping rather than ToDictionary, because the decision is
        // model output (untrusted JSON): a bare ToDictionary would throw ArgumentException on a
        // duplicate FromId and ArgumentNullException on a null one, contradicting this class's
        // documented "never throws" invariant. The resolution rule is therefore explicit and
        // deterministic:
        //   1. A null list element, or one whose FromId is null/blank, is skipped entirely — it can
        //      never match an OriginKeepFromIds entry. `FromId`/`Reason` are non-nullable `string`
        //      properties defaulting to string.Empty, but JSON can still null them, so this guard is
        //      load-bearing, not decoration.
        //   2. Among entries sharing a FromId, the FIRST one carrying a non-blank Reason wins; an
        //      entry with a blank Reason never displaces one that already has a reason.
        //   3. If no duplicate of that FromId carries a reason at all, the FIRST one seen wins.
        // (2) and (3) make the winner a function of Keep order alone, so it is stable across runs.
        Dictionary<string, VideoEditKeepSpan> keepByFromId = new(StringComparer.Ordinal);
        foreach (var keep in decision?.Keep ?? new List<VideoEditKeepSpan>())
        {
            if (keep is null || string.IsNullOrWhiteSpace(keep.FromId))
            {
                continue;
            }

            if (!keepByFromId.TryGetValue(keep.FromId, out var existing))
            {
                keepByFromId[keep.FromId] = keep;
                continue;
            }

            if (string.IsNullOrWhiteSpace(existing.Reason) && !string.IsNullOrWhiteSpace(keep.Reason))
            {
                keepByFromId[keep.FromId] = keep;
            }
        }

        var items = new List<TimelineItem>(resolvedSpans.Count);
        for (int i = 0; i < resolvedSpans.Count; i++)
        {
            var span = resolvedSpans[i];
            TimelinePlacement placement = i < timeline.Placements.Count ? timeline.Placements[i] : new TimelinePlacement(i, 0, 0);

            // Match best shot from analysis artifact for this span
            string? shotId = null;
            string? label = null;
            VideoAnalysisShot? bestShot = null;
            double maxOverlap = 0;

            if (artifact.Shots is not null)
            {
                foreach (var shot in artifact.Shots)
                {
                    if (shot.SourceIndex != span.SourceIndex)
                    {
                        continue;
                    }

                    double overlap = Math.Max(0, Math.Min(shot.EndSec, span.SnappedEnd) - Math.Max(shot.StartSec, span.SnappedStart));
                    if (overlap > maxOverlap)
                    {
                        maxOverlap = overlap;
                        bestShot = shot;
                    }
                }
            }

            if (bestShot != null)
            {
                shotId = bestShot.Id;
                label = !string.IsNullOrWhiteSpace(bestShot.Caption?.Summary)
                    ? bestShot.Caption.Summary
                    : $"Shot {bestShot.Id}";
            }
            else
            {
                label = $"Cut {i + 1}";
            }

            // Look group
            string? lookGroupId = null;
            if (shotId != null && artifact.LookGroups is not null)
            {
                var lg = artifact.LookGroups.FirstOrDefault(g => g.ShotIds.Contains(shotId, StringComparer.Ordinal));
                lookGroupId = lg?.Id;
            }

            // Agent rationale from decision — joined by the span's OWN originating Keep FromId(s),
            // never by loop index i (which silently mis-joins as soon as the span pipeline
            // coalesces, drops or caps a span). Null/empty origins resolve to null, then fall back
            // to the edit-level rationale below.
            string? rationale = span.OriginKeepFromIds?
                .Select(id => keepByFromId.GetValueOrDefault(id)?.Reason)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
            if (string.IsNullOrWhiteSpace(rationale))
            {
                rationale = decision?.EditRationale;
            }

            // Outgoing transition to the next cut
            string? transitionOut = null;
            if (seamPlans != null && i < seamPlans.Count)
            {
                var seam = seamPlans[i];
                if (seam.Treatment != SeamTreatment.HardCut)
                {
                    transitionOut = !string.IsNullOrWhiteSpace(seam.XfadeTransition)
                        ? seam.XfadeTransition
                        : seam.Treatment.ToString();
                }
            }

            // Real provenance — see this method's doc comment for the cuts-wins precedence rule.
            TimelineItemProvenance? itemProvenance = cutsProvenance
                ?? (colorGradeStr is not null ? gradeProvenance : null);

            items.Add(new TimelineItem(
                Id: $"cut-{i + 1}",
                ShotId: shotId,
                SourceIndex: span.SourceIndex,
                SourceStartSec: Math.Round(span.SnappedStart, 3),
                SourceEndSec: Math.Round(span.SnappedEnd, 3),
                TimelineStartSec: Math.Round(placement.OutputStartSec + bodyShift, 3),
                TimelineEndSec: Math.Round(placement.OutputEndSec + bodyShift, 3),
                Label: label,
                AgentRationale: rationale,
                LookGroup: lookGroupId,
                Color: colorGradeStr,
                ColorGrade: colorGradeStr,
                TransitionOut: transitionOut,
                AgentName: itemProvenance?.AgentName,
                ProviderName: itemProvenance?.ProviderName,
                StepResultId: itemProvenance?.StepResultId));
        }

        // Generated clips as the finished program places them: the film the viewer sees, not the body alone.
        if (generatedClips is { Count: > 0 })
        {
            foreach (TimelineGeneratedClip clip in generatedClips.OrderBy(c => c.StartSec))
            {
                string kind = clip.Purpose switch { "coldopen" => "cold open", "endcard" => "end card", _ => "cutaway" };
                items.Add(new TimelineItem(
                    Id: $"generated-{items.Count + 1}",
                    TimelineStartSec: Math.Round(clip.StartSec, 3),
                    TimelineEndSec: Math.Round(clip.EndSec, 3),
                    Label: $"generated {kind} clip {clip.ClipId} (placed)"));
            }
        }

        return new TimelineTrack(
            Id: "track-video",
            Type: "video",
            Name: "Video",
            Index: trackIndex,
            Items: items);
    }

    /// <summary>
    /// The tracked plate's corners as the encode placed them — output-relative frame index,
    /// canvas pixels, before overscan — re-expressed as output timeline seconds and 0..1 of the
    /// canvas, so a monitor drawing them lines up with the rendered picture whatever the cut or
    /// letterbox fit did to the source's own coordinates.
    /// </summary>
    internal static IReadOnlyList<VideoInsertQuadKeyframe>? OutputQuadKeyframes(
        ResolvedScreenInsert insert, int canvasWidth, int canvasHeight, double timeShiftSec = 0)
    {
        return OutputQuadKeyframes(
            insert.OutputStartSec + timeShiftSec, insert.FpsNum, insert.FpsDen, insert.PlateKeyframes ?? insert.Keyframes, canvasWidth, canvasHeight);
    }

    /// <summary>Warp keyframes (window-relative frame index, canvas pixels) as output seconds and 0..1 of the canvas.</summary>
    internal static IReadOnlyList<VideoInsertQuadKeyframe>? OutputQuadKeyframes(
        double outputStartSec, int fpsNum, int fpsDen, IReadOnlyList<InsertQuadFrame> frames, int canvasWidth, int canvasHeight)
    {
        if (frames is not { Count: > 0 } || canvasWidth <= 0 || canvasHeight <= 0 || fpsNum <= 0)
            return null;

        double frameSec = fpsDen / (double)fpsNum;
        return frames
            .Select(k => new VideoInsertQuadKeyframe(
                Math.Round(outputStartSec + k.FrameIndex * frameSec, 4),
                Math.Round(k.X0 / canvasWidth, 5), Math.Round(k.Y0 / canvasHeight, 5),
                Math.Round(k.X1 / canvasWidth, 5), Math.Round(k.Y1 / canvasHeight, 5),
                Math.Round(k.X2 / canvasWidth, 5), Math.Round(k.Y2 / canvasHeight, 5),
                Math.Round(k.X3 / canvasWidth, 5), Math.Round(k.Y3 / canvasHeight, 5)))
            .ToList();
    }

    private static TimelineTrack? BuildInsertsTrack(
        int trackIndex,
        VideoAnalysisArtifact artifact,
        IReadOnlyList<ResolvedScreenInsert>? inserts,
        int canvasWidth,
        int canvasHeight,
        double bodyShift = 0)
    {
        // Plate guards (a dark screen keyed over a plate nothing was composited into) are not
        // inserts the editor can show or edit; the EDL's inserts.unfilledPlates reports them.
        inserts = inserts?.Where(i => i.GuardFillColor is null).ToList();
        if (inserts is not { Count: > 0 })
        {
            return null;
        }

        var items = new List<TimelineItem>(inserts.Count);
        foreach (ResolvedScreenInsert insert in inserts)
        {
            VideoInsertRegionTrack? track = artifact.InsertRegions?.FirstOrDefault(
                r => string.Equals(r.Id, insert.RegionId, StringComparison.Ordinal));

            // What the compile step actually APPLIED. This used to say "mesh" for any non-zero
            // measured curvature, so an insert composited with one planar corner pin was labelled
            // a mesh warp in the editor.
            string surface = insert.Mesh is not null ? "mesh" : "planar";

            double? confidence = track != null ? Math.Round(track.Confidence, 3) : null;

            string? solvedColorKey = null;
            if (track?.ChromaKey is not null)
            {
                solvedColorKey = $"#{track.ChromaKey.R:X2}{track.ChromaKey.G:X2}{track.ChromaKey.B:X2}";
            }
            else if (!string.IsNullOrWhiteSpace(track?.ColorName))
            {
                solvedColorKey = track.ColorName;
            }

            items.Add(new TimelineItem(
                Id: $"insert-{insert.RegionId}",
                RegionId: insert.RegionId,
                Label: track?.Label,
                AssetStorageKey: insert.AssetStorageKey ?? insert.LocalAssetPath,
                TimelineStartSec: Math.Round(insert.OutputStartSec + bodyShift, 3),
                TimelineEndSec: Math.Round(insert.OutputEndSec + bodyShift, 3),
                Surface: surface,
                Confidence: confidence,
                SolvedColorKey: solvedColorKey,
                QuadKeyframes: track?.Keyframes,
                OutputQuadKeyframes: OutputQuadKeyframes(insert, canvasWidth, canvasHeight, bodyShift),
                Matted: insert.Matte is not null,
                Tuning: track?.Tuning));
        }

        return new TimelineTrack(
            Id: "track-inserts",
            Type: "inserts",
            Name: "Screen Inserts",
            Index: trackIndex,
            Items: items);
    }

    private static TimelineTrack? BuildGraphicsTrack(
        int trackIndex,
        IReadOnlyList<ResolvedOverlay>? overlays,
        TimelineItemProvenance? provenance,
        double bodyShift = 0)
    {
        if (overlays is not { Count: > 0 })
        {
            return null;
        }

        var items = new List<TimelineItem>(overlays.Count);
        foreach (ResolvedOverlay overlay in overlays)
        {
            string style = !string.IsNullOrWhiteSpace(overlay.Emphasis)
                ? overlay.Emphasis
                : overlay.Kind;

            items.Add(new TimelineItem(
                Id: $"overlay-{overlay.PlacementId}",
                PlacementId: overlay.PlacementId,
                Text: overlay.SanitizedText,
                Subtext: overlay.SanitizedSubtext,
                Style: style,
                TimelineStartSec: Math.Round(overlay.OutputStartSec + bodyShift, 3),
                TimelineEndSec: Math.Round(overlay.OutputEndSec + bodyShift, 3),
                AgentName: provenance?.AgentName,
                ProviderName: provenance?.ProviderName,
                StepResultId: provenance?.StepResultId));
        }

        return new TimelineTrack(
            Id: "track-graphics",
            Type: "graphics",
            Name: "Graphics",
            Index: trackIndex,
            Items: items);
    }

    private static TimelineTrack? BuildDialogueTrack(
        int trackIndex,
        VideoAnalysisArtifact artifact,
        OutputTimeline timeline,
        double bodyShift = 0)
    {
        var items = new List<TimelineItem>();

        if (artifact.Segments is { Count: > 0 })
        {
            foreach (VideoAnalysisSegment segment in artifact.Segments)
            {
                (double Start, double End)? mapped = timeline.MapWindowToOutput(
                    segment.StartSec, segment.EndSec, segment.SourceIndex);

                if (mapped.HasValue && mapped.Value.End > mapped.Value.Start)
                {
                    double? rmsDbfs = null;
                    var shot = artifact.Shots?.FirstOrDefault(s =>
                        s.SourceIndex == segment.SourceIndex &&
                        s.StartSec <= segment.StartSec && s.EndSec >= segment.EndSec)
                        ?? artifact.Shots?.FirstOrDefault(s =>
                            s.SourceIndex == segment.SourceIndex &&
                            Math.Max(0, Math.Min(s.EndSec, segment.EndSec) - Math.Max(s.StartSec, segment.StartSec)) > 0);

                    if (shot?.Audio?.RmsDbfs is not null)
                    {
                        rmsDbfs = Math.Round(shot.Audio.RmsDbfs, 1);
                    }

                    items.Add(new TimelineItem(
                        Id: segment.Id,
                        Text: segment.Text,
                        TimelineStartSec: Math.Round(mapped.Value.Start + bodyShift, 3),
                        TimelineEndSec: Math.Round(mapped.Value.End + bodyShift, 3),
                        RmsDbfs: rmsDbfs));
                }
            }
        }
        else if (artifact.Words is { Count: > 0 })
        {
            foreach (VideoAnalysisWord word in artifact.Words)
            {
                (double Start, double End)? mapped = timeline.MapWindowToOutput(
                    word.StartSec, word.EndSec, word.SourceIndex);

                if (mapped.HasValue && mapped.Value.End > mapped.Value.Start)
                {
                    items.Add(new TimelineItem(
                        Id: word.Id,
                        Text: word.Text,
                        TimelineStartSec: Math.Round(mapped.Value.Start, 3),
                        TimelineEndSec: Math.Round(mapped.Value.End, 3)));
                }
            }
        }

        if (items.Count == 0)
        {
            return null;
        }

        return new TimelineTrack(
            Id: "track-dialogue",
            Type: "dialogue",
            Name: "Dialogue",
            Index: trackIndex,
            Items: items);
    }

    private static TimelineTrack? BuildSfxTrack(
        int trackIndex,
        IReadOnlyList<ResolvedSfxCue>? sfx,
        TimelineItemProvenance? provenance,
        double bodyShift = 0)
    {
        if (sfx is not { Count: > 0 })
        {
            return null;
        }

        var items = new List<TimelineItem>(sfx.Count);
        foreach (ResolvedSfxCue cue in sfx)
        {
            double gainDb = cue.GainLinear > 0
                ? Math.Round(20.0 * Math.Log10(cue.GainLinear), 1)
                : -96.0;

            items.Add(new TimelineItem(
                Id: $"sfx-{cue.SfxId}",
                SfxId: cue.SfxId,
                ClipName: cue.ClipName,
                TimelineStartSec: Math.Round(cue.OutputStartSec + bodyShift, 3),
                TimelineEndSec: Math.Round(cue.OutputStartSec + cue.PlayDurationSec + bodyShift, 3),
                DurationSec: Math.Round(cue.PlayDurationSec, 3),
                GainDb: gainDb,
                Timing: cue.Timing ?? "OnBeat",
                AgentName: provenance?.AgentName,
                ProviderName: provenance?.ProviderName,
                StepResultId: provenance?.StepResultId));
        }

        return new TimelineTrack(
            Id: "track-sfx",
            Type: "sfx",
            Name: "Sound Effects",
            Index: trackIndex,
            Items: items);
    }

    private static TimelineTrack? BuildMusicTrack(
        int trackIndex,
        ResolvedMusic? music,
        TimelineItemProvenance? provenance)
    {
        if (music is null)
        {
            return null;
        }

        double bedDb = music.BedGainLinear > 0
            ? Math.Round(20.0 * Math.Log10(music.BedGainLinear), 1)
            : 0.0;
        double duckedDb = music.DuckGainLinear > 0
            ? Math.Round(20.0 * Math.Log10(music.DuckGainLinear), 1)
            : -12.0;

        double endSec = music.PlayEndSec > 0 ? music.PlayEndSec : music.OutputDurationSec;
        double ramp = Math.Max(0.01, music.RampSec);

        var points = new List<TimelineDuckingPoint>();
        if (music.LiftWindows is null || music.LiftWindows.Count == 0)
        {
            points.Add(new TimelineDuckingPoint(0.0, duckedDb));
            points.Add(new TimelineDuckingPoint(Math.Round(endSec, 3), duckedDb));
        }
        else
        {
            double cursor = 0.0;
            foreach (MusicLiftWindow w in music.LiftWindows)
            {
                double wStart = Math.Max(0, w.StartSec);
                double wEnd = Math.Min(endSec, w.EndSec);
                if (wEnd <= wStart)
                {
                    continue;
                }

                if (wStart > cursor + 1e-4)
                {
                    points.Add(new TimelineDuckingPoint(Math.Round(cursor, 3), duckedDb));
                    points.Add(new TimelineDuckingPoint(Math.Round(wStart, 3), duckedDb));
                }

                double rampUpEnd = Math.Min(wStart + ramp, (wStart + wEnd) / 2.0);
                double rampDownStart = Math.Max(wEnd - ramp, (wStart + wEnd) / 2.0);

                points.Add(new TimelineDuckingPoint(Math.Round(wStart, 3), duckedDb));
                points.Add(new TimelineDuckingPoint(Math.Round(rampUpEnd, 3), bedDb));
                points.Add(new TimelineDuckingPoint(Math.Round(rampDownStart, 3), bedDb));
                points.Add(new TimelineDuckingPoint(Math.Round(wEnd, 3), duckedDb));

                cursor = wEnd;
            }

            if (cursor < endSec - 1e-4)
            {
                points.Add(new TimelineDuckingPoint(Math.Round(cursor, 3), duckedDb));
                points.Add(new TimelineDuckingPoint(Math.Round(endSec, 3), duckedDb));
            }
            else if (points.Count > 0 && points[^1].TimeSec < endSec)
            {
                points.Add(new TimelineDuckingPoint(Math.Round(endSec, 3), points[^1].GainDb));
            }
        }

        // Clean up duplicate consecutive timestamps
        var distinctPoints = new List<TimelineDuckingPoint>();
        foreach (var pt in points)
        {
            if (distinctPoints.Count > 0 && Math.Abs(distinctPoints[^1].TimeSec - pt.TimeSec) < 1e-4)
            {
                distinctPoints[^1] = pt;
            }
            else
            {
                distinctPoints.Add(pt);
            }
        }

        var musicItem = new TimelineItem(
            Id: $"music-{music.TrackId}",
            TrackId: music.TrackId,
            TrackName: music.TrackName,
            TimelineStartSec: 0.0,
            TimelineEndSec: Math.Round(endSec, 3),
            BedDbfs: bedDb,
            DuckedDbfs: duckedDb,
            DuckingEnvelope: distinctPoints,
            AgentName: provenance?.AgentName,
            ProviderName: provenance?.ProviderName,
            StepResultId: provenance?.StepResultId);

        return new TimelineTrack(
            Id: "track-music",
            Type: "music",
            Name: "Music",
            Index: trackIndex,
            Items: new[] { musicItem });
    }
}
