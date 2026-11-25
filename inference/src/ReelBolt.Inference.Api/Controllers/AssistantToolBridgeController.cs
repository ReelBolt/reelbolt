using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Observability;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The external bridge over the platform assistant's tool set (docs/design/mcp-tool-bridge.md §3).
/// The MCP server is a thin proxy over these two routes; they are the single source of truth for the
/// catalog and for every tool invocation.
/// </summary>
/// <remarks>
/// <para>
/// <b>No second registry.</b> The catalog is generated from the very
/// <see cref="AssistantToolProvider.GetTools(AssistantToolContext)"/> list the chat transport runs —
/// name, description and JSON Schema are read off each <see cref="AIFunction"/> descriptor, then
/// projected by <see cref="AssistantMcpToolSurface"/> into the MCP names (each in-app
/// <c>Propose*</c>/<c>Apply*</c> pair is one callable tool on this surface; the in-app pair and its
/// approval cards are unchanged). There is no hand-written schema and no list maintained beside the
/// registrations, except the explicit pair-name map that a drift test holds complete.
/// </para>
/// <para>
/// <b>Authority is the chat path's, unchanged.</b> Authentication is the existing JWT
/// (<c>[Authorize]</c> — the same <c>middleware.Auth</c> every sibling controller uses), the acting
/// user is <see cref="ICurrentUser.UserId"/> (the verified <c>sub</c> claim) and is <b>never</b> read
/// from the body. Admin-gated tools are listed only when the caller's MCP admin opt-in is on, and
/// every one of them refuses inside its own body, per call, from the live
/// <see cref="ICurrentUser"/>. No role, admin bit or permission crosses this endpoint in either
/// direction.
/// </para>
/// <para>
/// <b>Not a chat turn.</b> Neither route creates, requires or touches an assistant thread, and
/// neither appends to chat history. <c>threadId</c> is not part of either contract.
/// </para>
/// <para>
/// <b>Refusals are results.</b> A tool-level refusal (admin gate, plan gate, rate limit, unknown
/// workflow) is returned as the tool's own text with <c>isError: false</c>, because the model reads
/// it. <c>isError: true</c> is reserved for a genuine execution failure. Only an unknown tool name
/// is an HTTP error, and it is <c>404</c> — never <c>403</c>, which would confirm a gated tool
/// exists.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/assistant")]
[Authorize]
public class AssistantToolBridgeController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<AssistantToolBridgeController> _logger;
    private readonly INonWorkflowChargeService _charges;

    /// <remarks>
    /// The charge service is required: a host that forgot to register it must fail loudly rather than hand out
    /// uncharged tool calls (with <c>Billing:Enabled</c> false it is itself a no-op).
    /// </remarks>
    private readonly ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? _productEvents;

    public AssistantToolBridgeController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        ILogger<AssistantToolBridgeController> logger,
        INonWorkflowChargeService charges,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _charges = charges;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _logger = logger;
    }

    /// <summary>
    /// Lists the assistant's registered tools as MCP tool declarations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No parameters and no page context: the registered set is static (every surface, including the
    /// gated admin one and the picture editor, registers on every page), so the catalog is the same
    /// on any page.
    /// </para>
    /// <para>
    /// <b>The gated tools are omitted unless the caller's <c>mcp_admin_tools_enabled</c> is on</b>
    /// (docs/design/mcp-oauth.md §6). The gated subset comes from
    /// <see cref="AssistantToolProvider.GetAdminToolNames"/> — the same admin registration list
    /// <see cref="AssistantToolProvider.GetTools"/> builds from — so a newly gated tool is filtered
    /// here automatically, with no second list.
    /// </para>
    /// </remarks>
    [HttpGet("tool-catalog")]
    public async Task<ActionResult<AssistantToolCatalogResponse>> GetToolCatalog(CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        // Provenance, not authority: everything a tool starts from this surface is labelled Mcp.
        AssistantToolContext context = new(userId, Initiator: ExecutionInitiator.Mcp);

        AssistantToolProvider provider = CreateToolProvider();
        HashSet<string> gated = provider.GetAdminToolNames(context).ToHashSet(StringComparer.Ordinal);
        bool adminToolsEnabled = await AdminToolsEnabledAsync(userId, ct);

        // MCP publishes the collapsed Propose/Apply surface (AssistantMcpToolSurface). The in-app
        // assistant keeps its pair and its approval cards; this projection is the only place the MCP
        // names are decided.
        IReadOnlySet<string> hidden = adminToolsEnabled ? new HashSet<string>(StringComparer.Ordinal) : gated;
        IReadOnlyList<AssistantToolDescriptorDto> tools = AssistantMcpToolSurface
            .Project(provider.GetTools(context), hidden)
            .Select(tool => new AssistantToolDescriptorDto(
                tool.Name,
                tool.Description,
                tool.InputSchema,
                tool.Annotation.Title,
                new AssistantToolAnnotationsDto(
                    tool.Annotation.Title,
                    tool.Annotation.ReadOnlyHint,
                    tool.Annotation.DestructiveHint,
                    tool.Annotation.IdempotentHint,
                    tool.Annotation.OpenWorldHint)))
            .ToList();

        return Ok(new AssistantToolCatalogResponse(tools));
    }

    private const string DeprecatedWaitForExecution = "WaitForExecution";

    private static string WithDeprecationNote(string text)
    {
        const string note = "WaitForExecution is replaced by GetExecutionStatus; it no longer waits";
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["deprecated"] = note;
                return obj.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }
        return text;
    }

    /// <summary>
    /// Executes exactly one assistant tool under the acting caller's own authority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A gated tool is refused unless the caller's <c>mcp_admin_tools_enabled</c> is on. That is an
    /// <b>additional</b> condition: with the setting on, the tool runs and its own per-call authority
    /// check (<c>ICurrentUser.IsPlatformAdmin</c> / <c>OrgAuthority.IsOrganizationAdmin</c>) decides,
    /// exactly as on the chat path. The refusal is an ordinary tool-level result — HTTP 200,
    /// <c>isError:false</c>, a JSON message the model reads — never a 403 that would confirm the tool
    /// exists and never a 500.
    /// </para>
    /// <para>
    /// One <c>mcp_tool_calls</c> row is written per call, success, refusal or failure alike
    /// (docs/design/mcp-oauth.md §6). The call itself writes nothing to <c>usage_events</c> (that ledger
    /// is provider cost, not tool usage), but any model call the tool makes is metered there under
    /// <see cref="UsageSource.Mcp"/>.
    /// </para>
    /// </remarks>
    [HttpPost("tool-call")]
    public async Task<ActionResult<AssistantToolCallResultDto>> CallTool(
        [FromBody] AssistantToolCallRequest request,
        CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        long startedAt = Stopwatch.GetTimestamp();

        // A run-watching token (runs.watch without a tool scope) may call exactly GetExecutionStatus. Checked first, so a
        // refused call does no lookup, charge or DB work. The deprecated WaitForExecution alias is not honoured for it.
        HashSet<string> scopes = McpScopeGuard.ReadScopes(User);
        if (scopes.Contains(McpScopeRoutes.RunsWatchScope)
            && !scopes.Contains(McpScopeRoutes.ToolsScope)
            && !scopes.Contains(McpScopeRoutes.AdminScope)
            && !string.Equals(request.Name, "GetExecutionStatus", StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = McpScopeGuard.ErrorCode, message = McpScopeGuard.ErrorMessage });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await RecordCallAsync(userId, string.Empty, succeeded: false, ErrorCodes.NameRequired, startedAt, ct);
            return BadRequest(new { error = "name is required" });
        }

        // product-analytics: the first tool call from an MCP client is "MCP connected". Best-effort,
        // first occurrence per user; the emitter swallows its own failures.
        if (_productEvents is not null)
        {
            await _productEvents.FirstAsync(
                userId, _currentUser.OrganizationId,
                ReelBolt.Inference.Api.Services.Telemetry.ProductEventNames.McpConnected, ct);
        }

        // DEPRECATED alias for one release: WaitForExecution no longer exists (it blocked); scripts that still call it get
        // GetExecutionStatus's answer, recorded and billed under the real name. It is never in the catalog or the tool set.
        bool deprecatedAlias = string.Equals(request.Name, DeprecatedWaitForExecution, StringComparison.Ordinal);
        string toolName = deprecatedAlias ? "GetExecutionStatus" : request.Name;

        // The acting user is the transport's, never the body's. AssistantToolContext carries no
        // authority; every gated tool re-reads ICurrentUser inside its own body on this call.
        AssistantToolContext context = new(
            userId,
            request.Context?.ProjectId,
            request.Context?.WorkflowId,
            request.Context?.ImageDocumentId,
            ExecutionInitiator.Mcp);

        AssistantToolProvider provider = CreateToolProvider();
        provider.FailureReporting = new AssistantToolFailureReporting(_logger, "mcp", AnswerExceptions: false);
        IReadOnlyList<AIFunction> tools = provider.GetTools(context);

        // Resolve the MCP name to its plan. A collapsed name runs Propose then Apply in this one call;
        // the retired Propose*/Apply* names are not MCP tools and are unknown here.
        if (!AssistantMcpToolSurface.TryResolve(toolName, tools, out McpToolCallPlan plan))
        {
            await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.UnknownTool, startedAt, ct);
            return NotFound(new { error = "unknown tool", name = toolName });
        }

        // The MCP admin opt-in. Applied identically to local and remote callers — the setting IS the
        // gate; the OAuth scope only mirrors it for auditability. A collapsed pair is gated by its
        // Apply half (the admin tools are all writes).
        if (plan.AdminToolName is { } adminTool
            && provider.GetAdminToolNames(context).Contains(adminTool, StringComparer.Ordinal)
            && !await AdminToolsEnabledAsync(userId, ct))
        {
            await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.AdminToolsDisabled, startedAt, ct);
            return Ok(AssistantToolCallResultDto.TextResult(AdminToolsDisabledJson));
        }

        // Credits (decision 9): MCP shares the assistant's monthly allowance. Each call holds
        // Billing:NonWorkflow:AssistantTurnHoldCredits (allowance first, then credits) under the workspace's ledger
        // lock, so concurrent calls cannot share the same allowance left; refused when allowance left + balance
        // cannot cover it, when there is no workspace to charge, or when the charge path cannot be evaluated (fail
        // closed). The refusal is a plain tool result (isError true), and the tool never runs.
        TurnHold? credits = await _charges.BeginTurnAsync(_currentUser.OrganizationId, UsageSource.Mcp, Guid.NewGuid(), ct: ct);
        if (credits is { Allowed: false, Code: NonWorkflowChargeCodes.TurnConcurrencyLimit })
        {
            // Too many charged calls of this workspace in flight: 429 with a readable message.
            await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.TurnConcurrencyLimit, startedAt, ct);
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { code = NonWorkflowChargeCodes.TurnConcurrencyLimit, message = NonWorkflowChargeResults.AssistantTurnConcurrencyText });
        }

        // QA-11: a tool the one cost table (AssistantPlatformTools.Tools) classifies Free spends nothing, so being out of
        // allowance and credits does not stop it: it runs with no hold (nothing was taken, so nothing is settled or
        // claimed). Only InsufficientCredits is waived; every other refusal stands. Platform-billed and unclassified
        // names (IsPlatformBilled fails closed) are still refused, and a free tool that queues paid work (ExecuteWorkflow)
        // is charged by that work's own gate.
        if (credits is { Allowed: false, Code: NonWorkflowChargeCodes.InsufficientCredits }
            && !AssistantPlatformTools.IsPlatformBilled(plan.ChargeToolName))
        {
            credits = null;
        }

        if (credits is { Allowed: false })
        {
            await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.InsufficientCredits, startedAt, ct);
            return Ok(AssistantToolCallResultDto.TextResult(NonWorkflowChargeResults.AssistantRefusalText(credits), isError: true));
        }

        await using TurnCharge turnCharge = new(_charges, credits, _logger);

        // Metering scope: any model call the tool makes is attributed to the caller's active organization and
        // the caller, under source=Mcp. The page's project is recorded only when the caller may view it, the
        // same rule the chat transport applies; a foreign or unknown project is simply absent.
        Guid? meteredProjectId = await ViewableProjectIdAsync(request.Context?.ProjectId, ct);
        UsageScopeState? meteredScope = null;

        // The hard wall clock (Assistant:MaxTurnSeconds, F-4.2) on the call's own work.
        IConfiguration? configuration = HttpContext.RequestServices.GetService<IConfiguration>();
        using var wallClock = new CancellationTokenSource(TimeSpan.FromSeconds(AssistantChatLimits.MaxTurnSeconds(configuration)));
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct, wallClock.Token);

        try
        {
            object? raw;
            using (UsageScope.Begin(_currentUser.OrganizationId, meteredProjectId, null, null, userId, UsageSource.Mcp))
            {
                meteredScope = UsageScope.Current;

                // The in-turn cut-off before the call's paid work: a stop is a readable tool error, and
                // the tool never runs.
                // the same hooks as the chat turn (the vision resolver, the look sizer and the picture
                // tools' worst case), bound to this request's picture tools and this call's tool context.
                AssistantTurnGuard guard = AssistantTurnGuard.ForCall(_charges, credits, meteredScope, null, HttpContext.RequestServices, context);
                AssistantTurnGuard.StepClaim claim;

                // T-095: the arguments are bound once; the claim may pin what it sized into them (the look's
                // layer), so the tool is invoked with the very dictionary the claim saw.
                AIFunctionArguments arguments = BindArguments(request.Arguments);
                if (deprecatedAlias)
                    arguments.Remove("timeoutSeconds");
                try
                {
                    // T-032, T-093: the call is pre-claimed at its worst case for its actual input
                    // (free tools at nothing, unclassified ones at the unpriced default) before it runs, under the same
                    // wall clock as the call itself.
                    claim = await guard.ClaimToolAsync(plan.ChargeToolName, callCts.Token, arguments);
                }
                catch (AssistantTurnStoppedException stop)
                {
                    await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.TurnStopped, startedAt, ct);
                    return Ok(AssistantToolCallResultDto.TextResult(NonWorkflowChargeResults.AssistantStopText(stop.Code), isError: true));
                }

                await using (claim)
                {
                    // Contract section 9: the automatic version before an MCP write or run start. The MCP
                    // path builds its tools without the chat interceptor, so it captures here. Best effort.
                    string? autoCause = plan.Steps
                        .Select(step => ProjectHistoryAutoCapture.CauseFor(step.Name))
                        .FirstOrDefault(c => c is not null);
                    if (autoCause is not null
                        && (ProjectHistoryAutoCapture.ProjectIdFrom(arguments) ?? request.Context?.ProjectId) is { } autoProjectId)
                    {
                        // QA finding 2: resolution itself is best effort too. The MCP test host (and any
                        // deployment with the feature unregistered) must not fail the tool because the
                        // history service is missing; GetService returns null, and a throwing factory is
                        // contained here as well.
                        try
                        {
                            IProjectHistoryService? history = HttpContext.RequestServices.GetService<IProjectHistoryService>();
                            if (history is not null)
                            {
                                using IDisposable autoScope = ProjectHistoryAutoCapture.Use(history, _projectAccess);
                                await ProjectHistoryAutoCapture.TryCaptureAsync(autoProjectId, autoCause, userId, callCts.Token);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Best effort: a failed capture or resolution never fails the tool call.
                        }
                    }

                    raw = await AssistantMcpToolSurface.InvokeAsync(plan, arguments, callCts.Token);
                }
            }

            string text = ToolResultText(raw);

            // The tool body may have DECLINED to do the work — the admin gate, the plan gate, the rate
            // limit and "workflow not found" are all ordinary results the model reads (isError false).
            // They must not be metered as successes, or an operator cannot see somebody hammering a
            // tool they have no authority for. See AssistantToolRefusal.
            bool refused = AssistantToolRefusal.IsRefusal(text);

            // A tool refused because its model is an unpriced Platform model: the work cannot be done at all until an
            // administrator prices it, so it is an isError result (with the tool's own plain sentence), not a result.
            if (refused && AssistantToolRefusal.TryRead(text, out string refusal, out string? code)
                && code == ModelUnpricedException.FailureCode)
            {
                await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.ModelUnpriced, startedAt, ct);
                return Ok(AssistantToolCallResultDto.TextResult(refusal, isError: true));
            }

            await RecordCallAsync(
                userId,
                toolName,
                succeeded: !refused,
                errorCode: refused ? ErrorCodes.ToolRefused : null,
                startedAt,
                ct);

            return Ok(AssistantToolCallResultDto.TextResult(deprecatedAlias ? WithDeprecationNote(text) : text));
        }
        catch (OperationCanceledException) when (wallClock.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Past the wall clock: cancelled, and answered as a readable tool error rather than a protocol error.
            _logger.LogWarning("Assistant tool {ToolName} for user {UserId} cancelled after Assistant:MaxTurnSeconds", toolName.ForLog(), userId);
            await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.TurnTimeout, startedAt, CancellationToken.None);
            return Ok(AssistantToolCallResultDto.TextResult(NonWorkflowChargeResults.AssistantTurnTooLongText, isError: true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled request is still one call: meter it on a token that is not itself cancelled,
            // then let the pipeline report the cancellation.
            await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.Cancelled, startedAt, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when ((ex as ModelUnpricedException ?? ex.InnerException as ModelUnpricedException) is { } unpriced)
        {
            // A tool whose model is an unpriced Platform model: the refusal's own plain sentence, as a tool error.
            _logger.LogWarning("Assistant tool {ToolName} for user {UserId} refused: {Reason}", toolName.ForLog(), userId, unpriced.Message.ForLog());
            await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.ModelUnpriced, startedAt, ct);
            return Ok(AssistantToolCallResultDto.TextResult(unpriced.Message, isError: true));
        }
        catch (Exception ex)
        {
            // The one place isError is true: the function could not be invoked or it threw. S-75: the caller reads ONE
            // fixed sentence plus a reference it can quote to an operator; the exception's own text (a storage key, an
            // internal host, an SDK message) stays in the server log, the same contract the chat stream keeps
            // (AssistantChatController logs the turn and closes with "(ref …)"). The reference is the request's own
            // trace identifier, so the log line and the answer name the same call.
            string reference = HttpContext.TraceIdentifier;
            _logger.LogError(ex, "Assistant tool {ToolName} failed for user {UserId} (ref {Reference})", toolName.ForLog(), userId, reference.ForLog());
            await RecordCallAsync(userId, toolName, succeeded: false, ErrorCodes.ExecutionFailed, startedAt, ct);
            return Ok(AssistantToolCallResultDto.TextResult(
                $"tool execution failed: the server could not run it. Try again in a moment. (ref {reference})", isError: true));
        }
        finally
        {
            // The hold settles at whatever the call metered, success or not: the usage already happened and is always
            // recorded. Nothing metered releases it. A settlement that fails is retried once, then logged.
            await turnCharge.SettleAsync(meteredScope);
        }
    }

    /// <summary>The page's project when the caller may view it in their active organization; otherwise null.</summary>
    private async Task<Guid?> ViewableProjectIdAsync(Guid? projectId, CancellationToken ct)
    {
        if (projectId is not { } requested || requested == Guid.Empty)
            return null;

        return (await _projectAccess.GetAsync(requested, ProjectPermission.View, ct)).IsFound ? requested : null;
    }

    /// <summary>
    /// The caller's per-user MCP admin opt-in, read from the database on the call that needs it (never
    /// cached — the setting is authority, and it can be turned off between two calls). A caller with no
    /// user row answers <c>false</c>: fail closed.
    /// </summary>
    private async Task<bool> AdminToolsEnabledAsync(Guid userId, CancellationToken ct) =>
        await _db.ApplicationUsers
            .Where(u => u.Id == userId)
            .Select(u => (bool?)u.McpAdminToolsEnabled)
            .FirstOrDefaultAsync(ct) ?? false;

    /// <summary>
    /// Writes the one <c>mcp_tool_calls</c> row for this call. Deliberately not best-effort: the row is
    /// the usage record the contract requires, so a failure to write it is a server failure rather than
    /// a silently unmetered call.
    /// </summary>
    private async Task RecordCallAsync(
        Guid userId,
        string toolName,
        bool succeeded,
        string? errorCode,
        long startedAt,
        CancellationToken ct)
    {
        _db.McpToolCalls.Add(new McpToolCall
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            // A verified token with no active organization still meters; Guid.Empty records exactly that.
            OrganizationId = _currentUser.OrganizationId ?? Guid.Empty,
            ToolName = toolName,
            CalledAt = DateTime.UtcNow,
            Succeeded = succeeded,
            ErrorCode = errorCode,
            DurationMs = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
        });

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The short machine-readable reasons a bridged call did not complete, stored on the row.</summary>
    private static class ErrorCodes
    {
        internal const string NameRequired = "name_required";
        internal const string UnknownTool = "unknown_tool";
        internal const string AdminToolsDisabled = "mcp_admin_tools_disabled";
        internal const string ExecutionFailed = "tool_execution_failed";
        internal const string Cancelled = "cancelled";

        /// <summary>The month's assistant allowance is used up and the workspace has no credits (or they could not be checked).</summary>
        internal const string InsufficientCredits = "insufficient_credits";

        /// <summary>The tool ran and returned its own refusal result (not a thrown failure).</summary>
        internal const string ToolRefused = "tool_refused";

        /// <summary>The workspace already had <c>MaxConcurrentTurnsPerOrg</c> charged calls in flight.</summary>
        internal const string TurnConcurrencyLimit = "turn_concurrency_limit";

        /// <summary>The in-turn cut-off stopped the call before its paid work (ceiling or credits).</summary>
        internal const string TurnStopped = "turn_stopped";

        /// <summary>The call ran past <c>Assistant:MaxTurnSeconds</c> and was cancelled.</summary>
        internal const string TurnTimeout = "turn_timeout";

        /// <summary>The tool's model is a Platform model with no effective price (<c>MODEL_UNPRICED</c>).</summary>
        internal const string ModelUnpriced = "model_unpriced";
    }

    /// <summary>
    /// The refusal text for a gated tool when the caller has not opted in. It is a JSON message the
    /// model reads (like every other tool-level refusal), never an HTTP error.
    /// </summary>
    private static readonly string AdminToolsDisabledJson = JsonSerializer.Serialize(new
    {
        error = "admin tools are not enabled for this account",
        code = "MCP_ADMIN_TOOLS_DISABLED",
        hint = "Turn on admin tools for MCP in the web portal's settings, then reconnect."
    });

    /// <summary>
    /// Builds the per-request provider through the SAME factory the chat transport uses, so the
    /// catalog and the chat turn can never describe different tool sets. The request-scoped surfaces
    /// are resolved from this request's own scope, sharing its DbContext and confirmation-token
    /// service.
    /// </summary>
    private AssistantToolProvider CreateToolProvider() =>
        AssistantToolProviderFactory.Create(
            _db,
            HttpContext.RequestServices.GetRequiredService<IWorkflowEditDiffService>(),
            HttpContext.RequestServices.GetRequiredService<IWorkflowExecutionService>(),
            HttpContext.RequestServices.GetRequiredService<IAssistantExecutionGuard>(),
            _currentUser,
            HttpContext.RequestServices);

    /// <summary>
    /// Hands the body's arguments to the tool binder verbatim. Values are cloned out of the request
    /// document so they stay valid for the whole invocation (and are the same <see cref="JsonElement"/>
    /// shape a chat client's tool call arrives in, which is what the binder expects).
    /// </summary>
    private static AIFunctionArguments BindArguments(Dictionary<string, JsonElement>? arguments)
    {
        var bound = new AIFunctionArguments();
        if (arguments is null)
            return bound;

        foreach ((string name, JsonElement value) in arguments)
            bound[name] = value.Clone();

        return bound;
    }

    /// <summary>
    /// The tool's own result as the text the model reads: a string tool result is passed through
    /// untouched, a serialized one keeps its exact JSON.
    /// </summary>
    private static string ToolResultText(object? raw) => raw switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(raw)
    };
}
