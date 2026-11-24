using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;

/// <summary>
/// The real cost-routing gate. Mirrors <c>DecisionGate</c> deliberately and closely — same
/// <c>ResolveDecisionAsync</c> → <c>IDecisionClientFactory</c> → <c>DecideAsync</c> shape, same
/// add-then-<c>SaveChangesAsync</c> persistence, same blanket <c>catch → LogWarning</c> — because the
/// two are the same kind of object: a best-effort decision recorded alongside a real agent run,
/// which must be incapable of failing that run.
///
/// <para>
/// <b>Two deliberate differences from <c>DecisionGate</c>.</b> First, this one is NOT inert: the
/// decision it records is also armed as a routing intent for the run about to start, which
/// <see cref="CostRoutingChatClientProvider"/> reads. Second, its observation is written in two
/// phases — the row is added and saved BEFORE the run (so a decision is on record even if the step
/// then dies), and <c>DownstreamOutcomeJson</c> is filled in from the run's real outcome when the
/// scope is disposed afterwards.
/// </para>
/// </summary>
public sealed class CostRoutingGate : ICostRoutingGate
{
    /// <summary>The question name this gate always asks. Stable — it is what the answer is looked up by.</summary>
    public const string QuestionName = "CostRouting.CheapTierWillPass";

    /// <summary>
    /// The two actions the decision chooses between, hashed into <c>OptionsHash</c>. Noul questions
    /// carry no offered options on the wire, but the column exists for deduplication and a constant
    /// empty hash would deduplicate nothing — the pair of possible routing ACTIONS is the meaningful
    /// thing to hash here.
    /// </summary>
    internal static readonly string[] RoutingActions = ["keep-cheap", "escalate"];

    /// <summary>
    /// Written into <c>DecisionObservation.Mode</c>. <c>DecisionGate</c> writes its mode name there;
    /// cost routing has no mode enum of its own, so it writes what it is.
    /// </summary>
    internal const string ModeName = "CostRouting";

    private const string Instructions =
        "A workflow agent step is about to run on a CHEAP model tier. Given the bounded descriptor of " +
        "the step in the state, answer Y if the cheap tier is likely to produce output that passes " +
        "this step's schema validation and any later review of it, or N if the cheap tier is likely " +
        "to fail and the step should run on an expensive tier instead.";

    private const int MaxSiteChars = 200;

    private readonly IInferenceProviderResolver _resolver;
    private readonly IDecisionClientFactory _clientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CostRoutingAmbientState _ambient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostRoutingGate> _logger;

    public CostRoutingGate(
        IInferenceProviderResolver resolver,
        IDecisionClientFactory clientFactory,
        IServiceScopeFactory scopeFactory,
        CostRoutingAmbientState ambient,
        IConfiguration configuration,
        ILogger<CostRoutingGate> logger)
    {
        _resolver = resolver;
        _clientFactory = clientFactory;
        _scopeFactory = scopeFactory;
        _ambient = ambient;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CostRoutingDecision?> BeginStepAsync(
        StepExecutionContext context, IReelBoltAgent agent, string resolvedAgentInput)
    {
        try
        {
            CostRoutingConfig config = ReadConfig(agent.Name);

            // Off, or nothing to escalate TO. A missing expensive tier is not an error to report —
            // it is the default state of every deployment that has not opted in.
            if (!config.Enabled || config.ExpensiveProviderId is null)
                return null;

            CancellationToken ct = context.CancellationToken;

            ResolvedDecisionProvider? provider = await _resolver.ResolveDecisionAsync(config.DecisionProviderId, ct);
            if (provider is null)
                return null;

            IDecisionClient client = _clientFactory.Get(provider);

            string descriptor = BuildDescriptor(context, agent, resolvedAgentInput);
            DecisionQuestion[] questions =
            [
                new DecisionQuestion(QuestionName, DecisionKind.Noul, Instructions, [])
            ];

            DecisionResult result = await client.DecideAsync(new DecisionState(descriptor), questions, ct);

            if (!result.Answers.TryGetValue(QuestionName, out DecisionAnswer? answer) || answer.Noul is null)
                return null;

            double noul = answer.Noul.Value;
            bool escalated = ShouldEscalate(noul, config);

            Guid observationId = Guid.NewGuid();
            string site = Bound($"CostRouting.{agent.Name}", MaxSiteChars);

            var observation = new DecisionObservation
            {
                Id = observationId,
                ExecutionId = context.Execution.Id,
                ProjectId = context.Execution.ProjectId,
                OrganizationId = context.Execution.OrganizationId,
                StepOrder = context.Step.StepOrder,
                AgentType = context.Step.AgentDefinition.AgentType,
                Site = site,
                Question = QuestionName,
                OptionsHash = HashOptions(RoutingActions),
                Kind = DecisionKind.Noul,
                Answer = noul.ToString(),
                // Noul has no Confidence — see DecisionAnswer's doc comment, and DecisionGate, which
                // records answer.Confidence verbatim for the same reason.
                Confidence = answer.Confidence,
                ProbabilitiesJson = BuildProbabilitiesJson(answer, noul),
                Mode = ModeName,
                Escalated = escalated,
                // Filled in after the run by CompleteRunAsync — the point of recording before the run
                // is that the decision survives even if the run never comes back.
                DownstreamOutcomeJson = null,
                ProviderId = provider.ProviderId,
                Model = result.Model,
                CreatedAt = DateTime.UtcNow,
            };

            using (IServiceScope scope = _scopeFactory.CreateScope())
            {
                WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
                db.DecisionObservations.Add(observation);
                await db.SaveChangesAsync(ct);
            }

            var state = new CostRoutingRunState
            {
                ObservationId = observationId,
                ExecutionId = context.Execution.Id,
                StepOrder = context.Step.StepOrder,
                AgentType = context.Step.AgentDefinition.AgentType,
                Escalated = escalated,
                CheapProviderId = config.CheapProviderId,
                ExpensiveProviderId = config.ExpensiveProviderId,
            };

            // NOT armed here — see CostRoutingDecision's doc comment: an AsyncLocal write performed
            // inside this async method would be discarded before control returns to the executor.
            return new CostRoutingDecision
            {
                RunState = state,
                Ambient = _ambient,
                Complete = runState => CompleteRunAsync(runState, ct),
            };
        }
        catch (Exception ex)
        {
            // Defence in depth on top of every early-return above: a bug in the descriptor, the
            // decision client, or the persistence must never escape into the executor's step.
            _logger.LogWarning(
                ex,
                "Cost routing decision failed for execution {ExecutionId} step {StepOrder} ({AgentType}); running unrouted",
                context.Execution.Id,
                context.Step.StepOrder,
                context.Step.AgentDefinition.AgentType);
            return null;
        }
    }

    /// <summary>
    /// Whether the step should run on the expensive tier.
    ///
    /// <para>
    /// The question is DIRECTED — <c>Noul</c> is P(the cheap tier will pass) — so unlike
    /// <c>DecisionGate</c>'s undirected "how confident is the model in whichever answer it gave"
    /// test, a HIGH confidence in NO is a confident reason to escalate, not a reason to stay. The
    /// two conditions are therefore (a) the model is not confident the cheap tier will pass, and
    /// (b) the two outcomes are too close to call (margin <c>|2p - 1|</c> under
    /// <see cref="CostRoutingConfig.MinMargin"/>), and either one escalates. The margin term is
    /// numerically the same quantity <c>DecisionGate</c> computes as <c>top - second</c> for a
    /// binary Noul; only the first term differs, and deliberately.
    /// </para>
    /// </summary>
    private static bool ShouldEscalate(double noul, CostRoutingConfig config) =>
        noul < config.AcceptAt || Math.Abs((2 * noul) - 1) < config.MinMargin;

    /// <summary>
    /// Disposal-time half of the two-phase observation write: fills in <c>DownstreamOutcomeJson</c>
    /// from what the run actually did. Runs inside the executor's own <c>await using</c> finally, so
    /// it must be absolutely incapable of throwing — an exception here would fail a step whose real
    /// result was already computed.
    /// </summary>
    private async ValueTask CompleteRunAsync(CostRoutingRunState state, CancellationToken ct)
    {
        try
        {
            CostRoutingOutcome outcome = state.Snapshot();

            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            DecisionObservation? row = await db.DecisionObservations
                .FirstOrDefaultAsync(o => o.Id == state.ObservationId, ct);

            if (row is null)
                return;

            row.DownstreamOutcomeJson = JsonSerializer.Serialize(outcome, JsonOptions);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Cost routing outcome update failed for execution {ExecutionId} step {StepOrder} ({AgentType}); the step result is unaffected",
                state.ExecutionId,
                state.StepOrder,
                state.AgentType);
        }
    }

    private CostRoutingConfig ReadConfig(string agentName)
    {
        CostRoutingConfig defaults = new();
        string prefix = $"Agents:{agentName}:CostRouting";

        return new CostRoutingConfig(
            Enabled: _configuration.GetValue<bool?>($"{prefix}:Enabled") ?? defaults.Enabled,
            CheapProviderId: _configuration.GetValue<Guid?>($"{prefix}:CheapProviderId") ?? defaults.CheapProviderId,
            ExpensiveProviderId: _configuration.GetValue<Guid?>($"{prefix}:ExpensiveProviderId") ?? defaults.ExpensiveProviderId,
            AcceptAt: _configuration.GetValue<double?>($"{prefix}:AcceptAt") ?? defaults.AcceptAt,
            MinMargin: _configuration.GetValue<double?>($"{prefix}:MinMargin") ?? defaults.MinMargin,
            DecisionProviderId: _configuration.GetValue<Guid?>($"{prefix}:DecisionProviderId") ?? defaults.DecisionProviderId);
    }

    private static string BuildDescriptor(StepExecutionContext context, IReelBoltAgent agent, string resolvedAgentInput) =>
        CostRoutingDescriptor.Build(
            stepOrder: context.Step.StepOrder,
            agentName: agent.Name,
            agentType: context.Step.AgentDefinition.AgentType,
            stepType: context.Step.StepType,
            toolCount: agent.Tools.Count,
            outputSchemaName: agent.OutputSchemaType?.Name,
            priorStepCount: context.StepOutputHistory.Count,
            iterationCount: context.IterationCount,
            resolvedAgentInput: resolvedAgentInput);

    /// <summary>
    /// Mirrors <c>DecisionGate</c>: a Noul answer's <c>Probabilities</c> dictionary is empty by
    /// construction (see the decision clients' Noul parsing), so the binary distribution is written
    /// out explicitly rather than persisting the useless <c>{}</c> the column would otherwise hold.
    /// </summary>
    private static string BuildProbabilitiesJson(DecisionAnswer answer, double noul) =>
        answer.Probabilities.Count > 0
            ? JsonSerializer.Serialize(answer.Probabilities, JsonOptions)
            : JsonSerializer.Serialize(new Dictionary<string, double> { ["yes"] = noul, ["no"] = 1 - noul }, JsonOptions);

    private static string HashOptions(IReadOnlyList<string> options)
    {
        string joined = string.Join('|', options.OrderBy(o => o, StringComparer.Ordinal));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash);
    }

    private static string Bound(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
