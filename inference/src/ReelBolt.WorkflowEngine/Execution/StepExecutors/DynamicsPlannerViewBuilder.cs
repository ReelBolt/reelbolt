using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Dynamics;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Builds the input of an <c>AgentType.DynamicsPlanner</c> step (design <c>docs/design/dynamic-edit.md</c> "Open"): the short view the
/// planner needs, and nothing else. The story editor runs before generation and cannot name a generated clip; this agent runs after the
/// take selection, so its view holds the clip ids the compile will accept as the other side of a <c>BeatAlternate</c>.
///
/// <para>
/// The view is words, ids and small counts only: the source's offered shot ids with a few caption words, the generated clips the compile
/// would actually offer (delivered, a picture purpose, admitted by the take selection) with their id, purpose and the planned shot's
/// description, the selected take ids, and the music facts as words and counts. Never a storage key, a file path or a time in seconds.
/// Nothing is fatal: a missing piece is left out and the planner is told what it was not given.
/// </para>
/// </summary>
public interface IDynamicsPlannerViewBuilder
{
    /// <summary>The planner's input for this step: the view followed by the run's user request.</summary>
    Task<string> BuildInputAsync(StepExecutionContext context, CancellationToken ct);
}

/// <inheritdoc cref="IDynamicsPlannerViewBuilder"/>
public sealed class DynamicsPlannerViewBuilder : IDynamicsPlannerViewBuilder
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Most source shots listed (the planner anchors to a few of them; a long film is already outlined by the analysis).</summary>
    internal const int MaxShots = 48;

    /// <summary>Most generated clips listed (the compile admits at most <c>MaxGeneratedClips</c> of them anyway).</summary>
    internal const int MaxClips = 16;

    /// <summary>Longest description word run per shot or clip.</summary>
    internal const int MaxWordsChars = 120;

    // The purposes the compile accepts as a side of an alternation (VideoCompileStepExecutor.ResolveAlternationClipsAsync).
    private static readonly HashSet<string> PicturePurposes = new(StringComparer.OrdinalIgnoreCase) { "cutaway", "coldopen", "endcard" };

    private readonly IProjectFileWorkspace _workspace;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<DynamicsPlannerViewBuilder> _logger;
    private readonly IAudioExtractor _audioExtractor;
    private readonly IMediaWorkspaceFactory? _mediaWorkspaces;

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DynamicsPlannerViewBuilder(
        IProjectFileWorkspace workspace,
        IOptions<VideoEditingOptions> options,
        ILogger<DynamicsPlannerViewBuilder> logger,
        IAudioExtractor audioExtractor,
        IMediaWorkspaceFactory? mediaWorkspaces = null)
    {
        _mediaWorkspaces = mediaWorkspaces;
        _audioExtractor = audioExtractor;
        _workspace = workspace;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> BuildInputAsync(StepExecutionContext context, CancellationToken ct)
    {
        VideoAnalysisArtifact? artifact = null;
        JsonArray? manifestClips = null;
        VideoScratchSpace? scratch = null;
        try
        {
            scratch = VideoScratchSpace.Create(_options, context.Execution.Id, context.Step.Id, _logger);
            StepOutputHistoryEntry? analyzeEntry = FindAnalyzeEntry(context);
            if (analyzeEntry is not null)
                artifact = await TryLoadAsync<VideoAnalysisArtifact>(context, scratch, analyzeEntry.ArtifactStorageKey!, "analysis-for-dynamics.json", ct);

            StepOutputHistoryEntry? generateEntry = FindGeneratedClipsEntry(context);
            if (generateEntry is not null)
            {
                JsonObject? manifest = !string.IsNullOrWhiteSpace(generateEntry.ArtifactStorageKey)
                    ? await TryLoadAsync<JsonObject>(context, scratch, generateEntry.ArtifactStorageKey!, "generated-clips-for-dynamics.json", ct)
                    : null;
                // Without the manifest the step's own output still lists the clips (no purpose, no storage key).
                manifestClips = manifest?["clips"] as JsonArray ?? OutputClips(generateEntry.Output);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A view is a convenience: whatever could not be read is simply absent, and the planner is told so.
            _logger.LogWarning(ex, "DynamicsPlanner step {StepOrder}: the view could not be fully built.", context.Step.StepOrder);
        }
        finally
        {
            scratch?.Dispose();
        }

        IReadOnlyList<string>? selectedTakes = FindSelectedTakes(context);
        string? planTrackId = FindMusicTrackId(context);
        VideoAnalysisMusicBeat? configuredBeat = null;
        string? beatGridFailure = null;
        if (MusicFacts(artifact, planTrackId) is null)
            (configuredBeat, beatGridFailure) = await ConfiguredMusicBeatAsync(context, artifact, planTrackId, ct);
        JsonObject view = Build(
            artifact, manifestClips, selectedTakes, planTrackId, configuredBeat, beatGridFailure,
            FindKeep(context), ConfiguredBeatSync(context.AllSteps, context.Step.StepOrder));
        return context.WithUserRequest(new JsonObject { ["view"] = view }.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    /// <summary>
    /// The view as data. Pure: <paramref name="artifact"/> is the source's analysis (offered shot ids and music facts),
    /// <paramref name="manifestClips"/> the generated-clips manifest, <paramref name="selectedTakeIds"/> the dailies selection (null
    /// when there was none) and <paramref name="musicTrackId"/> the track the music plan chose (null when none was planned).
    /// </summary>
    internal static JsonObject Build(
        VideoAnalysisArtifact? artifact, JsonArray? manifestClips, IReadOnlyList<string>? selectedTakeIds, string? musicTrackId,
        VideoAnalysisMusicBeat? configuredMusicBeat = null, string? beatGridFailure = null,
        IReadOnlyList<(string FromId, string ToId)>? keep = null, VideoBeatSync beatSync = VideoBeatSync.Off)
    {
        var view = new JsonObject();
        var notes = new JsonArray();

        VideoAnalysisMusicBeat? beat = MusicFacts(artifact, musicTrackId) ?? configuredMusicBeat;
        var shots = new JsonArray();
        // QA-DYN-45: with the editor's cut known, a shot's length is what is KEPT of it after the beat sync's trim, and the shot that holds the
        // drop is the one on screen when the music's drop falls in the program. Null when there is no cut or no beat to lay it against.
        // QA-DYN-54: the music starts with the film, so a cold open puts the body that long into the track: the drop and the bar lines are read
        // at the body's time. The length is the manifest's requested one (the compile measures the real clip), so the layout stays approximate.
        double coldOpenSec = ColdOpenSeconds(manifestClips, selectedTakeIds);
        Dictionary<string, KeptShot>? kept = artifact is not null && keep is { Count: > 0 } && beat is not null ? KeptLayout(artifact, keep, beat, beatSync, coldOpenSec) : null;
        bool dropInColdOpen = beat?.DropSec is { } musicDrop && coldOpenSec > 0 && musicDrop < coldOpenSec - 1e-6;
        bool dropHeld = false;
        if (artifact is not null)
        {
            HashSet<string> offered = new(artifact.OfferedIds ?? [], StringComparer.Ordinal);
            foreach (VideoAnalysisShot shot in artifact.Shots)
            {
                if (shots.Count >= MaxShots)
                    break;
                if (!offered.Contains(shot.Id))
                    continue;
                var node = new JsonObject { ["id"] = shot.Id };
                if (Words(shot.Caption) is { Length: > 0 } words)
                    node["words"] = words;
                // How much room the shot gives a move, as a word (never a number): the compile needs whole cells between a grid line and the
                // end of the shot, so a short shot drops a BeatAlternate (window_too_short) and only a long one takes division Bar.
                string? lengthWord = kept is null ? LengthWord(shot, beat) : kept.TryGetValue(shot.Id, out KeptShot keptShot) ? LengthWord(keptShot.KeptSec, beat) : null;
                if (lengthWord is not null)
                    node["length"] = lengthWord;
                if (kept is not null && kept.TryGetValue(shot.Id, out KeptShot holder) && holder.HoldsDrop)
                {
                    node["drop"] = "holds the drop";
                    dropHeld = true;
                }

                shots.Add(node);
            }
        }

        view["sourceShots"] = shots;
        if (shots.Count == 0)
            notes.Add("No source shot ids were offered.");

        var clips = new JsonArray();
        if (manifestClips is not null)
        {
            TakeSelectionFilterAdapter filter = new(selectedTakeIds, manifestClips);
            foreach (JsonNode? item in manifestClips)
            {
                if (clips.Count >= MaxClips)
                    break;
                if (item is not JsonObject clip || clip["clipId"]?.GetValue<string>() is not { Length: > 0 } clipId)
                    continue;
                if (!string.Equals(clip["status"]?.GetValue<string>(), "succeeded", StringComparison.OrdinalIgnoreCase))
                    continue;
                // The manifest names the purpose; the step's own output (the fallback) does not, so a clip without one is listed unlabelled.
                string? purpose = clip["purpose"]?.GetValue<string>()?.Trim().ToLowerInvariant();
                if (purpose is { Length: > 0 } && !PicturePurposes.Contains(purpose))
                    continue;
                if (!filter.Admits(clip, clipId))
                    continue;
                var node = new JsonObject { ["id"] = clipId };
                if (purpose is { Length: > 0 })
                    node["purpose"] = purpose;
                if (Trim(clip["description"]?.GetValue<string>()) is { Length: > 0 } words)
                    node["words"] = words;
                clips.Add(node);
            }
        }

        view["generatedClips"] = clips;
        if (clips.Count == 0)
            notes.Add("No generated clip is available to alternate with.");
        else
            notes.Add("Generated clip ids are only usable as otherId of BeatAlternate or StrobeCut; every anchorId must be a source shot id.");

        if (selectedTakeIds is { Count: > 0 })
        {
            view["selectedTakes"] = new JsonArray([.. selectedTakeIds
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode)id)]);
        }

        if (beat is null)
        {
            view["music"] = new JsonObject { ["present"] = false };
            notes.Add(beatGridFailure is null
                ? "No music beat is known, so there is no beat to land on."
                : $"The music beat could not be measured ({beatGridFailure}), so there is no beat to land on.");
        }
        else
        {
            view["music"] = new JsonObject
            {
                ["present"] = true,
                ["tempo"] = beat.Tempo,
                ["bpm"] = (int)Math.Round(beat.Bpm),
                ["beatsPerBar"] = beat.BeatSec > 0 ? Math.Max(1, (int)Math.Round(beat.BarSec / beat.BeatSec)) : 4,
                ["dropPresent"] = beat.DropSec is not null && !dropInColdOpen
            };
        }

        if (beat is not null && shots.Count > 0)
        {
            notes.Add("A shot's length word says how much room it gives a move: a short shot takes no BeatAlternate, a medium one takes division Beat or HalfBeat, only a long one takes division Bar.");
            if (dropInColdOpen)
                notes.Add("The music's drop plays inside the cold open, before the body starts, so no move takes moment OnDrop; use OnBar, OnBeat or OnAccent.");
            else if (beat.DropSec is not null)
            {
                if (kept is null)
                    notes.Add("moment OnDrop applies only to the one shot that holds the music's drop; on any other shot use OnBar, OnBeat or OnAccent.");
                else if (dropHeld)
                    notes.Add("moment OnDrop applies only to the one shot marked \"drop\": \"holds the drop\"; on any other shot use OnBar, OnBeat or OnAccent.");
                else
                    notes.Add("The music's drop falls on no kept shot, so no move takes moment OnDrop; use OnBar, OnBeat or OnAccent.");
            }
        }

        if (notes.Count > 0)
            view["notes"] = notes;
        return view;
    }

    /// <summary>
    /// The shot's length against the music's beat, as a word: <c>short</c> (under a bar), <c>medium</c> (a bar up to three bars) or <c>long</c>
    /// (three bars or more); null when the beat or the shot's extent is unknown. The words follow the compile's own limits: an alternation
    /// needs two whole cells after its first grid line, which a bar-division alternation only has in a long shot.
    /// </summary>
    internal static string? LengthWord(VideoAnalysisShot shot, VideoAnalysisMusicBeat? beat) => LengthWord(shot.EndSec - shot.StartSec, beat);

    /// <summary>The same words for a length in seconds (the kept part of a shot).</summary>
    internal static string? LengthWord(double seconds, VideoAnalysisMusicBeat? beat)
    {
        if (beat is null || !(beat.BeatSec > 0) || !(seconds > 0))
            return null;
        double beats = seconds / beat.BeatSec;
        return beats < 4 ? "short" : beats < 12 ? "medium" : "long";
    }

    /// <summary>The cold open's length the manifest asks for (succeeded cold open clips the take selection admits), 0 when there is none.</summary>
    internal static double ColdOpenSeconds(JsonArray? manifestClips, IReadOnlyList<string>? selectedTakeIds)
    {
        if (manifestClips is null)
            return 0;
        TakeSelectionFilterAdapter filter = new(selectedTakeIds, manifestClips);
        double sum = 0;
        foreach (JsonNode? item in manifestClips)
        {
            if (item is not JsonObject clip || clip["clipId"]?.GetValue<string>() is not { Length: > 0 } clipId)
                continue;
            if (!string.Equals(clip["purpose"]?.GetValue<string>()?.Trim(), "coldopen", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(clip["status"]?.GetValue<string>(), "succeeded", StringComparison.OrdinalIgnoreCase)
                || !filter.Admits(clip, clipId))
                continue;
            if (clip["durationSeconds"] is JsonValue dur && dur.TryGetValue(out double seconds) && seconds > 0)
                sum += seconds;
            else if (clip["durationSeconds"] is JsonValue durInt && durInt.TryGetValue(out int whole) && whole > 0)
                sum += whole;
        }

        return sum;
    }

    /// <summary>What the film keeps of one offered shot: its kept length in seconds and whether the music's drop falls inside it.</summary>
    internal readonly record struct KeptShot(double KeptSec, bool HoldsDrop);

    /// <summary>
    /// The offered shots as the compile will cut them. One span per keep entry (its first shot's start to its last shot's end, in the entry's
    /// source), the beat sync's trim applied with the same planner the compile runs (the tail moves back to a grid line, the drop is cut on),
    /// then each shot's kept length and, laid on the program from time 0 (the music starts with the program), the shot the drop falls in.
    /// A shot the editor did not keep is absent. Approximate: padding and a target-length fit are not modelled.
    /// </summary>
    internal static Dictionary<string, KeptShot> KeptLayout(
        VideoAnalysisArtifact artifact, IReadOnlyList<(string FromId, string ToId)> keep, VideoAnalysisMusicBeat beat, VideoBeatSync beatSync, double coldOpenSec = 0)
    {
        // The grid as the body sees it (QA-DYN-54): the phases and the drop shifted by the cold open; a drop inside the cold open is none.
        MusicBeatAnalysis bodyBeat = MusicFilmTime.ToBodyTime(MusicBeatProbe.FromFacts(beat), MusicFilmTime.BodyShiftSec(coldOpenSec), out _);
        var result = new Dictionary<string, KeptShot>(StringComparer.Ordinal);
        Dictionary<string, VideoAnalysisShot> byId = [];
        foreach (VideoAnalysisShot shot in artifact.Shots)
            byId.TryAdd(shot.Id, shot);

        var spans = new List<(int Source, double Start, double End)>();
        foreach ((string fromId, string toId) in keep)
        {
            if (!byId.TryGetValue(fromId.Trim(), out VideoAnalysisShot? from) || !byId.TryGetValue(toId.Trim(), out VideoAnalysisShot? to)
                || from.SourceIndex != to.SourceIndex)
                continue;
            double start = Math.Min(from.StartSec, to.StartSec), end = Math.Max(from.EndSec, to.EndSec);
            if (end > start)
                spans.Add((from.SourceIndex, start, end));
        }

        if (spans.Count == 0)
            return result;

        List<double> durations = [.. spans.Select(sp => sp.End - sp.Start)];
        if (beatSync != VideoBeatSync.Off && beat.BeatSec > 0)
        {
            var grid = new DynamicsGrid(bodyBeat.BeatSec, bodyBeat.BeatPhaseSec, bodyBeat.BarPhaseSec, bodyBeat.DropSec);
            double until = durations.Sum() + beat.BeatSec * 8;
            durations = BeatSyncPlanner.Plan(durations, grid.Beats(until), grid.Bars(until), beat.BeatSec, bodyBeat.DropSec, beatSync == VideoBeatSync.Bar).Durations;
        }

        double cursor = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            (int source, double start, double _) = spans[i];
            double keptEnd = start + durations[i];
            foreach (VideoAnalysisShot shot in artifact.Shots.Where(sh => sh.SourceIndex == source && sh.StartSec >= start - 1e-3 && sh.EndSec <= spans[i].End + 1e-3))
            {
                double a = Math.Max(shot.StartSec, start), b = Math.Min(shot.EndSec, keptEnd);
                if (b - a <= 1e-6)
                    continue;
                double programStart = cursor + (a - start), programEnd = programStart + (b - a);
                bool holds = bodyBeat.DropSec is { } drop && drop >= programStart - 1e-6 && drop < programEnd - 1e-6;
                result.TryAdd(shot.Id, new KeptShot(b - a, holds));
            }

            cursor += durations[i];
        }

        return result;
    }

    /// <summary>The story editor's cut: the <c>keep</c> entries (first and last shot id) of the latest earlier step that carries them; null when none does.</summary>
    internal static IReadOnlyList<(string FromId, string ToId)>? FindKeep(StepExecutionContext context)
    {
        foreach (StepOutputHistoryEntry entry in Earlier(context).Reverse())
        {
            if (Root(entry.Output) is not { } root || root["view"] is not null || root["keep"] is not JsonArray keep)
                continue;
            var entries = new List<(string, string)>();
            foreach (JsonObject item in keep.OfType<JsonObject>())
                if (item["fromId"] is JsonValue f && f.TryGetValue(out string? from) && item["toId"] is JsonValue t && t.TryGetValue(out string? to)
                    && !string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
                    entries.Add((from, to));
            return entries.Count > 0 ? entries : null;
        }

        return null;
    }

    /// <summary>The beat sync mode of the compile the plan feeds (the one that reads this planner, else the first compile after it).</summary>
    internal static VideoBeatSync ConfiguredBeatSync(IEnumerable<WorkflowStep> allSteps, int plannerStepOrder)
    {
        var compiles = allSteps.Where(s => s.StepOrder > plannerStepOrder && s.StepType == StepType.VideoCompile && !string.IsNullOrWhiteSpace(s.VideoCompileConfigJson))
            .OrderBy(s => s.StepOrder)
            .Select(s =>
            {
                try { return (Step: s, Config: JsonSerializer.Deserialize<VideoCompileStepConfig>(s.VideoCompileConfigJson!, ConfigJsonOptions)); }
                catch (JsonException) { return (Step: s, Config: null); }
            })
            .Where(t => t.Config is not null).ToList();
        var referencing = compiles.FirstOrDefault(t => t.Config!.DynamicsPlan is { } r
            && (r.From == ExtractInputSource.Previous || (r.From == ExtractInputSource.Step && r.StepOrder == plannerStepOrder)));
        return (referencing.Config ?? compiles.FirstOrDefault().Config)?.BeatSync ?? VideoBeatSync.Off;
    }

    /// <summary>
    /// The music track the downstream VideoCompile step is configured with (<c>MusicTrackProjectFileId</c>): the compile step whose
    /// <c>DynamicsPlan</c> reads this planner (Previous, or Step with this step's order), else the next compile step with a track.
    /// Null when none names one.
    /// </summary>
    internal static Guid? ConfiguredMusicFileId(IEnumerable<WorkflowStep> allSteps, int plannerStepOrder)
    {
        VideoCompileStepConfig? Config(WorkflowStep s)
        {
            if (s.StepType != StepType.VideoCompile || string.IsNullOrWhiteSpace(s.VideoCompileConfigJson))
                return null;
            try { return JsonSerializer.Deserialize<VideoCompileStepConfig>(s.VideoCompileConfigJson, ConfigJsonOptions); }
            catch (JsonException) { return null; }
        }

        var compiles = allSteps.Where(s => s.StepOrder > plannerStepOrder).OrderBy(s => s.StepOrder)
            .Select(s => (Step: s, Config: Config(s))).Where(t => t.Config is not null).ToList();
        var referencing = compiles.FirstOrDefault(t => t.Config!.DynamicsPlan is { } r
            && (r.From == ExtractInputSource.Previous ? t.Step.StepOrder > plannerStepOrder
                : r.From == ExtractInputSource.Step && r.StepOrder == plannerStepOrder));
        if (referencing.Config?.MusicTrackProjectFileId is { } direct)
            return direct;
        return compiles.Select(t => t.Config!.MusicTrackProjectFileId).FirstOrDefault(id => id is not null);
    }

    /// <summary>
    /// The beat facts of the track the compile will cut to, when the analysis carries none: the MusicPlan-selected
    /// track when the plan chose an offered one (as the compile's <c>ResolveBeatGridAsync</c> does), else the configured
    /// <c>MusicTrackProjectFileId</c>. Measured through the same media-workspace probe path as the compile, so on the
    /// compute pool it runs on the runner and not on the ffmpeg of this host. A failed measurement comes back with its
    /// reason (<c>beat_grid_failed:&lt;cause&gt;</c>), never as a silent null.
    /// </summary>
    private async Task<(VideoAnalysisMusicBeat? Beat, string? FailureReason)> ConfiguredMusicBeatAsync(
        StepExecutionContext context, VideoAnalysisArtifact? artifact, string? planTrackId, CancellationToken ct)
    {
        VideoScratchSpace? scratch = null;
        MediaWorkspaceLease? lease = null;
        try
        {
            Guid? fileId = null;
            if (planTrackId is { Length: > 0 } && (artifact?.OfferedMusicIds ?? []).Contains(planTrackId))
                fileId = artifact?.MusicCandidates?.FirstOrDefault(m => m.Id == planTrackId)?.ProjectFileId;
            fileId ??= ConfiguredMusicFileId(context.AllSteps, context.Step.StepOrder);
            if (fileId is not { } id)
                return (null, null);
            VideoAnalysisMusicBeat? known = artifact?.Music?.ProjectFileId == id
                ? artifact.Music.Beat
                : artifact?.MusicCandidates?.FirstOrDefault(m => m.ProjectFileId == id)?.Beat;
            if (known is not null)
                return (known, null);
            IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(context.Execution.ProjectId, ct);
            ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == id);
            if (file is null || !file.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                return (null, null);

            if (_mediaWorkspaces is null)
            {
                // No workspace factory (a hand-built builder): the absolute-path arm on this host, with its failures logged.
                scratch = VideoScratchSpace.Create(_options, context.Execution.Id, context.Step.Id, _logger);
                MusicBeatAnalysis? local = await MusicBeatProbe.MeasureAsync(
                    _workspace, _audioExtractor, context.Execution.ProjectId, file, scratch, "dynamics-music", ct, _logger);
                return (local is null ? null : MusicBeatProbe.ToFacts(local), null);
            }

            MediaWorkspaceOpenResult opened = await _mediaWorkspaces.OpenAsync(
                new MediaWorkspaceRequest(
                    ExecutionId: context.Execution.Id,
                    OrganizationId: context.Execution.OrganizationId,
                    ProjectId: context.Execution.ProjectId,
                    StepId: context.Step.Id,
                    StepOrder: context.Step.StepOrder,
                    JobType: RunnerJobType.VideoAnalyzeExtract,
                    Purpose: $"dynamics.planner.beat:{context.Step.StepOrder}"),
                ct).ConfigureAwait(false);
            if (opened.Lease is null)
            {
                string refusal = MusicBeatMeasurement.ReasonFor(opened.FailureCode ?? "workspace_refused");
                _logger.LogWarning(
                    "DynamicsPlanner step {StepOrder}: no media workspace to measure the music beat in: {Reason} ({Message})",
                    context.Step.StepOrder, refusal, opened.Message);
                return (null, refusal);
            }

            lease = opened.Lease;
            MusicBeatMeasurement measured = await MusicBeatProbe.TryMeasureAsync(
                lease.Workspace, _audioExtractor, file, "dynamics-music", _logger, ct).ConfigureAwait(false);
            return measured.Analysis is null
                ? (null, measured.FailureCause is null ? null : measured.Reason)
                : (MusicBeatProbe.ToFacts(measured.Analysis), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string reason = MusicBeatMeasurement.ReasonFor(MusicBeatProbe.CauseOf(ex));
            _logger.LogWarning(
                ex, "DynamicsPlanner step {StepOrder}: the music beat could not be measured ({Reason}): {ExceptionType}: {Message}",
                context.Step.StepOrder, reason, ex.GetType().Name, ex.Message);
            return (null, reason);
        }
        finally
        {
            scratch?.Dispose();
            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static VideoAnalysisMusicBeat? MusicFacts(VideoAnalysisArtifact? artifact, string? trackId)
    {
        if (artifact is null)
            return null;
        if (trackId is { Length: > 0 } && artifact.MusicCandidates?.FirstOrDefault(m => m.Id == trackId)?.Beat is { } planned)
            return planned;
        return artifact.Music?.Beat ?? artifact.MusicCandidates?.Select(m => m.Beat).FirstOrDefault(b => b is not null);
    }

    private static string? Words(VideoShotCaption? caption)
    {
        if (caption is null)
            return null;
        string[] parts = [caption.Summary, caption.TimeOfDay is "Night" or "GoldenHour" ? caption.TimeOfDay.ToLowerInvariant() : string.Empty];
        return Trim(string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p))));
    }

    private static string? Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= MaxWordsChars ? t : t[..MaxWordsChars] + "...";
    }

    // ---- History resolution: duck-typed by shape, like MotionGraphicsPlacementAnnotator (an Agent step has no config pointing at its inputs). ----

    private static IEnumerable<StepOutputHistoryEntry> Earlier(StepExecutionContext context) =>
        context.StepOutputHistory.Where(h => h.StepOrder < context.Step.StepOrder).OrderBy(h => h.StepOrder);

    /// <summary>The first earlier step whose output is the <c>{view, meta}</c> envelope and which kept an artifact: the source's analysis.</summary>
    private static StepOutputHistoryEntry? FindAnalyzeEntry(StepExecutionContext context) =>
        Earlier(context).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.ArtifactStorageKey) && Root(h.Output)?["view"] is JsonObject);

    /// <summary>The latest earlier step whose output lists generated <c>clips</c> (a VideoGenerate step).</summary>
    private static StepOutputHistoryEntry? FindGeneratedClipsEntry(StepExecutionContext context) =>
        Earlier(context).LastOrDefault(h => Root(h.Output) is { } root && root["view"] is null && root["clips"] is JsonArray);

    /// <summary>The take ids the dailies room selected (null when no earlier step carries a take selection).</summary>
    private static IReadOnlyList<string>? FindSelectedTakes(StepExecutionContext context)
    {
        foreach (StepOutputHistoryEntry entry in Earlier(context).Reverse())
        {
            if (Root(entry.Output) is not { } root || root["selected"] is not JsonArray selected)
                continue;
            return [.. selected.OfType<JsonObject>()
                .Select(s => s["takeId"]?.GetValue<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)];
        }

        return null;
    }

    /// <summary>The track a music plan chose (a MusicSupervisor output: <c>trackId</c> and <c>planRationale</c>), when one ran.</summary>
    private static string? FindMusicTrackId(StepExecutionContext context)
    {
        foreach (StepOutputHistoryEntry entry in Earlier(context).Reverse())
        {
            if (Root(entry.Output) is { } root && root["view"] is null && root["planRationale"] is not null
                && root["trackId"] is JsonValue id && id.TryGetValue(out string? trackId) && !string.IsNullOrWhiteSpace(trackId))
                return trackId;
        }

        return null;
    }

    private static JsonObject? Root(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        try
        {
            string? json = RobustJsonExtractor.ExtractLastJsonObject(output);
            return json is null ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray? OutputClips(string? output) => Root(output)?["clips"] as JsonArray;

    private async Task<T?> TryLoadAsync<T>(StepExecutionContext context, VideoScratchSpace scratch, string storageKey, string fileName, CancellationToken ct)
    {
        string local = scratch.GetPath(fileName);
        await _workspace.DownloadStorageKeyToFileAsync(context.Execution.ProjectId, storageKey, local, ct);
        string json = await File.ReadAllTextAsync(local, ct);
        return JsonSerializer.Deserialize<T>(json, ArtifactJsonOptions);
    }

    /// <summary>The compile's own take filter, so the view lists exactly the clips the compile would resolve (null selection admits every clip).</summary>
    private sealed class TakeSelectionFilterAdapter
    {
        private readonly VideoCompileStepExecutor.TakeSelectionFilter? _filter;

        public TakeSelectionFilterAdapter(IReadOnlyList<string>? selectedTakeIds, JsonArray clips)
        {
            if (selectedTakeIds is null)
                return;
            _filter = new VideoCompileStepExecutor.TakeSelectionFilter(selectedTakeIds);
            _filter.Prime(clips);
        }

        public bool Admits(JsonObject clip, string clipId) => _filter is null || _filter.Admits(clip, clipId, out _);
    }
}
