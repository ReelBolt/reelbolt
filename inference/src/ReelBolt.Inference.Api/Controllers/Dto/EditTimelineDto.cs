using System.Text.Json;
using ReelBolt.Shared.Editing;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Body of <c>POST /api/v1/projects/{projectId}/edit-timelines</c>: a name, and optionally the
/// <c>WorkflowStepResult</c> whose <c>TimelineManifest</c> seeds the new document.
/// </summary>
/// <param name="Name">User-facing timeline name. Required, non-blank.</param>
/// <param name="SeedStepResultId">
/// When null the timeline is created blank (two empty tracks). When set, the step result's manifest
/// is downloaded and mapped through <see cref="EditTimelineSeeder"/>.
/// </param>
/// <param name="Settings">
/// R01.3: the format the user picked (size, frame rate, pixel aspect, start timecode). Used for a BLANK
/// timeline only (a seeded one takes the manifest's own settings); null keeps the 1920x1080 / 30 fps default.
/// A value that fails document validation is a 400.
/// </param>
public sealed record CreateEditTimelineRequest(
    string? Name,
    Guid? SeedStepResultId,
    EditTimelineSettings? Settings = null);

/// <summary>
/// Body of <c>PATCH /api/v1/projects/{projectId}/edit-timelines/{id}</c>.
/// </summary>
/// <param name="BaseRevision">
/// The revision the caller edited FROM. Compared against the stored revision: a mismatch is a
/// <c>409</c> and no write happens — this is the optimistic-concurrency contract.
/// </param>
/// <param name="Ops">
/// The client's edit operations, <b>opaque to the server</b> — serialized into
/// <c>edit_timeline_ops.ops_json</c> verbatim and never applied here. The edit engine lives in
/// TypeScript on the client (initiative editor-v2-123, frontend E-202).
/// </param>
/// <param name="Document">The complete post-edit document, replacing the stored one on success.</param>
public sealed record PatchEditTimelineRequest(
    int BaseRevision,
    JsonElement? Ops,
    EditTimeline? Document);

/// <summary>
/// One entry of the lightweight list response — deliberately WITHOUT the document, which is the
/// point of the single-item GET.
/// </summary>
public sealed record EditTimelineSummaryResponse(
    Guid Id,
    string Name,
    int Revision,
    Guid? SeedStepResultId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // Trash list only (null, so omitted, everywhere else): when it was deleted and when it is purged.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DateTime? DeletedAt = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DateTime? PurgesAt = null);

/// <summary>
/// The single-item response shape shared by <c>POST</c>, <c>GET /{id}</c> and a successful
/// <c>PATCH</c>.
/// </summary>
public sealed record EditTimelineResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    int Revision,
    EditTimeline Document,
    Guid? SeedStepResultId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // POST only (null, so omitted, on GET/PATCH): the manifest items a seed could not place.
    // JSON: unplacedItems: [{ track, itemId, label, reason }], [] when none. No storage key/URL.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<UnplacedTimelineItemResponse>? UnplacedItems = null,
    // GET {id} only (null, so omitted, elsewhere): whether the caller may change this timeline
    // (the Edit permission PATCH checks). A viewer-role member reads false; the document still loads.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    bool? CanEdit = null,
    // Import only (omitted elsewhere): clips whose media is not in this project, left as a hole + an "Offline: name" marker
    // (itemId = that marker's id). JSON: offline: [{ itemId, name, reason }], [] when none. Always [] under ?strict=true.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<EditTimelineOfflineClip>? Offline = null,
    // Import only (R12.3): the files that matched and how (id | name | nameAndDuration), one per distinct file.
    // JSON: matched: [{ name, fileId, how }] (stepResultId instead of fileId for a render). [] when none.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<EditTimelineMatchedMedia>? Matched = null,
    // Import only (R12.3): names several project files share and nothing tells apart, left offline rather than guessed.
    // JSON: ambiguous: [{ name, candidates: [{ fileId, name, durationSec?, sizeBytes }] }].
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<EditTimelineAmbiguousMedia>? Ambiguous = null);

/// <summary>
/// One clip an OTIO import left offline. <paramref name="Reason"/> is <c>notFound</c>, <c>ambiguous</c> or
/// <c>durationMismatch</c>; <paramref name="Message"/> is the plain sentence for the user.
/// </summary>
public sealed record EditTimelineOfflineClip(Guid ItemId, string Name, string Reason, string? Message = null);

/// <summary>One file an import matched. <c>How</c> is <c>id</c>, <c>name</c> or <c>nameAndDuration</c>.</summary>
public sealed record EditTimelineMatchedMedia(
    string Name,
    Guid? FileId,
    string How,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? StepResultId = null);

/// <summary>One project file an ambiguous name could mean (ids are project-scoped; never a storage key).</summary>
public sealed record EditTimelineMediaCandidate(
    Guid FileId,
    string Name,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    double? DurationSec,
    long SizeBytes);

/// <summary>A name several project files share and nothing tells apart.</summary>
public sealed record EditTimelineAmbiguousMedia(string Name, IReadOnlyList<EditTimelineMediaCandidate> Candidates);

/// <summary>R12.2: what a hand-off package would carry, so the UI words the choice before it downloads.</summary>
public sealed record HandOffScopeResponse(bool Supported, int MediaFiles, long MediaBytes, bool WouldExceedCap, long CapBytes);

/// <summary>What one export format carries and drops, plus how it names media.</summary>
public sealed record InterchangeScopeResponse(string Format, IReadOnlyList<string> Carries, IReadOnlyList<string> Dropped, string MediaMode, HandOffScopeResponse? HandOff = null);

/// <summary>T-020: the server's truth about each export format's scope.</summary>
public static class InterchangeScope
{
    public static InterchangeScopeResponse For(string format) => format switch
    {
        "edl" => new("edl", ["First video track cuts", "Source in/out timecodes", "Clip names"],
            ["Audio tracks", "Upper video tracks", "Transitions beyond dissolves", "Effects and grades", "Captions", "Keyframes"], "reelbolt-url"),
        "srt" => new("srt", ["Caption text", "Caption timing"], ["Video and audio", "Styling", "Everything except captions"], "none"),
        "vtt" => new("vtt", ["Caption text", "Caption timing"], ["Video and audio", "Styling", "Everything except captions"], "none"),
        "fcpxml" => new("fcpxml", ["Cuts on the main track", "Upper video and audio tracks as connected clips", "Transitions as cross dissolves", "Markers", "Compound and multicam clips flattened"],
            ["Grades", "Keyframes", "Effects", "Captions"], "reelbolt-url"),
        _ => new("otio", ["The complete ReelBolt timeline (lossless in ReelBolt)", "Cuts, gaps and transitions for other tools"],
            ["Grades, keyframes and effects are not understood by other tools"], "reelbolt-url")
    };
}

/// <summary>One manifest item the seeder left off: <paramref name="Reason"/> is <c>source_unresolved</c> or <c>asset_unresolved</c>.</summary>
public sealed record UnplacedTimelineItemResponse(string Track, string ItemId, string? Label, string Reason);

/// <summary>
/// Body of a <c>409 Conflict</c>: the revision the caller must rebase onto. Carried by both the
/// pre-write check and the <c>DbUpdateConcurrencyException</c> race, so the client treats them
/// identically.
/// </summary>
public sealed record EditTimelineConflictResponse(int CurrentRevision);

/// <summary>
/// One <see cref="EditTimelineValidator"/> violation, as returned in a <c>400</c> body:
/// <paramref name="Path"/> says WHERE, <paramref name="Message"/> says WHAT and why.
/// </summary>
public sealed record EditTimelineViolation(string Path, string Message);

/// <summary>
/// Body of the <c>500</c> returned when a document the SERVER built (today only
/// <see cref="EditTimelineSeeder"/>'s output) fails validation. A caller who supplied a valid
/// <c>seedStepResultId</c> did nothing wrong, so this is never a 4xx.
/// </summary>
public sealed record EditTimelineSeedInvalidResponse(
    string Message,
    IReadOnlyList<EditTimelineViolation> Violations);

// ─────────────────────────────────────────────────────────────────────────────
// P3 (editable timeline shell) — history, revisions, duplicate
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One row of <c>GET /api/v1/projects/{projectId}/edit-timelines/{id}/history</c>: the op-log entry
/// a PATCH appended for one revision, newest first.
/// </summary>
/// <param name="Revision">The revision this entry PRODUCED (the PATCH's <c>baseRevision + 1</c>).</param>
/// <param name="CreatedAt">When that PATCH committed.</param>
/// <param name="Ops">
/// The caller's ops for that revision, exactly as stored — opaque to the server, so they are handed
/// back verbatim for the client's history list to describe. Never applied here.
/// </param>
/// <param name="HasSnapshot">
/// True when a full document snapshot exists for this revision, i.e. <c>GET …/revisions/{rev}</c>
/// can answer for it. The server never replays ops, so a revision without a snapshot (and that is
/// not the current one) cannot be restored from the server — the client's own undo stack is the
/// fine-grained history; these rows are the coarse, durable one.
/// </param>
public sealed record EditTimelineHistoryEntryResponse(
    int Revision,
    DateTime CreatedAt,
    JsonElement Ops,
    bool HasSnapshot);

/// <summary>
/// Body of <c>GET …/history</c>: the current revision plus the newest-first op-log page.
/// </summary>
public sealed record EditTimelineHistoryResponse(
    Guid Id,
    int CurrentRevision,
    IReadOnlyList<EditTimelineHistoryEntryResponse> Entries,
    IReadOnlyList<int> RestorableRevisions);

/// <summary>
/// Body of <c>GET …/revisions/{rev}</c>: the document as it stood at <paramref name="Revision"/>.
/// <paramref name="Source"/> says where it came from — <c>current</c> (the live row) or
/// <c>snapshot</c> (an <c>edit_timeline_snapshots</c> row). Nothing is ever replayed.
/// </summary>
public sealed record EditTimelineRevisionResponse(
    Guid Id,
    int Revision,
    string Source,
    EditTimeline Document);

/// <summary>
/// Body of the <c>404</c> a <c>GET …/revisions/{rev}</c> answers when the revision exists in the
/// op log but no stored document can answer for it. Names the revisions that CAN, so a client can
/// offer the nearest restore point instead of a dead end.
/// </summary>
public sealed record EditTimelineRevisionUnavailableResponse(
    string Message,
    int RequestedRevision,
    IReadOnlyList<int> RestorableRevisions);

/// <summary>
/// Body of <c>POST …/duplicate</c>: the new timeline's name. Null or blank defaults to
/// <c>"&lt;source name&gt; (copy)"</c>.
/// </summary>
public sealed record DuplicateEditTimelineRequest(string? Name);

/// <summary>
/// Body of <c>POST …/edit-timelines/{id}/render</c>. Every field is optional; the controller pins the
/// current revision when none is given and validates the resulting
/// <see cref="ReelBolt.Shared.Workflows.TimelineRenderStepConfig"/> before writing anything.
/// </summary>
public sealed record RenderEditTimelineRequest(
    int? Revision = null,
    ReelBolt.Shared.Workflows.TimelineRenderRange? Range = null,
    ReelBolt.Shared.Workflows.TimelineRenderPreset? Preset = null,
    ReelBolt.Shared.Workflows.TimelineRenderEncodeSettings? Encode = null,
    string? OutputFileName = null,
    bool? RegisterProjectFile = null,
    ReelBolt.Shared.Workflows.TimelineRenderLoudness? Loudness = null,
    ReelBolt.Shared.Workflows.TimelineRenderCaptions? Captions = null,
    PreviewRenderRequest? Preview = null);

/// <summary>
/// R10.4: the window of a "Preview 3 s" job — body of <c>render/preview</c> and the <c>preview</c> member of a
/// <c>render/estimate</c> body. <c>Seconds</c> defaults to 3 and must be 1..3.
/// </summary>
public sealed record PreviewRenderRequest(long AtFrame, double? Seconds = null);

/// <summary>R10.4: poll answer of a preview job. <c>Ready</c> is true only when the file can be fetched from <c>content</c>.</summary>
public sealed record PreviewJobResponse(Guid ExecutionId, string Status, string? ErrorMessage, double? DurationSec, bool Ready);

/// <summary>R09.7: one caption sidecar of a finished render; <c>FileId</c> is a project file, downloaded through the project files endpoint.</summary>
public sealed record EditTimelineCaptionFileResponse(string Format, string FileName, long SizeBytes, Guid FileId);

/// <summary>Body of <c>loudness</c> and <c>audition</c>: an optional pinned revision; <c>audition</c> also needs the range.</summary>
public sealed record AudioJobRequest(int? Revision = null, ReelBolt.Shared.Workflows.TimelineRenderRange? Range = null);

/// <summary>Poll answer of a loudness or audition job. Audition audio plays through the outputs download of <c>StepResultId</c>.</summary>
public sealed record AudioJobResponse(
    Guid ExecutionId,
    string Kind,
    string Status,
    int? Revision,
    string? ErrorMessage,
    EditTimelineRenderLoudnessResponse? Loudness,
    double? DurationSec,
    Guid? StepResultId);

/// <summary><c>202 Accepted</c> body: the queued execution and the system workflow that carries it.</summary>
public sealed record RenderEditTimelineResponse(
    Guid ExecutionId,
    Guid WorkflowId,
    Guid TimelineId,
    int Revision,
    string Preset,
    string OutputFileName);

/// <summary>One region of a finished render, as the ruler's cache indicator reads it.</summary>
public sealed record EditTimelineRenderRegionResponse(long StartFrame, long EndFrame, bool Cached);

/// <summary>The region cache summary of a finished render.</summary>
public sealed record EditTimelineRenderRegionsResponse(int Total, int Cached, List<EditTimelineRenderRegionResponse> List);

/// <summary>
/// P5: the loudness the render measured on its final mix (<c>loudnorm</c> pass 1), and whether a
/// target was applied. This is the authoritative number the mixer's live meter is compared against.
/// </summary>
public sealed record EditTimelineRenderLoudnessResponse(
    double IntegratedLufs,
    double TruePeakDbtp,
    double LoudnessRange,
    bool Normalised,
    IReadOnlyList<EditTimelineRenderQualityCheck>? Checks = null)
{
    /// <summary>Quality checks on the mix; empty for outputs stored before the checks existed.</summary>
    public IReadOnlyList<EditTimelineRenderQualityCheck> Checks { get; init; } = Checks ?? [];
}

/// <summary>
/// One render quality check. <c>Kind</c> is one of the closed quality kinds, <c>Severity</c> high|medium|low;
/// the web words it from kind + numbers, <c>Detail</c> is the plain-English fallback.
/// </summary>
public sealed record EditTimelineRenderQualityCheck(
    string Kind, string Severity, double? StartSec, double? EndSec, double? Measured, double? Limit, string Detail);

/// <summary>
/// One timeline item a render left out: which item (null when the output did not name a readable
/// id), its item type, and one plain sentence saying why. The renderer's internal reason code is not
/// exposed (it is not a closed set).
/// </summary>
public sealed record EditTimelineRenderSkippedItemResponse(Guid? ItemId, string? Type, string Message);

/// <summary>
/// One render of an editor timeline: the execution that carried it, the config it was asked for,
/// and — once the step finished — the playable output and the region cache picture.
/// </summary>
/// <remarks>
/// <c>Status</c> is the execution's <c>ExecutionStatus</c> name. <c>StepResultId</c>/<c>OutputStorageKey</c>
/// are null until the <c>TimelineRender</c> step completed; the output is fetched through the
/// outputs endpoints by <c>StepResultId</c>, never by storage key (no bucket path reaches the client
/// as a URL). <c>Regions</c> is parsed from the step's output JSON and is null when the step has not
/// produced one yet. <c>SkippedItems</c>/<c>Warnings</c> are what the render left out and its plain
/// warnings, read leniently from the same output (empty when absent or unreadable; at most 50 each,
/// each sentence at most 300 characters).
/// </remarks>
public sealed record EditTimelineRenderResponse(
    Guid ExecutionId,
    Guid WorkflowId,
    Guid TimelineId,
    string Status,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int? Revision,
    string Preset,
    string OutputFileName,
    string? ErrorMessage,
    Guid? StepResultId,
    bool HasOutput,
    EditTimelineRenderRegionsResponse? Regions,
    EditTimelineRenderLoudnessResponse? Loudness = null,
    IReadOnlyList<EditTimelineRenderSkippedItemResponse>? SkippedItems = null,
    IReadOnlyList<string>? Warnings = null,
    // R06: facts about the finished file, each null when unknown (never an error): the rendered
    // length, the size on disk, the project file the render registered, and when it was queued.
    double? DurationSec = null,
    long? SizeBytes = null,
    Guid? OutputFileId = null,
    DateTime? CreatedAt = null,
    IReadOnlyList<EditTimelineCaptionFileResponse>? CaptionFiles = null);

/// <summary>
/// R06.7: the answer of <c>POST …/edit-timelines/{id}/render/estimate</c>. With billing off only
/// <c>BillingEnabled=false</c> and <c>Affordable=true</c> are meaningful. <c>EstimateSeconds</c> is a
/// documented heuristic (an estimate, not a promise); <c>UnavailableReason</c> says why a figure is absent.
/// </summary>
public sealed record EditTimelineRenderEstimateResponse(
    bool BillingEnabled,
    double? Credits,
    double? CreditsLow,
    double? CreditsHigh,
    double? EstimateSeconds,
    double? BalanceCredits,
    bool Affordable,
    string? UnavailableReason,
    long? EstimatedSizeBytes = null,
    bool? SizeIsApproximate = null);

/// <summary>P6: body of <c>POST …/edit-timelines/import</c> — the OTIO JSON object and the new timeline's name.</summary>
public sealed record ImportEditTimelineRequest(string? Name, System.Text.Json.JsonElement Otio);

/// <summary>P6: why an OTIO import was refused — unresolved media URLs and/or structural errors.</summary>
public sealed record EditTimelineImportRefusedResponse(string Message, IReadOnlyList<string> Errors, IReadOnlyList<string> UnresolvedUrls);

/// <summary>P8: body of <c>POST …/edit-timelines/{id}/proposals</c> — which assist agent, over which range (defaults to the in/out marks, then the whole timeline), with an optional brief.</summary>
public sealed record EditProposalRequest(string Agent, EditRange? Range, string? Brief);

/// <summary>P8: one accept-or-reject unit of a proposal: the TS-engine ops it compiles to and the items it touches.</summary>
public sealed record EditProposalGroupResponse(
    string Label, JsonElement Ops, IReadOnlyList<Guid> TouchedItemIds, bool TouchesUserOwned, string? Reason, IReadOnlyList<EditProposalCutResponse>? Cuts = null);

/// <summary>A content cut inside one clip, in SOURCE seconds of the item's asset (same basis as <c>sourceIn / fps</c>); the client compiles it with its ripple cut.</summary>
public sealed record EditProposalCutResponse(Guid ItemId, double StartSec, double EndSec);

/// <summary>What the proposal did, by class: spans cut and their seconds, clips removed, trimmed, muted, and anything else.</summary>
public sealed record EditProposalDidResponse(int Cut, double CutSeconds, int Remove, int Trim, int Mute, int Other, IReadOnlyList<EditProposalDidCutResponse>? Cuts = null);

/// <summary>Cuts of one kind (speech | quiet | fillers | lines | keep): span count and seconds.</summary>
public sealed record EditProposalDidCutResponse(string Kind, int Spans, double Seconds);

/// <summary>What the editor's request was read as: <c>OpClass</c> cut | mute | trim | reorder and <c>Target</c> speech | silence | fillers | keepOnly, each null when the request names none.</summary>
public sealed record EditProposalAskedResponse(string? OpClass, string? Target);

/// <summary>Analysis the footage still lacks for the request: <c>Kinds</c> transcript | silence and up to eight <c>file:{guid}</c> or <c>step_result:{guid}</c> media refs.</summary>
public sealed record EditProposalNeedsAnalysisResponse(IReadOnlyList<string> Kinds, IReadOnlyList<string> MediaRefs);

/// <summary>P8: a compiled proposal. <c>Rejected</c> lists the model's actions that resolved to nothing (an unknown alias, a word outside the vocabulary) — never guessed.</summary>
public sealed record EditProposalResponse(
    Guid ProposalId, string Agent, string Summary, IReadOnlyList<EditProposalGroupResponse> Groups, IReadOnlyList<string> Rejected,
    string? ModelLabel = null, EditProposalDisclosure? Disclosure = null,
    EditProposalGroundingResponse? Grounding = null, bool AnalysisAvailable = false,
    EditProposalDidResponse? Did = null, EditProposalAskedResponse? Asked = null, int SwapsRefused = 0, EditProposalNeedsAnalysisResponse? NeedsAnalysis = null);

/// <summary>R11.2: how much of the range the model's facts cover. <c>Level</c>: transcriptAndShots | transcriptOnly | shotsOnly | namesOnly; <c>Note</c> is one plain sentence.</summary>
public sealed record EditProposalGroundingResponse(string Level, int ClipsWithFacts, int ClipsTotal, string Note);

/// <summary>R11.2: whether one agent can run on the range; <c>Reason</c> is a plain sentence when it cannot. Only Re-cut carries <c>Grounding</c>.</summary>
public sealed record EditProposalAgentAvailability(bool Available, EditProposalGroundingResponse? Grounding, string? Reason);

public sealed record EditProposalAgentsAvailability(
    EditProposalAgentAvailability Recut, EditProposalAgentAvailability Grade, EditProposalAgentAvailability Review,
    EditProposalAgentAvailability Graphics, EditProposalAgentAvailability Sfx);

/// <summary>R11.2: the answer of <c>GET …/proposals/availability</c>; <c>AnalysisAvailable</c> is true when some clip in range could still be analysed. <c>AnalysisHint</c> (additive, optional): the transcript kinds and <c>file:</c>/<c>step_result:</c> refs of analysable clips that lack one; Re-cut stays available.</summary>
public sealed record EditProposalAvailabilityResponse(EditProposalAgentsAvailability Agents, bool AnalysisAvailable, EditProposalNeedsAnalysisResponse? AnalysisHint = null);

/// <summary>R11.1: what a proposal sends to the model, as counts (never ids or names): clips in range, seconds they cover, offered sound files, brief length, transcript seconds (0: none is sent) and one plain sentence to show.</summary>
public sealed record EditProposalDisclosure(int ItemCount, double RangeSeconds, int SoundFileCount, int BriefChars, double TranscriptSeconds, string Summary);

/// <summary>R11.1: the answer of <c>POST …/proposals/estimate</c>: the render-estimate shape plus <c>Included</c> (the workspace's own provider answers, no credits), the model label, the disclosure and the hourly cap's state.</summary>
public sealed record EditProposalEstimateResponse(
    bool BillingEnabled,
    double? Credits,
    double? CreditsLow,
    double? CreditsHigh,
    double? EstimateSeconds,
    double? BalanceCredits,
    bool Affordable,
    string? UnavailableReason,
    bool Included,
    string? ModelLabel,
    EditProposalDisclosure Disclosure,
    int Used,
    int Limit,
    int RetryAfterSeconds,
    EditProposalGroundingResponse? Grounding = null,
    bool AnalysisAvailable = false);

/// <summary>P9: a timecoded review comment.</summary>
public sealed record EditTimelineCommentResponse(Guid Id, Guid TimelineId, Guid? ParentId, Guid AuthorUserId, string AuthorName, long Frame, long? DurationFrames, string Body, bool Resolved, DateTime CreatedAt, DateTime UpdatedAt,
    // Wave 4 guest review: a guest comment names its self-asserted GuestName (the UI shows "Name (guest)"); AuthorUserId is Guid.Empty for it.
    bool Guest = false, string? GuestName = null,
    // Additive: "person" or "apiKey" (the author is an API key's service account; AuthorName is then the key name).
    string ActorKind = "person");

/// <summary>P9: body of <c>POST …/edit-timelines/{id}/comments</c>. A reply takes its root's frame.</summary>
public sealed record CreateEditTimelineCommentRequest(string? Body, long Frame, long? DurationFrames, Guid? ParentId);

/// <summary>P9: body of <c>PATCH …/comments/{commentId}</c>: a new body (author only) and/or the resolved flag.</summary>
public sealed record UpdateEditTimelineCommentRequest(string? Body, bool? Resolved);

/// <summary>P9: a project preset; <c>payload</c> is the kind's validated JSON.</summary>
public sealed record EditorPresetResponse(Guid Id, string Kind, string Name, JsonElement Payload, Guid CreatedByUserId, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>P9: body of <c>POST/PUT …/edit-timelines/presets</c>.</summary>
public sealed record SaveEditorPresetRequest(string? Kind, string? Name, JsonElement Payload);

/// <summary>P9: a user's editor preferences (<c>GET/PUT /api/v1/editor/preferences</c>).</summary>
public sealed record EditorPreferencesResponse(JsonElement Keymap, JsonElement Layouts, DateTime? UpdatedAt);

/// <summary>P9: body of <c>PUT /api/v1/editor/preferences</c> — both blobs, replaced whole.</summary>
public sealed record SaveEditorPreferencesRequest(JsonElement Keymap, JsonElement Layouts);

/// <summary>One restorable version of a timeline (a stored snapshot). Optional fields are omitted when null.</summary>
public sealed record EditTimelineVersionResponse(
    int Revision,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Name,
    string Cause,
    DateTime CreatedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DateTime? ExpiresAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? CreatedByName,
    // Additive: "person" or "apiKey" (CreatedByName is then the key name). Omitted with CreatedByName.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ActorKind = null);

public sealed record EditTimelineVersionsResponse(IReadOnlyList<EditTimelineVersionResponse> Versions);

/// <summary><c>POST versions</c>: <c>cause</c> is <c>named</c> (default; needs a name) or <c>beforeProposal</c> (name optional).</summary>
public sealed record CreateEditTimelineVersionRequest(string? Name, string? Cause);

public sealed record NameEditTimelineVersionRequest(string? Name);

/// <summary>Body of the <c>409 HANDOFF_TOO_LARGE</c> a hand-off with media answers over the size or file cap.</summary>
public sealed record HandOffTooLargeResponse(string Code, string Message, long TotalBytes, long CapBytes);
