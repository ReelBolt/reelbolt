namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The full, non-truncated video analysis document — the source of truth persisted to
/// <c>projects/{projectId}/agentFiles/video-analysis/{executionId}/step-{order}-analysis.json</c>
/// and referenced by <c>WorkflowStepResult.ArtifactStorageKey</c>. The bounded <c>{view, meta}</c>
/// prompt envelope handed to the story-editor agent is a trimmed projection of this document.
/// </summary>
/// <remarks>
/// Ids are assigned here, deterministically by index (<c>s{n}</c> shots, <c>g{n}</c> silence
/// gaps, <c>t{n}</c> transcript segments, <c>w{n}</c> words), so a truncated prompt view's ids
/// still resolve back to this artifact unambiguously. <see cref="OfferedIds"/> records exactly
/// which ids made it into the prompt view that was actually shown to the model — the compile
/// step must reject any id that is merely present in this artifact but was never offered
/// (see plan §4.3, "not merely in the artifact — in the set actually shown to the model").
///
/// <para>
/// Phase 1 ("scene/visual analysis") bumps <see cref="Version"/> to 2 and appends
/// <see cref="DuplicateGroups"/> and <see cref="Pacing"/> at the end of this record, and
/// <see cref="VideoAnalysisShot"/> gains optional <c>Visual</c>/<c>Audio</c> descriptors. Every
/// Phase 1 addition is optional/nullable/default-valued and strictly appended — an artifact
/// persisted with <c>Version: 1</c> (no visual/audio descriptors at all) still deserializes into
/// this same record with those new fields simply absent/null.
/// </para>
/// <para>
/// Phase 2 ("vision shot captioning") stays at <see cref="Version"/> 2 rather than bumping to 3:
/// it appends exactly one more optional/nullable field (<see cref="VideoAnalysisShot.Caption"/>)
/// plus optional/default-valued fields on <see cref="VideoAnalysisProvenance"/>, and a
/// Version-2-without-captions artifact and a Version-2-with-captions artifact are both valid
/// under the identical shape — no consumer needs to structurally distinguish them (a consumer
/// that cares simply checks whether <c>Caption</c> is null). A version bump is reserved for a
/// change that breaks or reshapes existing fields, not for another purely-additive optional one.
/// </para>
/// </remarks>
public sealed record VideoAnalysisArtifact(
    int Version,
    VideoAnalysisMedia Media,
    IReadOnlyList<VideoAnalysisShot> Shots,
    IReadOnlyList<VideoAnalysisSilenceSpan> SilenceSpans,
    IReadOnlyList<VideoAnalysisSegment> Segments,
    IReadOnlyList<VideoAnalysisWord> Words,
    IReadOnlyList<string> OfferedIds,
    VideoAnalysisProvenance Provenance,
    IReadOnlyList<VideoAnalysisDuplicateGroup>? DuplicateGroups = null,
    VideoAnalysisPacing? Pacing = null,
    /// <summary>
    /// Phase 3 addition — deterministic overlay-placement candidates (see
    /// docs/video-editing.md "Motion graphics (Phase 3)"), populated only when
    /// <c>VideoAnalyzeStepConfig.EmitOverlayPlacements</c> is set. A SEPARATE id namespace
    /// (<c>p{n}</c>) from shots/silences/segments — deliberately NOT resolvable by
    /// <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>, which resolves only cut-anchor ids.
    /// </summary>
    IReadOnlyList<VideoAnalysisPlacement>? Placements = null,
    /// <summary>
    /// Exactly which placement ids were actually included in the bounded view shown to the
    /// motion-graphics agent — the Phase 3 analogue of <see cref="OfferedIds"/>, but a SEPARATE
    /// list/budget: a placement id must never be validated against <see cref="OfferedIds"/>, and
    /// a cut-anchor id must never be validated against this list.
    /// </summary>
    IReadOnlyList<string>? OfferedPlacementIds = null,
    /// <summary>
    /// Multi-source addition. One entry per analyzed source clip, in source-index order,
    /// recording exactly what <see cref="VideoAnalyzeStepExecutor"/> downloaded/probed for that
    /// clip — the same storage key <c>VideoCompileStepExecutor</c> must download to physically cut
    /// from it, and the same per-clip <see cref="VideoAnalysisMedia"/> (duration/fps/dimensions)
    /// every clip-aware computation (frame quantization, padding clamps, graphics geometry) must
    /// use instead of the single top-level <see cref="Media"/>. Null for an artifact produced
    /// before this field existed (a true legacy artifact, not merely a single-source one) — those
    /// deserialize with every item's <see cref="VideoAnalysisShot.SourceIndex"/> etc. defaulting to
    /// 0 and are the ONE case <c>VideoCompileStepExecutor</c> still re-derives the source storage
    /// key the old way (walking the VideoAnalyze step's own config) rather than reading it directly
    /// from here. Every artifact produced by the current executor populates this with at least one
    /// entry, even for a single source, so the top-level <see cref="Media"/> and
    /// <c>Sources[0].Media</c> agree exactly for that case.
    /// </summary>
    IReadOnlyList<VideoAnalysisSourceInfo>? Sources = null,
    /// <summary>
    /// Background-music addition (see docs/video-editing.md "Background music") — populated only
    /// when <c>VideoAnalyzeStepConfig.OfferMusicTracks</c> is set. One entry per <c>audio/*</c>
    /// project file, project-level (not per-source, unlike everything above). A SEPARATE id
    /// namespace (<c>m{n}</c>) from shots/silences/segments/placements — deliberately NOT
    /// resolvable by <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>.
    /// </summary>
    IReadOnlyList<VideoAnalysisMusicCandidate>? MusicCandidates = null,
    /// <summary>
    /// Exactly which music-track ids were actually included in the bounded view shown to the
    /// music-supervisor agent — the background-music analogue of <see cref="OfferedIds"/>/
    /// <see cref="OfferedPlacementIds"/>, but its OWN separate list: a music-track id must never
    /// be validated against either of those, and vice versa.
    /// </summary>
    IReadOnlyList<string>? OfferedMusicIds = null,
    /// <summary>
    /// Phase 4 addition — cross-source shot "look" (light/grade) groups computed by
    /// <c>FrameGridAnalyzer.GroupLooks</c>, populated only when
    /// <c>VideoAnalyzeStepConfig.DetectLookGroups</c> is set. A SEPARATE id namespace (<c>k{n}</c>)
    /// from every other id family — purely DESCRIPTIVE, deliberately never resolvable by
    /// <c>VideoCompileStepExecutor.BuildIdTimeIndex</c> (see docs/video-editing.md "Semantic visual
    /// dimensions (Phase 4)").
    /// </summary>
    IReadOnlyList<VideoAnalysisLookGroup>? LookGroups = null,
    /// <summary>
    /// Tracked screen-insert addition (see docs/video-editing.md "Tracked screen inserts
    /// (Phase 5)") — deterministic chroma-plate quad tracks computed by <c>ChromaQuadTracker</c>,
    /// populated only when <c>VideoAnalyzeStepConfig.DetectInsertRegions</c> is set. A SEPARATE id
    /// namespace (<c>r{n}</c>) from every other id family — deliberately NOT resolvable by
    /// <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>. The full per-frame numeric tracking data
    /// (<see cref="VideoInsertRegionTrack.Keyframes"/>) lives ONLY here, never in the bounded
    /// view — the agent sees an opaque id plus qualitative descriptors, and
    /// <c>VideoCompileStepExecutor</c> alone resolves the id back to these numbers.
    /// </summary>
    IReadOnlyList<VideoInsertRegionTrack>? InsertRegions = null,
    /// <summary>
    /// Exactly which insert-region ids were actually included in the bounded view shown to the
    /// motion-graphics agent — the screen-insert analogue of <see cref="OfferedIds"/>/
    /// <see cref="OfferedPlacementIds"/>/<see cref="OfferedMusicIds"/>, but its OWN separate list:
    /// an insert-region id must never be validated against any of those, and vice versa.
    /// </summary>
    IReadOnlyList<string>? OfferedInsertRegionIds = null,
    /// <summary>
    /// Sound-effects addition (see docs/video-editing.md "Sound effects") — populated only when
    /// <c>VideoAnalyzeStepConfig.OfferSfxClips</c> is set. One entry per <c>audio/*</c> project
    /// file, project-level (not per-source), exactly like <see cref="MusicCandidates"/>. A
    /// SEPARATE id namespace (<c>x{n}</c>) from every other id family — deliberately NOT
    /// resolvable by <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>: an SFX-clip id names a
    /// FILE, never a moment; the MOMENT a cue fires at is a separate, offered cut-anchor id.
    /// </summary>
    IReadOnlyList<VideoAnalysisSfxCandidate>? SfxCandidates = null,
    /// <summary>
    /// Exactly which SFX-clip ids were actually included in the bounded view shown to the
    /// sound-designer agent — the sound-effects analogue of <see cref="OfferedMusicIds"/>, but
    /// its OWN separate list: an SFX-clip id must never be validated against any other offered-id
    /// list, and vice versa.
    /// </summary>
    IReadOnlyList<string>? OfferedSfxIds = null,
    /// <summary>
    /// The track the edit is cut to and its beat grid, when <c>VideoAnalyzeStepConfig.AnalyzeMusicBeats</c>
    /// was on and <c>MusicBeatTrackProjectFileId</c> named an audio file; null otherwise. Read by the
    /// compile step's <c>BeatSync</c> instead of decoding the track again.
    /// </summary>
    VideoAnalysisMusicTrack? Music = null,
    /// <summary>
    /// T-110 (docs/video-editing.md "Event anchors inside a composition"): timed events an Author declared
    /// inside a render this analysis sees — as a source, or as insert content a downstream compile pins —
    /// populated only when <c>VideoAnalyzeStepConfig.OfferSfxClips</c> is set and such a render carries an
    /// event sidecar. A SEPARATE id namespace (<c>e{n}</c>) — never resolvable by
    /// <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>; a Keep span may name one only through the separate
    /// <c>CutOnEvents</c> lookup (<c>EventCutBoundaries</c>), and <c>SfxCue.AnchorId</c> may always. The seconds live ONLY here; the view shows id and label.
    /// </summary>
    IReadOnlyList<VideoAnalysisRenderEvent>? RenderEvents = null,
    /// <summary>
    /// The event ids a later step may name: the declared events, and every detected moment the view or the read
    /// tools (<c>ListVideoEvents</c>, <c>DescribeVideoWindow</c>, the outline's section lists) can show for this
    /// analysis, not only the ones the flat <c>view.events</c> list had room for. Validated by the compile step like
    /// <see cref="OfferedSfxIds"/>, its own separate list.
    /// </summary>
    IReadOnlyList<string>? OfferedEventIds = null,

    /// Text-prompted segmentation addition (initiative "prompted region effects", plan §4) — populated
    /// ONLY by analyze targets whose <see cref="ObjectTrackTarget.Method"/> is
    /// <see cref="ObjectTrackMethod.Segmentation"/>; null/empty (the default) is byte-identical to
    /// before this field existed. A SEPARATE id namespace (<c>g{n}</c>) from every other id family —
    /// deliberately NOT resolvable by <c>VideoCompileStepExecutor.BuildIdTimeIndex</c> and never
    /// matched by <c>CensorLabels</c>: a segmentation mask is not a quad, so it must not enter
    /// <see cref="InsertRegions"/>. WRITTEN by <c>VideoAnalyzeStepExecutor</c>'s segmentation path
    /// (T-004); READ by <c>VideoCompileStepExecutor</c>'s <c>RegionEffects</c> resolution (T-005) and,
    /// read-only, by the artifact endpoint and the web step form.
    /// </summary>
    IReadOnlyList<VideoSegmentationTrack>? SegmentationTracks = null,
    /// <summary>
    /// Pictures-and-videos addition (initiative "picture-overlay", design §5.3) — populated only
    /// when <c>VideoAnalyzeStepConfig.EmitOverlayOffers</c> is set. One entry per project
    /// <c>image/*</c>/<c>video/*</c> file (or, when the run brief names an <c>overlay</c> file set,
    /// only those files, in the brief's order), snapshotted so a file added after the analysis is
    /// not offered to this run. A SEPARATE id namespace (<c>m{n}</c>) from every other id family;
    /// the storage key is deliberately absent — the compile step resolves
    /// <see cref="VideoAnalysisMediaOffer.ProjectFileId"/> itself. Null (the default) is omitted from
    /// the persisted JSON, so a flag-off artifact stays byte-identical to the pre-change one.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VideoAnalysisMediaOffer>? OfferedMedia = null,
    /// <summary>
    /// The moments a picture/video may be shown over, derived deterministically from each shot's
    /// Phase 1 still windows by the same shared window helper placements use. A SEPARATE id
    /// namespace (<c>o{n}</c>), assigned in deterministic shot/start order after ranking by
    /// suitability and capping at <c>MaxOverlayWindows</c>, so the ids are contiguous and globally
    /// unique. Null (the default) is omitted from the persisted JSON.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VideoAnalysisOverlayWindow>? OverlayWindows = null,
    /// <summary>
    /// Exactly which <c>m{n}</c> ids the bounded view actually carried. Its OWN list, never
    /// validated against <see cref="OfferedIds"/> or any other family; empty when the whole offers
    /// node was suppressed for the prompt budget. Null (the default) is omitted from the persisted
    /// JSON.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? OfferedMediaIds = null,
    /// <summary>
    /// Exactly which <c>o{n}</c> ids the bounded view actually carried — the overlay-window twin of
    /// <see cref="OfferedMediaIds"/>, its own separate list. Null (the default) is omitted.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? OfferedOverlayWindowIds = null,
    /// <summary>
    /// Video events analysis (docs/video-editing.md "Events in the picture and the sound"): the events found in the
    /// analysed sources by the local detector (<c>VideoAnalyzeStepConfig.AnalyzeEvents</c>) MERGED with the
    /// render-declared ones, in ONE <c>e{n}</c> id space ordered by source then frame (declared ones keep
    /// <see cref="VideoEventOrigin.Declared"/>). Frames and seconds live ONLY here; the view shows id, type, strength
    /// word and region word. Null (the default, and every run with the flag off) is omitted from the persisted JSON.
    /// Like <see cref="RenderEvents"/> these ids are never resolvable by <c>BuildIdTimeIndex</c>.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VideoAnalysisRenderEvent>? Events = null,
    /// <summary>
    /// True when this analysis feeds a sound designer (<c>OfferSfxClips</c> with <c>SfxWorthyEventsOnly</c>): the
    /// outline's strongest moments and the events read tools list only <see cref="VideoEvents.SfxWorthyEvents"/>,
    /// so a beat found in speech is never presented as a moment. <see cref="Events"/> itself keeps every event.
    /// </summary>
    bool? SfxEventsOnly = null,
    /// <summary>
    /// True when the sound designer is also offered moments of ordinary filmed footage (<c>SfxOnFootage</c>); null (the
    /// default, omitted) leaves them out of the offer, as <see cref="VideoEvents.SfxWorthyEvents.IsPlainFootage"/> says.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    bool? SfxOnFootage = null,
    /// <summary>
    /// Ids of the events this analysis kept a three-frame strip for (<c>DescribeEvents</c>: offered, describable moments the
    /// step did not get to describe; at most <see cref="VideoEvents.VideoEventStrips.MaxStoredStrips"/>). Ids only: where a
    /// strip lives is derived by <see cref="VideoEvents.VideoEventStrips.KeyFor"/>, never stored here and never in the view.
    /// Null (the default, and every run that kept none) is omitted from the persisted JSON. Appended last.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? EventStripIds = null);

/// <summary>
/// One offered picture or video the run may show over the film (initiative "picture-overlay",
/// design §5.3). Id: <c>m{n}</c> — a SEPARATE namespace from <c>s{n}</c>/<c>g{n}</c>/<c>t{n}</c>/
/// <c>p{n}</c>/<c>x{n}</c>/<c>k{n}</c>/<c>r{n}</c>/<c>e{n}</c>/<c>g{n}</c>, never resolvable by
/// <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>. Deliberately carries no storage key: the
/// compile step resolves <see cref="ProjectFileId"/> back to a storage key once an overlay names
/// this id, so the model never sees or needs one.
/// <para>
/// Named <c>VideoAnalysisMediaOffer</c> rather than the design's <c>VideoAnalysisMedia</c>: that
/// name already belongs to the probed source-media record (<see cref="VideoAnalysisMedia"/>), and
/// the artifact's own <c>Media</c> member is the analysed source's probe. The fields are exactly
/// the design's.
/// </para>
/// </summary>
public sealed record VideoAnalysisMediaOffer(
    string Id,
    Guid ProjectFileId,
    string Name,
    /// <summary>"Image" or "Video" — from the file's MIME type.</summary>
    string Kind,
    int? Width,
    int? Height,
    /// <summary>Width / height, rounded to four decimals; null when the size is unknown.</summary>
    double? Aspect,
    /// <summary>"Landscape", "Portrait" or "Square"; null when the size is unknown.</summary>
    string? Orientation,
    /// <summary>A video's probed length; null for an image, or when no ingest probe is stored.</summary>
    double? DurationSec,
    long SizeBytes,
    /// <summary>True when the run brief named this file (the named set binds the offers); false when the offer came from the project-wide list.</summary>
    bool FromUser);

/// <summary>
/// One offered moment (<c>o{n}</c>) a picture/video may be shown over (initiative
/// "picture-overlay", design §5.3): a source-timeline window inside one shot, resolved by the
/// shared still-window/time-slice helper. A SEPARATE id namespace, assigned in deterministic
/// shot/start order after ranking by <see cref="Suitability"/> and capping, so the ids are
/// contiguous and globally unique. The compile step maps the window through the cut.
/// </summary>
public sealed record VideoAnalysisOverlayWindow(
    string Id,
    string ShotId,
    /// <summary>The analysed source clip that owns <see cref="ShotId"/> (see <see cref="VideoAnalysisShot.SourceIndex"/>).</summary>
    int SourceIndex,
    double StartSec,
    double EndSec,
    /// <summary>0..1 calm/low-motion/no-on-screen-text score used only to rank and cap the offers.</summary>
    double Suitability);

/// <summary>
/// One offered composition event (<c>e{n}</c>). Exactly one of <see cref="SourceIndex"/> (the event is on
/// that analysed source's timeline, already trim-adjusted, <see cref="ContentSec"/> in its seconds) or
/// <see cref="InsertContentStorageKey"/> (the event is on that insert content's own timeline, which the
/// compile maps through the applied insert's output window) is set. Produced only by deterministic C#.
/// </summary>
public sealed record VideoAnalysisRenderEvent(
    string Id,
    string Name,
    string Label,
    int Frame,
    double ContentSec,
    int? SourceIndex = null,
    string? InsertContentStorageKey = null,
    // -- Video events analysis: set on events the local detector found (and on declared ones merged beside them in
    //    VideoAnalysisArtifact.Events). Null is omitted from the JSON so a declared-only artifact is unchanged. --
    /// <summary>The closed event type (<c>VideoEventType</c> name, e.g. "Hit"); null on a declared event.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Type = null,
    /// <summary>"Pixels", "Audio" or "Declared" (<c>VideoEventOrigin</c> name); null on an event of <see cref="VideoAnalysisArtifact.RenderEvents"/> that was never merged.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Origin = null,
    /// <summary>0..1 strength measured by the detector.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    double? Strength = null,
    /// <summary>Normalised region of the frame (x, y, w, h in 0..1), when the event has one.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ReelBolt.Shared.Workflows.VideoEvents.VideoEventRegion? Region = null,
    /// <summary>Last frame of a spread-out event, when it has one.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? EndFrame = null,
    /// <summary>Direction word of a move or pop-in (left, right, up, down, up-left, ... or grow), or the way the camera goes on a camera move (the same words, or in / out), when the detector gave one.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Direction = null,
    /// <summary>Speed word of a move (slow, medium, fast), when the detector gave one.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? SpeedWord = null,
    /// <summary>
    /// What the picture shows at this event, in a few words from the vision provider (<c>DescribeEvents</c>), already
    /// through <c>EventSeenText.Sanitize</c>: letters only, at most twelve words. Untrusted text about the customer's
    /// video: it describes, it never instructs. Null (and omitted) when the event was not described.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Seen = null,
    /// <summary>
    /// What kind of picture the event happens in: <c>graphic</c>, <c>text</c> or <c>footage</c>
    /// (<c>VideoEventKinds</c>), from the code where the pixels are clear and from the picture model when it was asked.
    /// Null (and omitted) when nobody could tell.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Kind = null);

/// <summary>
/// One tracked frame (or keyframe) of a chroma-plate insert region's deforming quadrilateral —
/// four corners in NORMALIZED frame coordinates (0..1 of frame width/height, resolution-
/// independent), in ffmpeg <c>perspective</c>-filter corner order: 0 = top-left, 1 = top-right,
/// 2 = bottom-left, 3 = bottom-right. <see cref="TimeSec"/> is on the owning SOURCE clip's own
/// timeline. Produced only by deterministic C# (<c>ChromaQuadTracker</c>) — no model ever
/// authors, edits, or even sees one of these.
/// </summary>
public sealed record VideoInsertQuadKeyframe(
    double TimeSec,
    double X0, double Y0,
    double X1, double Y1,
    double X2, double Y2,
    double X3, double Y3,
    /// <summary>
    /// Non-planar addition (see docs/video-editing.md "Non-planar surfaces and multi-plate
    /// compositing"): how far this frame's actual chroma silhouette bows away from the straight
    /// edges of the quad above. <c>null</c> — the default, and the value on every keyframe
    /// written before this field existed — means "flat", which routes the compile step down the
    /// original single-<c>perspective</c> corner-pin path unchanged. Appended AFTER the eight
    /// corner coordinates so every existing positional-construction call site and every stored
    /// artifact keeps compiling/deserializing untouched.
    /// </summary>
    VideoInsertEdgeBows? Bows = null,
    /// <summary>
    /// Non-planar addition: the plate's corners after <c>ChromaPlateEdgeProfiler</c> refined them
    /// against the actual silhouette — normalized exactly like <c>X0..Y3</c> above, same corner
    /// order. Real screens have ROUNDED corners, and the tracker's extreme-point fit lands on the
    /// rounding's tangent point rather than where the straight edges would meet, so <c>Bows</c> is
    /// only meaningful relative to THESE corners. Used solely by the mesh path; the flat
    /// corner-pin path keeps using <c>X0..Y3</c>, which is what keeps a flat plate's output
    /// unchanged. <c>null</c> whenever curvature was not measured.
    /// </summary>
    VideoInsertSurfaceQuad? SurfaceCorners = null);

/// <summary>
/// A plate's four refined corners in normalized frame coordinates, in the same order as
/// <see cref="VideoInsertQuadKeyframe"/>'s own: 0 = top-left, 1 = top-right, 2 = bottom-left,
/// 3 = bottom-right.
/// </summary>
public sealed record VideoInsertSurfaceQuad(
    double X0, double Y0,
    double X1, double Y1,
    double X2, double Y2,
    double X3, double Y3);

/// <summary>
/// One frame's measured silhouette curvature for a tracked plate: per edge, a symmetric bow and
/// an antisymmetric skew, together describing the deviation profile
/// <c>dev(t) = 4t(1-t)*(bow + skew*(2t-1))</c> along that edge. Every value is a DIMENSIONLESS
/// fraction of the edge's own across-the-plate vector, which is what lets a number measured on
/// the coarse tracking grid be replayed against the canonical encode frame with no conversion.
/// Edge order matches the corner order above: <c>Top</c> runs corner 0→1, <c>Bottom</c> 2→3,
/// <c>Left</c> 0→2, <c>Right</c> 1→3. Produced only by deterministic C#
/// (<c>ChromaPlateEdgeProfiler</c>) and consumed only by deterministic C#
/// (<c>InsertSurfaceMesh</c>/<c>ScreenInsertMeshFilterBuilder</c>) — no model authors, edits, or
/// even sees one of these, exactly like the corner data it accompanies.
/// </summary>
public sealed record VideoInsertEdgeBows(
    double TopBow, double TopSkew,
    double RightBow, double RightSkew,
    double BottomBow, double BottomSkew,
    double LeftBow, double LeftSkew);

/// <summary>
/// One tracked chroma-plate insert region — a contiguous run of frames in which
/// <c>ChromaQuadTracker</c> found a uniform-color quadrilateral plate (e.g. a green screen inside
/// a phone held in frame) suitable for compositing a rendered scene into. Id: <c>r{n}</c> — a
/// SEPARATE namespace from every cut-anchor/placement/music/look id, never resolvable by
/// <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>. The bounded view offers only the id plus
/// qualitative descriptors (confidence/size/motion buckets); the per-frame numeric
/// <see cref="Keyframes"/> are compile-time-only data.
/// </summary>
public sealed record VideoInsertRegionTrack(
    string Id,
    /// <summary>The shot the track's midpoint falls in — view legibility only, no execution meaning (mirrors <see cref="VideoAnalysisSilenceSpan.AfterShot"/>).</summary>
    string? ShotId,
    double StartSec,
    double EndSec,
    IReadOnlyList<VideoInsertQuadKeyframe> Keyframes,
    /// <summary>0..1 — detection coverage x mean quad fill ratio; <c>VideoCompileStepConfig.MinInsertConfidence</c> gates on this.</summary>
    double Confidence,
    /// <summary>Mean fraction of the frame's area the tracked quad covers (0..1).</summary>
    double MeanAreaRatio,
    /// <summary>Mean width/height ratio of the tracked quad in pixel space — the aspect hint the agent uses to render suitably-shaped content.</summary>
    double MeanAspectRatio,
    /// <summary>"Static" / "Slow" / "Moving" — mean centroid displacement bucket.</summary>
    string MotionClass,
    /// <summary>The chroma plate color that matched ("green"/"blue"/"magenta") — C#-side matching only, never an ffmpeg value.</summary>
    string ColorName,
    /// <summary>Multi-source addition — see <see cref="VideoAnalysisShot.SourceIndex"/>.</summary>
    int SourceIndex = 0,
    /// <summary>
    /// Non-planar addition: the track's MEDIAN per-frame silhouette curvature — the peak
    /// <see cref="VideoInsertEdgeBows"/> deviation across its four edges, as a dimensionless
    /// fraction of the plate's own width/height. <c>0</c> (the default, and the value whenever
    /// curvature measurement is off) means "flat". <c>VideoCompileStepConfig.MinInsertCurvature</c>
    /// gates on this to decide between the single-quad corner pin and a mesh warp, and the bounded
    /// view turns it into a single descriptive word ("Flat"/"Curved") for the planning agent —
    /// never a number the agent could act on. Appended AFTER <see cref="SourceIndex"/> so every
    /// existing positional-construction call site keeps compiling.
    /// </summary>
    double MeanCurvature = 0,
    /// <summary>
    /// Silhouette-matting addition (see docs/video-editing.md "Silhouette matting"): the
    /// <c>colorkey</c> parameters grid-searched for THIS plate, letting the compile step clip the
    /// composited insert to the plate's actual per-frame shape — rounded bezel corners, a camera
    /// notch, a hand crossing the screen — instead of painting a filled quadrilateral over them.
    /// <c>null</c> when key solving was off, or when nothing scored well enough against the
    /// tracker's own segmentation to be trusted, in which case the compile step composites with
    /// the plain quad mask exactly as it did before matting existed. Compile-time-only: never
    /// shown in the bounded view, never model-authored. Appended AFTER
    /// <see cref="MeanCurvature"/> so every existing positional-construction call site keeps
    /// compiling.
    /// </summary>
    VideoInsertChromaKey? ChromaKey = null,
    /// <summary>
    /// How the tracker tuned itself for THIS footage: the parameters it settled on and the score
    /// every candidate earned in the per-source search (see <c>ChromaTrackerTuner</c>). Kept so
    /// the editor and the assistant can explain a track rather than just show it, and so a bad
    /// track can be diagnosed from the artifact alone. <c>null</c> on artifacts written before
    /// self-tuning existed, or when the workflow author pinned the parameters. Appended last for
    /// the same positional-construction reason as the fields above.
    /// </summary>
    VideoInsertTrackTuning? Tuning = null,
    /// <summary>
    /// How the region was tracked: <c>"chroma"</c> (a uniform-colour plate, <c>ChromaQuadTracker</c>)
    /// or <c>"feature"</c> (markerless planar tracking of an arbitrary object). Appended last.
    /// </summary>
    string Method = "chroma",
    /// <summary>What a feature-tracked region is ("licence plate") — the target's label; <c>null</c> for chroma plates.</summary>
    string? Label = null,
    /// <summary>
    /// Source-timeline stretches where the plate is VISIBLE but outside this track's span — the
    /// frames the tracker could not trust (a glare flash while the phone turns, the first frames
    /// of a whip into place) or that were too short to become a track of their own — attached to
    /// the track nearest in time. The compile step keys the plate to a neutral dark screen over
    /// them rather than letting raw green through, and reports it. <c>null</c> when there are
    /// none, and on every artifact written before this existed. See docs/video-editing.md
    /// "Tracking quality on the customer clips". Appended last.
    /// </summary>
    IReadOnlyList<VideoInsertPlateSpan>? UncoveredPlateSpans = null);

/// <summary>
/// A stretch of source time, in seconds, <c>[StartSec, EndSec)</c>, where a plate was visible
/// but untracked, plus where: the bounding box of every plate pixel seen over the stretch, in
/// normalized frame coordinates (<c>X</c>/<c>Y</c> its top-left, <c>W</c>/<c>H</c> its size).
/// </summary>
public sealed record VideoInsertPlateSpan(double StartSec, double EndSec, double X = 0, double Y = 0, double W = 1, double H = 1);

/// <summary>
/// The outcome of one tracker's per-source parameter search. <see cref="Parameters"/> holds the
/// winning values by name (e.g. <c>growExcessFraction</c>), <see cref="Score"/> the winner's
/// objective in 0..1, and <see cref="Candidates"/> every evaluated combination with its score
/// and the component measurements it was built from, in evaluation order.
/// </summary>
public sealed record VideoInsertTrackTuning(
    string Objective,
    IReadOnlyDictionary<string, double> Parameters,
    double Score,
    IReadOnlyList<VideoInsertTuningCandidate> Candidates,
    /// <summary>The winning parameters in words (e.g. "homography, 12px window, 4 levels") — numeric codes in <see cref="Parameters"/> are for machines.</summary>
    string? Summary = null);

/// <summary>One evaluated parameter combination in a <see cref="VideoInsertTrackTuning"/> search.</summary>
public sealed record VideoInsertTuningCandidate(
    IReadOnlyDictionary<string, double> Parameters,
    double Score,
    IReadOnlyDictionary<string, double> Measurements);

/// <summary>
/// The chroma-key parameters chosen for one tracked plate, in ffmpeg <c>colorkey</c>'s own terms:
/// a key colour, a <see cref="Similarity"/> radius in its normalized distance metric
/// (<c>sqrt((dr^2+dg^2+db^2)/3)/255</c>), and a <see cref="Blend"/> ramp width for a soft edge.
/// <see cref="Score"/> is the soft intersection-over-union the winning candidate achieved against
/// <c>ChromaQuadTracker</c>'s own segmentation of the same plate, in 0..1 — kept so the compile
/// step, and anyone reading an artifact, can see how much the matte is worth rather than
/// discovering it in the render. Every value is produced by deterministic C#
/// (<c>PlateChromaKeySolver</c>) and consumed by deterministic C#; no model authors, edits or
/// sees one.
/// </summary>
public sealed record VideoInsertChromaKey(
    int R, int G, int B,
    double Similarity,
    double Blend,
    double Score,
    /// <summary>
    /// The plate's own typical chroma DOMINANCE — target channel minus the strongest rival, in
    /// 0..255 — measured over the tracker's strict core segmentation (median over the track).
    /// When present the compile step keys the matte on dominance relative to this level instead
    /// of on <c>colorkey</c> distance (see <c>ScreenInsertFilterBuilder.AppendPlateMatte</c>):
    /// a reflection whitening part of the plate stays plate, and dull scenery green — which can
    /// sit inside <c>colorkey</c>'s distance radius — does not. <c>null</c> (an artifact written
    /// before this existed) keeps the <c>colorkey</c> matte.
    /// </summary>
    int? PlateDominance = null);

/// <summary>
/// A group of shots that share a similar light/grade "look" (see <c>FrameGridAnalyzer.GroupLooks</c>),
/// single-linkage clustered over the whole artifact — deliberately NOT windowed like
/// <see cref="VideoAnalysisDuplicateGroup"/>, since a shared look is routinely NOT temporally
/// adjacent (e.g. interior coverage at the start of one clip and the end of another). Id: <c>k{n}</c>.
/// <see cref="ShotIds"/> is in first-member (ascending shot-index) order; <see cref="RepresentativeShotId"/>
/// is the member with <c>LookRank == 0</c> (closest to the group centroid).
/// </summary>
public sealed record VideoAnalysisLookGroup(
    string Id,
    IReadOnlyList<string> ShotIds,
    string RepresentativeShotId,
    double Cohesion,
    string? ColorTemperatureClass = null,
    string? ToneClass = null,
    string? SaturationClass = null);

/// <summary>
/// One candidate background-music track — an <c>audio/*</c> project file offered to
/// <c>AgentType.MusicSupervisor</c>. Id: <c>m{n}</c> — a SEPARATE namespace from
/// <c>s{n}</c>/<c>g{n}</c>/<c>t{n}</c>/<c>p{n}</c>, never resolvable by
/// <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>. Deliberately small: no storage key, no size —
/// <c>VideoCompileStepExecutor</c> resolves <see cref="ProjectFileId"/> back to a storage key
/// itself once a track is actually chosen, so the model never sees or needs one.
/// </summary>
public sealed record VideoAnalysisMusicCandidate(
    string Id,
    Guid ProjectFileId,
    string FileName,
    string MimeType,
    long SizeBytes,
    /// <summary>The track's measured beat grid and landmarks, when <c>VideoAnalyzeStepConfig.AnalyzeMusicBeats</c> analysed it; null otherwise.</summary>
    VideoAnalysisMusicBeat? Beat = null);

/// <summary>
/// A music track's measured beat grid and energy landmarks (see docs/video-editing.md "Cutting to the
/// beat"). Every time is seconds from the start of the track, which is also the start of the program
/// (background music starts at output time 0). Measured by code, never by a model.
/// </summary>
public sealed record VideoAnalysisMusicBeat(
    double Bpm,
    double BeatSec,
    double BarSec,
    double BeatPhaseSec,
    double BarPhaseSec,
    double DurationSec,
    /// <summary>The drop — the biggest sustained rise in loudness — on a beat; null when the track has none.</summary>
    double? DropSec,
    /// <summary>Where the track winds down after the drop; null when it does not within the track.</summary>
    double? OutroSec,
    /// <summary>How firmly the onsets sit on the grid, 0..1.</summary>
    double Confidence,
    /// <summary>"slow", "medium", "fast" or "very fast".</summary>
    string Tempo);

/// <summary>The track the edit is cut to (<c>VideoAnalyzeStepConfig.MusicBeatTrackProjectFileId</c>), with its beat grid.</summary>
public sealed record VideoAnalysisMusicTrack(Guid ProjectFileId, string FileName, VideoAnalysisMusicBeat Beat);

/// <summary>
/// One candidate sound-effect clip — an <c>audio/*</c> project file offered to
/// <c>AgentType.SoundDesigner</c>. Id: <c>x{n}</c> — a SEPARATE namespace from
/// <c>s{n}</c>/<c>g{n}</c>/<c>t{n}</c>/<c>p{n}</c>/<c>m{n}</c>, never resolvable by
/// <c>VideoCompileStepExecutor.BuildIdTimeIndex</c>. Deliberately identical in shape to
/// <see cref="VideoAnalysisMusicCandidate"/> and just as small: no storage key, no duration —
/// <c>VideoCompileStepExecutor</c> resolves <see cref="ProjectFileId"/> back to a storage key
/// itself once a cue actually names this clip, so the model never sees or needs one.
/// </summary>
public sealed record VideoAnalysisSfxCandidate(
    string Id,
    Guid ProjectFileId,
    string FileName,
    string MimeType,
    long SizeBytes,
    /// <summary>
    /// Set for a built-in library sound (<c>VideoAnalyzeStepConfig.IncludeSfxLibrary</c>): the <c>sfx_library_items</c> id.
    /// <see cref="ProjectFileId"/> is then <see cref="Guid.Empty"/> and the compile resolves the id through its trusted
    /// server-side lookup, never a key. Null (omitted from the JSON) for a project file.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? LibraryItemId = null,
    /// <summary>The library item's category word, shown to the sound designer. Null for a project file.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Category = null,
    /// <summary>The library item's tag words (including the event types it suits). Null for a project file.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// Probed media characteristics. Fps is carried as an exact rational (ffprobe's
/// <c>r_frame_rate</c>, e.g. 30000/1001) rather than a single double, so downstream frame-exact
/// arithmetic never accumulates floating-point drift.
///
/// <para>
/// <see cref="VideoCodec"/>, <see cref="AudioCodec"/>, <see cref="Container"/>,
/// <see cref="AudioChannels"/>, <see cref="AudioSampleRateHz"/> and <see cref="BitrateBps"/> are
/// <b>trailing optional parameters</b>, so every positional construction that predates them — the
/// ~20 sites in src and tests — still compiles unchanged.
/// </para>
/// <para>
/// <b>They are absent-null on every artifact persisted before they existed.</b> An older artifact
/// deserializes with all six null, which is the only honest reading available: the analysis that
/// produced it never probed those fields, so null means <b>not known</b> — never zero, never "no
/// audio", never "uncompressed". A consumer must render null as "not known" rather than
/// substituting a default, since a default here would state something ffprobe never measured.
/// </para>
/// </summary>
public sealed record VideoAnalysisMedia(
    double DurationSec,
    int FpsNum,
    int FpsDen,
    int Width,
    int Height,
    string? VideoCodec = null,
    string? AudioCodec = null,
    string? Container = null,
    int? AudioChannels = null,
    int? AudioSampleRateHz = null,
    long? BitrateBps = null);

/// <summary>
/// Multi-source addition — records one analyzed source clip: which physical object it came from
/// and what was probed about it. See <see cref="VideoAnalysisArtifact.Sources"/>.
/// </summary>
public sealed record VideoAnalysisSourceInfo(
    int SourceIndex,
    string StorageKey,
    VideoAnalysisMedia Media,
    /// <summary>
    /// Whether this source is a finished, DESIGNED composition (an authored render with its own
    /// typography) rather than footage. No overlay placement is offered on such a source.
    /// </summary>
    bool IsComposition = false,
    /// <summary>
    /// When the workflow author used only part of this source (<c>VideoSourceRef.InSec</c>/
    /// <c>OutSec</c>): the window, in seconds of the ORIGINAL file. <see cref="StorageKey"/> and
    /// <see cref="Media"/> then describe the cut window itself, and every time in the artifact is
    /// relative to its start. Null for an untrimmed source.
    /// </summary>
    double? TrimInSec = null,
    double? TrimOutSec = null,
    /// <summary>
    /// The generated clip this source IS, when it came from a <see cref="VideoSourceKind.GeneratedClips"/>
    /// reference — the <c>v{n}</c>/<c>v{n}t{k}</c> clip id the VideoGenerate manifest named, surfaced
    /// as <c>view.sources[].clipId</c> so a dailies room can refer to a take by the id the compile
    /// step will later resolve. Null for every other source kind. Appended (defaulted) so every
    /// persisted artifact written before it still deserializes.
    /// </summary>
    string? ClipId = null,
    /// <summary>
    /// The project file this source was resolved from (<c>VideoSourceKind.ProjectFile</c>), so a
    /// reader can map a trimmed or still source — whose <see cref="StorageKey"/> is an analysis scratch
    /// key — back to its asset. Appended and omitted when null: every earlier artifact still reads.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ProjectFileId = null,
    /// <summary>The step result this source was resolved from (<c>VideoSourceKind.StepResult</c>); see <see cref="ProjectFileId"/>.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? StepResultId = null,
    /// <summary>
    /// The decode geometry this source's masks and tracked regions were analysed under
    /// (<see cref="VideoAnalysisSourceInfo.UprightGeometry"/> = in display orientation, i.e. after ffmpeg's
    /// autorotate). Absent on an analysis made before the upright change: the compile then refuses its masks
    /// and censor tracks when the source alters pixels on decode (180/mirror/any angle), whose same-size
    /// masks would otherwise land flipped. Additive and omitted when null.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DecodeGeometry = null,
    /// <summary>
    /// The project's own people and objects this generated take shows (docs/design/avatar-objects.md "Avatars and
    /// objects in planned shots"), copied from the <c>VideoGenerate</c> manifest's clip: names (cleaned) and kinds
    /// only, surfaced as <c>view.sources[].subjects</c> for the dailies room. Null for every other source, and then
    /// omitted, so every earlier artifact and view is byte-identical.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VideoAnalysisSourceSubject>? Subjects = null)
{
    /// <summary>The marker VideoAnalyze writes: geometry is in the autorotated (display) orientation.</summary>
    public const string UprightGeometry = "upright-1";
}

/// <summary>
/// One subject a generated take shows: the avatar's display name (cleaned) and its kind word (<c>person</c> or <c>object</c>).
/// <paramref name="Fidelity"/> is the take check's verdict word (<c>match</c>, <c>mismatch</c>, <c>unsure</c>; docs/design/avatar-objects.md
/// "Object fidelity"), null when the take was not checked or the check was unavailable.
/// </summary>
public sealed record VideoAnalysisSourceSubject(string Name, string Kind, string? Fidelity = null);

/// <summary>
/// A detected shot/scene. Id: <c>s{n}</c>, assigned by index in scene-detection order.
/// <see cref="Visual"/>/<see cref="Audio"/> are Phase 1 additions — deterministic, ffmpeg+pure-C#
/// derived descriptors, populated only when <c>VideoAnalyzeStepConfig.AnalyzeVisuals</c>/
/// <c>AnalyzeAudioLevels</c> are on AND analysis did not degrade (see
/// <see cref="VideoAnalysisProvenance.VisualAnalysisDegraded"/>).
/// </summary>
public sealed record VideoAnalysisShot(
    string Id,
    double StartSec,
    double EndSec,
    VideoAnalysisShotVisual? Visual = null,
    VideoAnalysisShotAudio? Audio = null,
    /// <summary>
    /// Phase 2 addition — a short, AI-generated scene description, populated only when this shot
    /// was selected for captioning by <c>KeyframeSelector.SelectShotsToCaption</c> AND captioning
    /// did not fail/degrade for it. Absence is normal (captioning is budget-limited and opt-in via
    /// <c>VideoAnalyzeStepConfig.Vision</c>), never a signal the shot is empty or unimportant.
    /// </summary>
    VideoShotCaption? Caption = null,
    /// <summary>
    /// Multi-source addition — which entry of <see cref="VideoAnalysisArtifact.Sources"/> this
    /// shot was detected in. <see cref="StartSec"/>/<see cref="EndSec"/> are always relative to
    /// THIS source's own timeline, never a global one — there is no such thing as a shared
    /// timeline across distinct source clips. Defaults to 0, so a legacy (pre-multi-source)
    /// artifact — which only ever had one implicit source — deserializes as if every item
    /// explicitly said "source 0", the only source that could exist.
    /// </summary>
    int SourceIndex = 0,
    /// <summary>
    /// Where the shot's main subject sits in its source frame (normalized 0..1), as located by the
    /// vision provider on the shot's keyframe when <c>VideoAnalyzeStepConfig.LocateSubjects</c> is
    /// on; null otherwise. Used only to place a crop (<c>VideoOutputFit.Subject</c>) — never a
    /// cut, a time or anything an agent sees.
    /// </summary>
    VideoAnalysisRect? Subject = null,
    /// <summary>
    /// When a long detected shot was split into sub-shots (<c>VideoAnalyzeStepConfig.MaxShotSec</c>):
    /// which part of it this is, as "2/3". The editor keeps part of a long shot by keeping only some
    /// of its parts. Null for an unsplit shot.
    /// </summary>
    string? Part = null,
    /// <summary>
    /// How this shot's START boundary was found: <c>"soft"</c> for a transition the scene score missed
    /// (<c>SceneDetection: Soft</c>); null for every other shot, and then absent from the JSON.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Method = null);

/// <summary>
/// A detected silence gap. Id: <c>g{n}</c>. <see cref="AfterShot"/> links each gap to the shot
/// it trails, purely to make the bounded prompt view legible — it carries no execution meaning.
/// </summary>
public sealed record VideoAnalysisSilenceSpan(
    string Id,
    double StartSec,
    double EndSec,
    string? AfterShot = null,
    /// <summary>Multi-source addition — see <see cref="VideoAnalysisShot.SourceIndex"/>.</summary>
    int SourceIndex = 0);

/// <summary>
/// A transcript segment. Id: <c>t{n}</c>. <see cref="Shot"/> links each segment back to the shot
/// it falls within, again only to aid the prompt view.
/// </summary>
public sealed record VideoAnalysisSegment(
    string Id,
    string? Shot,
    double StartSec,
    double EndSec,
    string Text,
    /// <summary>Multi-source addition — see <see cref="VideoAnalysisShot.SourceIndex"/>.</summary>
    int SourceIndex = 0,
    /// <summary>Opaque speaker id (<c>S1</c>, <c>S2</c>... in order of first appearance); null = unlabeled.</summary>
    string? Speaker = null);

/// <summary>
/// A single transcribed word. Id: <c>w{n}</c>. Persisted in the full artifact for audit and as
/// the phase-2 basis for subtitle export, but never offered to the model directly in v1 — the
/// segment is the offered granularity (see plan §8, "Explicitly NOT in this iteration").
/// </summary>
public sealed record VideoAnalysisWord(
    string Id,
    double StartSec,
    double EndSec,
    string Text,
    /// <summary>Multi-source addition — see <see cref="VideoAnalysisShot.SourceIndex"/>.</summary>
    int SourceIndex = 0,
    /// <summary>Opaque speaker id (<c>S1</c>, <c>S2</c>... in order of first appearance); null = unlabeled.</summary>
    string? Speaker = null);

/// <summary>
/// Records how this artifact was produced, for audit and as the basis for the
/// <c>meta.transcription</c>/<c>meta.visual</c>/<c>meta.audioLevels</c> blocks surfaced in the
/// bounded prompt view (including the "degraded" signal when a best-effort analysis stage fell
/// back to producing no data rather than failing the whole step).
/// </summary>
public sealed record VideoAnalysisProvenance(
    VideoTranscriptionMode TranscriptionMode,
    bool TranscriptionApplied,
    bool TranscriptionDegraded,
    string? TranscriptionProvider = null,
    string? TranscriptionLanguage = null,
    DateTime? AnalyzedAt = null,
    bool VisualAnalysisApplied = false,
    bool VisualAnalysisDegraded = false,
    bool AudioLevelsApplied = false,
    bool SharpnessAvailable = false,
    // -- Phase 2: vision shot captioning (see docs/video-editing.md "Vision captioning") --
    VideoVisionMode VisionMode = VideoVisionMode.Off,
    bool VisionApplied = false,
    bool VisionDegraded = false,
    string? VisionProvider = null,
    int CaptionedShotCount = 0,
    /// <summary>
    /// True when the aggregate <c>VideoAnalyzeStepConfig.VisionTimeoutSeconds</c> wall-clock
    /// budget was exceeded mid-pass, so captioning stopped early with whatever succeeded so far
    /// (as opposed to <see cref="VisionDegraded"/>, which covers "no vision provider resolved" or
    /// "zero captions obtained at all").
    /// </summary>
    bool VisionPartial = false,
    // -- Phase 4: semantic visual dimensions (see docs/video-editing.md "Semantic visual
    //    dimensions (Phase 4)") --
    /// <summary>D1-D3 (and the look pass below reading them) actually ran and produced data.</summary>
    bool ColorGradingApplied = false,
    /// <summary>D4 look grouping (<c>artifact.LookGroups</c>) actually ran.</summary>
    bool LookGroupingApplied = false,
    /// <summary>Count of <c>artifact.LookGroups</c> — carried on provenance too so <c>meta.look</c> never has to dereference the (possibly-trimmed) view list.</summary>
    int LookGroupCount = 0,
    /// <summary>True when exactly one look group covers every analyzed shot — the single-camera-talking-head case, where per-shot <c>look</c> keys carry no discriminating information and are suppressed (see docs/video-editing.md).</summary>
    bool LookUniform = false,
    /// <summary>D6 letterbox/pillarbox detection actually ran (<c>VideoAnalyzeStepConfig.DetectLetterbox</c> was on AND visual analysis applied).</summary>
    bool LetterboxDetectionApplied = false,
    /// <summary>Count of shots that received a real (non-null) <c>Sharpness</c> measurement this step.</summary>
    int SharpnessShotCount = 0,
    // -- Tracked screen inserts (see docs/video-editing.md "Tracked screen inserts (Phase 5)") --
    /// <summary>The chroma-plate insert-region tracking pass (<c>ChromaQuadTracker</c>) actually ran and produced data (possibly zero tracks).</summary>
    bool InsertTrackingApplied = false,
    /// <summary>Insert-region tracking was configured on but failed and degraded to "no tracks" rather than failing the step — same convention as <see cref="VisualAnalysisDegraded"/>.</summary>
    bool InsertTrackingDegraded = false,
    /// <summary>Count of <c>artifact.InsertRegions</c> tracks found across every source.</summary>
    int InsertRegionCount = 0,
    // -- Markerless object tracking (docs/video-editing.md "Tracking objects") --
    bool ObjectTrackingApplied = false,
    bool ObjectTrackingDegraded = false,
    /// <summary>Why object tracking degraded (no Vision provider, a failed lookup...), or null.</summary>
    string? ObjectTrackingReason = null,
    int ObjectTrackCount = 0,
    int ObjectVisionCalls = 0,
    /// <summary>Every label object tracking looked for — what a censor step checks its own labels against, so "found nothing" is distinguishable from "never looked".</summary>
    IReadOnlyList<string>? ObjectTrackingLabels = null,
    /// <summary>
    /// Transcript segments discarded because the audio under them carried no speech — recognised
    /// from music, room tone or silence (the classic "Thank you for watching." on b-roll). Never
    /// offered as <c>t{n}</c> ids. See docs/video-editing.md "Transcripts from clips with no speech".
    /// </summary>
    int TranscriptSegmentsDroppedNoSpeech = 0,
    /// <summary>Shots whose subject the vision provider located (<c>LocateSubjects</c>).</summary>
    int SubjectsLocated = 0,
    /// <summary>Shots whose subject lookup failed or found nothing.</summary>
    int SubjectsMissed = 0,
    /// <summary>Why subject location did not run at all ("no_vision_provider", "no_locator"); null when it ran or was not asked for.</summary>
    string? SubjectsReason = null,
    /// <summary>
    /// Every segmentation PROMPT this step sent, in source-index order — the per-source twin of
    /// <see cref="ObjectTrackingLabels"/>, so a compile step can tell "the analyze step never looked
    /// for the speedometer" from "it looked and found nothing" (the difference between a config
    /// mistake and a model miss). Each entry is the SANITIZED, length-capped prompt actually sent,
    /// never the raw user text. Null (the default) when no target asked for segmentation — the
    /// byte-identical case. WRITTEN by <c>VideoAnalyzeStepExecutor</c> (T-004); READ by the compile
    /// step's fail-closed resolution (T-005) and by the step's own <c>meta</c> block.
    /// </summary>
    IReadOnlyList<string>? SegmentationPrompts = null,
    /// <summary>
    /// Speaker diarization outcome; null (the default) when the provider labelled nothing, which is
    /// the byte-identical case. Ids are namespaced per source, so <c>S1</c> of one source is never
    /// merged with another source's speakers.
    /// </summary>
    VideoAnalysisSpeakerLabels? SpeakerLabels = null);

/// <summary>
/// Provenance of speaker labels: <c>Applied</c> when any segment carries one, <c>Count</c> the number of
/// distinct ids, <c>Reliable</c> false when labels could not be trusted across the whole track
/// (never the case today: multi-chunk transcripts are left unlabeled instead).
/// </summary>
public sealed record VideoAnalysisSpeakerLabels(bool Applied, int Count, bool Reliable);

// =============================================================================================
// Phase 1: visual/audio scene descriptors (grid-sampling based, deterministic, no LLM call).
// See docs/video-editing.md "Scene/visual analysis (Phase 1)" for the full design.
// =============================================================================================

/// <summary>
/// A calm/motionless window within a shot: a run of consecutive sampled grid frames whose
/// per-frame motion stayed below <c>VideoAnalyzeStepConfig.StillMotionThreshold</c> for at least
/// <c>MinStillWindowMs</c>. Useful to a downstream cutter/story-editor for "land the cut here,
/// not mid-motion".
/// </summary>
public sealed record VideoAnalysisStillWindow(double StartSec, double EndSec, double MeanMotion);

/// <summary>One of a shot's top-3 dominant colors, from a 64-bin (4 levels/channel) RGB histogram.</summary>
public sealed record VideoAnalysisColor(string Hex, double Share);

/// <summary>A normalized (0..1 of frame width/height) rectangle — resolution-independent.</summary>
public sealed record VideoAnalysisRect(double X, double Y, double W, double H);

/// <summary>
/// One spatial region of a shot (either a 3x3 grid cell, named <c>R0</c>..<c>R8</c> row-major, or
/// one of the three named overlay-candidate bands <c>LowerThird</c>/<c>UpperThird</c>/
/// <c>CenterBand</c>) with descriptors relevant to placing a text/graphic overlay there in a
/// later phase. See <c>FrameGridAnalyzer</c> for the exact <see cref="Suitability"/> formula.
/// </summary>
public sealed record VideoAnalysisRegion(
    string Name,
    VideoAnalysisRect Rect,
    double LumaMean,
    double LumaStdDev,
    double TemporalMotion,
    double Suitability,
    string TextColor);

/// <summary>
/// Deterministic, ffmpeg+pure-C#-derived visual descriptors for one shot, computed by
/// <c>FrameGridAnalyzer</c> from a single low-res raw-frame grid pass over the source video (see
/// docs/video-editing.md). <see cref="CameraMove"/>/<see cref="CameraMoveConfidence"/> is a
/// documented heuristic (1-D SAD pixel-shift search), not ground truth — always read alongside
/// its confidence.
/// </summary>
public sealed record VideoAnalysisShotVisual(
    double MotionMean,
    double MotionPeak,
    double MotionStdDev,
    string MotionClass,
    string CameraMove,
    double CameraMoveConfidence,
    double HeadMotion,
    double TailMotion,
    IReadOnlyList<VideoAnalysisStillWindow> StillWindows,
    double BrightnessMean,
    double BrightnessStdDev,
    double ContrastRms,
    double ClippedHighlightRatio,
    double CrushedBlackRatio,
    double SaturationMean,
    IReadOnlyList<VideoAnalysisColor> DominantColors,
    IReadOnlyList<VideoAnalysisRegion> Regions,
    string? BestOverlayRegion,
    VideoAnalysisRect? ActiveCrop,
    double? Sharpness,
    bool KenBurnsCandidate,
    string? KenBurnsReason,
    string? DuplicateGroupId,
    int? GroupRank,
    bool IsBestTake,
    // -- Phase 4: semantic visual dimensions D1-D4/D6/D7 (see docs/video-editing.md "Semantic
    //    visual dimensions (Phase 4)"). All null/false/0 when AnalyzeColorGrading is off, or when
    //    visual analysis did not run at all — same "absent/null == not computed" convention as
    //    ActiveCrop/Sharpness above. --
    /// <summary>D1. "Warm" / "Cool" / "Neutral" — null when <c>AnalyzeColorGrading</c> is off.</summary>
    string? ColorTemperatureClass = null,
    /// <summary>D1. Normalized -1..1; a ±0.25 raw mean-channel difference saturates the scale.</summary>
    double Warmth = 0,
    /// <summary>D1. Normalized -1..1, green-magenta axis.</summary>
    double Tint = 0,
    /// <summary>D2. "Blown" / "Crushed" / "Flat" / "Contrasty" / "Normal" — null when <c>AnalyzeColorGrading</c> is off.</summary>
    string? ToneClass = null,
    /// <summary>D2. 5th-percentile luma, 0..1.</summary>
    double BlackPoint = 0,
    /// <summary>D2. 95th-percentile luma, 0..1.</summary>
    double WhitePoint = 0,
    /// <summary>D3. "Muted" / "Natural" / "Vivid", a pure projection of <see cref="SaturationMean"/> — null when <c>AnalyzeColorGrading</c> is off.</summary>
    string? SaturationClass = null,
    /// <summary>
    /// D7. Heuristic only — named with the codebase's existing "-Candidate" suffix for exactly this
    /// confidence level (see <see cref="KenBurnsCandidate"/>). Surfaced at Full detail only, and fed
    /// to the vision prompt so a model that can actually see the frame adjudicates it.
    /// </summary>
    bool BacklitCandidate = false,
    /// <summary>D4. This shot's look-group id (<c>k{n}</c>), or null when it belongs to no group (a look unlike any other shot analyzed).</summary>
    string? LookGroupId = null,
    /// <summary>D4. Rank within <see cref="LookGroupId"/> by ascending distance to the group centroid; 0 is the most representative member. Null when <see cref="LookGroupId"/> is null.</summary>
    int? LookRank = null);

/// <summary>
/// Deterministic audio-level descriptors for one shot, derived from <c>WavRmsSampler</c> windows
/// over the (already-extracted-for-transcription, or freshly-extracted) full-track WAV, plus the
/// already-detected silence spans for <see cref="SpeechRatio"/> — no new audio analysis pass.
/// </summary>
public sealed record VideoAnalysisShotAudio(
    double RmsDbfs,
    double PeakDbfs,
    double SpeechRatio,
    string LoudnessClass,
    // -- Phase 4 (D5): audio character — see docs/video-editing.md "Semantic visual dimensions
    //    (Phase 4)". A heuristic, hence paired with a confidence, exactly as CameraMove is paired
    //    with CameraMoveConfidence. All 0/default when AnalyzeAudioLevels is off. --
    /// <summary>Peak-to-RMS ratio in dB.</summary>
    double CrestFactorDb = 0,
    /// <summary>10th-percentile window RMS in dBFS — "how loud is this shot when nothing is happening".</summary>
    double NoiseFloorDbfs = 0,
    /// <summary>1 - clamp(stddev(window RMS)/12dB, 0, 1); 1 == perfectly level.</summary>
    double LevelStability = 0,
    /// <summary>Overlap-weighted zero-crossing rate of the shot's audio windows.</summary>
    double ZeroCrossingRate = 0,
    /// <summary>"Dialogue" / "Music" / "Ambient" / "Noisy" / "Silent" — null when <c>AnalyzeAudioLevels</c> is off.</summary>
    string? AudioCharacterClass = null,
    /// <summary>0..1 confidence in <see cref="AudioCharacterClass"/> — makes shipping a ZCR/crest/floor heuristic instead of an FFT classifier honest.</summary>
    double AudioCharacterConfidence = 0);

/// <summary>
/// A group of near-duplicate/multi-take shots (single-linkage clustered over a sliding time
/// window — see <c>FrameGridAnalyzer.GroupDuplicates</c>), so a downstream editor can prefer the
/// best take instead of keeping every attempt. <see cref="ShotIds"/> is in original chronological
/// (shot-index) order; <see cref="BestShotId"/> is the member with <c>GroupRank == 0</c>.
/// </summary>
public sealed record VideoAnalysisDuplicateGroup(
    string Id,
    IReadOnlyList<string> ShotIds,
    string BestShotId,
    double MeanSimilarity);

/// <summary>Whole-video pacing summary, computed once at the artifact level from the shot list.</summary>
public sealed record VideoAnalysisPacing(
    double MeanShotSeconds,
    double MedianShotSeconds,
    double CutsPerMinute,
    IReadOnlyList<double> MotionTimeline,
    double TimelineBinSeconds);

// =============================================================================================
// Phase 2: optional vision-LLM shot captioning (see docs/video-editing.md "Vision captioning").
// Purely additive on top of Phase 1's schema — Version stays 2 (see VideoAnalyzeStepExecutor's
// doc comment for why no Version 3 bump is needed).
// =============================================================================================

/// <summary>
/// A short, structured scene description of one shot's representative keyframe, produced by a
/// single vision-capable chat-completions call (<c>IShotCaptioner</c>). Every field is
/// model-authored prose/tags — deliberately no numeric or time-bearing property, same discipline
/// as <c>VideoEditDecisionOutput</c>, though for a different reason here: a caption describes one
/// still frame, so it has nothing meaningful to say about timing at all.
/// </summary>
/// <remarks>
/// <b>Safety property, enforced by the caller, not by this type:</b> <see cref="ShotId"/> as
/// returned by the model must never be trusted for binding a caption to a shot.
/// <c>VideoAnalyzeStepExecutor</c> always overwrites this field with the id it actually requested
/// before attaching the caption to a shot — see <c>IShotCaptioner.CaptionAsync</c>'s remarks.
/// </remarks>
public sealed record VideoShotCaption(
    string ShotId,
    string Summary,
    IReadOnlyList<string> Subjects,
    string Action,
    string Setting,
    string Mood,
    /// <summary>Free-text shot-scale guess, typically one of "Wide", "Medium", "CloseUp" — not enum-constrained since a vision model's own wording is more robust than forcing an exact enum match.</summary>
    string ShotScale,
    string CameraAngle,
    IReadOnlyList<string> OnScreenText,
    IReadOnlyList<string> Tags,
    /// <summary>"Day" | "Night" | "GoldenHour" | "Indoor" | "Unknown".</summary>
    string TimeOfDay,
    /// <summary>Interpretive prose, e.g. "soft window light from camera left", "harsh overhead".</summary>
    string Lighting,
    /// <summary>The grading/look read, e.g. "flat ungraded log", "warm documentary grade".</summary>
    string VisualStyle,
    /// <summary>Composition judgment, e.g. "centered, generous headroom", "subject cropped at frame edge".</summary>
    string Framing,
    /// <summary>
    /// The highest-value field in this record: the defects a human editor spots instantly that no
    /// pixel statistic reaches — "soft focus", "blown window", "visible banding", "rolling shutter".
    /// </summary>
    IReadOnlyList<string> TechnicalIssues);

// =============================================================================================
// Phase 3: optional motion-graphics overlay placement candidates (see docs/video-editing.md
// "Motion graphics (Phase 3)"). Purely additive on top of Phase 1/2's schema — Version stays 2,
// same rationale as Phase 2: every new field here is optional/nullable/default-valued.
// =============================================================================================

/// <summary>
/// One deterministic overlay-placement candidate, derived from a shot's Phase 1
/// <see cref="VideoAnalysisRegion"/> data by <c>OverlayPlacementBuilder</c>. Id: <c>p{n}</c> —
/// a SEPARATE namespace from <c>s{n}</c>/<c>g{n}</c>/<c>t{n}</c> cut-anchor ids, assigned by a
/// global counter across the whole artifact (not per-shot). Carries a fully server-resolved
/// time window ([<see cref="StartSec"/>, <see cref="EndSec"/>), clamped to the owning shot's own
/// bounds) so <c>VideoCompileStepExecutor</c> never has to guess a placement's timing — only its
/// on/off decision and text content are left to the motion-graphics agent.
/// </summary>
public sealed record VideoAnalysisPlacement(
    string Id,
    string ShotId,
    /// <summary>One of the three named overlay-candidate bands: "LowerThird" / "UpperThird" / "CenterBand".</summary>
    string Region,
    VideoAnalysisRect Rect,
    /// <summary>How <see cref="StartSec"/>/<see cref="EndSec"/> were chosen: "LongestStillWindow" / "ShotMiddle" / "ShotStart".</summary>
    string TimeAnchor,
    double StartSec,
    double EndSec,
    double Suitability,
    string TextColor,
    /// <summary>
    /// Multi-source addition — the source clip that owns this placement's <see cref="ShotId"/> (see
    /// <see cref="VideoAnalysisShot.SourceIndex"/>). Redundant with looking the owning shot up by
    /// id, but carried directly here so <c>VideoCompileStepExecutor</c> never has to do that lookup
    /// just to map a placement's source-timeline window onto the right source's output timeline.
    /// </summary>
    int SourceIndex = 0);

// =============================================================================================
// Prompted region effects (initiative "prompted region effects", plan §4): text-prompted video
// segmentation tracks. Written by VideoAnalyzeStepExecutor's Method=Segmentation path (T-004), read
// by VideoCompileStepExecutor's RegionEffects resolution (T-005). See docs/video-editing.md
// "Prompted region effects".
// =============================================================================================

/// <summary>
/// One object followed through one source by a hosted text-prompted segmentation model, with its
/// per-frame masks already materialised as a full-frame grey8 video in the project's storage.
/// <b>APPEND ONLY</b> — positional record, new members go LAST.
/// </summary>
/// <remarks>
/// <b>The mask is the artifact.</b> The compile step never re-calls the provider: it downloads
/// <see cref="MaskStorageKey"/>, verifies <see cref="MaskContentHash"/>, and conforms the mask with
/// the same scale/crop/pad geometry it conforms the source's picture — so the mask lands on the
/// object after a reframe. 255 = the object, 0 = everything else, asserted once here rather than
/// re-derived per effect.
/// </remarks>
/// <param name="Id">Its own id namespace (<c>g{n}</c>), assigned deterministically by index like every other id family; the compile step's <c>RegionEffects[].Target</c> resolves to this first, then by <see cref="Prompt"/>.</param>
/// <param name="SourceIndex">The analysed source clip this track belongs to (0-based, the order of the analyze step's sources — see <see cref="VideoAnalysisSourceInfo"/>).</param>
/// <param name="Prompt">The SANITIZED, length-capped prompt actually sent to the model ("speedometer"), never the raw user text and never a timestamp. Also the case-insensitive match key for a <c>RegionEffects</c> entry, so a customer can name the object in words.</param>
/// <param name="StartSec">First moment of this track on its SOURCE clip's own timeline.</param>
/// <param name="EndSec">Last moment of this track on its source's own timeline.</param>
/// <param name="FrameCount">The mask is FULL-SOURCE: it holds every frame of its source clip (frame k = source frame k) at the source's own frame rate, so this equals the SOURCE's frame count — never the tracked span's. Outside <see cref="StartSec"/>..<see cref="EndSec"/> the mask is black; <see cref="StartSec"/> and <see cref="EndSec"/> are the first and last frame the object was actually DETECTED on, so a black run before or after them is the model not finding it, not a shorter mask video.</param>
/// <param name="Fps">The source's frame rate the mask was encoded at; the compile step refuses a mismatch rather than resampling silently.</param>
/// <param name="Coverage">Mean fraction of the frame the mask covers (0..1) — how big the object is, which is what a customer's "it did nothing" report is usually about.</param>
/// <param name="Confidence">min(1, mean model score) when the vendor returned scores, else a coverage-derived proxy; carried so the step's own <c>meta</c> can say the model was unsure.</param>
/// <param name="MaskStorageKey">Where the grey8 mask video lives, always under <c>projects/{projectId}/agentFiles/segmentation/</c>. The compile step downloads this exact key and nothing else.</param>
/// <param name="MaskContentHash">Lower-case hex SHA-256 of the mask video's BYTES. The compile step re-hashes what it downloaded and fails <c>SEGMENTATION_MASK_UNVERIFIED</c> on any mismatch — a mask that is not the one this artifact recorded must never be applied to the customer's picture.</param>
/// <param name="Model">The endpoint id the masks came from (e.g. <c>fal-ai/sam-3-1/video-rle</c>), for audit and for the content-cache key.</param>
/// <param name="ProviderFingerprint">Provider id + kind + model, never key material — the same shape <c>ResolvedProviderFingerprint</c> hashes, carried per track so a stored artifact states which backend produced it.</param>
/// <param name="BoxKeyframes">Per-frame normalised <c>[x,y,w,h]</c> boxes for the view/editor (a mask is not renderable in a browser timeline; a box is), derived from the DECODED MASK's own non-zero pixels — never from the vendor's <c>boxes</c>/<c>metadata.box</c> (decision D-05). T-002 measured those vendor boxes centre-anchored and a median 21% (worst 33%) of the frame diagonal away from the mask they claim to bound, so they are never an acceptance measure and never reach the artifact. Null when the mask yielded no boxes.</param>
/// <param name="Facts">Quality facts for the step's <c>meta</c> block and for QA's bar (empty frames, longest gap, threshold used, truncation).</param>
public sealed record VideoSegmentationTrack(
    string Id,
    int SourceIndex,
    string Prompt,
    double StartSec,
    double EndSec,
    int FrameCount,
    double Fps,
    double Coverage,
    double Confidence,
    string MaskStorageKey,
    string MaskContentHash,
    string Model,
    string? ProviderFingerprint = null,
    IReadOnlyList<VideoSegmentationBoxFrame>? BoxKeyframes = null,
    VideoSegmentationFacts? Facts = null,
    // How many distinct vendor object slots the answer named, whatever the prompt selected — the
    // retained surrogate for the raw answer the platform does not keep (X-Fal-Store-IO: 0), so the
    // analyze step's meta can report the vendor's shape. Appended last and nullable so an artifact
    // written before this fact still deserializes.
    int? DistinctObjects = null);

/// <summary>
/// One frame's normalised bounding box for a <see cref="VideoSegmentationTrack"/> — <c>[x, y, w, h]</c>
/// as fractions of the frame (0..1), the same normalised convention every other rectangle in this
/// artifact uses. <see cref="TimeSec"/> is on the track's own SOURCE timeline, like every other
/// source-relative time here. Written by the analyze step (T-004) from the vendor's own per-frame
/// boxes, read by the web timeline and the editor; never used for rendering (the mask is).
/// </summary>
public sealed record VideoSegmentationBoxFrame(double TimeSec, double X, double Y, double W, double H);

/// <summary>
/// What the model's answer actually looked like, for the step's <c>meta</c> block and for QA's bar
/// (plan §7): a mask that is empty for most frames, or that disappears for a long stretch, is a
/// model/quality fact the customer and the reviewer must be able to see — never inferred from a
/// <see cref="VideoSegmentationTrack.Coverage"/> alone.
/// </summary>
/// <param name="EmptyFrames">Frames whose mask is entirely 0 (the object was not found at all).</param>
/// <param name="LongestGapFrames">Longest consecutive run of empty frames inside the tracked span — an occlusion reads as a gap, and a long one is why an effect "blinks off".</param>
/// <param name="DetectionThreshold">The <c>detection_threshold</c> actually sent, after clamping.</param>
/// <param name="Truncated">True when the vendor's answer was cut at <c>max_num_objects</c>, so a further instance of the object exists that this track does not cover.</param>
/// <param name="FirstNonEmptyFrame">First frame (0-based, on the track's own source timeline) whose mask is not entirely empty — where the object was first found. <c>null</c> when the mask is empty everywhere, or when the artifact predates this fact.</param>
/// <param name="LastNonEmptyFrame">Last frame whose mask is not entirely empty — where the object was last found. <c>null</c> under the same conditions as <paramref name="FirstNonEmptyFrame"/>.</param>
public sealed record VideoSegmentationFacts(
    int EmptyFrames = 0,
    int LongestGapFrames = 0,
    double DetectionThreshold = 0.5,
    bool Truncated = false,
    int? FirstNonEmptyFrame = null,
    int? LastNonEmptyFrame = null);
