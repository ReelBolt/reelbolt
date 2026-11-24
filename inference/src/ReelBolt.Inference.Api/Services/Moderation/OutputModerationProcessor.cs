using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// Judges one stored output once and records the verdict. Runs only in the background lane, never in
/// a request. An output that already has a decided verdict for its current bytes is not read and not
/// sent anywhere: that is the "judged once" guarantee.
/// </summary>
public sealed class OutputModerationProcessor
{
    private readonly InferenceApiDbContext _db;
    private readonly PublishModeration _publish;
    private readonly IOptions<ContentModerationOptions> _options;
    private readonly ILogger<OutputModerationProcessor> _logger;
    private readonly ITenantContext? _tenant;

    public OutputModerationProcessor(
        InferenceApiDbContext db,
        PublishModeration publish,
        IOptions<ContentModerationOptions> options,
        ILogger<OutputModerationProcessor> logger,
        ITenantContext? tenant = null)
    {
        _db = db;
        _publish = publish;
        _options = options;
        _logger = logger;
        _tenant = tenant;
    }

    /// <summary>
    /// Judges the output. Returns true when the object changed while it was being judged: nothing was
    /// stored and the caller should offer it again.
    /// </summary>
    public async Task<bool> ProcessAsync(OutputModerationTask task, CancellationToken ct) =>
        await ProcessForOutcomeAsync(task, ct) == OutputModerationRunOutcome.Reoffer;

    /// <summary>
    /// Judges the output and says what happened, so the lane knows whether the output is now governed by its stored row
    /// (<see cref="OutputModerationRunOutcome.Stored"/>), waits on the CSAM control (<see cref="OutputModerationRunOutcome.Held"/>),
    /// must be offered again at once (<see cref="OutputModerationRunOutcome.Reoffer"/>), or could not be judged at all.
    /// </summary>
    internal async Task<OutputModerationRunOutcome> ProcessForOutcomeAsync(OutputModerationTask task, CancellationToken ct)
    {
        ContentModerationOptions options = _options.Value;
        if (!options.Enabled) return OutputModerationRunOutcome.Nothing;

        Guid projectId = task.ProjectId;
        string key = task.StorageKey;
        if (!key.StartsWith($"projects/{projectId}/", StringComparison.Ordinal)) return OutputModerationRunOutcome.Nothing;

        // Before any byte is read: a held project is left pending, with no row and no audit row.
        if (await IsProjectHeldAsync(projectId, ct))
        {
            _logger.LogInformation("An output was not moderated: its project is held by the CSAM control");
            return OutputModerationRunOutcome.Held;
        }

        Guid organizationId;
        using (_tenant.System())
        {
            organizationId = await _db.Projects
                .Where(p => p.Id == projectId)
                .Select(p => p.OrganizationId)
                .FirstOrDefaultAsync(ct);
        }

        if (organizationId == Guid.Empty) return OutputModerationRunOutcome.Nothing;

        string? version = await _publish.ReadObjectVersionAsync(projectId, key, ct);
        if (version is null) return OutputModerationRunOutcome.Nothing;

        OutputModerationVerdict? row = await _db.OutputModerationVerdicts
            .FirstOrDefaultAsync(v => v.ProjectId == projectId && v.StorageKey == key, ct);
        DateTime now = DateTime.UtcNow;
        bool sameVersion = row is not null && string.Equals(row.SourceVersion, version, StringComparison.Ordinal);
        // The stored row already governs this output (a decided verdict, or an outage whose retry time has not come).
        if (sameVersion && !IsRetryDue(row!, now)) return OutputModerationRunOutcome.Stored;

        List<StepRef> steps;
        using (_tenant.System())
        {
            steps = await _db.WorkflowStepResults
                .AsNoTracking()
                .Where(r => r.OutputStorageKey == key && r.WorkflowExecution.ProjectId == projectId)
                .Select(r => new StepRef(r.Id, r.WorkflowExecutionId, r.CompletedAt ?? r.ExecutedAt))
                .ToListAsync(ct);
        }

        StepRef? first = steps.Count == 0 ? null : steps.OrderBy(s => s.ProducedAt).First();

        Stored stored;
        ContentModerationDecision? audit = !sameVersion
            ? await FindPriorAuditAsync(projectId, steps, version, ct)
            : null;
        if (audit is not null)
        {
            // Already judged and audited with these bytes (before this table existed): record it, send nothing.
            stored = new Stored(
                audit.Action, audit.ReasonCode, audit.Action == nameof(ModerationAction.Block) ? audit.ReasonMessage : null,
                string.IsNullOrEmpty(audit.ModelSnapshot) ? options.Model : audit.ModelSnapshot);
        }
        else
        {
            (string mime, bool disagrees) = await ResolveMimeTypeAsync(projectId, key, ct);
            PublishModeration.Evaluated result;
            using (_tenant.Organization(organizationId))
            {
                // A row type that contradicts the key's own type is never judged: it is unclassified and refuses.
                result = disagrees
                    ? new PublishModeration.Evaluated(
                        PublishModerationOutcome.Unavailable(), PublishModeration.CacheKind.Unavailable,
                        nameof(ModerationAction.Unavailable), ModerationReasonCodes.Unclassified)
                    : await _publish.JudgeStoredAsync(
                    projectId, key, mime,
                    new ModerationAttribution(
                        organizationId, projectId, UserId: null, first?.ExecutionId, first?.StepResultId,
                        ModerationSurface.Publish, ModerationContentKind.Text, version),
                    async token => !await IsProjectHeldAsync(projectId, token),
                    ct);
            }

            if (result.Held)
            {
                _logger.LogInformation("An output was not sent for moderation: its project is held by the CSAM control");
                return OutputModerationRunOutcome.Held;
            }

            stored = new Stored(
                result.State, result.ReasonCode,
                result.State == nameof(ModerationAction.Block) ? result.Outcome.Message : null,
                options.Model, result.NoRetry);
        }

        // The bytes may have been replaced while they were judged: a verdict for other bytes is worthless.
        string? after = await _publish.ReadObjectVersionAsync(projectId, key, ct);
        if (!string.Equals(after, version, StringComparison.Ordinal)) return OutputModerationRunOutcome.Reoffer;

        await UpsertAsync(row, sameVersion, organizationId, projectId, key, version, first, stored, ct);
        return OutputModerationRunOutcome.Stored;
    }

    /// <summary>The suspected-CSAM rule for a project (see <see cref="PublishModeration.IsProjectHeldAsync"/>): any doubt is "held".</summary>
    internal Task<bool> IsProjectHeldAsync(Guid projectId, CancellationToken ct) =>
        _publish.IsProjectHeldAsync(projectId, ct);

    internal static bool IsRetryDue(OutputModerationVerdict row, DateTime now)
    {
        if (row.State != nameof(ModerationAction.Unavailable)) return false;
        if (row.ReasonCode == OutputModerationReasonCodes.NothingToClassify) return false;
        return row.NextAttemptAt is DateTime next
            ? next <= now
            : row.DecidedAt <= now - OutputModerationBackoff.MaxGap;
    }

    /// <summary>
    /// The content type the pipeline is chosen by. The key's extension decides; the project file row's
    /// type (user-editable) is only a hint for a key whose extension is unknown, and never selects the
    /// text path. A row that disagrees with a known extension in kind yields <c>Disagrees</c>.
    /// </summary>
    private async Task<(string Mime, bool Disagrees)> ResolveMimeTypeAsync(Guid projectId, string key, CancellationToken ct)
    {
        string? fromRow;
        using (_tenant.System())
        {
            fromRow = await _db.ProjectFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(f => f.ProjectId == projectId && f.StorageKey == key)
                .Select(f => f.MimeType)
                .FirstOrDefaultAsync(ct);
        }

        string fromKey = OutputMimeTypes.FromKey(key);
        string rowKind = OutputMimeTypes.KindOf(fromRow);
        if (fromKey != OutputMimeTypes.Unknown)
        {
            string keyKind = OutputMimeTypes.KindOf(fromKey);
            return (fromKey, rowKind != "other" && rowKind != keyKind);
        }

        // Unknown extension (none, .bin, or one the engine does not know): a media hint may pick the media pipeline;
        // otherwise the file's own streams decide, never its name. What is not media is refused by the probe.
        return (rowKind is "video" or "audio" or "image" ? fromRow!.Trim() : FfmpegModerationMediaSampler.FollowProbeMimeType, false);
    }

    /// <summary>
    /// The audit rows of this output at this version, reduced to a Block, or null to judge afresh.
    /// Only a Block is reused, however old: a refusal is never undone. An Allow or Review row came from
    /// an older, partial sampling (a 40 s window, a first frame), so it is not good enough to be permanent;
    /// the output is judged once more, by the current pipeline, and that verdict is the one kept.
    /// </summary>
    private async Task<ContentModerationDecision?> FindPriorAuditAsync(
        Guid projectId, List<StepRef> steps, string version, CancellationToken ct)
    {
        if (steps.Count == 0) return null;

        List<Guid> ids = steps.Select(s => s.StepResultId).ToList();
        List<ContentModerationDecision> rows = await _db.ContentModerationDecisions
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId
                        && d.Surface == nameof(ModerationSurface.Publish)
                        && d.StepResultId != null && ids.Contains(d.StepResultId.Value)
                        && d.SourceVersion == version)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        return rows.FirstOrDefault(d => d.Action == nameof(ModerationAction.Block));
    }

    private async Task UpsertAsync(
        OutputModerationVerdict? row, bool sameVersion, Guid organizationId, Guid projectId, string key,
        string version, StepRef? first, Stored stored, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        bool outage = stored.State == nameof(ModerationAction.Unavailable);
        bool firstJudgement = !sameVersion && !outage;
        int previousAttempts = sameVersion ? row!.Attempts : 0;

        if (row is null)
        {
            row = new OutputModerationVerdict
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ProjectId = projectId,
                StorageKey = key,
                CreatedAt = now
            };
            _db.OutputModerationVerdicts.Add(row);
        }

        if (!sameVersion)
        {
            row.SourceVersion = version;
            row.StepResultId = first?.StepResultId;
            row.OutputProducedAt = first?.ProducedAt;
        }

        row.State = stored.State;
        row.ReasonCode = Truncate(stored.ReasonCode, 64);
        row.ReasonMessage = Truncate(stored.ReasonMessage, 300);
        row.ModelSnapshot = Truncate(stored.ModelSnapshot, 200) ?? string.Empty;
        row.DecidedAt = now;
        if (outage)
        {
            row.Attempts = previousAttempts + 1;
            row.NextAttemptAt = stored.ReasonCode == OutputModerationReasonCodes.NothingToClassify
                ? null
                : stored.NoRetry
                    ? OutputModerationBackoff.Never
                    : OutputModerationBackoff.NextAttemptAt(row.Attempts, now);
        }
        else
        {
            row.Attempts = 0;
            row.NextAttemptAt = null;
        }

        // Only this verdict is saved: an audit row the gate failed to save stays Added in this scope's
        // context, and must neither sink the verdict nor be written along with it.
        foreach (var entry in _db.ChangeTracker.Entries()
                     .Where(e => e.State == EntityState.Added && e.Entity is not OutputModerationVerdict)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Another process stored a verdict for this output first; its row stands.
            _db.ChangeTracker.Clear();
            _logger.LogInformation("An output verdict was stored concurrently; keeping the existing row");
            return;
        }

        _logger.LogInformation(
            "Output moderation verdict {State} stored (attempt {Attempts})", stored.State, row.Attempts);

        if (firstJudgement && row.OutputProducedAt is DateTime produced)
        {
            TimeSpan age = now - produced;
            if (age >= TimeSpan.Zero && age <= OutputModerationDiagnostics.MaxRecordedAge)
                OutputModerationDiagnostics.VerdictSeconds.Record(age.TotalSeconds);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private sealed record StepRef(Guid StepResultId, Guid ExecutionId, DateTime ProducedAt);

    private sealed record Stored(string State, string? ReasonCode, string? ReasonMessage, string ModelSnapshot, bool NoRetry = false);
}

/// <summary>What one run of the output-moderation lane did with an offer.</summary>
internal enum OutputModerationRunOutcome
{
    /// <summary>Nothing could be judged (moderation off, the object or its project is gone): no row was written.</summary>
    Nothing,

    /// <summary>A row for the current bytes exists (written now, or already there): it, not the offer, governs what happens next.</summary>
    Stored,

    /// <summary>The project is held by the CSAM control (a quarantine, the marker, or a scan still pending): no row, nothing sent.</summary>
    Held,

    /// <summary>The bytes changed while they were judged: nothing was stored and the output must be offered again.</summary>
    Reoffer
}
