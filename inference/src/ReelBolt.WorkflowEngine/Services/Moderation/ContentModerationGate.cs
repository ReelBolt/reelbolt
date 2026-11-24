using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Moderation;

/// <summary>
/// The WorkflowEngine's <see cref="IContentModerationGate"/>: resolves the moderation provider, applies
/// the shared policy, records one $0 usage event and appends one content-free audit row through the
/// engine's own <see cref="WorkflowEngineDbContext"/> mapping.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage is best-effort; the audit row is part of a Block/Review decision.</b> The usage event is
/// logged and swallowed on failure. The audit write is retried three times. If it still fails:
/// <list type="bullet">
///   <item><description><b>Allow</b>: logged and swallowed.</description></item>
///   <item><description><b>Block or Review</b>: never silently downgraded. On the publish surface the
///   answer becomes <see cref="ModerationAction.Unavailable"/> (Available=false); off it a Block still
///   blocks and a Review proceeds with <c>ReviewQueued = false</c> because no row exists.</description></item>
/// </list>
/// The caller's own cancellation is always re-thrown — a stopped step must not look like an outage.
/// </para>
/// </remarks>
public sealed class ContentModerationGate : IContentModerationGate
{
    private readonly IInferenceProviderResolver _resolver;
    private readonly IContentModerationClientFactory _clientFactory;
    private readonly IOptions<ContentModerationOptions> _options;
    private readonly IUsageRecorder _usage;
    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbContextFactory;
    private readonly ILogger<ContentModerationGate> _logger;

    public ContentModerationGate(
        IInferenceProviderResolver resolver,
        IContentModerationClientFactory clientFactory,
        IOptions<ContentModerationOptions> options,
        IUsageRecorder usage,
        IDbContextFactory<WorkflowEngineDbContext> dbContextFactory,
        ILogger<ContentModerationGate> logger)
    {
        _resolver = resolver;
        _clientFactory = clientFactory;
        _options = options;
        _usage = usage;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    private ContentModerationOptions Options => _options.Value;

    public async Task<ModerationDecision> ModerateAsync(
        ModerationInput input,
        ModerationAttribution attribution,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(attribution);

        ContentModerationOptions options = Options;
        string hash = ModerationContentHash.Compute(input);

        if (!options.Enabled)
        {
            return new ModerationDecision(
                ModerationAction.Allow, Available: false, ReviewQueued: false,
                ReasonCode: null, UserMessage: string.Empty, hash);
        }

        ResolvedContentModerationProvider? provider =
            await _resolver.ResolveContentModerationAsync(null, ct);
        if (provider is null)
        {
            _logger.LogWarning(
                "No content moderation provider is configured; {Surface} fails open",
                attribution.Surface);
            return new ModerationDecision(
                ModerationAction.Allow, Available: false, ReviewQueued: false,
                ReasonCode: null, UserMessage: string.Empty, hash);
        }

        IContentModerationClient client = _clientFactory.Create(provider, options);

        ModerationClassification classification;
        try
        {
            classification = await client.ClassifyAsync(
                new ModerationClassifyRequest(input.Text, input.Images, options.Model), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's step was cancelled: not a moderation outage, re-throw it as-is.
            throw;
        }
        catch (ContentModerationUnavailableException ex)
        {
            _logger.LogWarning(ex,
                "Content moderation is unavailable for {Surface}; applying the surface's failure stance",
                attribution.Surface);
            return new ModerationDecision(
                ModerationAction.Allow, Available: false, ReviewQueued: false,
                ReasonCode: null, UserMessage: string.Empty, hash);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Content moderation did not finish for {Surface}; applying the surface's failure stance",
                attribution.Surface);
            return new ModerationDecision(
                ModerationAction.Allow, Available: false, ReviewQueued: false,
                ReasonCode: null, UserMessage: string.Empty, hash);
        }

        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(classification, options, input.Kind);

        // A scoreless/flagless answer is not a clean answer: the policy calls it Unavailable so the
        // surface's failure stance applies. It still gets an audit row (never recorded as Allow).
        bool unavailable = verdict.Action == ModerationAction.Unavailable;

        await RecordUsageAsync(provider, attribution, ct);
        bool auditWritten = await WriteAuditRowAsync(
            provider, attribution, input, classification, verdict, hash, options, ct);

        // An audit row is part of a Block/Review decision. Only Allow is best-effort.
        if (!auditWritten && verdict.Action is ModerationAction.Block or ModerationAction.Review)
        {
            _logger.LogError(
                "The moderation audit row for a {Action} decision on {Surface} could not be written after {Attempts} attempts",
                verdict.Action, attribution.Surface, AuditWriteAttempts);

            if (attribution.Surface == ModerationSurface.Publish)
            {
                return new ModerationDecision(
                    ModerationAction.Unavailable, Available: false, ReviewQueued: false,
                    ReasonCode: ModerationReasonCodes.AuditUnavailable, UserMessage: string.Empty, hash);
            }

            return new ModerationDecision(
                verdict.Action, Available: !unavailable, ReviewQueued: false,
                verdict.ReasonCode, verdict.UserMessage, hash);
        }

        if (!auditWritten)
        {
            _logger.LogWarning(
                "The moderation audit row for an Allow decision on {Surface} was not written; the decision stands",
                attribution.Surface);
        }

        return new ModerationDecision(
            verdict.Action,
            Available: !unavailable,
            ReviewQueued: auditWritten && verdict.Action == ModerationAction.Review,
            verdict.ReasonCode,
            verdict.UserMessage,
            hash);
    }

    public async Task<bool> ScreenGenerationPromptAsync(
        string text,
        IReadOnlyList<ModerationImageInput> images,
        ModerationAttribution attribution,
        CancellationToken ct)
    {
        if (!Options.Enabled) return true;

        IReadOnlyList<ModerationImageInput> sampled = images ?? [];
        ModerationContentKind kind = sampled.Count > 0
            ? ModerationContentKind.Image
            : ModerationContentKind.Text;
        ModerationDecision decision = await ModerateAsync(
            new ModerationInput(text, sampled, kind), attribution, ct);
        if (!decision.Available) LogFailOpen(attribution.Surface);
        return decision.Action != ModerationAction.Block;
    }

    public async Task<bool> ScreenGeneratedTextAsync(
        string text,
        string site,
        ModerationAttribution attribution,
        CancellationToken ct)
    {
        if (!Options.Enabled) return true;

        ModerationDecision decision = await ModerateAsync(
            new ModerationInput(text, [], ModerationContentKind.Text), attribution, ct);
        if (!decision.Available) LogFailOpen(attribution.Surface);
        return decision.Action != ModerationAction.Block;
    }

    public async Task<bool> ScreenPublishAsync(
        ModerationInput input,
        ModerationAttribution attribution,
        CancellationToken ct)
    {
        if (!Options.Enabled) return true;

        ModerationDecision decision = await ModerateAsync(input, attribution, ct);
        if (!decision.Available)
        {
            _logger.LogWarning(
                "Content moderation is unavailable for publish; refusing egress because that surface fails closed");
            return false;
        }

        return decision.Action is ModerationAction.Allow or ModerationAction.Review;
    }

    private async Task RecordUsageAsync(
        ResolvedContentModerationProvider provider,
        ModerationAttribution attribution,
        CancellationToken ct)
    {
        try
        {
            // The run has usually opened its own scope; opening a second one would mis-attribute the
            // call, so this only supplies a scope when the caller has none.
            using IDisposable? scope = UsageScope.Current is null
                ? UsageScope.Begin(
                    attribution.OrganizationId,
                    attribution.ProjectId,
                    attribution.ExecutionId,
                    attribution.StepResultId,
                    attribution.UserId,
                    UsageSource.Moderation)
                : null;

            await _usage.RecordAsync(new UsageRecordRequest(
                UsageKind.ModerationCalls,
                1m,
                provider.ProviderId,
                provider.Kind,
                provider.ModelName,
                provider.IsPlatformManaged ? UsageBillingMode.Platform : UsageBillingMode.Byo,
                Capability: InferenceProviderCapability.ContentModeration,
                AccountId: provider.AccountId), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The moderation decision already happened; a ledger failure must not change it.
            _logger.LogError(ex,
                "Failed to record moderation usage for {Surface}; the decision stands", attribution.Surface);
        }
    }

    private async Task<bool> WriteAuditRowAsync(
        ResolvedContentModerationProvider provider,
        ModerationAttribution attribution,
        ModerationInput input,
        ModerationClassification classification,
        ModerationPolicyVerdict verdict,
        string hash,
        ContentModerationOptions options,
        CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        ContentModerationDecision row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = attribution.OrganizationId,
            ProjectId = attribution.ProjectId,
            UserId = attribution.UserId,
            ExecutionId = attribution.ExecutionId,
            StepResultId = attribution.StepResultId,
            Surface = attribution.Surface.ToString(),
            Action = verdict.Action.ToString(),
            ContentKind = attribution.Kind.ToString(),
            ContentSha256 = hash,
            ContentLength = ContentLength(input),
            ProviderId = provider.ProviderId,
            AccountId = provider.AccountId,
            ProviderKind = provider.Kind.ToString(),
            Model = provider.ModelName,
            ModelSnapshot = options.Model,
            TopCategory = verdict.TopCategory,
            TopScore = verdict.TopScore,
            FlaggedCategoriesJson = JsonSerializer.Serialize(classification.FlaggedCategories),
            ScoresJson = JsonSerializer.Serialize(classification.Scores),
            InputTypesJson = JsonSerializer.Serialize(classification.InputTypes),
            ReasonCode = verdict.ReasonCode,
            ReasonMessage = verdict.UserMessage,
            ReviewState = (verdict.Action == ModerationAction.Review
                ? ModerationReviewState.Pending
                : ModerationReviewState.NotRequired).ToString(),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.AuditRetentionDays)
        };

        // A fresh context per attempt: a failed save leaves the previous one in an unusable state,
        // and the engine's scoped context may hold the run/file rows it is editing.
        for (int attempt = 1; attempt <= AuditWriteAttempts; attempt++)
        {
            try
            {
                await using WorkflowEngineDbContext db =
                    await _dbContextFactory.CreateDbContextAsync(ct);
                db.ContentModerationDecisions.Add(row);
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < AuditWriteAttempts)
            {
                _logger.LogWarning(ex,
                    "Audit-row write attempt {Attempt}/{Attempts} failed; retrying",
                    attempt, AuditWriteAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Audit-row write failed after {Attempts} attempts for {Surface}",
                    AuditWriteAttempts, attribution.Surface);
                return false;
            }
        }

        return false;
    }

    private const int AuditWriteAttempts = 3;

    private void LogFailOpen(ModerationSurface surface) =>
        _logger.LogWarning(
            "Content moderation was unavailable for {Surface}; allowing because that surface fails open",
            surface);

    /// <summary>
    /// Text is measured in UTF-8 bytes; media is the sum of the sampled image/frame bytes that were
    /// actually classified (never the original file size), so the audit row describes what was sent.
    /// </summary>
    private static long ContentLength(ModerationInput input)
    {
        if (input.Images is { Count: > 0 } images)
        {
            long total = 0;
            foreach (ModerationImageInput image in images)
                total += image.Bytes?.LongLength ?? 0;
            return total;
        }

        return Encoding.UTF8.GetByteCount(input.Text ?? string.Empty);
    }
}
