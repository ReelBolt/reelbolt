using ReelBolt.Shared.Briefs;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Where the source video for a <c>StepType.VideoAnalyze</c> step comes from. Render outputs
/// are NOT <c>ProjectFile</c> rows (they live only as <c>WorkflowStepResult.OutputStorageKey</c>),
/// so the source model must resolve a step-result storage key as a first-class kind rather than
/// assuming everything is addressable by a <c>ProjectFile</c> id.
/// </summary>
public enum VideoSourceKind
{
    /// <summary>An uploaded video/audio file: resolves via <c>project_files.storage_key</c>.</summary>
    ProjectFile,

    /// <summary>A specific step's result: resolves via that step's <c>WorkflowStepResult.OutputStorageKey</c>.</summary>
    StepOutput,

    /// <summary>The latest completed step result in this execution that has a non-null output storage key.</summary>
    PreviousStepOutput,

    /// <summary>
    /// EVERY delivered clip of a <c>StepType.VideoGenerate</c> step (issue #93 phase 4, dailies):
    /// resolves through that step result's <c>ArtifactStorageKey</c> — the per-clip manifest — to
    /// one analyzed source PER succeeded clip, in manifest order, each tagged with its
    /// <c>v{n}</c>/<c>v{n}t{k}</c> clip id (<c>VideoAnalysisSourceInfo.ClipId</c>, surfaced as
    /// <c>view.sources[].clipId</c>). Appended, never inserted: members persist by name. Requires
    /// <see cref="VideoSourceRef.StepOrder"/>. A step whose manifest holds zero delivered clips
    /// contributes no source, which the analyze step then reports as <c>SOURCE_UNRESOLVED</c>.
    /// </summary>
    GeneratedClips,

    /// <summary>
    /// A step result of an EARLIER run in the SAME project, addressed by its id (T-010):
    /// <c>{"kind":"StepResult","stepResultId":"&lt;guid&gt;"}</c>. Requires
    /// <see cref="VideoSourceRef.StepResultId"/>. Appended, never inserted: members persist by name.
    /// The engine resolves it only when the result's execution belongs to the running execution's
    /// project and its <c>OutputStorageKey</c> is one of the project's addressable output keys
    /// (<c>ProjectOutputKeys.IsAddressableOutput</c>): a render under <c>outputFiles/</c> or a generated clip
    /// under <c>agentFiles/generated/</c>; a foreign, missing, pruned or out-of-prefix id all fail <c>SOURCE_NOT_FOUND</c>.
    /// </summary>
    StepResult,

    /// <summary>
    /// The files the RUN's brief named for <see cref="VideoSourceRef.Role"/> (<c>brief.sourceFiles[role]</c>):
    /// <c>{"kind":"BriefFiles","role":"Video"}</c>. Expands at run time into one <see cref="ProjectFile"/> source
    /// per file, in the brief's order, so the run page chooses clips without editing the graph. Requires
    /// <see cref="VideoSourceRef.Role"/>. Every file id is re-checked against the running execution's project;
    /// a brief with no files for the role fails the step with <c>INPUT_MISSING</c>. Appended, never inserted:
    /// members persist by name.
    /// </summary>
    BriefFiles
}

/// <summary>
/// Resolves to exactly one S3/MinIO object holding the source video. Never a URL, never a
/// model-supplied string — always closed, typed, and resolved server-side.
/// </summary>
public sealed record VideoSourceRef(
    VideoSourceKind Kind,
    Guid? ProjectFileId = null,  // Kind=ProjectFile   -> project_files.storage_key
    int? StepOrder = null,       // Kind=StepOutput    -> that step's workflow_step_results.output_storage_key
                                 // Kind=PreviousStepOutput -> latest completed result with a non-null key (StepOrder ignored)
                                 // Kind=GeneratedClips -> that VideoGenerate step's manifest (artifact_storage_key), one source per delivered clip
                                 // -- Author-level trim (see docs/video-editing.md "Using only part of a source"). Appended:
                                 //    every stored payload keeps deserializing; both null (default) is byte-identical. --
    /// <summary>
    /// Use the source only from this second on (null = from the start). The analyze step cuts the
    /// window out once, analyses only it, and records the cut window as the source every later step
    /// reads — so every id, time and track is relative to the window's own start.
    /// </summary>
    double? InSec = null,
    /// <summary>Use the source only up to this second (null = to the end). Must be after <see cref="InSec"/>.</summary>
    double? OutSec = null,
    /// <summary>Kind=StepResult -> that step result's <c>output_storage_key</c>, same project only. Appended, defaulted.</summary>
    Guid? StepResultId = null,
    /// <summary>Kind=BriefFiles -> which role of the run brief's <c>sourceFiles</c> this source expands to. Appended, defaulted.</summary>
    RunBriefFileRole? Role = null)
{
    /// <summary>
    /// The reason this reference is malformed, or null when it is well formed. Only the identity
    /// rules of <see cref="VideoSourceKind.StepResult"/> are enforced here; the older kinds keep the
    /// resolution-time checks they have always had, so no stored config starts failing.
    /// </summary>
    public string? Validate()
    {
        if (Kind == VideoSourceKind.StepResult)
        {
            if (StepResultId is null || StepResultId == Guid.Empty)
                return "Source=StepResult requires StepResultId.";
            if (ProjectFileId.HasValue || StepOrder.HasValue)
                return "Source=StepResult must not also set ProjectFileId or StepOrder.";
            return null;
        }

        if (Kind == VideoSourceKind.BriefFiles)
        {
            if (Role is null)
                return "Source=BriefFiles requires Role (video, code, music, sfx, image, document or insertContent).";
            if (ProjectFileId.HasValue || StepOrder.HasValue || StepResultId.HasValue)
                return "Source=BriefFiles must not also set ProjectFileId, StepOrder or StepResultId.";
            return null;
        }

        if (Role.HasValue)
            return $"Role is only valid with Source=BriefFiles, not {Kind}.";

        return StepResultId.HasValue ? $"StepResultId is only valid with Source=StepResult, not {Kind}." : null;
    }

    /// <summary>
    /// The longest moment any supported film can name, in seconds (24 h). Finiteness alone is not
    /// enough: `1e20` is finite and still not a moment ffmpeg can seek to, so the window and
    /// <c>ThumbnailGenerateStepConfig.AtTimeSec</c> share this ceiling.
    /// </summary>
    public const double MaxWindowSec = 86_400;

    /// <summary>
    /// The reason <see cref="InSec"/>/<see cref="OutSec"/> is not a finite forward range, phrased as
    /// the tail of "Source.InSec/OutSec ..." (the caller supplies the label), or null when the window
    /// is usable. Separate from <see cref="Validate"/> so a caller that reports the field by name is
    /// not forced into that sentence shape.
    /// </summary>
    public string? DescribeWindowProblem()
    {
        if (InSec is { } inSec && (!double.IsFinite(inSec) || inSec < 0 || inSec > MaxWindowSec))
            return $"must be a finite number of seconds in 0..{MaxWindowSec}, not {inSec:0.###}.";

        if (OutSec is { } outSec && (!double.IsFinite(outSec) || outSec <= (InSec ?? 0) || outSec > MaxWindowSec))
            return $"must end at a finite second after InSec {(InSec ?? 0):0.###}, not {outSec:0.###}.";

        return null;
    }
}
