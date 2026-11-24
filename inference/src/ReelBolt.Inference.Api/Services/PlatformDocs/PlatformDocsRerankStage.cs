using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>
/// The model rerank of the platform-docs retrieval (stage B). Resolves the <c>Rerank</c> capability, scores the top
/// candidates through <see cref="IRerankClient"/> and meters one <see cref="UsageKind.RerankSearches"/> row per
/// successful call.
/// </summary>
/// <remarks>
/// <b>Charging.</b> The usage row is recorded under the AMBIENT <see cref="UsageScope"/> of the assistant turn
/// (source <see cref="UsageSource.Assistant"/>), so its priced credits join the turn's running total and are settled
/// by <c>NonWorkflowChargeService.SettleTurnAsync</c> inside the turn's existing hold and allowance: the user sees no
/// extra line. With billing off the turn has no hold and nothing is charged; the usage row is still written.
/// <para>
/// <b>Bounds.</b> At most one call per invocation, <see cref="RerankLimits.MaxCandidates"/> candidates of at most
/// <see cref="RerankLimits.MaxCandidateChars"/> characters, a query of at most <see cref="RerankLimits.MaxQueryChars"/>,
/// and <c>PlatformDocs:Rerank:TimeoutSeconds</c>. The client re-applies the same caps.
/// </para>
/// <para>
/// <b>Fail-soft.</b> Every failure answers <c>null</c> (stage A's order). An unpriced platform rerank model is
/// "rerank unavailable"; a caller cancellation is re-thrown.
/// </para>
/// </remarks>
public sealed class PlatformDocsRerankStage : IPlatformDocsRerankStage
{
    // One rerank call per assistant turn (spec 7.6), across the injection lookup and every SearchPlatformDocs call:
    // the first call in a usage scope claims the slot, later ones keep stage A. Keyed on the turn's ambient scope,
    // weakly, so a finished turn frees its entry. Outside any scope (no turn to bound) nothing is claimed.
    private static readonly ConditionalWeakTable<UsageScopeState, object> TurnClaims = new();

    private readonly IInferenceProviderResolver _resolver;
    private readonly IRerankClientFactory _clients;
    private readonly IUsageRecorder _usage;
    private readonly IOptions<PlatformDocsOptions> _options;
    private readonly ILogger<PlatformDocsRerankStage> _logger;

    public PlatformDocsRerankStage(
        IInferenceProviderResolver resolver,
        IRerankClientFactory clients,
        IUsageRecorder usage,
        IOptions<PlatformDocsOptions> options,
        ILogger<PlatformDocsRerankStage> logger)
    {
        _resolver = resolver;
        _clients = clients;
        _usage = usage;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlatformDocsHit>?> RerankAsync(
        string query, IReadOnlyList<PlatformDocsHit> candidates, CancellationToken ct)
    {
        PlatformDocsRerankOptions options = _options.Value.Rerank;
        if (!options.Enabled || candidates.Count < 2 || string.IsNullOrWhiteSpace(query)) return null;

        int take = Math.Clamp(options.Candidates, 1, RerankLimits.MaxCandidates);
        PlatformDocsHit[] head = [.. candidates.Take(take)];
        if (head.Length < 2) return null;

        if (!ClaimTurnSlot()) return null;

        try
        {
            ResolvedRerankProvider? provider = await _resolver.ResolveRerankAsync(null, ct).ConfigureAwait(false);
            if (provider is null) return null;

            // Internal developer-audience documentation must never go to a tenant-controlled reranker: only an
            // operator-configured platform row is used, whatever the ambient scope resolved.
            if (!provider.IsPlatformManaged || provider.OrganizationId is not null) return null;

            int timeoutSeconds = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : RerankLimits.DefaultTimeoutSeconds;
            IRerankClient client = _clients.Create(provider, timeoutSeconds);
            using IDisposable? ownedClient = client as IDisposable;

            string[] texts = [.. head.Select(c => Cap(CandidateText(c), RerankLimits.MaxCandidateChars))];
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            IReadOnlyList<RerankScore>? scores;
            try
            {
                scores = await client.RerankAsync(Cap(query.Trim(), RerankLimits.MaxQueryChars), texts, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }

            if (scores is null || !CoversEveryCandidate(scores, head.Length))
            {
                // A response body arrived but was malformed or short: the vendor may have billed it, so meter it.
                // Timeouts, transport errors and non-success statuses write no row.
                if (client.ReachedVendor) await RecordUsageAsync(provider, ct).ConfigureAwait(false);
                return null;
            }

            await RecordUsageAsync(provider, ct).ConfigureAwait(false);

            List<PlatformDocsHit> ordered = [.. scores.Select(s => head[s.Index])];
            ordered.AddRange(candidates.Skip(head.Length));
            return ordered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelUnpricedException)
        {
            _logger.LogInformation("Rerank is unavailable: the platform rerank model has no price; using the deterministic order");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Platform-docs rerank failed; using the deterministic order");
            return null;
        }
    }

    private static bool ClaimTurnSlot()
    {
        UsageScopeState? scope = UsageScope.Current;
        if (scope is null) return true;
        lock (TurnClaims)
        {
            if (TurnClaims.TryGetValue(scope, out _)) return false;
            TurnClaims.Add(scope, new object());
            return true;
        }
    }

    private static string CandidateText(PlatformDocsHit hit) =>
        string.IsNullOrEmpty(hit.Section) ? $"{hit.Title}\n{hit.Content}" : $"{hit.Title} - {hit.Section}\n{hit.Content}";

    private static bool CoversEveryCandidate(IReadOnlyList<RerankScore> scores, int count)
    {
        if (scores.Count != count) return false;
        HashSet<int> seen = [];
        return scores.All(s => s.Index >= 0 && s.Index < count && seen.Add(s.Index));
    }

    private async Task RecordUsageAsync(ResolvedRerankProvider provider, CancellationToken ct)
    {
        try
        {
            await _usage.RecordAsync(new UsageRecordRequest(
                UsageKind.RerankSearches,
                1m,
                provider.ProviderId,
                provider.Kind,
                provider.ModelName,
                provider.IsPlatformManaged ? UsageBillingMode.Platform : UsageBillingMode.Byo,
                Capability: InferenceProviderCapability.Rerank,
                AccountId: provider.AccountId), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The search already happened; a metering failure must not fail retrieval.
            _logger.LogError(ex, "Could not record the rerank usage row");
        }
    }

    private static string Cap(string value, int max) => value.Length > max ? value[..max] : value;
}
