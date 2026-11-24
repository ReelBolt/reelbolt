using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The persisted form of a <see cref="VideoAnalysisExtract"/> — plan D10b, "Persist the intermediate
/// extract artefact".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> D10a split <c>VideoAnalyze</c> into an extraction phase (every
/// ffmpeg/ffprobe job plus the pure-C# analysis derived from it) and an inference phase (everything
/// that leaves the host), and handed the extract between them <i>in memory</i>. That is enough while
/// both phases run back to back in one attempt on one machine, and it is not enough for anything
/// else: a step attempt that fails in the inference phase (an ASR outage, a vision provider timing
/// out, a caption that will not validate) throws away minutes of decoding with it. This type makes
/// the handover value survive the attempt, so a later attempt can start from the extraction the
/// first one already did.
/// </para>
/// <para>
/// <b>Versioned and additive.</b> <see cref="SchemaVersion"/> is checked on load and an unrecognised
/// value is a miss, never a best-effort parse: a document written by a future/other producer must
/// be re-extracted rather than half-trusted. Bump <see cref="CurrentSchemaVersion"/> whenever a
/// member's meaning changes; old documents then simply stop being hits.
/// </para>
/// <para>
/// <b>Not a <see cref="IReadOnlyList{T}"/> mirror of the record, deliberately.</b> The DTO exists
/// for two reasons the record itself cannot serve:
/// </para>
/// <list type="number">
/// <item>
/// <see cref="VideoAnalysisExtractSource.LocalVideoPath"/> (and the virtual path beside it) are
/// session-scoped, not facts about the extraction: a host-absolute path is meaningless on another
/// machine, and a virtual name belongs to the session that was open when the extraction ran. Neither
/// is persisted. The loader is handed both strings to put back
/// (<see cref="ToExtract"/>), so they are reconstructed where the session actually is.
/// </item>
/// <item>
/// <see cref="VideoAnalysisExtractSource.SilenceSpans"/> is an
/// <c>IReadOnlyList&lt;(double StartSec, double EndSec)&gt;</c>. <c>System.Text.Json</c> serializes a
/// <c>ValueTuple</c> as <c>{}</c> — its members are fields, not properties — so serializing the
/// record directly would drop every silence span on the floor and produce a document that looks
/// fine. The flat <see cref="VideoAnalysisExtractArtifactSpan"/> exists so that cannot happen.
/// </item>
/// </list>
/// <para>
/// <b>What it is not.</b> This is a resume aid, not a cache and not a second source of truth. It
/// never replaces the analysis artifact (<c>step-{order}-analysis.json</c>) that
/// <c>WorkflowStepResult.ArtifactStorageKey</c> points at, and it is never served to a user: it
/// lives beside that artifact under the same <c>agentFiles/video-analysis/</c> prefix, which is the
/// prefix that exists precisely because a JSON artifact must never land where
/// <c>OutputsController.ListOutputs</c> would treat it as a playable video (see
/// docs/video-editing.md "Where artifacts live").
/// </para>
/// <para>
/// <b>Media is not in here.</b> Every file the extract names stays where extraction put it: the
/// analysed source under the object store key <see cref="VideoAnalysisExtractArtifactSource.StorageKey"/>,
/// the extracted WAV under <see cref="VideoAnalysisExtractArtifactSource.AudioStorageKey"/>, and the
/// caption keyframes as session files under their own virtual paths. The loader re-materializes
/// what it can (<see cref="VideoAnalysisExtractSource.StorageKey"/> and the WAV are storage keys, so
/// they are re-staged) and reports what it cannot, so the caller decides — see
/// <c>VideoAnalyzeStepExecutor.TryResumeExtractAsync</c>.
/// </para>
/// </remarks>
public sealed record VideoAnalysisExtractArtifact(
    /// <summary>The schema this document was written under; see <see cref="CurrentSchemaVersion"/>.</summary>
    string SchemaVersion,
    /// <summary>The execution the extraction ran in. A document for another execution is a miss.</summary>
    Guid ExecutionId,
    /// <summary>The step order the extraction ran at. A document for another step is a miss.</summary>
    int StepOrder,
    /// <summary>Per-source extraction facts, in analysis (= source-index) order.</summary>
    IReadOnlyList<VideoAnalysisExtractArtifactSource> Sources,
    /// <summary>The merged, globally-id'd deterministic analysis every source contributed to.</summary>
    VideoAnalysisExtractDeterministic Deterministic,
    /// <summary>Project-level <c>audio/*</c> candidates — populated only when the config asked for them.</summary>
    IReadOnlyList<VideoAnalysisMusicCandidate> MusicCandidates,
    /// <summary>Sound-effect candidates — populated only when the config asked for them.</summary>
    IReadOnlyList<VideoAnalysisSfxCandidate> SfxCandidates,
    /// <summary>Beat facts for the music the edit may be cut to, or null when beat analysis is off/degraded.</summary>
    VideoAnalysisMusicTrack? Music,
    /// <summary>One entry per shot selected for captioning, in selection order.</summary>
    IReadOnlyList<VideoAnalysisExtractKeyframe> Keyframes,
    /// <summary>Keyframe extractions that failed, formatted <c>"ExceptionType: message"</c>.</summary>
    IReadOnlyList<string> KeyframeFailures)
{
    /// <summary>
    /// The schema version this build writes. Also spelled in the file name (see
    /// <see cref="BuildStorageKey"/>) so an operator looking at the bucket can see which layout a
    /// document is without opening it — the in-document value is the one that is actually enforced.
    /// </summary>
    public const string CurrentSchemaVersion = "analysis-extract.v1";

    /// <summary>
    /// Same options the analysis artifact is written with — web (camelCase) property naming and the
    /// enum-as-name converter — so an operator reading either document sees the same conventions.
    /// </summary>
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    /// <summary>
    /// The file name (relative to the <c>agentFiles</c> category) this step's extract is stored
    /// under, matching the <c>step-{order}-…</c> convention the analysis artifact and the keyframes
    /// already use inside <c>video-analysis/{executionId}/</c>.
    /// </summary>
    public static string FileName(Guid executionId, int stepOrder)
        => $"video-analysis/{executionId:D}/step-{stepOrder}-extract.v1.json";

    /// <summary>
    /// The full storage key the extract is written to and read back from — derived, never stored on
    /// a row. It is a pure function of (project, execution, step order), which is exactly what makes
    /// an attempt able to find the previous attempt's extraction without a new column: the plan's
    /// "stable key" is the one the step already owns.
    /// </summary>
    public static string BuildStorageKey(Guid projectId, Guid executionId, int stepOrder)
        => ProjectFilePath.BuildStorageKey(projectId, "agentFiles", directoryPath: null, storageFileName: FileName(executionId, stepOrder));

    /// <summary>
    /// Captures an extraction as a persistable document.
    /// </summary>
    /// <param name="extract">The extraction to persist.</param>
    /// <param name="executionId">The execution it ran in — written into the document as the identity a loader checks.</param>
    /// <param name="stepOrder">The step order it ran at — same purpose.</param>
    public static VideoAnalysisExtractArtifact FromExtract(
        VideoAnalysisExtract extract,
        Guid executionId,
        int stepOrder)
    {
        ArgumentNullException.ThrowIfNull(extract);

        return new VideoAnalysisExtractArtifact(
            SchemaVersion: CurrentSchemaVersion,
            ExecutionId: executionId,
            StepOrder: stepOrder,
            Sources: extract.Sources
                .Select(source => new VideoAnalysisExtractArtifactSource(
                    SourceIndex: source.SourceIndex,
                    StorageKey: source.StorageKey,
                    ResolvedStorageKey: source.ResolvedStorageKey,
                    Probe: source.Probe,
                    Media: source.Media,
                    SilenceSpans: source.SilenceSpans
                        .Select(span => new VideoAnalysisExtractArtifactSpan(span.StartSec, span.EndSec))
                        .ToList(),
                    MeasuredLevels: source.MeasuredLevels,
                    HasAudioStream: source.HasAudioStream,
                    AudioVirtualPath: source.AudioVirtualPath,
                    AudioStorageKey: source.AudioStorageKey,
                    AudioBytes: source.AudioBytes,
                    AudioFailureMessage: source.AudioFailureMessage,
                    Shots: source.Shots,
                    Silences: source.Silences,
                    Placements: source.Placements,
                    DuplicateGroups: source.DuplicateGroups,
                    InsertRegions: source.InsertRegions,
                    Provenance: source.Provenance,
                    IsComposition: source.IsComposition,
                    Scenes: source.Scenes,
                    IsStill: source.IsStill,
                    TrimInSec: source.TrimInSec,
                    TrimOutSec: source.TrimOutSec,
                    ClipId: source.ClipId,
                    SubShots: source.SubShots,
                    InputProfile: source.InputProfile,
                    InputProfileSource: source.InputProfileSource,
                    InputProfileIgnoredHdr: source.InputProfileIgnoredHdr,
                    InputProfileNotAppliedReason: source.InputProfileNotAppliedReason,
                    AnalysisCopy: source.AnalysisCopy))
                .ToList(),
            Deterministic: extract.Deterministic,
            MusicCandidates: extract.MusicCandidates,
            SfxCandidates: extract.SfxCandidates,
            Music: extract.Music,
            Keyframes: extract.Keyframes,
            KeyframeFailures: extract.KeyframeFailures);
    }

    /// <summary>
    /// Serializes the document the way the artifact JSON is written.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, ArtifactJsonOptions);

    /// <summary>
    /// Reads a document back, refusing anything that is not exactly this step's own extraction.
    /// </summary>
    /// <param name="json">The downloaded document.</param>
    /// <param name="executionId">The execution the caller is running.</param>
    /// <param name="stepOrder">The step order the caller is running.</param>
    /// <param name="artifact">The parsed document when this returns true.</param>
    /// <param name="reason">
    /// Why the document was refused — always populated on a refusal, and always specific enough to
    /// be worth a log line (a version mismatch, a foreign execution, malformed JSON).
    /// </param>
    public static bool TryDeserialize(
        string json, Guid executionId, int stepOrder, out VideoAnalysisExtractArtifact? artifact, out string? reason)
    {
        artifact = null;
        reason = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "the persisted extract document is empty.";
            return false;
        }

        VideoAnalysisExtractArtifact? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<VideoAnalysisExtractArtifact>(json, ArtifactJsonOptions);
        }
        catch (JsonException ex)
        {
            reason = $"the persisted extract document is not valid JSON: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            reason = "the persisted extract document deserialized to null.";
            return false;
        }

        // Version first, and by exact string equality: a document that does not state the schema
        // this build understands is re-extracted rather than reinterpreted. There is no "compatible
        // older version" here on purpose — the only producer is this executor, and the only thing a
        // lenient parse could buy is a wrong hit.
        if (!string.Equals(parsed.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            reason = $"the persisted extract document declares schema '{parsed.SchemaVersion}', not '{CurrentSchemaVersion}'.";
            return false;
        }

        if (parsed.ExecutionId != executionId || parsed.StepOrder != stepOrder)
        {
            reason = $"the persisted extract document belongs to execution {parsed.ExecutionId:D} step {parsed.StepOrder}, not this one.";
            return false;
        }

        artifact = parsed;
        return true;
    }

    /// <summary>
    /// Rebuilds the in-memory extraction the inference phase consumes.
    /// </summary>
    /// <param name="media">
    /// Per source, where the caller has (re-)staged the analysed media — the one thing this document
    /// deliberately does not carry. Each entry names the media BOTH ways: the session-relative
    /// virtual path every workspace op takes, and the path the caller materialized it at (which is
    /// the very same absolute path for a local workspace). The caller has staged the bytes by the
    /// time it calls this, so neither string is a host path recovered from another machine.
    /// </param>
    public VideoAnalysisExtract ToExtract(IReadOnlyList<VideoAnalysisExtractMedia> media)
    {
        ArgumentNullException.ThrowIfNull(media);

        var sources = new List<VideoAnalysisExtractSource>(Sources.Count);
        foreach (VideoAnalysisExtractArtifactSource source in Sources)
        {
            if (source.SourceIndex >= media.Count)
            {
                throw new ArgumentException(
                    $"No media was supplied for source {source.SourceIndex}.", nameof(media));
            }

            sources.Add(new VideoAnalysisExtractSource(
                SourceIndex: source.SourceIndex,
                StorageKey: source.StorageKey,
                ResolvedStorageKey: source.ResolvedStorageKey,
                VideoVirtualPath: media[source.SourceIndex].VirtualPath,
                LocalVideoPath: media[source.SourceIndex].LocalPath,
                Probe: source.Probe,
                Media: source.Media,
                SilenceSpans: source.SilenceSpans
                    .Select(span => (span.StartSec, span.EndSec))
                    .ToList(),
                MeasuredLevels: source.MeasuredLevels,
                HasAudioStream: source.HasAudioStream,
                AudioVirtualPath: source.AudioVirtualPath,
                AudioStorageKey: source.AudioStorageKey,
                AudioBytes: source.AudioBytes,
                AudioFailureMessage: source.AudioFailureMessage,
                Shots: source.Shots,
                Silences: source.Silences,
                Placements: source.Placements,
                DuplicateGroups: source.DuplicateGroups,
                InsertRegions: source.InsertRegions,
                Provenance: source.Provenance,
                IsComposition: source.IsComposition,
                Scenes: source.Scenes,
                IsStill: source.IsStill,
                TrimInSec: source.TrimInSec,
                TrimOutSec: source.TrimOutSec,
                ClipId: source.ClipId,
                SubShots: source.SubShots,
                InputProfile: source.InputProfile,
                InputProfileSource: source.InputProfileSource,
                InputProfileIgnoredHdr: source.InputProfileIgnoredHdr,
                    InputProfileNotAppliedReason: source.InputProfileNotAppliedReason,
                AnalysisCopy: source.AnalysisCopy,
                // Not in the document: the version of the bytes this attempt staged.
                PassSourceVersion: media[source.SourceIndex].PassSourceVersion));
        }

        return new VideoAnalysisExtract(
            Sources: sources,
            Deterministic: Deterministic,
            MusicCandidates: MusicCandidates,
            SfxCandidates: SfxCandidates,
            Music: Music,
            Keyframes: Keyframes,
            KeyframeFailures: KeyframeFailures);
    }
}

/// <summary>
/// One source's persisted extraction facts. Mirrors <see cref="VideoAnalysisExtractSource"/> member
/// for member with exactly one difference, and it is the point:
/// <see cref="VideoAnalysisExtractSource.LocalVideoPath"/> and
/// <see cref="VideoAnalysisExtractSource.VideoVirtualPath"/> are both absent — a host-absolute path
/// must not leave the machine that produced it, and a virtual name only means something inside the
/// session that was open at the time. The loader supplies both for the session it is actually in.
/// <c>VideoAnalysisExtractArtifactShapeTests</c> asserts that member correspondence, so a member
/// added to the record cannot be silently dropped from the document.
/// </summary>
public sealed record VideoAnalysisExtractArtifactSource(
    int SourceIndex,
    string StorageKey,
    string ResolvedStorageKey,
    MediaProbeResult Probe,
    VideoAnalysisMedia Media,
    IReadOnlyList<VideoAnalysisExtractArtifactSpan> SilenceSpans,
    IReadOnlyList<WavRmsSampler.RmsWindow>? MeasuredLevels,
    bool HasAudioStream,
    string? AudioVirtualPath,
    string? AudioStorageKey,
    long AudioBytes,
    string? AudioFailureMessage,
    IReadOnlyList<VideoAnalysisShot> Shots,
    IReadOnlyList<VideoAnalysisSilenceSpan> Silences,
    IReadOnlyList<VideoAnalysisPlacement> Placements,
    IReadOnlyList<VideoAnalysisDuplicateGroup> DuplicateGroups,
    IReadOnlyList<VideoInsertRegionTrack> InsertRegions,
    VideoAnalysisProvenance Provenance,
    bool IsComposition,
    VideoAnalysisSceneSummary? Scenes,
    bool IsStill,
    double? TrimInSec,
    double? TrimOutSec,
    /// <summary>#93 phase 4: the generated clip id this source stands for (GeneratedClips expansion), else null. Appended and defaulted so earlier documents still deserialize.</summary>
    string? ClipId = null,
    /// <summary>The shots a content split cut into parts, and how (see <see cref="VideoAnalysisExtractSource.SubShots"/>). Appended and defaulted so earlier documents still deserialize.</summary>
    IReadOnlyList<VideoAnalysisSubShotSplit>? SubShots = null,
    /// <summary>log-footage-color T-013: the camera profile the analysis copy was converted with, and where it came from. Appended and defaulted so earlier documents still deserialize.</summary>
    string? InputProfile = null,
    string? InputProfileSource = null,
    bool InputProfileIgnoredHdr = false,
    string? InputProfileNotAppliedReason = null,
    /// <summary>The analysis copy this source's pixel passes read, by policy (see <see cref="VideoAnalysisExtractSource.AnalysisCopy"/>). Appended and defaulted so earlier documents still deserialize.</summary>
    VideoAnalysisCopyPlan? AnalysisCopy = null);

/// <summary>
/// A silence span, persisted flat. See <see cref="VideoAnalysisExtractArtifact"/>'s remarks: the
/// record's own <c>IReadOnlyList&lt;(double, double)&gt;</c> would serialize as an array of empty
/// objects and silently lose every span.
/// </summary>
public sealed record VideoAnalysisExtractArtifactSpan(double StartSec, double EndSec);
