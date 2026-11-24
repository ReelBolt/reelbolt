using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>One analysed source as the hold sees it: seconds, pixels (0 = unknown, priced at the 1080p reference) and whether it has sound.</summary>
public sealed record SourceFootage(double DurationSec, int Width, int Height, bool HasAudio, bool Trimmed = false);

/// <summary>
/// The files the run's brief chose, per role, for the code that prices a run on the Inference API (the engine reads
/// its own <c>RunBriefAmbient</c> and passes the same shape). Ambient so no estimator signature changes; null = no brief.
/// </summary>
public static class SourceFootageBrief
{
    private static readonly AsyncLocal<Func<RunBriefFileRole, IReadOnlyList<Guid>>?> Current_ = new();

    /// <summary>The brief's files by role on this async flow, or null.</summary>
    public static Func<RunBriefFileRole, IReadOnlyList<Guid>>? Current => Current_.Value;

    /// <summary>Makes <paramref name="brief"/>'s source files the ones the hold resolves until the result is disposed.</summary>
    public static IDisposable Enter(IReadOnlyList<ReelBolt.Shared.Briefs.RunBriefSourceFiles>? sourceFiles)
    {
        Func<RunBriefFileRole, IReadOnlyList<Guid>>? previous = Current_.Value;
        Current_.Value = sourceFiles is { Count: > 0 }
            ? role => sourceFiles.Where(f => f.Role == role).SelectMany(f => f.FileIds ?? []).Distinct().ToList()
            : null;
        return new Restore(previous);
    }

    private sealed class Restore(Func<RunBriefFileRole, IReadOnlyList<Guid>>? previous) : IDisposable
    {
        public void Dispose() => Current_.Value = previous;
    }
}

/// <summary>
/// Definition-to-project hints for a run that is priced BEFORE its workflow row exists (the object-analysis estimate and
/// 402 gate price a probe step of a hidden workflow that is only saved once every gate passes). Ambient, like
/// <see cref="SourceFootageBrief"/>, so no estimator signature changes; <see cref="SourceFootageCredits.ResolveAsync"/>
/// merges it with the stored definitions, so the probe resolves its footage exactly as the saved step will.
/// </summary>
public static class SourceFootageDefinitions
{
    private static readonly AsyncLocal<IReadOnlyDictionary<Guid, Guid>?> Current_ = new();

    /// <summary>Definition id to project id hints on this async flow, or null.</summary>
    public static IReadOnlyDictionary<Guid, Guid>? Current => Current_.Value;

    /// <summary>Hints that <paramref name="definitionId"/> belongs to <paramref name="projectId"/> until the result is disposed.</summary>
    public static IDisposable Enter(Guid definitionId, Guid projectId)
    {
        IReadOnlyDictionary<Guid, Guid>? previous = Current_.Value;
        Current_.Value = new Dictionary<Guid, Guid>(previous ?? new Dictionary<Guid, Guid>()) { [definitionId] = projectId };
        return new Restore(previous);
    }

    private sealed class Restore(IReadOnlyDictionary<Guid, Guid>? previous) : IDisposable
    {
        public void Dispose() => Current_.Value = previous;
    }
}

/// <summary>
/// The credit hold a <see cref="StepType.VideoAnalyze"/> step adds when its sources are KNOWN project files
/// (initiative large-source-footage). The cold profile holds a fixed 138.7 s of analyse compute at the 1080p tier,
/// while the settle meters the probed duration at the source's own tier (<c>UsageKind.AnalyzeMediaSeconds</c>, UHD =
/// 4x the 1080p price) and, when the step transcribes, the audio seconds (<c>UsageKind.AsrAudioSeconds</c>). The
/// profile prices no ASR at all. Both are quantities of the same pricer that prices the profile, so no price is
/// restated here.
/// <para>
/// An ADDITION, never a replacement: <c>max(0, analyse(real) - analyse(138.7 s at 1080p)) + asr(real)</c> is added to the
/// step's hold after the history/default ladder, exactly like the segmentation and web-search add-ons, so a hold
/// can only grow. Unknown sources (not a project file, no stored probe and no size, a resolver failure) add 0 and the
/// step holds today's figure. The Inference API (402 preflight, shown estimate, approval card) and the engine
/// (reservations) both read it through <see cref="RunCreditHolds"/>, so they cannot disagree.
/// </para>
/// </summary>
public static class SourceFootageCredits
{
    /// <summary>The seconds the cold VideoAnalyze profile prices (<see cref="StepUsageProfiles"/>), already inside every hold.</summary>
    public const decimal ProfileAnalyzeSeconds = 138.7m;

    /// <summary>
    /// Bytes per second assumed for a source known by SIZE only (about 16 Mbit/s: ordinary 1080p phone footage). A
    /// higher real bitrate means fewer real seconds, so this errs toward a larger hold. Tier is unknown, so priced at 1080p.
    /// </summary>
    public const long AssumedBytesPerSecond = 2_000_000;

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The 1080p-equivalent price factor of the tier the meter bills a source at (the seeded tier prices are exactly this far apart).</summary>
    public static decimal TierFactor(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return 1m;
        }

        return ComputeMetering.TierFor(width, height) switch
        {
            ComputeMetering.TierUhd => 4m,
            ComputeMetering.Tier720 => 720m * 720m / (1080m * 1080m),
            _ => 1m
        };
    }

    /// <summary>A source known by its stored probe. Null when the probe has no usable duration.</summary>
    public static SourceFootage? FromProbe(MediaSourceProbe probe) =>
        probe.DurationSec > 0 ? new SourceFootage(probe.DurationSec, probe.Width, probe.Height, probe.HasAudio) : null;

    /// <summary>A source known by size only: capped seconds at <see cref="AssumedBytesPerSecond"/>, 1080p, assumed to have sound.</summary>
    public static SourceFootage? FromSize(long sizeBytes) =>
        sizeBytes > 0
            ? new SourceFootage(Math.Min(Math.Ceiling((double)sizeBytes / AssumedBytesPerSecond), VideoSourceLimits.DefaultMaxSeconds), 0, 0, true)
            : null;

    /// <summary>The analyse and ASR millicredits a step buys for <paramref name="sources"/>, before the profile is netted off.</summary>
    public static long AddOnMillis(WorkflowStep step, IReadOnlyList<SourceFootage>? sources, IProfileQuantityPricer pricer)
    {
        if (step.StepType != StepType.VideoAnalyze || sources is not { Count: > 0 })
        {
            return 0;
        }

        try
        {
            VideoAnalyzeStepConfig? config = Parse(step.VideoAnalyzeConfigJson);
            bool transcribes = config is null || config.Transcription != VideoTranscriptionMode.Off;
            IReadOnlyList<VideoSourceRef> refs = config is null ? [] : config.Sources is { Count: > 0 } list ? list : [config.Source];

            decimal referenceSeconds = 0;
            decimal audioSeconds = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                SourceFootage source = sources[i];
                decimal seconds = (decimal)source.DurationSec;
                if (!source.Trimmed && i < refs.Count)
                {
                    decimal from = (decimal)(refs[i].InSec ?? 0);
                    decimal to = refs[i].OutSec is { } o ? Math.Min((decimal)o, seconds) : seconds;
                    seconds = Math.Max(0, to - Math.Max(0, from));
                }

                seconds = Math.Ceiling(seconds);
                referenceSeconds += seconds * TierFactor(source.Width, source.Height);
                if (source.HasAudio)
                {
                    audioSeconds += seconds;
                }
            }

            referenceSeconds = Math.Ceiling(referenceSeconds);
            long profileMillis = Quote(pricer, UsageKind.AnalyzeMediaSeconds, ProfilePriceTarget.Compute, ProfileAnalyzeSeconds);
            long analyzeMillis = Quote(pricer, UsageKind.AnalyzeMediaSeconds, ProfilePriceTarget.Compute, referenceSeconds);
            long asrMillis = transcribes && audioSeconds > 0
                ? Quote(pricer, UsageKind.AsrAudioSeconds, ProfilePriceTarget.Transcription, audioSeconds)
                : 0;
            return Math.Max(0, analyzeMillis - profileMillis) + asrMillis;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The refs the engine will analyse for <paramref name="refs"/> once the run's brief is applied (the same rules as
    /// <c>RunBriefOverrides.ApplyToAnalyzeSources</c> then <c>BriefFilesSourceResolver</c>): with brief video files the first
    /// open ProjectFile slot becomes those files (further open slots are dropped), a BriefFiles source becomes its role's
    /// files, each keeping the slot's in/out. A source that is still not a known project file is left out (unknown = adds 0) and the rest still count; null when none is known.
    /// </summary>
    internal static List<VideoSourceRef>? Expand(IReadOnlyList<VideoSourceRef> refs, Func<RunBriefFileRole, IReadOnlyList<Guid>>? briefFiles, out bool partial)
    {
        partial = false;
        bool openToVideo = briefFiles is not null && briefFiles(RunBriefFileRole.Video).Count > 0;
        List<VideoSourceRef> result = [];
        bool replacedOpen = false;
        foreach (VideoSourceRef r in refs)
        {
            if (r is null)
            {
                partial = true;
                continue;
            }

            bool open = r is { Kind: VideoSourceKind.ProjectFile, ProjectFileId: null };
            if (open && openToVideo)
            {
                if (replacedOpen)
                {
                    continue;
                }

                replacedOpen = true;
                AddFiles(result, briefFiles!(RunBriefFileRole.Video), r);
            }
            else if (r.Kind == VideoSourceKind.BriefFiles && r.Role is { } role && briefFiles is not null && briefFiles(role).Count > 0)
            {
                AddFiles(result, briefFiles(role), r);
            }
            else if (r is { Kind: VideoSourceKind.ProjectFile, ProjectFileId: not null })
            {
                result.Add(r);
            }
            else
            {
                // Not a known project file (a step's output, an open slot with no brief file): it adds 0, the rest still count.
                partial = true;
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static void AddFiles(List<VideoSourceRef> into, IReadOnlyList<Guid> ids, VideoSourceRef slot) =>
        into.AddRange(ids.Select(id => new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: id, InSec: slot.InSec, OutSec: slot.OutSec)));

    /// <summary>Applies the slot's in/out to footage that came from the brief (whose refs no longer line up with the config's), so the add-on does not re-trim it.</summary>
    private static SourceFootage Pretrim(SourceFootage footage, VideoSourceRef r, bool force)
    {
        if (!force)
        {
            return footage;
        }

        double from = Math.Max(0, r.InSec ?? 0);
        double to = r.OutSec is { } o ? Math.Min(o, footage.DurationSec) : footage.DurationSec;
        return footage with { DurationSec = Math.Max(0, to - from), Trimmed = true };
    }

    private static long Quote(IProfileQuantityPricer pricer, UsageKind kind, ProfilePriceTarget target, decimal quantity)
    {
        ProfileQuote quote = pricer.Price(new ProfileQuantity(kind, target, quantity), AgentType.Custom, null);
        return quote.OwnProvider ? 0 : Math.Max(0, quote.CreditsMillis);
    }

    private static VideoAnalyzeStepConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(json, ConfigJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The known footage of every VideoAnalyze step whose sources are project files, by step order: the stored probe
    /// of the file, else its size. A step with any unknown source is left out (it holds today's figure). Never
    /// throws except on cancellation: a resolver that fails leaves the steps at today's hold.
    /// A file counts only inside the step's own project and the given organization: a step config can name any
    /// id, so a file elsewhere is unknown, adds nothing and reveals nothing. No organization means no footage.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>>> ResolveAsync(
        IQueryable<ProjectFile> files,
        IQueryable<MediaDerivative> derivatives,
        IQueryable<WorkflowDefinition> definitions,
        IReadOnlyList<WorkflowStep> steps,
        Guid? organizationId,
        CancellationToken ct,
        Func<RunBriefFileRole, IReadOnlyList<Guid>>? briefFiles = null)
    {
        Dictionary<int, IReadOnlyList<SourceFootage>> result = [];
        if (organizationId is not { } org)
        {
            return result;
        }

        try
        {
            Dictionary<int, List<VideoSourceRef>> refsByStep = [];
            HashSet<int> partialSteps = [];
            foreach (WorkflowStep step in steps.Where(s => s.StepType == StepType.VideoAnalyze))
            {
                VideoAnalyzeStepConfig? config = Parse(step.VideoAnalyzeConfigJson);
                if (config is null)
                {
                    continue;
                }

                IReadOnlyList<VideoSourceRef> refs = config.Sources is { Count: > 0 } list ? list : [config.Source];
                if (Expand(refs, briefFiles, out bool partial) is { } expanded)
                {
                    refsByStep[step.StepOrder] = expanded;
                    if (partial)
                    {
                        partialSteps.Add(step.StepOrder);
                    }
                }
            }

            if (refsByStep.Count == 0)
            {
                return result;
            }

            List<Guid> definitionIds = [.. steps.Where(s => refsByStep.ContainsKey(s.StepOrder)).Select(s => s.WorkflowDefinitionId).Distinct()];
            Dictionary<Guid, Guid> projectByDefinition = await definitions.AsNoTracking()
                .Where(d => definitionIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.ProjectId, ct).ConfigureAwait(false);
            foreach ((Guid hintedDefinition, Guid hintedProject) in SourceFootageDefinitions.Current ?? new Dictionary<Guid, Guid>())
            {
                if (definitionIds.Contains(hintedDefinition)) projectByDefinition.TryAdd(hintedDefinition, hintedProject);
            }

            List<Guid> projectIds = [.. projectByDefinition.Values.Distinct()];

            List<Guid> ids = [.. refsByStep.Values.SelectMany(v => v).Select(r => r.ProjectFileId!.Value).Distinct()];
            var rows = await files.AsNoTracking()
                .Where(f => ids.Contains(f.Id) && projectIds.Contains(f.ProjectId) && f.Project.OrganizationId == org)
                .Select(f => new { f.Id, f.ProjectId, f.StorageKey, f.SizeBytes })
                .ToListAsync(ct).ConfigureAwait(false);
            List<string> keys = [.. rows.Select(r => r.StorageKey).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal)];
            var probeRows = await derivatives.AsNoTracking()
                .Where(d => d.SourceProbeJson != null && projectIds.Contains(d.ProjectId) && keys.Contains(d.SourceStorageKey))
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new { d.ProjectId, d.SourceStorageKey, d.SourceProbeJson })
                .ToListAsync(ct).ConfigureAwait(false);

            Dictionary<Guid, SourceFootage> byFile = [];
            foreach (var row in rows)
            {
                SourceFootage? footage = null;
                foreach (var p in probeRows.Where(p => p.ProjectId == row.ProjectId && p.SourceStorageKey == row.StorageKey))
                {
                    if (MediaSourceProbe.TryParse(p.SourceProbeJson) is { } probe && FromProbe(probe) is { } known)
                    {
                        footage = known;
                        break;
                    }
                }

                footage ??= FromSize(row.SizeBytes);
                if (footage is not null)
                {
                    byFile[row.Id] = footage;
                }
            }

            Dictionary<Guid, Guid> projectByFile = rows.ToDictionary(r => r.Id, r => r.ProjectId);
            foreach ((int order, List<VideoSourceRef> stepRefs) in refsByStep)
            {
                Guid definitionId = steps.First(s => s.StepOrder == order).WorkflowDefinitionId;
                if (!projectByDefinition.TryGetValue(definitionId, out Guid project))
                {
                    continue;
                }

                // A file elsewhere (another project) is unknown and adds 0; the step's other known files still count.
                List<VideoSourceRef> known = [.. stepRefs.Where(r => byFile.ContainsKey(r.ProjectFileId!.Value) && projectByFile[r.ProjectFileId!.Value] == project)];
                if (known.Count > 0)
                {
                    // Refs no longer line up with the config's by index when some are left out or the brief expanded them,
                    // so the trim is applied here and the add-on does not trim again.
                    bool force = briefFiles is not null || partialSteps.Contains(order) || known.Count != stepRefs.Count;
                    result[order] = [.. known.Select(r => Pretrim(byFile[r.ProjectFileId!.Value], r, force))];
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Dictionary<int, IReadOnlyList<SourceFootage>>();
        }

        return result;
    }
}
