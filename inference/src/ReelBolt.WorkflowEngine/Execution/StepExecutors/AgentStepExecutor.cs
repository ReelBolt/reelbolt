using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Observability;
using ReelBolt.WorkflowEngine.Services.Billing;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes a standard agent step - runs the agent and returns its output.
/// </summary>
public class AgentStepExecutor : IStepExecutor, IChangeRequestAwareStepExecutor
{
    private readonly IAgentRegistry _agentRegistry;
    private readonly IWorkflowExecutionContextAccessor _executionContextAccessor;
    private readonly IMotionGraphicsPlacementAnnotator _placementAnnotator;
    private readonly IConfiguration _configuration;
    private readonly IDecisionGate _decisionGate;
    private readonly ILogger<AgentStepExecutor> _logger;

    // Optional, with a null default, for two reasons: DI falls back to the default value when the
    // service is not registered (so an embedding that never registers cost routing keeps working
    // unchanged), and every existing construction site — including
    // AgentStepExecutorDecisionShadowTests.CreateExecutor — keeps compiling untouched. Null means a
    // pure no-op: no provider call, no config read, no ambient state.
    //
    // Fully qualified rather than reached through a using directive on purpose: this file is
    // contended (the Shadow gate hook below is owned by a concurrently-running ticket), so the
    // cost-routing change adds NOTHING to the using block — exactly one constructor parameter and
    // exactly one statement.
    private readonly Execution.Decision.CostRouting.ICostRoutingGate? _costRouting;

    // Step option "continue from an earlier run's composition" (Services/Compositions). Built from the
    // container (IServiceProvider is always resolvable) so no registration or using directive is added.
    private readonly Services.Compositions.CompositionContinuation? _compositionContinuation;

    // Step change requests (docs/change-requests.md "Script (by scene)"): only the scriptwriter applies one.
    private readonly IStepChangeRequestContext? _changeRequests;
    private readonly IServiceProvider? _services;

    public AgentStepExecutor(
        IAgentRegistry agentRegistry,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        IMotionGraphicsPlacementAnnotator placementAnnotator,
        IConfiguration configuration,
        IDecisionGate decisionGate,
        ILogger<AgentStepExecutor> logger,
        Execution.Decision.CostRouting.ICostRoutingGate? costRouting = null,
        IServiceProvider? services = null,
        IStepChangeRequestContext? changeRequests = null,
        // Moderation of the text a structured script change writes (docs/change-requests.md). Null = skipped.
        IContentModerationGate? moderation = null,
        IOptions<ContentModerationOptions>? moderationOptions = null)
    {
        _moderation = moderation;
        _moderationMaxChars = moderationOptions?.Value.MaxTextChars ?? 8000;
        _changeRequests = changeRequests;
        _agentRegistry = agentRegistry;
        _executionContextAccessor = executionContextAccessor;
        _placementAnnotator = placementAnnotator;
        _configuration = configuration;
        _decisionGate = decisionGate;
        _logger = logger;
        _costRouting = costRouting;
        _services = services;
        _compositionContinuation = Services.Compositions.CompositionContinuation.FromServices(services);
    }

    private readonly IContentModerationGate? _moderation;
    private readonly int _moderationMaxChars;

    public StepType StepType => StepType.Agent;

    /// <inheritdoc />
    public IReadOnlyCollection<AgentType> SupportedAgentTypes => [AgentType.ScriptwriterAgent];

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        // A8, runtime half. A built-in agent is shared by every organization; a custom agent
        // belongs to exactly one, and an execution in another organization must never run it —
        // not even if the row was saved before the save-side check existed, or that check was
        // bypassed. This deliberately sits BEFORE the registry lookup: an unresolvable agent is
        // SKIPPED further down, and a silently skipped step lets a cross-organization run look
        // successful while doing nothing.
        //
        // Only a definition that actually carries an organization can be foreign. A built-in row's
        // organization_id is null by the A1 check constraint, and every pre-tenancy workflow
        // carries the same shape, so both stay byte-identically unaffected.
        if (context.Step.AgentDefinition.OrganizationId is { } agentOrganizationId
            && !context.Step.AgentDefinition.IsBuiltIn
            && agentOrganizationId != context.Execution.OrganizationId)
        {
            string organizationMismatch =
                $"Agent definition {context.Step.AgentDefinitionId} belongs to organization {agentOrganizationId}, " +
                $"but this execution runs for organization {context.Execution.OrganizationId}.";
            _logger.LogWarning(
                "Agent step {StepOrder} refused: {Reason}", context.Step.StepOrder, organizationMismatch);
            return new StepExecutionResult
            {
                Output = context.AccumulatedOutput,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                DurationMs = 0,
                TokensUsed = 0,
                Status = StepStatus.Failed,
                ErrorDetails = organizationMismatch
            };
        }

        // The dynamics planner exists to plan a dynamic edit. A run with no effective pace (no style dynamics and no brief pace) has none,
        // so the step is a normal Skipped result before any input is built, gate asked or model called: nothing is spent, and the compile
        // reads its plan as unresolved and runs without one (docs/video-editing.md "The dynamics planner").
        if (ReelBolt.Shared.Metering.DynamicsPlannerPricing.Skips(
                context.Step.AgentDefinition.AgentType, Services.Styles.RunStyleOverrides.EffectiveDynamics(context.Execution) is not null))
        {
            _logger.LogInformation(
                "Agent step {StepOrder} (DynamicsPlanner) skipped: the run has no pace", context.Step.StepOrder);
            return new StepExecutionResult
            {
                Output = context.AccumulatedOutput,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                DurationMs = 0,
                TokensUsed = 0,
                Status = StepStatus.Skipped,
                ErrorDetails = "The run has no pace (no style dynamics and no brief pace), so there is no dynamic edit to plan."
            };
        }

        Guid? customAgentId = context.Step.AgentDefinition.AgentType == AgentType.Custom
            ? context.Step.AgentDefinitionId
            : null;

        IReelBoltAgent? agent = _agentRegistry.GetByType(context.Step.AgentDefinition.AgentType, customAgentId);
        if (agent == null)
        {
            _logger.LogWarning(
                "No agent found for type {AgentType} (AgentDefinitionId: {AgentDefinitionId}), skipping",
                context.Step.AgentDefinition.AgentType,
                context.Step.AgentDefinitionId);
            return new StepExecutionResult
            {
                Output = context.AccumulatedOutput,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                DurationMs = 0,
                TokensUsed = 0,
                Status = StepStatus.Skipped,
                ErrorDetails = $"No agent registered for type {context.Step.AgentDefinition.AgentType} and agent definition id {context.Step.AgentDefinitionId}"
            };
        }

        // Code analyzers read the project's source. With none readable they would run blind and be billed, so the
        // step fails here, before cost routing, a gate or the model, charging nothing (zero usage settles at 0).
        if (await CodeSourceGuard.CheckAsync(_services, context.Execution.ProjectId, context.Step.AgentDefinition.AgentType, context.CancellationToken)
            is { } noSource)
        {
            return new StepExecutionResult
            {
                Output = context.AccumulatedOutput,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                DurationMs = 0,
                TokensUsed = 0,
                Status = StepStatus.Failed,
                ErrorDetails = $"{noSource.Code}: {noSource.Message}",
                FailureClass = FailureClass.UserInput,
                FailureCode = noSource.Code,
                UserMessage = noSource.Message,
                Retryable = false
            };
        }

        // A change request on the script is applied by scene, before any input is built or a gate asked.
        if (context.Step.AgentDefinition.AgentType == AgentType.ScriptwriterAgent && _changeRequests is not null
            && await _changeRequests.FindAsync(context.Execution, context.Step, context.CancellationToken) is { } changeScope)
            return await ExecuteScriptChangeAsync(context, agent, changeScope);

        // Narrowly scoped, agent-type-specific INPUT augmentation (the mirror image of the
        // AgentType.AuthorAgent-specific OUTPUT validation further down): the motion-graphics
        // planner is the one agent whose prompt contains overlay-placement candidates AND, via
        // AgentInputContextMode.FullWorkflow, the story editor's already-made cut decision — so it
        // is the first and only point in the pipeline where "does this placement survive the cut?"
        // is answerable at all. Display-only: it adds an `inEdit` flag to each candidate, never
        // removes one. A no-op for every other agent type, and internally soft-failing (see
        // IMotionGraphicsPlacementAnnotator), so the prompt is simply un-annotated if anything
        // about the resolution goes wrong.
        if (context.Step.AgentDefinition.AgentType == AgentType.MotionGraphicsPlanner)
            await _placementAnnotator.AnnotateAsync(context, context.CancellationToken);

        string stepInput = await context.BuildAgentInputAsync(context.CancellationToken);

        // The dynamics planner is handed a purpose-built view (source shot ids, the generated clips the compile would accept, the
        // selected takes, the music facts as words and counts) instead of every earlier step's output: it never sees a storage key.
        // Resolved from the container like the other optional collaborators; a host without the builder keeps the ordinary input.
        if (context.Step.AgentDefinition.AgentType == AgentType.DynamicsPlanner
            && _services?.GetService(typeof(IDynamicsPlannerViewBuilder)) is IDynamicsPlannerViewBuilder dynamicsView)
        {
            stepInput = await dynamicsView.BuildInputAsync(context, context.CancellationToken);
            context.RecordResolvedInput(stepInput);
        }

        // Gate mode INVERTS the order for the two decision sites it covers: the bounded descriptor
        // state and the decision call happen BEFORE the agent, because a fully-accepted decision
        // means the agent never runs at all. Off and Shadow are untouched — there the agent runs
        // first exactly as it always has, and the observation hook further down is unchanged. See
        // RunPreAgentGateAsync for the accept/escalate rule and for every failure path.
        GatePreflight gatePreflight = await RunPreAgentGateAsync(context, stepInput, context.CancellationToken);
        if (gatePreflight.AcceptedResult is not null)
            return gatePreflight.AcceptedResult;

        if (gatePreflight.PromptInput is not null && gatePreflight.PromptInput != stepInput)
        {
            stepInput = gatePreflight.PromptInput;

            // The prior block is part of the prompt the agent actually receives, so the resolved
            // input the persisted InputJson is built from must include it. That record is
            // informational only — it does NOT reach the step-cache key, which
            // WorkflowExecutorService captures as initialInputJson BEFORE the executor is called
            // and uses unchanged for both the lookup and the store. What keeps a Gate-configured
            // step out of the cache is StepCachePolicy, not this.
            context.RecordResolvedInput(stepInput);
        }

        // Continue composition: restore the source version into this run's sandbox before the agent runs.
        Services.Compositions.CompositionContinuation.Outcome continuation =
            await Services.Compositions.CompositionContinuation.ApplyAsync(_compositionContinuation, context, stepInput);
        if (continuation.Failure is not null)
            return continuation.Failure;
        stepInput = continuation.Input;

        _logger.LogInformation(
            "Executing agent step {StepOrder}: {AgentName} (AgentType={AgentType}, AgentDefinitionId={AgentDefinitionId}, Tools={ToolCount})",
            context.Step.StepOrder,
            agent.Name,
            context.Step.AgentDefinition.AgentType,
            context.Step.AgentDefinitionId,
            agent.Tools.Count);
        _logger.LogDebug(
            "Agent step {StepOrder} input prepared (Chars={InputChars}, Preview={InputPreview})",
            context.Step.StepOrder,
            stepInput.Length,
            CreatePreview(stepInput, 300));

        Stopwatch sw = Stopwatch.StartNew();
        using IDisposable _ = _executionContextAccessor.BeginScope(
            context.Execution.Id,
            context.Execution.ProjectId,
            context.CorrelationId,
            context.Execution.OrganizationId,
            stepResultId: context.StepResultId);
        // Cost routing hook (see docs/decision-models.md § cost routing). ONE statement, and the
        // only one this file gets: it records the routing decision and arms it as an ambient intent
        // the agent's own chat-client resolution reads while it builds its client inside RunAsync.
        // The scope's disposal at the end of this method is the "after the run" half, which writes
        // the run's real outcome back onto the observation row — that is why this is `await using`
        // and not a plain call. Null gate (or a gate that declines) yields the shared no-op scope:
        // nothing resolves, nothing is written, and the step below runs exactly as it did before
        // cost routing existed.
        //
        // CostRoutingScope.Enter runs in THIS frame on purpose — an AsyncLocal write performed
        // inside the gate's own async method would be discarded before control returns here, and the
        // intent would never reach the run. See CostRoutingDecision's doc comment.
        await using Execution.Decision.CostRouting.CostRoutingScope costRoutingScope = Execution.Decision.CostRouting.CostRoutingScope.Enter(
            _costRouting is null ? null : await _costRouting.BeginStepAsync(context, agent, stepInput));

        // T-009: the sandbox render tool records a failed full render on the step's ledger, and this
        // publication is what makes that ledger visible to the tool calls below. The ledger lives on
        // the step context, so it survives this attempt and the cap counts across all attempts.
        using IDisposable renderFailureScope = context.RenderFailures.Publish();
        // Motion-graphics legibility (docs/design/graphics-see-and-size.md): the render tool measures
        // rendered graphics against this step's canvas floor. Inert unless the annotator configured it.
        using IDisposable legibilityScope = context.OverlayLegibility.Publish();

        AgentRunResult result = await agent.RunAsync(stepInput, context.Step.AgentDefinitionId, context.CancellationToken);
        sw.Stop();

        _logger.LogInformation(
            "Agent step {StepOrder}: {AgentName} completed in {DurationMs}ms (Tokens={TotalTokens}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, Success={Success})",
            context.Step.StepOrder,
            agent.Name,
            sw.ElapsedMilliseconds,
            result.TokensUsed,
            result.InputTokens,
            result.OutputTokens,
            result.Success);
        _logger.LogDebug(
            "Agent step {StepOrder} output preview: {OutputPreview}",
            context.Step.StepOrder,
            CreatePreview(result.Output, 300));

        string? outputStorageKey = _executionContextAccessor.Current?.PendingOutputStorageKey;

        // An Author that answers without ever rendering the film gets ONE more turn in this attempt, told
        // plainly to render now (production run 36df4287: three attempts edited and previewed, none
        // called RenderVideoAndUploadToStorage). The sandbox keeps its work between the two runs. Bounded:
        // one reminder; if it still does not render, the step fails below exactly as it did before.
        if (NeedsFinalRenderReminder(context, result, outputStorageKey))
        {
            result = await RunFinalRenderReminderAsync(context, agent, stepInput, result);
            outputStorageKey = _executionContextAccessor.Current?.PendingOutputStorageKey;
        }

        // T-009: a render of some composition hit the failed-render cap (terminal). If the step
        // already has its artifact — or a non-Author agent has usable output — let it finish with
        // what it has; the refusal already told the model that composition will not render again.
        // Only a step left with no usable output fails with the render's own code and message.
        if (context.RenderFailures.TerminalRefused && !HasUsableStepOutput(context, result, outputStorageKey))
            return RenderFailedStepResult(context, result, outputStorageKey, sw.ElapsedMilliseconds);

        // FailWorkflow is deliberately NOT enforced here, and that is a considered decision
        // rather than an oversight. The tool throws AgentWorkflowException, but
        // FunctionInvokingChatClient catches a tool's exception and returns it to the model as a
        // tool result, so the abort never propagates on its own.
        //
        // Enforcing it was tried and reverted. This stack's model calls the tool spuriously and
        // routinely: once with the reason "No decision needed - proceeding with selection. (This
        // call is not made; see JSON below.)" and once as a bare "No-op check: proceeding with
        // decision instead." -- it pokes the tool to see what happens. Honoring those killed two
        // consecutive VideoStoryEditor steps that were otherwise fine, and a guard that ignored
        // the abort whenever a valid answer existed did not help, because the model makes the call
        // on turns where it has not emitted its JSON yet.
        //
        // The failure enforcement was meant to fix -- an agent looping tool calls forever -- is
        // already bounded from two directions: the per-agent run timeout ends the run, and that
        // timeout is not retried (see ExecuteStepWithRetryAsync), while the schema check below
        // fails any run that ends without a usable answer. Those are cheaper than trusting a
        // control-flow signal this model does not use reliably.

        if (!result.Success)
        {
            string failureReason = BuildFailureReason(result.Output, result.FailureReason, agent.Name);
            _logger.LogWarning(
                "Agent step {StepOrder}: {AgentName} returned failed status. Reason={FailureReason}",
                context.Step.StepOrder,
                agent.Name,
                failureReason);
            return new StepExecutionResult
            {
                Output = result.Output,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                DurationMs = sw.ElapsedMilliseconds,
                TokensUsed = result.TokensUsed,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                PrimaryProviderId = result.PrimaryProviderId,
                PrimaryModelName = result.PrimaryModelName,
                ToolCalls = result.ToolCalls,
                Reasoning = result.Reasoning,
                Status = StepStatus.Failed,
                ErrorDetails = failureReason,
                OutputStorageKey = outputStorageKey
            };
        }

        if (context.Step.AgentDefinition.AgentType == AgentType.AuthorAgent)
        {
            if (TryDetectAuthorOutputFailure(result.Output, out string failureReason))
            {
                return new StepExecutionResult
                {
                    Output = result.Output,
                    NextStepIndex = context.CurrentStepIndex + 1,
                    NewIterationCount = context.IterationCount,
                    DurationMs = sw.ElapsedMilliseconds,
                    TokensUsed = result.TokensUsed,
                    InputTokens = result.InputTokens,
                    OutputTokens = result.OutputTokens,
                    PrimaryProviderId = result.PrimaryProviderId,
                    PrimaryModelName = result.PrimaryModelName,
                    ToolCalls = result.ToolCalls,
                    Reasoning = result.Reasoning,
                    Status = StepStatus.Failed,
                    ErrorDetails = failureReason,
                    OutputStorageKey = outputStorageKey
                };
            }

            if (string.IsNullOrWhiteSpace(outputStorageKey))
            {
                // T-009: an Author whose render failed and who then produced no film fails with the
                // render's own code and sentence (exit code / kill) instead of the generic
                // STEP_INTERNAL_ERROR; with no render failure recorded, the pre-existing failure
                // below is exactly what it always was.
                if (context.RenderFailures.HasFailed)
                    return RenderFailedStepResult(context, result, outputStorageKey, sw.ElapsedMilliseconds);

                return new StepExecutionResult
                {
                    Output = result.Output,
                    NextStepIndex = context.CurrentStepIndex + 1,
                    NewIterationCount = context.IterationCount,
                    DurationMs = sw.ElapsedMilliseconds,
                    TokensUsed = result.TokensUsed,
                    InputTokens = result.InputTokens,
                    OutputTokens = result.OutputTokens,
                    PrimaryProviderId = result.PrimaryProviderId,
                    PrimaryModelName = result.PrimaryModelName,
                    ToolCalls = result.ToolCalls,
                    Reasoning = result.Reasoning,
                    Status = StepStatus.Failed,
                    ErrorDetails = AuthorNeverRenderedMessage,
                    OutputStorageKey = outputStorageKey
                };
            }
        }

        // An agent that DECLARES a structured output schema must actually produce a parseable JSON
        // object. Without this check the schema contract was declared but never verified: the step
        // was marked Completed on any output at all, and the breakage surfaced at whatever distant
        // consumer eventually tried to read it.
        //
        // Observed live producing a real promo: a VideoStoryEditor step returned 38KB of pure
        // reasoning prose containing not a single brace, was recorded Completed, and the workflow
        // then spent ~50 more minutes running MotionGraphicsPlanner, SoundDesigner and Colorist on
        // top of a decision that did not exist -- before VideoCompile finally failed with
        // "Decision input did not contain a recognizable JSON object", a message that names the
        // compile step rather than the step that actually broke.
        //
        // Validating with ExtractLastJsonObject specifically (not a private parser, and not the
        // first-match ExtractJsonObject) is the point: it is the exact extractor the downstream
        // consumers use for these payloads, so a step that passes here cannot fail there for lack
        // of a JSON object. Using the stricter first-match variant would also false-fail a
        // reasoning model whose chain-of-thought contains incidental brace shorthand before the
        // real answer -- the very case ExtractLastJsonObject exists to handle. Returning Failed
        // rather than throwing hands this to the normal retry-with-feedback path, which gives the
        // agent another attempt that is actually told what was wrong.
        if (agent.OutputSchemaType != null)
        {
            string? schemaError = StructuredOutputValidator.Validate(result.Output, agent.OutputSchemaType);
            if (schemaError != null)
            {
                _logger.LogWarning(
                    "Agent step {StepOrder}: {AgentName} output does not match schema {Schema}: {Error}. Running one repair turn.",
                    context.Step.StepOrder, agent.Name, agent.OutputSchemaType.Name, schemaError);

                // ONE repair turn, same agent/model/provider, with NO tools (RunRepairAsync), so a
                // render or sandbox write is never repeated. An agent without a no-tools mode
                // returns null and goes straight to the failure below.
                AgentRunResult? repair = await agent.RunRepairAsync(
                    BuildRepairPrompt(stepInput, result.Output, schemaError),
                    context.Step.AgentDefinitionId,
                    context.CancellationToken);

                if (repair != null)
                {
                    // Keep the repair turn's usage so metering/settle stays truthful.
                    result = new AgentRunResult
                    {
                        Output = repair.Output,
                        Success = repair.Success,
                        FailureReason = repair.FailureReason,
                        TokensUsed = result.TokensUsed + repair.TokensUsed,
                        InputTokens = (result.InputTokens ?? 0) + (repair.InputTokens ?? 0),
                        OutputTokens = (result.OutputTokens ?? 0) + (repair.OutputTokens ?? 0),
                        PrimaryProviderId = result.PrimaryProviderId ?? repair.PrimaryProviderId,
                        PrimaryModelName = result.PrimaryModelName ?? repair.PrimaryModelName,
                        ToolCalls = [.. result.ToolCalls, .. repair.ToolCalls],
                        Reasoning = [.. result.Reasoning, .. repair.Reasoning],
                        ModelCalls = (result.ModelCalls ?? 0) + (repair.ModelCalls ?? 0),
                        PeakContextChars = result.PeakContextChars,
                        CharsCompacted = result.CharsCompacted,
                        ToolLoopCapReached = result.ToolLoopCapReached
                    };
                    schemaError = StructuredOutputValidator.Validate(result.Output, agent.OutputSchemaType);
                }

                if (schemaError != null)
                {
                    string schemaFailure =
                        $"Agent '{agent.Name}' declares the structured output schema " +
                        $"'{agent.OutputSchemaType.Name}' but its response does not match it" +
                        (repair != null ? " (after one repair turn)" : string.Empty) +
                        $": {schemaError} Return ONLY a single JSON object matching the schema, with no prose, " +
                        "markdown fences or commentary.";
                    return new StepExecutionResult
                    {
                        Output = result.Output,
                        NextStepIndex = context.CurrentStepIndex + 1,
                        NewIterationCount = context.IterationCount,
                        DurationMs = sw.ElapsedMilliseconds,
                        TokensUsed = result.TokensUsed,
                        InputTokens = result.InputTokens,
                        OutputTokens = result.OutputTokens,
                        PrimaryProviderId = result.PrimaryProviderId,
                        PrimaryModelName = result.PrimaryModelName,
                        ToolCalls = result.ToolCalls,
                        Reasoning = result.Reasoning,
                        Status = StepStatus.Failed,
                        ErrorDetails = schemaFailure,
                        FailureClass = FailureClass.PlatformProviderError,
                        FailureCode = AgentOutputSchemaInvalidCode,
                        OutputStorageKey = outputStorageKey
                    };
                }
            }
        }

        // The interleave guard (docs/video-editing.md "Using every clip offered"): one in-step retry
        // with feedback when several clips were offered and the decision used one, then the verdict is
        // recorded on the output — never a step failure. Inert (result untouched) otherwise.
        if (context.Step.AgentDefinition.AgentType == AgentType.VideoStoryEditor)
            result = await VideoStoryEditorInterleaveEnforcer.EnforceAsync(context, agent, stepInput, result, _logger);

        // Shadow-mode Decision gate (System One): observes agreement/confidence for
        // Colorist/MusicSupervisor steps against their real (System Two) decision, entirely for
        // later analysis — never allowed to change anything below. Defense in depth on top of
        // IDecisionGate.RunShadowAsync's own never-throws contract: this call site gets its own
        // try/catch so a bug in the gate itself, or in building its questions here, can never
        // touch the StepExecutionResult this method returns.
        try
        {
            // The escalated Gate path completes the observation rows DecideAsync already wrote with
            // System Two's real answers, so the pre-agent rows gain a truthful `agreed` instead of
            // being thrown away.
            if (gatePreflight.AttachSystemTwoAnswers)
                await AttachGateSystemTwoAnswersAsync(context, result, context.CancellationToken);

            // Gate mode OWNS this step's observation rows. RunShadowAsync passes the config's own
            // mode through, so calling it here under Mode=Gate would write a SECOND row per question
            // for the same step — the exact double-write T-009's Attach contract assumes cannot
            // happen.
            if (!gatePreflight.IsGateMode)
                await RunDecisionShadowAsync(context, agent, result, stepInput);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Agent step {StepOrder}: decision-gate shadow hook failed; ignoring (real result unaffected)",
                context.Step.StepOrder);
        }

        return new StepExecutionResult
        {
            // An Author render's outputResolution report rides on the step output (QA-P0-5).
            Output = context.Step.AgentDefinition.AgentType switch
            {
                AgentType.AuthorAgent => AuthorRenderResolution.Merge(result.Output, result.ToolCalls),
                // The engine's measurement of each rendered overlay, which the compile enforces.
                AgentType.MotionGraphicsPlanner => OverlayLegibilityFindings.Merge(result.Output, context.OverlayLegibility),
                _ => result.Output
            },
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = sw.ElapsedMilliseconds,
            TokensUsed = result.TokensUsed,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            PrimaryProviderId = result.PrimaryProviderId,
            PrimaryModelName = result.PrimaryModelName,
            ToolCalls = result.ToolCalls,
            Reasoning = result.Reasoning,
            Status = StepStatus.Completed,
            OutputStorageKey = outputStorageKey
        };
    }

    // ------------------------------------------------------------------
    // Change request on the script (by scene)
    // ------------------------------------------------------------------

    internal const string ChangeNotAppliedCode = "CHANGE_NOT_APPLIED";

    /// <summary>The model's answer to a script rewrite was not a usable script: the platform's failure (refunded), never retried.</summary>
    internal const string ScriptRewriteInvalidCode = "SCRIPT_REWRITE_INVALID";

    /// <summary>
    /// Applies a change request to a scriptwriter step. Structured overrides are written straight into the previous
    /// script (no model call); a scene named with an instruction only is rewritten by ONE model call, and the answer is
    /// folded back in code so that no scene outside the request can differ from the previous script.
    /// </summary>
    private async Task<StepExecutionResult> ExecuteScriptChangeAsync(
        StepExecutionContext context, IReelBoltAgent agent, StepChangeRequestScope changeScope)
    {
        Stopwatch sw = Stopwatch.StartNew();
        if (changeScope.Malformed)
            return ScriptChangeFailure(context, sw, StepChangeRequestScope.MalformedRefusal, FailureClass.UserInput);
        JsonObject? previous = ScriptScenes.ParseObject(changeScope.PreviousOutputJson);
        IReadOnlyList<ScriptSceneInfo> scenes = ScriptScenes.Read(previous);
        if (previous is null || scenes.Count == 0)
            return ScriptChangeFailure(context, sw, "The script this change starts from is not available, so nothing was changed.", FailureClass.UserInput);

        ScriptChangePlan plan = ScriptChange.Plan(changeScope.Payload, scenes);
        if (plan.Matched == 0)
        {
            return ScriptChangeFailure(context, sw,
                "The change request names no scene of this script, so nothing was changed. Choose a scene (or all scenes) and ask again.",
                FailureClass.UserInput);
        }

        JsonObject current = ScriptChange.ApplyDirect(previous, plan, out List<string> applied);
        if (plan.DirectOnly && applied.Count == 0)
            return ScriptChangeFailure(context, sw, "The new text for the scene is empty once cleaned, so nothing was changed.", FailureClass.UserInput);
        // Texts written by a structured override are screened before any model prompt is built (direct and mixed paths).
        if (applied.Count > 0 && await ScreenAppliedScriptTextAsync(context, current, applied) is { } blocked)
            return ScriptChangeBlocked(context, sw, blocked);
        if (plan.DirectOnly)
        {
            string direct = ScriptChange.WithChangeRequest(current, changeScope.RequestId, plan.RedoneSceneIds, scenes.Count);
            if (!ScriptChange.IsValidScript(direct, scenes.Count))
                return ScriptChangeFailure(context, sw, "The changed script is not a valid script.", FailureClass.UserInput);
            sw.Stop();
            return new StepExecutionResult
            {
                Output = direct,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                DurationMs = sw.ElapsedMilliseconds,
                TokensUsed = 0,
                Status = StepStatus.Completed
            };
        }

        string prompt = BuildScriptChangePrompt(current, plan, changeScope.Payload.Instruction);
        context.RecordResolvedInput(prompt);

        using IDisposable _ = _executionContextAccessor.BeginScope(
            context.Execution.Id,
            context.Execution.ProjectId,
            context.CorrelationId,
            context.Execution.OrganizationId,
            stepResultId: context.StepResultId);
        await using Execution.Decision.CostRouting.CostRoutingScope costRoutingScope = Execution.Decision.CostRouting.CostRoutingScope.Enter(
            _costRouting is null ? null : await _costRouting.BeginStepAsync(context, agent, prompt));

        AgentRunResult result = await agent.RunAsync(prompt, context.Step.AgentDefinitionId, context.CancellationToken);
        sw.Stop();

        StepExecutionResult Done(StepStatus status, string output, string? error = null, string? code = null) => new()
        {
            Output = output,
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = sw.ElapsedMilliseconds,
            TokensUsed = result.TokensUsed,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            PrimaryProviderId = result.PrimaryProviderId,
            PrimaryModelName = result.PrimaryModelName,
            ToolCalls = code == ScriptRewriteInvalidCode ? [] : result.ToolCalls,
            Reasoning = code == ScriptRewriteInvalidCode ? [] : result.Reasoning,
            Status = status,
            ErrorDetails = error,
            FailureClass = code is null ? FailureClass.None : FailureClass.PlatformProviderError,
            FailureCode = code,
            Retryable = code is null
        };

        // A failed model call is reached only if the agent returns one; like an invalid answer it never stores or shows
        // the raw model output.
        if (!result.Success)
            return InvalidRewrite("the model call did not succeed");

        JsonObject? rewritten = ScriptScenes.ParseObject(RobustJsonExtractor.ExtractLastJsonObject(result.Output ?? string.Empty));
        JsonObject? merged = ScriptChange.Enforce(current, rewritten, plan, out string? error);
        if (merged is null)
            return InvalidRewrite(error);

        string output = ScriptChange.WithChangeRequest(merged, changeScope.RequestId, plan.RedoneSceneIds, scenes.Count);
        return ScriptChange.IsValidScript(output, scenes.Count)
            ? Done(StepStatus.Completed, output)
            : InvalidRewrite("the changed script is not a valid script");

        // The model's answer is never stored or shown on this failure (no free model text by steering the
        // instruction into an "invalid" answer): neutral output, short reason, no reasoning or tool calls.
        StepExecutionResult InvalidRewrite(string? reason)
        {
            string shortReason = (reason ?? "the answer was not a usable script").ReplaceLineEndings(" ");
            if (shortReason.Length > 160)
                shortReason = shortReason[..160];
            return Done(StepStatus.Failed, string.Empty, $"{ScriptRewriteInvalidCode}: {shortReason}. Nothing was changed.", ScriptRewriteInvalidCode);
        }
    }

    /// <summary>
    /// Screens the new texts a structured script change wrote (voiceover, on-screen text, visual description of the
    /// applied scenes) through the same gate generated text goes through elsewhere: fail-open when unavailable, a block
    /// fails the step before any output is stored. Returns the user message of a block, else null.
    /// </summary>
    private async Task<string?> ScreenAppliedScriptTextAsync(StepExecutionContext context, JsonObject current, IReadOnlyList<string> applied)
    {
        if (_moderation is null || applied.Count == 0 || !ScriptScenes.TryGet(current, "scenes", out JsonNode? node) || node is not JsonArray array)
            return null;
        List<string> texts = [];
        foreach (ScriptSceneInfo info in ScriptScenes.Read(current).Where(i => applied.Contains(i.Id)))
        {
            if (array[info.Index] is not JsonObject scene)
                continue;
            foreach (string key in new[] { "voiceover", "onScreenText", "visualDescription" })
            {
                JsonNode? value = scene[ScriptScenes.KeyOf(scene, key)];
                if (value is JsonArray lines)
                    texts.AddRange(lines.Select(l => l?.ToString() ?? string.Empty));
                else if (value is not null)
                    texts.Add(value.ToString());
            }
        }

        string all = string.Join("\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (string.IsNullOrWhiteSpace(all))
            return null;
        if (!ModerationTextChunker.TryChunk(all, _moderationMaxChars, out IReadOnlyList<string> chunks))
            return "The new text is too long to check, so nothing was changed.";

        var attribution = new ModerationAttribution(
            context.Execution.OrganizationId, context.Execution.ProjectId, context.Execution.InitiatedByUserId,
            context.Execution.Id, context.StepResultId, ModerationSurface.GeneratedText, ModerationContentKind.Text);
        foreach (string chunk in chunks)
        {
            ModerationDecision decision = await _moderation.ModerateAsync(
                new ModerationInput(chunk, [], ModerationContentKind.Text), attribution, context.CancellationToken);
            if (decision.Action == ModerationAction.Block)
                return string.IsNullOrWhiteSpace(decision.UserMessage) ? "This text cannot be used." : decision.UserMessage;
            if (!decision.Available)
                _logger.LogWarning("Content moderation was unavailable for a script change at step {StepOrder}; proceeding (fails open)", context.Step.StepOrder);
        }

        return null;
    }

    private static StepExecutionResult ScriptChangeBlocked(StepExecutionContext context, Stopwatch sw, string message) =>
        ScriptChangeFailure(context, sw, message, FailureClass.UserInput, "CONTENT_BLOCKED");

    private static StepExecutionResult ScriptChangeFailure(StepExecutionContext context, Stopwatch sw, string message, FailureClass failureClass, string code = ChangeNotAppliedCode)
    {
        sw.Stop();
        return new StepExecutionResult
        {
            Output = context.AccumulatedOutput,
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = sw.ElapsedMilliseconds,
            TokensUsed = 0,
            Status = StepStatus.Failed,
            ErrorDetails = $"{code}: {message}",
            FailureClass = failureClass,
            FailureCode = code,
            Retryable = false
        };
    }

    /// <summary>The one prompt of a script rewrite: the script so far, the scenes to change and the owner's instruction.</summary>
    internal static string BuildScriptChangePrompt(JsonObject script, ScriptChangePlan plan, string? instruction)
    {
        JsonObject copy = (JsonObject)script.DeepClone();
        copy.Remove("changeRequest");
        string words = GeneratedPromptSanitizer.StripControlCharacters(instruction).Trim();
        if (words.Length > StepChangeCapabilities.MaxInstructionLength)
            words = words[..StepChangeCapabilities.MaxInstructionLength];
        int count = ScriptScenes.Read(copy).Count;
        return
            "This is a revision of an existing video script, not a new script.\n" +
            $"Change ONLY these scenes: {string.Join(", ", plan.ModelSceneIds)}.\n" +
            $"What to change: {words}\n\n" +
            "Rules:\n" +
            "- Reply with the FULL script as ONE JSON object in exactly the shape of the script below.\n" +
            $"- Keep exactly {count} scenes, in the same order, with the same sceneId, startTime and duration.\n" +
            "- Every scene not listed above must be returned exactly as it is below, word for word.\n" +
            "- Only the voiceover, onScreenText and visualDescription of the listed scenes may differ.\n\n" +
            "Script so far:\n" + copy.ToJsonString();
    }

    /// <summary>
    /// Builds the fixed enum-word question set for a Colorist/MusicSupervisor step and hands it to
    /// <see cref="IDecisionGate.RunShadowAsync"/>. A no-op for every other agent type, for an agent
    /// whose output didn't parse to the expected DTO, and whenever the resolved config's Mode is
    /// <see cref="DecisionGateMode.Off"/> (the <see cref="IDecisionGate"/> implementation's own
    /// no-op path, reached here with zero extra work when nothing is configured).
    /// </summary>
    private async Task RunDecisionShadowAsync(
        StepExecutionContext context, IReelBoltAgent agent, AgentRunResult result, string stepInput)
    {
        AgentType agentType = context.Step.AgentDefinition.AgentType;

        // ConfigNameFor is the single AgentType -> Agents:<name> mapping, shared with
        // StepCachePolicy: null means "not a gate site", which is what this early return is.
        if (DecisionGateAgentConfig.ConfigNameFor(agentType) is not string agentConfigName)
            return;

        string? json = RobustJsonExtractor.ExtractLastJsonObject(result.Output ?? string.Empty);
        if (json is null)
            return;

        List<DecisionGateQuestion> questions = agentType == AgentType.Colorist
            ? BuildColoristQuestions(json)
            : BuildMusicSupervisorQuestions(json);

        if (questions.Count == 0)
            return;

        // One call carries every question for this step, so it must be configured at the agent
        // level: a site override resolved for one question and applied here would silently tighten
        // or loosen its siblings too (see ResolveDecisionGateConfig).
        DecisionGateConfig config = ResolveDecisionGateConfig(_configuration, agentConfigName, logger: _logger);

        var state = new DecisionState(stepInput);
        await _decisionGate.RunShadowAsync(
            context.Execution.Id, context.Step.StepOrder, agentType, state, questions, config, context.CancellationToken);
    }

    /// <summary>
    /// Resolves the <see cref="DecisionGateConfig"/> for ONE decision site (a single question).
    /// <paramref name="questionName"/> is the question's <see cref="DecisionGateQuestion.Name"/>
    /// exactly as it is posed to the gate (<c>"Look"</c>, <c>"Strength"</c>, …); pass <c>null</c>
    /// for the agent-level config with no site layer at all, which is what a call carrying several
    /// questions must use.
    ///
    /// <para>
    /// <c>AcceptAt</c>/<c>MinMargin</c> resolve in this order, and a missing key is never an error:
    /// <list type="number">
    /// <item><c>Agents:{agent}:DecisionGate:Sites:{questionName}:AcceptAt|MinMargin</c></item>
    /// <item><c>Agents:{agent}:DecisionGate:AcceptAt|MinMargin</c> — the pre-existing key, unchanged</item>
    /// <item><see cref="DecisionGateConfig"/>'s own defaults (0.85 / 0.25)</item>
    /// </list>
    /// Those defaults are the phase-1 <b>fallback, not a tuned value</b>: the local install has no
    /// <c>Decision</c> provider and zero calibration rows, so nothing here could have been
    /// calibrated (see <c>docs/decision-models.md</c>).
    /// </para>
    ///
    /// <para>
    /// The site layer is therefore resolved <b>per question</b>, by the caller, one question at a
    /// time — that is the whole point: one site's tighter threshold must never leak onto its
    /// siblings. <c>Mode</c> and <c>ProviderId</c> stay agent-level; phase 2 adds no per-site mode
    /// and no per-site provider.
    /// </para>
    /// </summary>
    internal static DecisionGateConfig ResolveDecisionGateConfig(
        IConfiguration configuration, string agentConfigName, string? questionName = null,
        ILogger? logger = null)
    {
        DecisionGateConfig defaults = new();
        string agentPrefix = $"Agents:{agentConfigName}:DecisionGate";

        double? SiteValue(string threshold) => questionName is null
            ? null
            : configuration.GetValue<double?>($"{agentPrefix}:Sites:{questionName}:{threshold}");

        return new DecisionGateConfig(
            // Through the one non-throwing reader — never Configuration.GetValue<DecisionGateMode?>
            // directly. That call throws InvalidOperationException on a non-empty unparseable
            // string, and RunPreAgentGateAsync resolves the mode BEFORE its own try block opens, so
            // the throw left ExecuteAsync and WorkflowExecutorService (which excludes
            // InvalidOperationException from retry) marked the step Failed and aborted the run.
            // StepCachePolicy reads the same key through the same helper, so the two cannot drift.
            Mode: DecisionGateAgentConfig.ReadMode(configuration, agentConfigName, logger),
            AcceptAt: SiteValue("AcceptAt")
                ?? configuration.GetValue<double?>($"{agentPrefix}:AcceptAt")
                ?? defaults.AcceptAt,
            MinMargin: SiteValue("MinMargin")
                ?? configuration.GetValue<double?>($"{agentPrefix}:MinMargin")
                ?? defaults.MinMargin,
            ProviderId: configuration.GetValue<Guid?>($"{agentPrefix}:ProviderId") ?? defaults.ProviderId);
    }

    // ---------------------------------------------------------------------
    // Gate mode — System One decides INSTEAD OF the agent (Colorist/MusicSupervisor only).
    //
    // The order inverts here on purpose: the agent is the fallback, not the default. Off and Shadow
    // never enter this region at all, so their flow stays byte-identical to phase 1. Everything in
    // it is soft-failing: any error, and the step behaves exactly as it does with no gate.
    // ---------------------------------------------------------------------

    /// <summary>What a pre-agent Gate attempt handed back to <see cref="ExecuteAsync"/>.</summary>
    /// <param name="AcceptedResult">
    /// Non-null when the decision model's answers cleared every threshold: the caller returns this
    /// instead of running the agent at all.
    /// </param>
    /// <param name="IsGateMode">
    /// Whether the resolved agent-level mode was <see cref="DecisionGateMode.Gate"/>. Gate owns the
    /// step's observation rows, so the post-agent Shadow hook is skipped when this is true.
    /// </param>
    /// <param name="PromptInput">
    /// The agent input to use when it differs from the one already built — the escalated path's
    /// input plus System One's prior block. Null means "unchanged".
    /// </param>
    /// <param name="AttachSystemTwoAnswers">
    /// Whether pre-agent observation rows exist for System Two's answers to complete. False on the
    /// accepted path (no agent answer exists) and whenever no decision was reached (no rows).
    /// </param>
    private sealed record GatePreflight(
        StepExecutionResult? AcceptedResult,
        bool IsGateMode,
        string? PromptInput,
        bool AttachSystemTwoAnswers)
    {
        /// <summary>The step is not a Gate site at all — no probe, no outbound call, nothing.</summary>
        public static GatePreflight NotGateMode { get; } = new(null, false, null, false);
    }

    private async Task<GatePreflight> RunPreAgentGateAsync(
        StepExecutionContext context, string stepInput, CancellationToken ct)
    {
        AgentType agentType = context.Step.AgentDefinition.AgentType;

        // Same single mapping as the Shadow hook and as StepCachePolicy — null means "not a gate
        // site", so no probe, no outbound call, nothing.
        if (DecisionGateAgentConfig.ConfigNameFor(agentType) is not string agentConfigName)
            return GatePreflight.NotGateMode;

        // Mode is agent-level by construction — phase 2 adds no per-site mode (see
        // ResolveDecisionGateConfig) — so this one resolution decides whether the step gates at all.
        DecisionGateConfig agentConfig = ResolveDecisionGateConfig(_configuration, agentConfigName, logger: _logger);
        if (agentConfig.Mode != DecisionGateMode.Gate)
            return GatePreflight.NotGateMode;

        try
        {
            string descriptorState = agentType == AgentType.Colorist
                ? DecisionDescriptorView.ForColorist(context)
                : DecisionDescriptorView.ForMusicSupervisor(context);

            // No descriptor is not an empty question — it is no question at all. Asking the model to
            // choose between options from no evidence produces a confidently-arbitrary answer that
            // the accept rule cannot detect, so the gate escalates and the agent runs exactly as it
            // would with nothing configured.
            if (string.IsNullOrWhiteSpace(descriptorState))
            {
                _logger.LogInformation(
                    "Agent step {StepOrder}: Gate mode has no bounded descriptor state for {AgentName}; escalating to the agent without a decision call",
                    context.Step.StepOrder,
                    agentConfigName);
                return new GatePreflight(null, IsGateMode: true, PromptInput: null, AttachSystemTwoAnswers: false);
            }

            // The SystemTwoAnswer-less variant: no agent has run yet, so there is no counterfactual.
            List<DecisionGateQuestion> questions = agentType == AgentType.Colorist
                ? BuildColoristQuestions(agentJson: null)
                : BuildMusicSupervisorQuestions(agentJson: null);

            if (questions.Count == 0)
                return new GatePreflight(null, IsGateMode: true, PromptInput: null, AttachSystemTwoAnswers: false);

            var state = new DecisionState(descriptorState);

            // ONE DecideAsync PER RESOLVED CONFIG, not one for the whole step. DecisionGateRequest
            // carries a single config, so a per-site AcceptAt/MinMargin could not otherwise take
            // effect — the constraint T-007 reported. With no site key set (the normal case: nothing
            // ships with Gate enabled) every question resolves to the same config, and this is
            // exactly one outbound call.
            List<IGrouping<DecisionGateConfig, DecisionGateQuestion>> groups = questions
                .GroupBy(question => ResolveDecisionGateConfig(_configuration, agentConfigName, question.Name, _logger))
                .ToList();

            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            var topTwo = new Dictionary<string, (double Top, double Second)>(StringComparer.Ordinal);
            bool anyDecision = false;
            bool allAccepted = true;
            int decisionInputTokens = 0;
            int decisionOutputTokens = 0;

            Stopwatch sw = Stopwatch.StartNew();

            foreach (IGrouping<DecisionGateConfig, DecisionGateQuestion> group in groups)
            {
                DecisionGateOutcome outcome = await _decisionGate.DecideAsync(
                    new DecisionGateRequest(
                        context.Execution.Id,
                        context.Step.StepOrder,
                        agentType,
                        Site: $"{agentConfigName}.step",
                        state,
                        group.ToList(),
                        group.Key),
                    ct);

                // Step-level all-or-nothing ACROSS groups as well as within one (decisions.md §4): a
                // group that reached no decision, or whose own thresholds failed, escalates the whole
                // step regardless of what its siblings did.
                anyDecision |= outcome.HasDecision;
                allAccepted &= outcome.Accepted;

                if (outcome.Result is not null)
                {
                    decisionInputTokens += outcome.Result.InputTokens;
                    decisionOutputTokens += outcome.Result.OutputTokens;

                    foreach (KeyValuePair<string, DecisionAnswer> answer in outcome.Result.Answers)
                        answers[answer.Key] = answer.Value;
                }

                foreach (KeyValuePair<string, (double Top, double Second)> pair in outcome.TopTwoProbabilities)
                    topTwo[pair.Key] = pair.Value;
            }

            sw.Stop();

            if (anyDecision && allAccepted && EveryAnswerIsUsable(questions, answers))
            {
                _logger.LogInformation(
                    "Agent step {StepOrder}: {AgentName} accepted from the System One decision model; no agent run ({QuestionCount} questions over {GroupCount} call(s))",
                    context.Step.StepOrder,
                    agentConfigName,
                    questions.Count,
                    groups.Count);

                // What this step actually consumed is the bounded descriptor state plus the
                // resolved thresholds — not an agent prompt, because no agent ran. Recording that
                // as the resolved input follows the same "compact descriptor, not the raw upstream
                // blob" convention RecordResolvedInput exists for (an Extract step does the same).
                //
                // It is informational, NOT a cache guard. RecordResolvedInput feeds
                // WorkflowStepResult.InputJson; it does not feed the step cache key, which
                // WorkflowExecutorService builds from the pre-execution prompt BEFORE this step
                // runs. The gate's Mode/AcceptAt/MinMargin/ProviderId live in appsettings and in no
                // step config blob, so nothing recorded here could keep a gate-produced plan out of
                // a run whose gate is OFF. What actually does that is StepCachePolicy: an Agent step
                // whose resolved decision mode is not Off is not cacheable at all, and its own
                // comment states the reasoning.
                context.RecordResolvedInput(RenderGateResolvedInput(agentConfigName, descriptorState, groups));

                return new GatePreflight(
                    BuildAcceptedResult(context, agentType, answers, sw.ElapsedMilliseconds, decisionInputTokens, decisionOutputTokens),
                    IsGateMode: true,
                    PromptInput: null,
                    AttachSystemTwoAnswers: false);
            }

            _logger.LogInformation(
                "Agent step {StepOrder}: {AgentName} escalated to the agent ({AnsweredCount}/{QuestionCount} questions answered, allAccepted={AllAccepted})",
                context.Step.StepOrder,
                agentConfigName,
                answers.Count,
                questions.Count,
                allAccepted);

            return new GatePreflight(
                AcceptedResult: null,
                IsGateMode: true,
                PromptInput: AppendSystemOnePrior(stepInput, questions, answers, topTwo),
                AttachSystemTwoAnswers: anyDecision);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's own cancellation, never the gate's deadline — DecideAsync converts its own
            // timeout into a NoDecision outcome (D-004). Observed and rethrown, not turned into "no
            // decision".
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Agent step {StepOrder}: pre-agent decision gate failed for {AgentName}; escalating to the agent (real behaviour unchanged)",
                context.Step.StepOrder,
                agentConfigName);

            return new GatePreflight(null, IsGateMode: true, PromptInput: null, AttachSystemTwoAnswers: false);
        }
    }

    /// <summary>
    /// Completes the pre-agent Gate rows once the agent has actually run on the escalated path, using
    /// the same extractor and the same question builder the Shadow path uses — so an escalated Gate
    /// run records exactly the comparison a Shadow run would have, and the two cannot drift.
    /// </summary>
    private async Task AttachGateSystemTwoAnswersAsync(
        StepExecutionContext context, AgentRunResult result, CancellationToken ct)
    {
        string? json = RobustJsonExtractor.ExtractLastJsonObject(result.Output ?? string.Empty);
        if (json is null)
            return;

        List<DecisionGateQuestion> answered = context.Step.AgentDefinition.AgentType == AgentType.Colorist
            ? BuildColoristQuestions(json)
            : BuildMusicSupervisorQuestions(json);

        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DecisionGateQuestion question in answered)
        {
            if (question.SystemTwoAnswer is not null)
                answers[question.Name] = question.SystemTwoAnswer;
        }

        if (answers.Count == 0)
            return;

        await _decisionGate.AttachSystemTwoAnswersAsync(
            context.Execution.Id, context.Step.StepOrder, answers, result.TokensUsed, ct);
    }

    /// <summary>
    /// Synthesizes the ordinary <see cref="StepStatus.Completed"/> result a Gate-accepted step
    /// returns in place of an agent run.
    ///
    /// <para>
    /// Serialized with <see cref="DecisionOutputJsonOptions"/>, the same options every other consumer
    /// of these DTOs deserializes with, so the compile step cannot tell a gate-accepted plan from an
    /// agent's own. The prose fields say plainly that no agent ran: they are machine-generated text
    /// standing where a reasoned explanation would be, and the Global Constraints require that to be
    /// visible rather than disguised.
    /// </para>
    /// </summary>
    private static StepExecutionResult BuildAcceptedResult(
        StepExecutionContext context,
        AgentType agentType,
        IReadOnlyDictionary<string, DecisionAnswer> answers,
        long durationMs,
        int inputTokens,
        int outputTokens)
    {
        const string acceptedReason =
            "Machine-generated by the System One decision model; no agent ran for this step.";
        const string acceptedRationale =
            "Machine-generated: every field above is the decision model's own answer, accepted because "
            + "every question cleared its configured AcceptAt and MinMargin thresholds. There is no "
            + "agent prose behind this plan.";

        string output = agentType == AgentType.Colorist
            ? JsonSerializer.Serialize(
                new ColorGradePlanOutput
                {
                    Look = Choice(answers, ColoristLookQuestion),
                    Strength = Choice(answers, ColoristStrengthQuestion),
                    ShadowTone = Choice(answers, ColoristShadowToneQuestion),
                    HighlightTone = Choice(answers, ColoristHighlightToneQuestion),
                    Reason = acceptedReason,
                    PlanRationale = acceptedRationale,
                },
                DecisionOutputJsonOptions)
            : JsonSerializer.Serialize(
                new MusicPlanOutput
                {
                    // Deliberately NULL, never a guessed candidate. TrackId is a dynamic offered-id
                    // choice (which uploaded track, if any, from this project's own list) that the
                    // question set excludes by construction, so there is no answer to synthesize it
                    // from — and picking the first offered candidate would be a real content bug, not
                    // a harmless default. The compile step's existing "no track" path handles a null
                    // TrackId (VideoCompileStepExecutor guards with IsNullOrWhiteSpace).
                    TrackId = null!,
                    Intensity = Choice(answers, MusicIntensityQuestion),
                    Ducking = Choice(answers, MusicDuckingQuestion),
                    Fit = Choice(answers, MusicFitQuestion),
                    Reason = acceptedReason,
                    PlanRationale = acceptedRationale
                        + " TrackId is null because the question set deliberately does not ask for one.",
                },
                DecisionOutputJsonOptions);

        return new StepExecutionResult
        {
            Output = output,
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = durationMs,
            TokensUsed = inputTokens + outputTokens,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Status = StepStatus.Completed,
        };
    }

    /// <summary>
    /// A deterministic rendering of everything a gate-accepted step consumed: the bounded descriptor
    /// state, and the resolved per-group config (which questions shared which thresholds and
    /// provider, and what those thresholds were). Deterministic ordering, so two identical runs
    /// render identically and a changed threshold changes the resolved input.
    /// </summary>
    private static string RenderGateResolvedInput(
        string agentConfigName,
        string descriptorState,
        IReadOnlyList<IGrouping<DecisionGateConfig, DecisionGateQuestion>> groups)
    {
        StringBuilder builder = new($"{agentConfigName}.decision-gate\n{descriptorState}");

        foreach (IGrouping<DecisionGateConfig, DecisionGateQuestion> group in groups)
        {
            string names = string.Join(",", group.Select(q => q.Name).OrderBy(name => name, StringComparer.Ordinal));
            builder.Append($"\n{names} acceptAt={group.Key.AcceptAt:0.####} minMargin={group.Key.MinMargin:0.####} provider={group.Key.ProviderId}");
        }

        return builder.ToString();
    }

    private static string Choice(IReadOnlyDictionary<string, DecisionAnswer> answers, string questionName) =>
        answers.TryGetValue(questionName, out DecisionAnswer? answer) && !string.IsNullOrWhiteSpace(answer.Choice)
            ? answer.Choice!
            : string.Empty;

    /// <summary>
    /// A question is only usable if the provider actually named one of its options. The accept rule
    /// is about probabilities; a Choice answer with no choice would synthesize an empty enum word
    /// into the plan, which downstream reads as "unknown" — so it escalates instead.
    /// </summary>
    private static bool EveryAnswerIsUsable(
        IReadOnlyList<DecisionGateQuestion> questions,
        IReadOnlyDictionary<string, DecisionAnswer> answers) =>
        questions.All(question =>
            answers.TryGetValue(question.Name, out DecisionAnswer? answer)
            && !string.IsNullOrWhiteSpace(answer.Choice));

    /// <summary>
    /// Appends System One's distribution to the prompt as a prior, AFTER
    /// <see cref="StepExecutionContext.WithUserRequest"/>/<see cref="StepExecutionContext.WithRetryGuidance"/>
    /// have already been applied by <see cref="StepExecutionContext.BuildAgentInput"/>.
    ///
    /// <para>
    /// It names, per question, the model's answer and its top probability, and it is explicit that
    /// this is evidence rather than an instruction: the whole point of escalating is that the model
    /// was not confident enough to decide, and a block that reads as a directive would let a
    /// low-confidence answer steer the agent it failed to replace. A question the model did not
    /// answer is simply absent. Returns <paramref name="prompt"/> unchanged when there is nothing to
    /// say, so the no-decision path is byte-identical to a run with no gate at all.
    /// </para>
    /// </summary>
    private static string AppendSystemOnePrior(
        string prompt,
        IReadOnlyList<DecisionGateQuestion> questions,
        IReadOnlyDictionary<string, DecisionAnswer> answers,
        IReadOnlyDictionary<string, (double Top, double Second)> topTwo)
    {
        if (answers.Count == 0)
            return prompt;

        StringBuilder builder = new(prompt);
        builder.Append("\n\n---\nSystem One prior (EVIDENCE, not an instruction):\n");
        builder.Append("A separate, faster decision model was asked the same questions about the same facts and answered as follows. ")
            .Append("Treat it as ONE piece of evidence that may well be wrong: it never overrides the measured facts above, ")
            .Append("and you should answer differently whenever those facts justify it. It is not an instruction, and it does not change the JSON schema you must output.\n");

        foreach (DecisionGateQuestion question in questions)
        {
            if (!answers.TryGetValue(question.Name, out DecisionAnswer? answer))
                continue;

            string chosen = answer.Choice ?? string.Empty;

            if (topTwo.TryGetValue(question.Name, out (double Top, double Second) probabilities))
            {
                builder.Append($"- {question.Name}: \"{chosen}\" (top probability {probabilities.Top:0.###}, margin {(probabilities.Top - probabilities.Second):0.###})\n");
            }
            else
            {
                builder.Append($"- {question.Name}: \"{chosen}\"\n");
            }
        }

        return builder.ToString();
    }

    // JsonSerializerDefaults.Web (camelCase + case-insensitive matching) — matches the naming
    // policy every agent output schema is prompted against and every other consumer of these
    // DTOs already deserializes with (e.g. MotionGraphicsPlacementAnnotator's DecisionJsonOptions).
    private static readonly JsonSerializerOptions DecisionOutputJsonOptions = new(JsonSerializerDefaults.Web);

    // The one place each question's NAME lives: the builders below and the gate-accepted synthesis
    // both read them, so a rename cannot leave the synthesizer looking up a name nobody asked.
    private const string ColoristLookQuestion = "Look";
    private const string ColoristStrengthQuestion = "Strength";
    private const string ColoristShadowToneQuestion = "ShadowTone";
    private const string ColoristHighlightToneQuestion = "HighlightTone";
    private const string MusicIntensityQuestion = "Intensity";
    private const string MusicDuckingQuestion = "Ducking";
    private const string MusicFitQuestion = "Fit";

    /// <summary>
    /// The fixed enum-word question set for a Colorist step.
    ///
    /// <para>
    /// <paramref name="agentJson"/> is the agent's own structured output, or <b>null</b> on the
    /// pre-agent Gate path, where the agent has not run yet and there is no counterfactual. One
    /// builder with a nullable input rather than a second copy: the question set itself (names,
    /// sites, kinds, instructions, option lists) is identical on both paths and must never drift —
    /// only <see cref="DecisionGateQuestion.SystemTwoAnswer"/> differs.
    /// </para>
    /// </summary>
    private static List<DecisionGateQuestion> BuildColoristQuestions(string? agentJson)
    {
        ColorGradePlanOutput? plan = null;
        if (agentJson is not null)
        {
            plan = JsonSerializer.Deserialize<ColorGradePlanOutput>(agentJson, DecisionOutputJsonOptions);
            if (plan is null)
                return [];
        }

        return
        [
            new DecisionGateQuestion(
                "Colorist.Look", ColoristLookQuestion, DecisionKind.Choice,
                "Whole-program colour-grade look for the compiled edit.",
                ["None", "Warm", "Cool", "Filmic", "Vibrant", "Muted", "Mono"], plan?.Look),
            new DecisionGateQuestion(
                "Colorist.Strength", ColoristStrengthQuestion, DecisionKind.Choice,
                "Strength of the chosen grade.",
                ["Subtle", "Normal", "Strong"], plan?.Strength),
            new DecisionGateQuestion(
                "Colorist.ShadowTone", ColoristShadowToneQuestion, DecisionKind.Choice,
                "Shadow tone adjustment for the grade.",
                ["Neutral", "Lifted", "Deepened"], plan?.ShadowTone),
            new DecisionGateQuestion(
                "Colorist.HighlightTone", ColoristHighlightToneQuestion, DecisionKind.Choice,
                "Highlight tone adjustment for the grade.",
                ["Neutral", "Softened", "Brightened"], plan?.HighlightTone),
        ];
    }

    /// <summary>
    /// The fixed enum-word question set for a MusicSupervisor step. See
    /// <see cref="BuildColoristQuestions"/> for why <paramref name="agentJson"/> is nullable.
    /// </summary>
    private static List<DecisionGateQuestion> BuildMusicSupervisorQuestions(string? agentJson)
    {
        MusicPlanOutput? plan = null;
        if (agentJson is not null)
        {
            plan = JsonSerializer.Deserialize<MusicPlanOutput>(agentJson, DecisionOutputJsonOptions);
            if (plan is null)
                return [];
        }

        // TrackId is deliberately excluded: it's a dynamic offered-id choice (which uploaded
        // track, if any, from this project's own candidate list) rather than a fixed enum word,
        // so it doesn't fit the Decision gate's fixed-option-set model the way Intensity/
        // Ducking/Fit do. A gate-accepted music plan therefore carries a null TrackId rather than
        // a guessed candidate — see BuildAcceptedResult.
        return
        [
            new DecisionGateQuestion(
                "MusicSupervisor.Intensity", MusicIntensityQuestion, DecisionKind.Choice,
                "How prominent the music bed should sit relative to dialogue.",
                ["Quiet", "Balanced", "Feature"], plan?.Intensity),
            new DecisionGateQuestion(
                "MusicSupervisor.Ducking", MusicDuckingQuestion, DecisionKind.Choice,
                "How much the bed should duck down under dialogue.",
                ["Off", "Light", "Normal", "Heavy"], plan?.Ducking),
            new DecisionGateQuestion(
                "MusicSupervisor.Fit", MusicFitQuestion, DecisionKind.Choice,
                "Whether the track loops to fill the edit or plays once.",
                ["LoopToFit", "PlayOnce"], plan?.Fit),
        ];
    }

    /// <summary>FailureCode for a structured answer that still violates its schema after the repair turn.</summary>
    internal const string AgentOutputSchemaInvalidCode = "AGENT_OUTPUT_SCHEMA_INVALID";

    private const int RepairPriorAnswerMaxChars = 30_000;

    private static string BuildRepairPrompt(string stepInput, string? invalidOutput, string error)
    {
        string prior = invalidOutput ?? string.Empty;
        if (prior.Length > RepairPriorAnswerMaxChars)
            prior = prior[..RepairPriorAnswerMaxChars] + "\n[truncated]";
        return stepInput +
            "\n\n[OUTPUT REPAIR]\nYour previous answer did not match the required JSON schema.\n" +
            "Previous answer:\n" + prior +
            "\n\nSchema error: " + error +
            "\n\nReturn ONLY the corrected JSON object. No tools, no prose, no markdown fences.";
    }

    /// <summary>The failure of an Author step that ended, even after the reminder, without rendering the film.</summary>
    internal const string AuthorNeverRenderedMessage =
        "Author step did not produce a rendered media artifact: the agent never rendered the final video " +
        "(it ended without a successful RenderVideoAndUploadToStorage call, even after being reminded once). Missing outputStorageKey.";

    /// <summary>Most characters of the agent's own last answer quoted back in the reminder.</summary>
    internal const int FinalRenderReminderAnswerChars = 2_000;

    /// <summary>
    /// Whether this Author run gets the one final-render reminder: it finished normally, no full render
    /// succeeded in this attempt, and no render of it hit the failed-render cap (that refusal is final).
    /// </summary>
    internal static bool NeedsFinalRenderReminder(StepExecutionContext context, AgentRunResult result, string? outputStorageKey) =>
        context.Step.AgentDefinition.AgentType == AgentType.AuthorAgent
        && result.Success
        && string.IsNullOrWhiteSpace(outputStorageKey)
        && !context.RenderFailures.TerminalRefused;

    /// <summary>The reminder turn's prompt: the instruction first, then the agent's last answer, then the step's input for reference.</summary>
    internal static string BuildFinalRenderReminderPrompt(string stepInput, string? previousAnswer, string? lastRenderRefusal = null)
    {
        string answer = (previousAnswer ?? string.Empty).Trim();
        if (answer.Length > FinalRenderReminderAnswerChars)
            answer = answer[..FinalRenderReminderAnswerChars] + " ...[truncated]";

        string refusal = string.IsNullOrWhiteSpace(lastRenderRefusal)
            ? string.Empty
            : Services.Compositions.AgentFixableRenderFailure.Classify(lastRenderRefusal) == Services.Compositions.AgentFixableRenderFailureKind.CodeError
                ? "Your last RenderVideoAndUploadToStorage call crashed on an error in your composition's code: " +
                  QuoteRenderRefusal(lastRenderRefusal) + " Fix that code in src/ (the name, import or prop it quotes), check the scene " +
                  "that failed with RenderPreviewStills, then render.\n"
                : "Your last RenderVideoAndUploadToStorage call was refused before it rendered anything: " +
                  QuoteRenderRefusal(lastRenderRefusal) + " Make exactly the change it names, then render.\n";

        return
            "[FINAL RENDER MISSING]\n" +
            "You ended your previous turn without rendering the final video: RenderVideoAndUploadToStorage was never called " +
            "successfully, so this step has no film and will fail. Your composition is still in the sandbox exactly as you left it. " +
            "Call RenderVideoAndUploadToStorage now for the main composition. Before that, fix only what blocks it: if a tool said " +
            "files are missing (MISSING_STATIC_FILE), stage them with StageProjectFile using the exact id or file name " +
            "ListProjectFiles shows; if a render was refused before it ran (HTML5_VIDEO_IN_RENDER, NETWORK_FONT), make exactly " +
            "the code change the refusal names; if a render crashed on an error in your code (a ReferenceError, an import that " +
            "does not resolve, a bad prop), fix that code and check it with RenderPreviewStills. Do not start the composition again, " +
            "and do not make other changes.\n" +
            refusal +
            "[END FINAL RENDER MISSING]\n\n" +
            "Your previous final answer was:\n" + (answer.Length == 0 ? "(empty)" : answer) + "\n\n" +
            "For reference, the step's original input follows.\n\n" + stepInput;
    }

    /// <summary>Most characters of a render refusal quoted back in the reminder.</summary>
    internal const int FinalRenderReminderRefusalChars = 2_000;

    /// <summary>
    /// The error of the LAST <c>RenderVideoAndUploadToStorage</c> call of the run when the agent can fix it in its own code
    /// (<see cref="Services.Compositions.AgentFixableRenderFailure"/>: a pre-render refusal, or a crash on the composition's
    /// code such as <c>ReferenceError</c>), as the render tool's error text with Remotion's own error line; else null. The
    /// reminder is a fresh turn, so without this the agent would not see why its render failed until it rendered again.
    /// </summary>
    internal static string? LastFixableRenderRefusal(IReadOnlyList<AgentToolCallTrace> toolCalls)
    {
        AgentToolCallTrace? last = toolCalls.LastOrDefault(t => t.ToolName == "RenderVideoAndUploadToStorage");
        if (last?.Result is not { Length: > 0 } text)
            return null;

        bool hasError = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            hasError = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            // A trace cut short is not JSON: no refusal quoted (the reminder still names the codes).
        }

        if (!hasError)
            return null;

        // The same text a revision's platform render reports: the error plus Remotion's own last error line.
        string error = ReactRemotionSandboxTools.RenderErrorText(text);
        return Services.Compositions.AgentFixableRenderFailure.IsFixable(error) ? error : null;
    }

    private static string QuoteRenderRefusal(string refusal)
    {
        string clean = new(refusal.Select(c => c is '\n' or '\t' ? ' ' : c).Where(c => !char.IsControl(c)).ToArray());
        clean = clean.Trim();
        return clean.Length <= FinalRenderReminderRefusalChars ? clean : clean[..FinalRenderReminderRefusalChars] + " ...[truncated]";
    }

    /// <summary>
    /// Runs the one reminder turn (with tools, same agent and provider) and returns the combined result: the
    /// reminder's answer and status, the usage and traces of both runs. A reminder that throws (other than the
    /// run being cancelled) leaves the first result as it was, so the step fails the way it always did.
    /// </summary>
    private async Task<AgentRunResult> RunFinalRenderReminderAsync(
        StepExecutionContext context, IReelBoltAgent agent, string stepInput, AgentRunResult first)
    {
        _logger.LogWarning(
            "Agent step {StepOrder}: {AgentName} ended without rendering the final video; giving it one more turn to render",
            context.Step.StepOrder, agent.Name);

        AgentRunResult reminder;
        try
        {
            reminder = await agent.RunAsync(
                BuildFinalRenderReminderPrompt(stepInput, first.Output, LastFixableRenderRefusal(first.ToolCalls)),
                context.Step.AgentDefinitionId,
                context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Agent step {StepOrder}: the final-render reminder turn of {AgentName} failed; keeping the first answer",
                context.Step.StepOrder, agent.Name);
            return first;
        }

        return new AgentRunResult
        {
            Output = reminder.Output,
            Success = reminder.Success,
            FailureReason = reminder.FailureReason,
            TokensUsed = first.TokensUsed + reminder.TokensUsed,
            InputTokens = (first.InputTokens ?? 0) + (reminder.InputTokens ?? 0),
            OutputTokens = (first.OutputTokens ?? 0) + (reminder.OutputTokens ?? 0),
            PrimaryProviderId = first.PrimaryProviderId ?? reminder.PrimaryProviderId,
            PrimaryModelName = first.PrimaryModelName ?? reminder.PrimaryModelName,
            ToolCalls = [.. first.ToolCalls, .. reminder.ToolCalls],
            Reasoning = [.. first.Reasoning, .. reminder.Reasoning],
            ModelCalls = (first.ModelCalls ?? 0) + (reminder.ModelCalls ?? 0),
            PeakContextChars = MaxOrNull(first.PeakContextChars, reminder.PeakContextChars),
            CharsCompacted = MaxOrNull(first.CharsCompacted, reminder.CharsCompacted),
            ToolLoopCapReached = first.ToolLoopCapReached || reminder.ToolLoopCapReached
        };

        static int? MaxOrNull(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    }

    private static bool TryDetectAuthorOutputFailure(string? output, out string failureReason)
    {
        failureReason = string.Empty;

        if (string.IsNullOrWhiteSpace(output))
            return false;

        if (TryDetectJsonFailure(output, out failureReason))
            return true;

        if (ContainsTypecheckFailureSignature(output))
        {
            failureReason = "Author step output indicates tool/typecheck errors.";
            return true;
        }

        return false;
    }

    private static string CreatePreview(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength];
    }

    private static bool TryDetectJsonFailure(string output, out string failureReason)
    {
        failureReason = string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(output);

            if (TryFindBooleanLikeProperty(document.RootElement, "hasErrors", true))
            {
                failureReason = "Author step output indicates tool/typecheck errors (hasErrors=true).";
                return true;
            }

            if (TryFindBooleanLikeProperty(document.RootElement, "success", false))
            {
                failureReason = "Author step output indicates failure (success=false).";
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindBooleanLikeProperty(JsonElement element, string propertyName, bool expectedValue)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.NameEquals(propertyName) || string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetBooleanLikeValue(property.Value, out bool parsedValue) && parsedValue == expectedValue)
                            return true;
                    }

                    if (TryFindBooleanLikeProperty(property.Value, propertyName, expectedValue))
                        return true;
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (TryFindBooleanLikeProperty(item, propertyName, expectedValue))
                        return true;
                }

                break;
        }

        return false;
    }

    private static bool TryGetBooleanLikeValue(JsonElement value, out bool parsed)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                parsed = true;
                return true;
            case JsonValueKind.False:
                parsed = false;
                return true;
            case JsonValueKind.String:
                {
                    string stringValue = value.GetString() ?? string.Empty;
                    if (bool.TryParse(stringValue, out bool boolResult))
                    {
                        parsed = boolResult;
                        return true;
                    }

                    if (stringValue == "1")
                    {
                        parsed = true;
                        return true;
                    }

                    if (stringValue == "0")
                    {
                        parsed = false;
                        return true;
                    }

                    break;
                }
            case JsonValueKind.Number:
                if (value.TryGetInt32(out int intValue))
                {
                    if (intValue == 1)
                    {
                        parsed = true;
                        return true;
                    }

                    if (intValue == 0)
                    {
                        parsed = false;
                        return true;
                    }
                }

                break;
        }

        parsed = default;
        return false;
    }

    private static bool ContainsTypecheckFailureSignature(string output)
    {
        return output.Contains("error TS", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Cannot find module", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// T-009: whether the step already has something usable to deliver. A recorded artifact is
    /// always enough. For every agent but the Author, a non-blank answer is the step's own output;
    /// the Author's contract is the rendered video, so its prose is not.
    /// </summary>
    private static bool HasUsableStepOutput(StepExecutionContext context, AgentRunResult result, string? outputStorageKey)
    {
        if (!string.IsNullOrWhiteSpace(outputStorageKey))
            return true;

        return context.Step.AgentDefinition.AgentType != AgentType.AuthorAgent
            && !string.IsNullOrWhiteSpace(result.Output);
    }

    /// <summary>
    /// T-009: the failed step result for a sandbox render that failed (or that the per-composition
    /// cap refused). Carries the bounded sentence naming the exit code and, when the output shows it,
    /// that the renderer was killed (likely out of memory); <see cref="FailureClass.PlatformInternal"/>
    /// so the step's credits are refunded. Never the generic platform sentence.
    /// </summary>
    private static StepExecutionResult RenderFailedStepResult(
        StepExecutionContext context, AgentRunResult result, string? outputStorageKey, long durationMs)
    {
        RenderFailureLedger ledger = context.RenderFailures;
        string message = ledger.LastMessage is { Length: > 0 } last
            ? last
            : FailureMessages.UserMessage(FailureMessages.RenderFailedCode);

        return new StepExecutionResult
        {
            Output = result.Output,
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = durationMs,
            TokensUsed = result.TokensUsed,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            PrimaryProviderId = result.PrimaryProviderId,
            PrimaryModelName = result.PrimaryModelName,
            ToolCalls = result.ToolCalls,
            Reasoning = result.Reasoning,
            Status = StepStatus.Failed,
            ErrorDetails = message,
            FailureClass = FailureClass.PlatformInternal,
            FailureCode = FailureMessages.RenderFailedCode,
            UserMessage = message,
            OutputStorageKey = outputStorageKey
        };
    }

    private static string BuildFailureReason(string? output, string? explicitReason, string agentName)
    {
        if (!string.IsNullOrWhiteSpace(explicitReason))
            return explicitReason.Trim();

        if (string.IsNullOrWhiteSpace(output))
            return $"Agent {agentName} reported failure.";

        string[] lines = output
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
                line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("TS", StringComparison.Ordinal)
                || line.Contains("Cannot find module", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToArray();

        if (lines.Length == 0)
            return $"Agent {agentName} reported failure. Output preview: {CreatePreview(output, 220)}";

        StringBuilder builder = new($"Agent {agentName} reported failure diagnostics:");
        foreach (string line in lines)
            builder.Append($"\n- {line}");

        return builder.ToString();
    }

}
