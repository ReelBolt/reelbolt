using System.Text.Json;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>
/// The app's chosen models in the price of a replay (docs/design/app-inputs.md 29, the F2 28.7 narrowing): a change request's
/// or a composition revision's 402 preflight and estimate price the agents' and steps' chosen models exactly when the engine
/// will hold them, which is when the replay's definition carries the app page (the engine reads the choices from that page
/// and the run's values: <c>ExecutionSnapshotter.ReplayAgentModelsAsync</c> and <c>AppInputSnapshotBinding.StateFor</c>).
/// With no page or no values nothing is entered, so every other price is unchanged.
/// </summary>
internal static class ReplayModelPricing
{
    /// <summary>
    /// The choices of a replay whose definition carries <paramref name="pageJson"/> and that runs with <paramref name="values"/>.
    /// An agent choice that no longer resolves prices at the ordinary model (the engine refuses such a run with
    /// <c>APP_INPUTS_STALE</c> before holding anything). The scope itself is entered by the caller with
    /// <see cref="ReplayModelChoices.Enter"/>: an async-local set inside an awaited method would not reach the caller.
    /// </summary>
    public static async Task<ReplayModelChoices> ResolveAsync(
        IAppInputModelResolverSource? models, Guid? organizationId, Guid projectId, string? pageJson,
        IReadOnlyDictionary<string, JsonElement>? values, CancellationToken ct)
    {
        AppManifest? page = AppManifestJson.TryParse(pageJson);
        if (page is null || values is not { Count: > 0 })
            return ReplayModelChoices.None;

        IReadOnlyDictionary<Guid, Guid>? agents = null;
        if (models is not null && AppModelRules.HasAgentModelValue(page, values))
        {
            IReadOnlyList<AppModelCandidate> candidates = await models.CandidatesAsync(organizationId, projectId, ct);
            IReadOnlyDictionary<Guid, Guid> chosen = AppModelRules.AgentModels(page, values, candidates, out _);
            agents = chosen.Count > 0 ? chosen : null;
        }

        return new ReplayModelChoices(agents, AppModelRules.StepModelChoices(page, values));
    }
}

/// <summary>The chosen models of one replay: agent to Chat capability, and the steps' chosen capabilities (each null when none).</summary>
internal sealed record ReplayModelChoices(IReadOnlyDictionary<Guid, Guid>? Agents, IReadOnlySet<Guid>? Steps)
{
    public static readonly ReplayModelChoices None = new(null, null);

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>Enters the <see cref="AppAgentModelAmbient"/> scope in the CALLER's context (nothing when there is no choice); dispose to restore.</summary>
    public IDisposable Enter() => Agents is null && Steps is null ? Nothing.Instance : AppAgentModelAmbient.Enter(Agents, Steps);
}
