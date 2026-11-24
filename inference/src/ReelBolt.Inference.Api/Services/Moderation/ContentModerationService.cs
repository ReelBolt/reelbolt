using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// The Inference API's <see cref="IContentModerationGate"/>: resolves the moderation provider, applies
/// the shared policy, records one usage event and appends one content-free audit row per call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage is best-effort; the audit row is part of the decision.</b> A usage/ledger failure is logged
/// and swallowed (the decision already happened). The audit write is retried three times. If it still
/// fails:
/// <list type="bullet">
///   <item><description><b>Allow</b>: logged and swallowed — the content is allowed either way.</description></item>
///   <item><description><b>Block or Review</b>: never silently downgraded. On the publish surface the
///   answer becomes <see cref="ModerationAction.Unavailable"/> (Available=false), so egress refuses;
///   off the publish surface a Block still blocks and a Review proceeds with
///   <c>ReviewQueued = false</c>, because no row exists to queue.</description></item>
/// </list>
/// This is the one place the "fail open on infrastructure" rule stops: an unrecorded Block/Review at
/// egress is a security gap, not an outage to paper over.
/// </para>
/// </remarks>
public sealed class ContentModerationService : IContentModerationGate
{
    private readonly IInferenceProviderResolver _resolver;
    private readonly IContentModerationClientFactory _clientFactory;
    private readonly IUsageRecorder _usage;
    private readonly InferenceApiDbContext _db;
    private readonly IOptions<ContentModerationOptions> _options;
    private readonly ILogger<ContentModerationService> _logger;

    public ContentModerationService(
        IInferenceProviderResolver resolver,
        IContentModerationClientFactory clientFactory,
        IUsageRecorder usage,
        InferenceApiDbContext db,
        IOptions<ContentModerationOptions> options,
        ILogger<ContentModerationService> logger)
    {
        _resolver = resolver;
        _clientFactory = clientFactory;
        _usage = usage;
        _db = db;
        _options = options;
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
            // The caller's upload/step was cancelled: not a moderation outage, re-throw it as-is.
            throw;
        }
        catch (ContentModerationUnavailableException ex)
        {
            _logger.LogWarning(ex,
                "Content moderation is unavailable for {Surface}; applying the surface's failure stance",
                attribution.Surface);
            // Record the outage so the publish gate's short negative cache can key it by object
            // identity; the decision itself still takes the surface's stance.
            await RecordUnavailableAuditRowAsync(input, attribution, provider, options, hash, ct);
            return new ModerationDecision(
                ModerationAction.Allow, Available: false, ReviewQueued: false,
                ReasonCode: null, UserMessage: string.Empty, hash);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Content moderation did not finish for {Surface}; applying the surface's failure stance",
                attribution.Surface);
            await RecordUnavailableAuditRowAsync(input, attribution, provider, options, hash, ct);
            return new ModerationDecision(
                ModerationAction.Allow, Available: false, ReviewQueued: false,
                ReasonCode: null, UserMessage: string.Empty, hash);
        }

        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(classification, options, input.Kind);

        // A scoreless/flagless answer is not a clean answer: the policy calls it Unavailable so the
        // surface's failure stance applies. It still gets an audit row (never recorded as Allow).
        bool unavailable = verdict.Action == ModerationAction.Unavailable;

        await RecordUsageAsync(provider, attribution, ct);

        DateTime now = DateTime.UtcNow;
        ContentModerationDecision row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = attribution.OrganizationId,
            ProjectId = attribution.ProjectId,
            UserId = attribution.UserId,
            ExecutionId = attribution.ExecutionId,
            StepResultId = attribution.StepResultId,
            SourceVersion = attribution.SourceVersion,
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
            // The policy already leaves both null/empty for Allow; Unavailable carries "unclassified".
            ReasonCode = verdict.ReasonCode,
            ReasonMessage = verdict.UserMessage,
            ReviewState = (verdict.Action == ModerationAction.Review
                ? ModerationReviewState.Pending
                : ModerationReviewState.NotRequired).ToString(),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.AuditRetentionDays)
        };

        bool auditWritten = await TryWriteAuditRowAsync(row, ct);

        // An audit row is part of the decision for Block and Review: a Block must not disappear,
        // and a Review must never be reported as queued when nothing was recorded. Only an Allow
        // decision is best-effort (see the class remarks).
        if (!auditWritten && verdict.Action is ModerationAction.Block or ModerationAction.Review)
        {
            _logger.LogError(
                "The moderation audit row for a {Action} decision on {Surface} could not be written after {Attempts} attempts",
                verdict.Action, attribution.Surface, AuditWriteAttempts);

            if (attribution.Surface == ModerationSurface.Publish)
            {
                // Egress is the only fail-closed surface: an unrecorded Block/Review is Unavailable,
                // so the caller refuses publication.
                return new ModerationDecision(
                    ModerationAction.Unavailable, Available: false, ReviewQueued: false,
                    ReasonCode: ModerationReasonCodes.AuditUnavailable, UserMessage: string.Empty, hash);
            }

            // Off the publish surface the action still holds: a Block blocks; a Review proceeds but
            // is NOT claimed as queued because no row exists.
            return new ModerationDecision(
                verdict.Action, Available: !unavailable, ReviewQueued: false,
                verdict.ReasonCode, verdict.UserMessage, hash);
        }

        if (!auditWritten)
        {
            // Allow only: the decision is already safe to apply; the missing audit row is an
            // operational alert, not a content decision.
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

    /// <summary>Bounded retries for one audit row; a transient fault gets three tries before giving up.</summary>
    private const int AuditWriteAttempts = 3;

    /// <summary>
    /// Appends a content-free row for a vendor failure or timeout. The row is not a decision (the
    /// surface's failure stance still applies); it exists so the publish gate can negative-cache the
    /// outage for <see cref="ContentModerationOptions.UnavailableCacheSeconds"/> instead of sampling
    /// the same object on every request. A row is never recorded as <c>Allow</c>.
    /// </summary>
    private async Task RecordUnavailableAuditRowAsync(
        ModerationInput input,
        ModerationAttribution attribution,
        ResolvedContentModerationProvider provider,
        ContentModerationOptions options,
        string hash,
        CancellationToken ct)
    {
        // Only a step result can be looked up by object identity in the database; without one the
        // publish gate's in-process negative cache owns the outage, so no row is needed. Keeping the
        // row to the identity-cacheable case also keeps an outage from adding unbounded audit noise.
        if (attribution.StepResultId is null) return;

        DateTime now = DateTime.UtcNow;
        double retentionSeconds = options.AuditRetentionDays * 24d * 60d * 60d;
        double negativeSeconds = Math.Min(options.EffectiveUnavailableCacheSeconds, retentionSeconds);

        ContentModerationDecision row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = attribution.OrganizationId,
            ProjectId = attribution.ProjectId,
            UserId = attribution.UserId,
            ExecutionId = attribution.ExecutionId,
            StepResultId = attribution.StepResultId,
            SourceVersion = attribution.SourceVersion,
            Surface = attribution.Surface.ToString(),
            Action = ModerationAction.Unavailable.ToString(),
            ContentKind = attribution.Kind.ToString(),
            ContentSha256 = hash,
            ContentLength = ContentLength(input),
            ProviderId = provider.ProviderId,
            AccountId = provider.AccountId,
            ProviderKind = provider.Kind.ToString(),
            Model = provider.ModelName,
            ModelSnapshot = options.Model,
            ReviewState = ModerationReviewState.NotRequired.ToString(),
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(negativeSeconds)
        };

        if (!await TryWriteAuditRowAsync(row, ct))
        {
            _logger.LogWarning(
                "The moderation audit row for an Unavailable decision on {Surface} was not written; the failure stance still applies",
                attribution.Surface);
        }
    }

    private async Task<bool> TryWriteAuditRowAsync(ContentModerationDecision row, CancellationToken ct)
    {
        _db.ContentModerationDecisions.Add(row);

        for (int attempt = 1; attempt <= AuditWriteAttempts; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
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
                    "Audit-row write failed after {Attempts} attempts", AuditWriteAttempts);
                // Do not clear the tracker: the scoped context may also hold the caller's file/run
                // row. The entity stays Added; nothing was partially committed by a failed save.
                return false;
            }
        }

        return false;
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

        // An unrecorded Block/Review is never an outage to fail open on: deny regardless of the
        // operator's FailClosedAtPublish choice. Checked before the fail-open branch.
        if (decision.ReasonCode == ModerationReasonCodes.AuditUnavailable)
        {
            _logger.LogWarning(
                "Content moderation could not record a Block/Review for publish; refusing egress regardless of {Setting}",
                nameof(ContentModerationOptions.FailClosedAtPublish));
            return false;
        }

        // A scoreless/unclassified answer is not an outage: never fail open on it, whatever the
        // operator's FailClosedAtPublish choice.
        if (decision.Action == ModerationAction.Unavailable)
        {
            _logger.LogWarning(
                "Content moderation could not classify a publish payload; refusing egress regardless of {Setting}",
                nameof(ContentModerationOptions.FailClosedAtPublish));
            return false;
        }

        if (!decision.Available)
        {
            if (Options.FailClosedAtPublish)
            {
                _logger.LogWarning(
                    "Content moderation is unavailable for publish; refusing egress because {Setting} is true",
                    nameof(ContentModerationOptions.FailClosedAtPublish));
                return false;
            }

            LogFailOpen(attribution.Surface);
        }

        return decision.Action != ModerationAction.Block;
    }

    private async Task RecordUsageAsync(
        ResolvedContentModerationProvider provider,
        ModerationAttribution attribution,
        CancellationToken ct)
    {
        try
        {
            using IDisposable scope = UsageScope.Begin(
                attribution.OrganizationId,
                attribution.ProjectId,
                attribution.ExecutionId,
                attribution.StepResultId,
                attribution.UserId,
                UsageSource.Moderation);

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
