using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <summary>
/// A revision tag per deterministic step type, hashed into every step-cache key
/// (<see cref="StepCacheKeyBuilder"/>). The key already covers a step's configuration and its fully
/// resolved inputs, but not the CODE that turns them into output — so when a deterministic
/// executor's output changes for the same inputs, an identical re-run kept being served the old
/// result from cache. That is exactly how a fixed tracker would have kept replaying the broken
/// analysis it was fixed for.
///
/// <para>
/// <b>Bump the tag for a step type whenever its executor would produce different output from the
/// same config and inputs.</b> Prompt changes need no bump for an Agent step — its system prompt is
/// already hashed — but they do for a room step such as EditRoom, whose charter and seat/director
/// prompts are not. A step type with no entry hashes the empty string.
/// </para>
/// </summary>
public static class DeterministicStepRevisions
{
    /// <summary>The revision tag for <paramref name="stepType"/>.</summary>
    public static string For(StepType stepType) => stepType switch
    {
        // Purity-gated, self-tuned chroma growth; edge-contrast/temporal confidence; stride motion;
        // markerless object tracking with a format-tolerant vision locator; sub-pixel edge-line
        // insert corners sampled at the source's own frame rate.
        // Transient (sub-MinShotMs) shot spans folded into the shot they cut into; author-declared
        // scenes and generated-clip descriptions replace detection/captioning; no placements on a
        // composition; lenient caption list parsing; transcript segments with no speech (ASR
        // hallucinations on music/silence) discarded before ids are assigned; located shot subjects,
        // refined on a zoomed crop (LocateSubjects); an "orientation" block naming each source's shape and
        // the run's target format (docs/editing-styles.md); author-trimmed source windows (VideoSourceRef
        // InSec/OutSec) and long shots split into sub-shots (MaxShotSec, Part); measured music beat
        // facts (AnalyzeMusicBeats); plate edges re-located from the neighbouring frame's
        // (finger/reflection-robust), and visible-but-untracked plate spans recorded; sub-shot cut
        // points from the content (SubShotMode Content: motion pauses, framing changes, audio dips,
        // meta.subShots), each hold cut at its centre, every sub-shot boundary on a source frame edge;
        // insert plates detected on a full-chroma grid, outliers judged by a single-kink (hinge) path,
        // and corners, surface corners and bows smoothed by a robust, motion-adaptive, held-frame-aware fit.
        // The mask content key is now segmented by ObjectMatchScope (mask-scope-key-5): an analyze step
        // cached before that fix could hold the other scope's track set for the same config.
        // The artifact's sources[] now carry the ProjectFile / StepResult a source was resolved from
        // (source-identity-6), so a cached analysis lacking it would seed a trimmed source unresolved.
        // display-rotation-7 (#772): probe width/height are the DECODED size (Display Matrix 90/270 swaps them) and
        // segmentation uploads an upright mezzanine, so a cached analysis of a rotated source holds sideways geometry/masks.
        // edit-moments-1 (VideoAnalyze): the event detector reads an edited video with EditMomentAnalyzer. Stored events of the same video change: frame-exact hard cuts (also inside panels and in montages), soft transitions, appear/leave/move/change by cell wavefronts, a Flash type (whole-frame and local), CaptionTick for running caption words, whole-picture moves; view.events and the outline carry a plain-word "what"; the offer to a sound designer is one per 2 s at most, 0.8 s apart. Events also carry a "kind" where it is known (a hard cut between two full pictures and a camera move inside one are "footage"), and footage is left out of the sound offer. A camera move found by the global motion fit carries the way the camera goes (left, right, up, down, a diagonal, in, out) and its "what" says so.
        // hdr-tonemap-1 (VideoAnalyze, VideoCompile, ThumbnailGenerate, ExtractFrame): HLG/PQ sources are tone-mapped to BT.709 before the keyframes, the frame grabs and the compiled picture are made.
        // hdr-tonemap-2 (VideoAnalyze, VideoCompile, ThumbnailGenerate, ExtractFrame): the HLG tone-map knee moved to mobius param=0.2, so every delivered picture and frame grab from HLG changes. VideoAnalyze is here because its vision keyframes and contact sheets go through HdrToneMap.Filter (the delivery chain), not the unchanged AnalysisFilter; a cached analysis would otherwise carry captions from the pre-change rendering.
        // untrusted-view-names-1 (picture-overlay security): every analysis view now sanitises the
        // user-supplied names it carries (overlayMedia, music, musicTracks, sfxClips) and adds the
        // untrustedText notice, for flag-on and flag-off configs alike. A pre-fix cache entry holds
        // the old, unsanitised view, so this suffix is UNCONDITIONAL: an existing analysis re-runs
        // once rather than replaying raw names to a downstream agent.
        // events-1 (video events analysis): view.events / meta.events / the artifact's merged Events (AnalyzeEvents). UNCONDITIONAL like the other tags: an analysis cached before the flag existed re-runs once.
        // outline-1 (progressive context): view.outline / view.sectionEvents / meta.outline (Outline); the events digest joins the outline digest. UNCONDITIONAL like events-1.
        StepType.VideoAnalyze => "2026-10-17.insert-smoothing-content-sub-shots-6+prompted-region-effects-1+mask-gap-check-2+reacquire-slots-3+match-scope-4+mask-scope-key-5+source-identity-6+run-brief-source-1+display-rotation-7+display-upright-9+hdr-tonemap-2+analysis-copy-3+untrusted-view-names-1+events-1+outline-1+view-coverage-1+speech-outline-1+edit-moments-1+tap-seed-1",
        // Project-file insert content, content fit, matted-content padding, plate keyframes,
        // censoring, whole-source compiles; dense (balanced-tree) insert keyframe expressions;
        // burned captions, voiceover.headroom / voiceover.wer facts, dialogue gating, pickups; seam
        // bridges, aspect-preserving overlay assets, unpadded composition cuts, blurred canvas fill;
        // anchored narration on its own clip, silent program base, largest-source canvas, output
        // format/fit/target length, compile warnings, run-named output files; captions burned
        // through libass (bundled fonts, presets, position, automatic size, validated colours); muted
        // source audio, music at the track's own level when it is the whole soundtrack, through a
        // peak limiter; crops reframed onto the subject (OutputFit=Subject); narration mixed after
        // the program fade and the last frame held until the last line has finished; earlier
        // registered renders of the same step replaced in Files (registration node); KeepOrder;
        // per-screen insert contents; dips to black in insert content frozen over by default; cuts
        // snapped to the music's beat or bar (BeatSync), never shorter than the narration spoken
        // over a span; program loudness (LoudnessTarget); automatic captions moved off the
        // picture's own text; insert corners no longer one frame ahead (perspective's `in` is
        // 1-based), smeared overscan for flat matted inserts, plate guards (dark screen +
        // insert_untracked_plate_visible) over unfilled plates; matted insert branches padded from
        // t=0 instead of time-shifted (bounded memory), every segment held to its timeline length
        // (no raw plate on a later clip's first frame); the interleave review fact + single_source_edit
        // warning (re-derived from the artifact, the decision and the brief); brand-aware overlay
        // colours and caption fonts (DesignSystemColors/DesignSystemFonts, SPEC 14.2); rendered WebM
        // overlays whose alpha is tagged ALPHA_MODE (any case, e.g. after a stream-copy remux)
        // composited instead of dropped as asset_missing_alpha_channel (#340); tracked-insert content
        // and mask prescaled to the insert's size, content edge smeared instead of black-padded, and
        // the perspective warp run on the insert's bounding window (no minification aliasing, no dark rim), the
        // insert content sampled with the cubic kernel (D7); a Source compile sized to the run's chosen
        // resolution (outputResolution report); keyframed reframing (Reframes, zoompan per span),
        // per-source fits (SourceFits) and the Subject safe area (text-bearing shots padded, never cropped);
        // an absolute music gain (MusicGainDb: no -6 dB clamp, unity pass-through), generated-plate audio
        // modes (GeneratedClipAudio) and the loudness explanation / music gain / insert audio reports;
        // a target length met by whole spans (TargetDurationMode WholeShots) with its chosen/dropped report.
        // Revision 28 (QA-P1-13): insert and censor geometry is sampled at the reframe's own knots, and an
        // overlay band is mapped at the source moment it actually begins, so the same config produces
        // different bytes. Bumped so a cached compile cannot replay the pre-fix geometry.
        // Master's prompted-region-effects revision is carried in the same tag: both changes alter the
        // output for the same input, so one combined tag invalidates either side's cached compile.
        // display-rotation-8 (#772): canvas fit/crop/pad and region masks follow the probed (decoded) size of a rotated source.
        // stream-copy-reencode-1 (VideoCompile): a StreamCopy cut of more than one span is now re-encoded frame-accurately at the planned spans instead of copied and joined, so its bytes differ; a single-span copy is unchanged.
        // vfr-sync-1 (VideoCompile): the picture is normalized to the probed constant rate before the cut (fps start_time=0, last frame held), so variable-frame-rate sources keep the planned length and stay in sync with the sound; a span with no frame holds the previous frame. Constant-rate sources encode byte-identically; the step cache key still changes, so existing compiles re-render once.
        // follow-subject-1 (VideoCompile): a followSubject entry expands into reframes; configs without it are unchanged, the tag keeps pre-feature cache entries from standing in for a followed compile.
        // reframe-visible-anchor-1 (VideoCompile): a reframe may anchor on the visible picture (Anchor=Visible), which pans the cover crop under a covering fit; Window reframes are unchanged, the tag keeps earlier followSubject cache entries (window-anchored) from standing in.
        // canvas-overlays-1 (VideoCompile): overlay legibility (docs/design/graphics-see-and-size.md): drawtext is raised to MinOverlayCapHeightPct, textSize/anchor words size and place drawn overlays, an assetCanvas "Output" graphic is composited full frame, and a graphic measured below the floor is replaced by its text.
        // reframe-owns-safe-area-1 (VideoCompile): a span a reframe covers (hand-written or followSubject) is no longer padded by the subject safe area, nor its source by the Subject "no crop keeps it" pad; a follow that cannot move warns (follow_subject_cannot_move).
        // region-mask-span-fit-1 (VideoCompile): a region-effect mask on a reframed or safe-padded span takes that span's own geometry, not the clip's static conform.
        // region-effect-object-radius-1 (VideoCompile): Blur sigma / Pixelate block also scale with the masked object's size (a near face is anonymised).
        // follow-keep-inside-1 (VideoCompile): a followed subject that fits the visible window is kept inside it (keep-in-picture pass + decimation check).
        // region-mask-crop-geometry-1 (VideoCompile): a non-inverted Blur/Pixelate mask is grown by two feather sigmas before it is feathered, so the whole object is painted at full strength (run 5d420a0f: the outer band of a face stayed readable); geometry pinned for static Crop/Pad with a moving object.
        // explicit-output-fit-1 (VideoCompile): an outputFit the step's config names itself (Crop included) survives the run's format override; output.fitSetBy reports an override.
        // overlay-geometry-1 (VideoCompile): graphics.appliedOverlays[] also records startSec/endSec and, for a plain-text overlay, the drawn box (x,y,w,h in output pixels) and its text/box colours; the picture is unchanged, the tag keeps earlier cache entries from standing in for the richer compile summary.
        // speed-ramps-1 (VideoCompile): a speedRamps entry retimes the finished program at the event; configs without it are unchanged.
        // event-moves-1 (VideoCompile): an eventMoves entry expands into reframes at the event's frame; configs without it are unchanged.
        // speech-safe-cuts-1 (VideoCompile): a final cut that landed inside a spoken word moves to the word edge (preferring a pause) unless SnapCutsToSpeech is false; the EDL spans move with it.
        // film-quality-1 (VideoCompile): a beat-synced program keeps its snapped cuts (the speech-safe pass is skipped; beatSync.maxOffsetMs reports the rendered offset); a music mix with no LoudnessTarget is normalised to Web; the sole-soundtrack bed is -6 dB, not -1. film-quality-2: a beat-synced program keeps hard cuts whatever the transition policy (an overlapping seam shifted later cuts off the beat), and a failed loudness pass falls back to a true-peak limiter.
        // take-shot-numbering-1 (VideoCompile): the dailies take filter numbers a shot by its clip id (v{n}), not by the one-based manifest shotIndex read as zero-based, so an unselected take of the last selected shot is dropped instead of placed.
        // beat-lock-bounded-1 (VideoCompile): a beat-lock snap removes at most one bar (a drop further back is skipped), and the music bed spans the whole program including a generated cold open / end card (mixed after the concat).
        StepType.VideoCompile => "2026-10-19.whole-shots-target-length-27+prompted-region-effects-1+reframe-knots-28+mask-gap-check-2+run-brief-length-1+transcript-captions-1+voiceover-speech-guard-1+display-rotation-8+hdr-tonemap-2+stream-copy-reencode-1+vfr-sync-1+media-overlays-1+untrusted-view-names-1+follow-subject-1+reframe-visible-anchor-1+reframe-owns-safe-area-1+region-mask-span-fit-1+explicit-output-fit-1+region-effect-object-radius-1+follow-keep-inside-1+canvas-overlays-1+region-mask-crop-geometry-1+event-moves-1+flash-glow-1+speed-ramps-1+event-cut-1+event-graphics-1+sfx-events-1+speech-safe-cuts-1+speech-coverage-1+overlay-geometry-1+film-quality-2+take-shot-numbering-1+beat-lock-bounded-1",
        // run-brief-language-voice-1: the brief fills Language / ProjectVoiceId and voice mode none skips the step.
        // run-brief-length-1 (VideoCompile) and run-brief-source-1 (VideoAnalyze): the brief fills TargetDurationSec / the ASR language hint.
        // Lines carry their sanitized spoken text and anchor id (what captions are cut from);
        // health-aware speech provider choice + voice_provider_* warnings.
        // narration-fit-measured-1: the pre-synthesis gate moved from the 1.8 budget x 1.15 to the
        // 4.0 words/s hard ceiling, so the same input can now complete where it previously failed.
        StepType.Voiceover => "2026-10-04.voiceover-text-provider-health-1+run-brief-language-voice-1+pronunciation-overrides-nfc-1+narration-fit-enforce-1+fish-language-1+project-pronunciation-glossary-1+narration-fit-measured-1",
        // First revision. The selection rule (ThumbnailCandidateSelector), the fit/quality args and
        // the text band all live in first-party code and change the bytes from the same config, so
        // this tag is what lets a later change to any of them invalidate a cached thumbnail.
        StepType.ThumbnailGenerate => "2026-10-09.thumbnail-candidate-selection-1+hdr-tonemap-2",
        // First revision: the frame-seek rule (time / first / last), crop-then-downscale filter and encoder args.
        // Revision 2 (QA-P1-14): the output JSON no longer carries frameStorageKey (the no-storage-key rule),
        // so an identical re-run must not be served the cached output that still did.
        StepType.ExtractFrame => "2026-10-18.extract-frame-2+hdr-tonemap-2",
        // First revision: region selection (explicit pins over the catch-all, screens only) and the emitted plan shape.
        StepType.PinInsert => "2026-10-18.pin-insert-1",
        StepType.AudioCleanup => "2026-11-02.audio-cleanup-1",
        StepType.VideoEnhance => "2026-11-03.video-enhance-1",
        // First revision. An EditRoom step has no AgentDefinition of its own: its seats and director are named
        // inside EditRoomConfigJson by id, and the charter (EditRoomStepExecutor.EditRoomCharterPrompt) plus the
        // built-in seat and director prompts are resolved at run time, so none of them is in the key. A change to
        // any of those prompts therefore needs this bump or an identical re-run replays the old decision.
        // edit-room-shot-judging-1: speech is not the only content (action and scenery judged on merit), "g" ids
        // are silence spans attached to the shot they start in, and "the brief" is the User Request.
        // outline-ids-2: ids come from meta.offeredIdRanges too (a very long film has no phrase list), ColorGradeRoom reads outline.colour.
        // outline-ids-1: in outline mode the ids listed as ranges (view.outline.phrases / shots) are offered too, and the charter
        // carries the long-footage paragraph.
        StepType.EditRoom => "2026-11-01.edit-room-shot-judging-1+outline-ids-2",
        // outline-ids-1 (GraphicsRoom, ColorGradeRoom): the charter carries the long-footage paragraph, ColorGradeRoom also offers the
        // shot ids listed as ranges in outline mode, and the GraphicsRoom turns may read the footage and events tools. The charters are
        // resolved at run time, so only this tag turns the change into a cache miss.
        StepType.GraphicsRoom => "2026-11-10.outline-ids-1",
        StepType.ColorGradeRoom => "2026-11-10.outline-ids-2",
        // First revision: the loudness node carries checks[] (LoudnessAssessor findings, silence pass).
        StepType.TimelineRender => "2026-11-10.loudness-checks-1",
        _ => string.Empty
    };

    /// <summary>
    /// The revision tag for a step with this configuration. Identical to <see cref="For(StepType)"/> except
    /// for config-conditional suffixes: a VideoAnalyze whose <c>sceneDetection</c> is <c>Sensitive</c>, that
    /// produces an offer list (view-budget-1) or emits overlay offers, and a VideoCompile that cuts to the
    /// beat (beat-sync-target-1). Each lets one feature be revised without touching any other key.
    /// <paramref name="configJson"/> is the step's OWN config (VideoAnalyze's or VideoCompile's).
    /// </summary>
    public static string For(StepType stepType, string? configJson) => For(stepType, configJson, null);

    /// <summary>
    /// The revision tag for a step with this configuration under a run whose effective dynamics (RunStyleOverrides.DynamicsCacheKeyComponent, null when none) is <paramref name="runDynamics"/>
    /// (QA-DYN-53): a compile with no <c>dynamics</c> block of its own plans a dynamic edit when the run asks for one, so it gets the
    /// Dynamics suffixes too. A compile that sets its own block is decided by that block alone (the run never overrides it).
    /// </summary>
    public static string For(StepType stepType, string? configJson, string? runDynamics)
    {
        string tag = For(stepType);

        // beat-sync-target-1 (issue 4, run a60d5aae): after a target-length fit, beat sync aims each cut at
        // the fitted program time and may use the footage the fit trimmed, and a length lost to snapping
        // is reported as beat_sync_trimmed. Only a compile that cuts to the beat changes, so the suffix is
        // conditional and every other compile keeps its cache key.
        if (stepType == StepType.VideoCompile)
        {
            if (UsesBeatSync(configJson))
                tag += "+beat-sync-target-1";
            // cutaway-clip-start-1: a generated cutaway clip starts at its window (frame 0 on the window's first frame, whole-frame
            // edges); before, it ran from the program's t=0. Only a compile with generated clips enabled changes.
            if (TryReadBoolProperty(configJson, "enableGeneratedClips", IsTrue))
                tag += "+cutaway-clip-start-1";
            // generated-film-timeline-1 (dynamic-edit QA-DYN-41): with generated clips the manifest describes the whole
            // finished film (header length, cold open/cutaway/end card items) and EVERY track is on the film's clock
            // (graphics, censor, inserts, captions and sfx shifted by the cold open). The reviewer and the editor seed
            // read it, so an identical re-run must not replay the old 17.65 s body-time manifest.
            if (TryReadBoolProperty(configJson, "enableGeneratedClips", IsTrue))
                tag += "+generated-film-timeline-1";
            // dynamics-1 (dynamic-edit T-003): an enabled Dynamics block plans punch-ins, splits and alternations.
            // Conditional, so every compile without it keeps its cache key.
            bool dynamics = UsesDynamics(configJson)
                || (runDynamics is not null && !HasDynamicsBlock(configJson));
            if (dynamics)
                tag += "+dynamics-1";
            // dynamics-2 (dynamic-edit QA-DYN-33/43/44): a cutaway sits on the un-rewritten footage and starts on the frame of
            // the cut it follows, its return edge is snapped, and body cuts come from a cumulative frame layout (no per-span drift).
            // The picture of a Dynamics compile changes for the same config, so only a Dynamics compile gets the suffix.
            if (dynamics)
                tag += "+dynamics-2";
            // cutaway-min-length-1 (dynamic-edit QA-DYN-62): a generated cutaway shorter than the minimum is skipped (cutaway_too_short)
            // even without Dynamics, so a generated-clips film's picture changes. Only a compile with generated clips enabled.
            if (TryReadBoolProperty(configJson, "enableGeneratedClips", IsTrue))
                tag += "+cutaway-min-length-1";
            // beat-grid-read-1 (dynamic-edit): the beat grid is read with the remote-session retry and a long track keeps its
            // grid (before, over about 131 s it came back empty), so the beatSync result and the cuts change. A compile
            // that cuts to the beat or runs Dynamics reads the grid; every other compile keeps its key.
            if (UsesBeatSync(configJson) || dynamics)
                tag += "+beat-grid-read-1";
            // beat-grid-film-time-1 (dynamic-edit QA-DYN-54): with a cold open the beat lines, bar lines, accents and the drop the cuts are placed on
            // are the music's at the body's time (the bed starts with the film), no longer the track's own time, so the cuts and the bed shift
            // change for the same config. Only a compile that has generated clips (a cold open) and cuts to the beat or runs Dynamics changes.
            if (TryReadBoolProperty(configJson, "enableGeneratedClips", IsTrue) && (UsesBeatSync(configJson) || dynamics))
                tag += "+beat-grid-film-time-1";
            // dynamics-plan-1: a compile that reads a DynamicsPlanner's plan merges its moves into the dynamics list. Only then.
            if (UsesDynamicsPlan(configJson))
                tag += "+dynamics-plan-1";
            return tag;
        }

        if (stepType != StepType.VideoAnalyze)
            return tag;

        if (UsesSensitiveSceneDetection(configJson))
            tag += "+sensitive-scenes-1";

        // soft-scenes-1 (dynamic-edit T-001): SceneDetection Soft adds a lagged frame-difference pass and
        // marks its shots. Only a Soft analysis changes, so every other config keeps its cache key.
        if (UsesSoftSceneDetection(configJson))
            tag += "+soft-scenes-1";

        // view-budget-1 (issue 3, run a60d5aae): a view whose detail DEGRADED to None keeps its overlay
        // placements when they still fit, and the envelope warns (overlay_placements_suppressed,
        // music_tracks_suppressed, sfx_clips_suppressed, insert_regions_suppressed,
        // overlay_offers_suppressed) when an offer list was left out for budget. Only a step that can
        // produce one of those lists changes, so the suffix is conditional on them and every other
        // analysis keeps its cache key (and is not re-analysed for this). Placed before
        // media-overlay-offers-1 so that suffix stays the tag's last.
        if (UsesViewBudgetOffers(configJson))
            tag += "+view-budget-1";

        // reserved-view-2 adds outline.colour, meta.offeredIdRanges and no limit key on outline warnings.
        // reserved-view-1 (footage edit quality): with the outline on, the offer lists, the outline and the whole-film phrase list
        // are kept and the size limit is raised for them (meta.effectiveMaxOutputChars) instead of the lists being dropped.
        // Conditional on the outline flag, so every other analysis keeps its cache key.
        if (TryReadBoolProperty(configJson, "outline", IsTrue))
            tag += "+reserved-view-2";

        // sfx-worthy-events-1 (footage edit quality): an analysis that offers sound-effect clips lists only the
        // moments a sound belongs to (not every beat) unless sfxWorthyEventsOnly is false. Conditional on the
        // option being effective, so every other analysis keeps its cache key.
        if (UsesSfxWorthyEvents(configJson))
            tag += "+sfx-worthy-events-1";

        // sfx-library-1 (footage edit quality): includeSfxLibrary adds the built-in library sounds to the x{n} offers (with
        // category and tag words). Conditional on the flag and the offer, so every other analysis keeps its cache key.
        if (TryReadBoolProperty(configJson, "offerSfxClips", IsTrue) && TryReadBoolProperty(configJson, "includeSfxLibrary", IsTrue))
            tag += "+sfx-library-1";

        // insert-rigid-pose-1 (green screen occlusion): a tracked insert region is solved as one rigid plate
        // (ChromaPlatePoseTracker), so a track whose corner or edge something covers keeps its geometry, and
        // a track follows its plate over the frames it is in view. The same footage yields different
        // insertRegions. Conditional on detectInsertRegions, so every other analysis keeps its cache key.
        if (TryReadBoolProperty(configJson, "detectInsertRegions", IsTrue))
            tag += "+insert-rigid-pose-1";

        // media-overlay-offers-1 (picture-overlay T-002): a flag-on VideoAnalyze lists the project's
        // pictures/videos and the overlay windows and appends them to the artifact and view, so the
        // same config produces different output. Conditional on the flag exactly as the design asks
        // (§6.3), so a flag-off analysis keeps today's cache key and is never re-analysed for this.
        if (UsesOverlayOffers(configJson))
            tag += "+media-overlay-offers-1";

        // music-beat-read-1 (dynamic-edit): analyzeMusicBeats measures the beat grid with the remote-session retry, so a long
        // track keeps its grid and every failure is named. Conditional on the flag; every other analysis keeps its key.
        if (TryReadBoolProperty(configJson, "analyzeMusicBeats", IsTrue))
            tag += "+music-beat-read-1";

        return tag;
    }

    /// <summary>True when the config JSON carries a <c>dynamics</c> object that is not explicitly disabled.</summary>
    private static bool UsesDynamics(string? configJson) =>
        TryReadBoolProperty(configJson, "dynamics", value =>
            value.ValueKind == JsonValueKind.Object
            && !(value.TryGetProperty("enabled", out JsonElement en) && en.ValueKind == JsonValueKind.False));

    /// <summary>True when the config JSON carries a non-null <c>dynamics</c> value (the run's dynamics never replace it).</summary>
    private static bool HasDynamicsBlock(string? configJson) =>
        TryReadBoolProperty(configJson, "dynamics", value => value.ValueKind != JsonValueKind.Null);

    /// <summary>True when the config JSON names a <c>dynamicsPlan</c> reference (an object).</summary>
    private static bool UsesDynamicsPlan(string? configJson) =>
        TryReadBoolProperty(configJson, "dynamicsPlan", value => value.ValueKind == JsonValueKind.Object);

    /// <summary>True when the config JSON's <c>beatSync</c> is anything but <c>Off</c> (the compile's default).</summary>
    private static bool UsesBeatSync(string? configJson) =>
        TryReadBoolProperty(configJson, "beatSync", value => value.ValueKind switch
        {
            JsonValueKind.String => !string.Equals(value.GetString(), nameof(VideoBeatSync.Off), StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => value.TryGetInt32(out int n) && n != (int)VideoBeatSync.Off,
            _ => false
        });

    /// <summary>The config flags whose offer lists the view's budget can leave out (view-budget-1).</summary>
    private static readonly string[] ViewBudgetOfferFlags =
        ["emitOverlayPlacements", "offerMusicTracks", "offerSfxClips", "detectInsertRegions", "emitOverlayOffers"];

    private static bool UsesViewBudgetOffers(string? configJson) =>
        ViewBudgetOfferFlags.Any(flag => TryReadBoolProperty(configJson, flag, IsTrue))
        || TryReadBoolProperty(configJson, "trackObjects", value => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0);

    private static bool UsesSfxWorthyEvents(string? configJson) =>
        TryReadBoolProperty(configJson, "offerSfxClips", IsTrue)
        && !TryReadBoolProperty(configJson, "sfxWorthyEventsOnly", value => value.ValueKind == JsonValueKind.False);

    private static bool IsTrue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetInt32(out int n) && n != 0,
        JsonValueKind.String => bool.TryParse(value.GetString(), out bool enabled) && enabled,
        _ => false
    };

    private static bool UsesSensitiveSceneDetection(string? configJson) =>
        TryReadBoolProperty(configJson, "sceneDetection", value => value.ValueKind switch
        {
            JsonValueKind.String => string.Equals(value.GetString(), nameof(SceneDetectionMode.Sensitive), StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => value.TryGetInt32(out int n) && n == (int)SceneDetectionMode.Sensitive,
            _ => false
        });

    private static bool UsesSoftSceneDetection(string? configJson) =>
        TryReadBoolProperty(configJson, "sceneDetection", value => value.ValueKind switch
        {
            JsonValueKind.String => string.Equals(value.GetString(), nameof(SceneDetectionMode.Soft), StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => value.TryGetInt32(out int n) && n == (int)SceneDetectionMode.Soft,
            _ => false
        });

    /// <summary>True when the config JSON's <c>emitOverlayOffers</c> is explicitly true (the analyzer's default is false).</summary>
    private static bool UsesOverlayOffers(string? configJson) =>
        TryReadBoolProperty(configJson, "emitOverlayOffers", value => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out int n) && n != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool enabled) && enabled,
            _ => false
        });

    private static bool TryReadBoolProperty(
        string? configJson, string name, Func<JsonElement, bool> predicate)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return predicate(property.Value);
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
