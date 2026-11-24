using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing.Proposals;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>R11.2 (<c>Editing:Proposals:MaxTranscriptChars</c>): bounds of the grounded part of a proposal's prompt.</summary>
public sealed class EditProposalGroundingOptions
{
    public const string SectionName = "Editing:Proposals";

    /// <summary>The most transcript characters one proposal request sends; the lowest-value lines are dropped first.</summary>
    public int MaxTranscriptChars { get; set; } = ProposalFactText.DefaultMaxTranscriptChars;
}

/// <summary>
/// R11.2: reads what stored analysis says about the project files a proposal's clips use: the per-file analyses
/// (<c>media_analysis_results</c>: silence, shots, motion, transcript, beats, events) and the project's newest <c>VideoAnalyze</c> artifact
/// (shot colour/brightness descriptors, captions, segments). Read-only and cheap: it never starts an analysis (that has its
/// own price, R09.1) and a missing or unreadable result is simply no facts, never a failure.
/// </summary>
public static class ProposalFactsLoader
{
    private sealed class FileBucket
    {
        public List<FactLine> Lines { get; } = [];
        public List<FactShot> Shots { get; } = [];
        public List<FactGap> Gaps { get; } = [];
        public List<FactWord> Words { get; } = [];
        public List<FactMoment> Moments { get; } = [];
    }

    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static async Task<Dictionary<Guid, SourceFacts>> LoadAsync(
        InferenceApiDbContext db, IFileStorageService storage, ILogger logger, Guid projectId, IReadOnlyCollection<Guid> fileIds, CancellationToken ct) =>
        (await LoadWithStatusAsync(db, storage, logger, projectId, fileIds, [], ct)).Facts;

    /// <summary>The facts per media source (project file id or step result id) and whether the stored analysis could not be read.</summary>
    public sealed record LoadResult(Dictionary<Guid, SourceFacts> Facts, bool ReadFailed);

    /// <summary>
    /// As <see cref="LoadAsync"/>, plus the workflow render outputs (<paramref name="stepResultIds"/>) whose ready analysis rows are keyed
    /// <c>step_result:{id}</c>. A render output the hold gate does not serve contributes no facts. A read failure still proposes without
    /// facts, but is reported so the answer can say so.
    /// </summary>
    public static async Task<LoadResult> LoadWithStatusAsync(
        InferenceApiDbContext db, IFileStorageService storage, ILogger logger, Guid projectId,
        IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> stepResultIds, CancellationToken ct)
    {
        Dictionary<Guid, SourceFacts> result = [];
        if (fileIds.Count == 0 && stepResultIds.Count == 0) return new LoadResult(result, false);
        bool failed = false;

        Dictionary<Guid, FileBucket> perFile = [];
        foreach (Guid id in fileIds) perFile[id] = new FileBucket();

        List<Guid> servedSteps = [];
        try
        {
            foreach (Guid stepId in stepResultIds)
            {
                if (!await Outputs.OutputGate.IsResultHeldAsync(db, stepId, ct)) servedSteps.Add(stepId);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failed = true;
            servedSteps.Clear();
            logger.LogWarning(ex, "Could not check the output hold of project {ProjectId}; proposing without render-output analysis", projectId);
        }

        foreach (Guid id in servedSteps) perFile.TryAdd(id, new FileBucket());

        Dictionary<Guid, BeatFacts> beats = [];
        try
        {
            await ReadMediaAnalysisAsync(db, projectId, fileIds, servedSteps, perFile, beats, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failed = true;
            logger.LogWarning(ex, "Could not read the stored analysis of project {ProjectId}; proposing without it", projectId);
        }

        try
        {
            await ReadArtifactAsync(db, storage, projectId, fileIds, perFile, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failed = true;
            logger.LogWarning(ex, "Could not read the video-analysis artifact of project {ProjectId}; proposing without it", projectId);
        }

        Dictionary<Guid, string> inputProfiles = await ReadInputProfilesAsync(db, logger, projectId, fileIds, ct);
        foreach ((Guid id, FileBucket bucket) in perFile)
        {
            SourceFacts facts = new(bucket.Lines, bucket.Shots, bucket.Gaps, inputProfiles.GetValueOrDefault(id), beats.GetValueOrDefault(id)) { Words = bucket.Words, Moments = bucket.Moments };
            if (!facts.IsEmpty || facts.HasBeats || facts.HasMoments) result[id] = facts;
        }

        return new LoadResult(result, failed);
    }

    /// <summary>
    /// log-footage-color: per project file, the camera profile id when its stored probe's detector hint is High (a Medium hint
    /// or no stored probe says nothing). Read-only; any failure is no profile, never a failure of the proposal.
    /// </summary>
    internal static async Task<Dictionary<Guid, string>> ReadInputProfilesAsync(
        InferenceApiDbContext db, ILogger logger, Guid projectId, IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        Dictionary<Guid, string> result = [];
        try
        {
            var files = await db.ProjectFiles.AsNoTracking()
                .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id))
                .Select(f => new { f.Id, f.StorageKey }).ToListAsync(ct);
            Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(db, projectId, files.Select(f => f.StorageKey).ToList(), ct);
            foreach (var file in files)
            {
                if (probes.TryGetValue(file.StorageKey, out MediaSourceProbe? probe) && StoredMediaProbes.HighProfileOf(probe) is { } profile)
                    result[file.Id] = profile;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the stored probes of project {ProjectId}; proposing without camera profiles", projectId);
        }

        return result;
    }

    private static async Task ReadMediaAnalysisAsync(
        InferenceApiDbContext db, Guid projectId, IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> stepResultIds,
        Dictionary<Guid, FileBucket> perFile, Dictionary<Guid, BeatFacts> beats, CancellationToken ct)
    {
        string[] refs = fileIds.Select(f => $"file:{f}").Concat(stepResultIds.Select(s => $"step_result:{s}")).ToArray();
        List<MediaAnalysisRequest> rows = (await db.MediaAnalysisRequests.AsNoTracking()
            .Where(r => r.ProjectId == projectId && refs.Contains(r.MediaRef) && r.State == MediaAnalysisStates.Ready && r.ResultId != null)
            .ToListAsync(ct))
            .GroupBy(r => (r.MediaRef, r.Kind))
            .Select(g => g.OrderByDescending(r => r.UpdatedAt).First())
            .ToList();
        if (rows.Count == 0) return;

        Guid[] resultIds = rows.Select(r => r.ResultId!.Value).ToArray();
        Dictionary<Guid, (Guid Org, string Json)> results = (await db.MediaAnalysisResults.AsNoTracking()
            .Where(r => resultIds.Contains(r.Id)).Select(r => new { r.Id, r.OrganizationId, r.ResultJson }).ToListAsync(ct))
            .ToDictionary(r => r.Id, r => (r.OrganizationId, r.ResultJson));

        Dictionary<Guid, List<AnalysisMotionWindow>> motion = [];
        Dictionary<Guid, List<AnalysisRange>> shotRanges = [];
        foreach (MediaAnalysisRequest row in rows)
        {
            if (!Guid.TryParse(row.MediaRef.AsSpan(row.MediaRef.StartsWith("step_result:", StringComparison.Ordinal) ? 12 : 5), out Guid fileId) || !perFile.TryGetValue(fileId, out var bucket)) continue;
            if (!results.TryGetValue(row.ResultId!.Value, out var stored) || stored.Org != row.OrganizationId) continue;
            try
            {
                switch (row.Kind)
                {
                    case MediaAnalysisKinds.Transcript:
                        AnalysisTranscriptResult? t = JsonSerializer.Deserialize<AnalysisTranscriptResult>(stored.Json, MediaAnalysisJson.Options);
                        bucket.Lines.AddRange(ProposalFactText.LinesFromWords((t?.Words ?? []).Select(w => (w.Text, w.StartSec, w.EndSec))));
                        bucket.Words.AddRange(WordFacts((t?.Words ?? []).Select(w => (w.Text, w.StartSec, w.EndSec))));
                        break;
                    case MediaAnalysisKinds.Silence:
                        AnalysisSilenceResult? s = JsonSerializer.Deserialize<AnalysisSilenceResult>(stored.Json, MediaAnalysisJson.Options);
                        bucket.Gaps.AddRange((s?.Ranges ?? []).Select(r => new FactGap(r.StartSec, r.EndSec)));
                        break;
                    case MediaAnalysisKinds.Shots:
                        shotRanges[fileId] = [.. JsonSerializer.Deserialize<AnalysisShotsResult>(stored.Json, MediaAnalysisJson.Options)?.Shots ?? []];
                        break;
                    case MediaAnalysisKinds.Beats:
                        if (ToBeatFacts(JsonSerializer.Deserialize<AnalysisBeatsResult>(stored.Json, MediaAnalysisJson.Options)) is { } grid) beats[fileId] = grid;
                        break;
                    case MediaAnalysisKinds.Events:
                        // Only a result of this detector version is read; an older row is no moments until the editor runs the
                        // analysis again. A degraded row of this version is read: its picture moments are real (it may
                        // lack beats or speech edges, or its frame rate could not be checked, so on a variable-rate file a
                        // moment may sit a frame or two off), and a file that degrades every time must not lose them for good.
                        AnalysisEventsResult? ev = JsonSerializer.Deserialize<AnalysisEventsResult>(stored.Json, MediaAnalysisJson.Options);
                        if (ev is not null && ev.Version == MediaAnalysisJson.EventsVersion) bucket.Moments.AddRange(ProposalMoments.FromStored(ev));
                        break;
                    case MediaAnalysisKinds.Motion:
                        motion[fileId] = [.. JsonSerializer.Deserialize<AnalysisMotionResult>(stored.Json, MediaAnalysisJson.Options)?.Windows ?? []];
                        break;
                }
            }
            catch (JsonException)
            {
                // an unreadable result is no facts for that kind
            }
        }

        foreach ((Guid fileId, List<AnalysisRange> ranges) in shotRanges)
        {
            motion.TryGetValue(fileId, out List<AnalysisMotionWindow>? windows);
            foreach (AnalysisRange r in ranges)
            {
                string? word = windows?.Where(w => w.EndSec > r.StartSec && w.StartSec < r.EndSec)
                    .GroupBy(w => w.MotionClass).OrderByDescending(g => g.Sum(w => w.EndSec - w.StartSec)).Select(g => MotionWord(g.Key)).FirstOrDefault();
                perFile[fileId].Shots.Add(new FactShot(r.StartSec, r.EndSec, word, null, null, null));
            }
        }
    }

    /// <summary>The stored beat grid as facts; null when it has no usable tempo or beats. Times are finite, sorted and capped.</summary>
    internal static BeatFacts? ToBeatFacts(AnalysisBeatsResult? b)
    {
        if (b is null || !(b.Bpm > 0) || !(b.BeatSec > 0) || double.IsInfinity(b.BeatSec)) return null;
        double[] times = [.. (b.BeatTimesSec ?? []).Where(double.IsFinite).OrderBy(t => t).Take(MaxBeats)];
        if (times.Length == 0) return null;
        int perBar = b.BeatsPerBar is >= 1 and <= 16 ? b.BeatsPerBar : 4;
        int firstDown = b.FirstDownbeatIndex >= 0 ? b.FirstDownbeatIndex : 0;
        return new BeatFacts(times, b.BeatSec, perBar, firstDown, double.IsFinite(b.Confidence) ? b.Confidence : 0, b.Steady, BeatFacts.TempoWordOf(b.Bpm), b.DropSec is { } d && double.IsFinite(d));
    }

    private const int MaxBeats = 20000;

    private static async Task ReadArtifactAsync(
        InferenceApiDbContext db, IFileStorageService storage, Guid projectId, IReadOnlyCollection<Guid> fileIds,
        Dictionary<Guid, FileBucket> perFile, CancellationToken ct)
    {
        string prefix = $"projects/{projectId}/agentFiles/video-analysis/";
        string? key = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecution.ProjectId == projectId && r.WorkflowStep.StepType == StepType.VideoAnalyze
                        && r.ArtifactStorageKey != null && r.ArtifactStorageKey.StartsWith(prefix))
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .Select(r => r.ArtifactStorageKey).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(key)) return;
        // An analysis whose output is not served adds no facts.
        if (await Outputs.OutputGate.IsKeyHeldAsync(db, projectId, key, ct)) return;

        Dictionary<string, Guid> byStorageKey = (await db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id)).Select(f => new { f.Id, f.StorageKey }).ToListAsync(ct))
            .GroupBy(f => f.StorageKey).ToDictionary(g => g.Key, g => g.First().Id);

        await using Stream stream = await storage.DownloadAsync(projectId, key, ct);
        VideoAnalysisArtifact? artifact = await JsonSerializer.DeserializeAsync<VideoAnalysisArtifact>(stream, ArtifactJson, ct);
        foreach (VideoAnalysisSourceInfo source in artifact?.Sources ?? [])
        {
            Guid fileId = source.ProjectFileId ?? (byStorageKey.TryGetValue(source.StorageKey ?? string.Empty, out Guid g) ? g : Guid.Empty);
            if (fileId == Guid.Empty || !perFile.TryGetValue(fileId, out var bucket)) continue;
            double offset = source.TrimInSec ?? 0;

            List<FactShot> shots = [];
            foreach (VideoAnalysisShot shot in artifact!.Shots.Where(s => s.SourceIndex == source.SourceIndex))
            {
                shots.Add(ShotFact(shot, offset));
            }

            if (shots.Count > 0) { bucket.Shots.Clear(); bucket.Shots.AddRange(shots); }
            if (bucket.Lines.Count == 0)
            {
                bucket.Lines.AddRange(artifact.Segments.Where(s => s.SourceIndex == source.SourceIndex && !string.IsNullOrWhiteSpace(s.Text))
                    .Select(s => new FactLine(s.StartSec + offset, s.EndSec + offset, s.Text)));
            }

            if (bucket.Words.Count == 0)
            {
                bucket.Words.AddRange(WordFacts(artifact.Words.Where(w => w.SourceIndex == source.SourceIndex).Select(w => (w.Text, w.StartSec + offset, w.EndSec + offset))));
            }

            if (bucket.Gaps.Count == 0)
            {
                bucket.Gaps.AddRange(artifact.SilenceSpans.Where(s => s.SourceIndex == source.SourceIndex).Select(s => new FactGap(s.StartSec + offset, s.EndSec + offset)));
            }
        }
    }

    private static IEnumerable<FactWord> WordFacts(IEnumerable<(string Text, double StartSec, double EndSec)> words) =>
        words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.EndSec >= w.StartSec && double.IsFinite(w.StartSec) && double.IsFinite(w.EndSec))
            .OrderBy(w => w.StartSec).Select(w => new FactWord(w.StartSec, w.EndSec, w.Text.Trim()));

    private static FactShot ShotFact(VideoAnalysisShot shot, double offset)
    {
        VideoAnalysisShotVisual? v = shot.Visual;
        string? motion = v is null ? null : MotionWord(v.MotionClass);
        string? brightness = null;
        List<string> colour = [];
        if (v is not null)
        {
            bool graded = v.ColorTemperatureClass is not null || v.ToneClass is not null || v.SaturationClass is not null;
            if (v.BrightnessMean > 0 || graded) brightness = ProposalFactText.BrightnessWord(v.BrightnessMean);
            if (v.ColorTemperatureClass is { Length: > 0 } temp) colour.Add(temp.ToLowerInvariant() + " tones");
            if (v.ToneClass is { Length: > 0 } tone) colour.Add(ToneWord(tone));
            if (v.SaturationClass is { Length: > 0 } sat) colour.Add(sat.ToLowerInvariant() + " colour");
        }

        return new FactShot(shot.StartSec + offset, shot.EndSec + offset, motion, brightness, colour.Count > 0 ? string.Join(", ", colour) : null, shot.Caption?.Summary);
    }

    private static string? MotionWord(string? cls) => cls?.ToLowerInvariant() switch
    {
        "still" => "still",
        "low" or "calm" => "calm",
        "high" or "busy" => "busy",
        "medium" or "moderate" => "moderate movement",
        null or "" => null,
        var other => other.Length <= 20 && other.All(char.IsLetter) ? other : null
    };

    private static string ToneWord(string tone) => tone switch
    {
        "Blown" => "blown highlights",
        "Crushed" => "crushed shadows",
        "Flat" => "flat contrast",
        "Contrasty" => "strong contrast",
        _ => "normal contrast"
    };
}
