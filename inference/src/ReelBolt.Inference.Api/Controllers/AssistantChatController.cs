using ReelBolt.Inference.Api.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The platform assistant's single transport endpoint. <c>projectId</c> is <b>per-call context</b>
/// carried in the body (not route state), so a page with no project can still talk to the
/// assistant. Conversation history is server-persisted against the thread.
/// </summary>
[ApiController]
[Route("api/v1/assistant")]
[Authorize]
public class AssistantChatController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IAgentChatClientProvider _chatClientProvider;
    private readonly IProjectAccessService _projectAccess;
    private readonly INonWorkflowChargeService? _charges;

    /// <summary>
    /// The live turn this request is answering, once <see cref="ChatStream"/> has registered it with the
    /// process-wide <see cref="AssistantTurnHub"/>. Every frame the turn writes is logged and fanned out
    /// through it, so a re-attach can replay the turn from the start. Null on a request that never reached
    /// the turn (a 400/404) — and on the re-attach path, which only subscribes.
    /// </summary>
    private AssistantLiveTurn? _live;

    private readonly ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? _productEvents;

    public AssistantChatController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IAgentChatClientProvider chatClientProvider,
        IProjectAccessService projectAccess,
        INonWorkflowChargeService? charges = null,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _charges = charges;
        _projectAccess = projectAccess;
        _db = db;
        _currentUser = currentUser;
        _chatClientProvider = chatClientProvider;
    }

    /// <summary>
    /// Appends one turn to the caller's thread and streams the assistant's reply as Server-Sent
    /// Events (SSE) with the ui-message-stream protocol:
    /// start, text-start, text-delta, text-end, [optional data-workflowDiff], finish, [DONE].
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assistant is backed by <see cref="AssistantToolProvider"/>, which composes the shipped
    /// <b>59-tool</b> surface: the <b>7 core</b> workflow tools (ListAgents, ListStepSchemas,
    /// ListProjectFiles, ValidateWorkflow, ProposeWorkflow, ApplyWorkflow, ExecuteWorkflow) plus
    /// <c>AssistantProjectTools</c> <b>10</b> (ListProjects, GetProject, Propose/ApplyCreateProject,
    /// Propose/ApplyUpdateProject, Propose/ApplyDeleteProject, Propose/ApplySavePronunciation), <c>AssistantFileTools</c> <b>5</b>
    /// (ReadProjectFile, Propose/ApplyWriteProjectFile, Propose/ApplyDeleteProjectFile),
    /// <c>AssistantWorkflowTools</c> <b>6</b> (ListWorkflows, GetWorkflow, ListWorkflowExecutions,
    /// GetWorkflowExecution, Propose/ApplyDeleteWorkflow), <c>AssistantKnowledgeTools</c> <b>3</b>
    /// (SearchPlatformDocs, ListWorkflowTemplates, GetWorkflowTemplate), <c>AssistantStyleTools</c> <b>3</b>
    /// (ListStyles, Propose/ApplySaveStyle), <c>AssistantPictureTools</c> <b>14</b> (DescribePictureDocument,
    /// ListPictureTools and the Propose/Apply pairs for text layers, layer edits, layer deletion, the canvas,
    /// a paid AI picture tool and a designed graphic — always registered, answering "no picture open" off the
    /// picture editor; each applied change is streamed as a <c>data-imageDocumentChanged</c> frame) and
    /// <c>AssistantAdminTools</c> <b>13</b> (GetDecisionCalibration, ListInferenceProviders, TestInferenceProvider, Propose/Apply
    /// Create/Update/DeleteInferenceProvider, ListSkills, Propose/ApplyAssignAgentSkills,
    /// ListAgentsAdmin). Every mutating capability on that surface is a <b>propose-then-apply
    /// pair</b>: the propose half computes the change and returns an opaque server-side confirmation
    /// token, and the apply half recomputes and verifies that token before writing anything, so a
    /// stale or forged token is refused rather than applied. The 13-tool admin subset is
    /// additionally gated per call on the caller's <b>live</b> admin claim — never a claim captured
    /// when the request started.
    /// </para>
    /// <para>
    /// A thread id that is absent from the request is <b>allocated</b>, not refused (see
    /// <see cref="AssistantChatRequest"/>), and the id the turn landed on is returned in the
    /// <c>X-Assistant-Thread-Id</c> response header on every successful turn. A supplied-but-unknown
    /// thread id is a 404. A turn is not written until it has passed every bound, so a refused turn
    /// records nothing.
    /// </para>
    /// <para>
    /// The turn is bounded on both sides — <see cref="AssistantChatLimits.MaxMessageChars"/> on what
    /// may be persisted, and <see cref="AssistantChatLimits.MaxReplayMessages"/> on how much of the
    /// thread is replayed — because a persisted message is replayed into every later prompt of that
    /// thread. The character bound applies to the <b>resolved</b> turn text, so an AI-SDK
    /// <c>messages[]</c> turn is bounded exactly like a flat <c>message</c>. An oversized message is
    /// refused with a 400 and records nothing.
    /// </para>
    /// <para>
    /// The character bound is <b>symmetric</b>: it applies to the <b>assistant</b> reply as well as
    /// to the user's message, because both halves land in the same table and are replayed the same
    /// way. The assistant half cannot be refused — its tokens are already spent by the time the
    /// length is known — so it is truncated to the cap by
    /// <see cref="AssistantChatLimits.TruncateAssistantReply"/> and marked as truncated in the
    /// persisted row. The <c>text-delta</c> frame carries that same truncated string, so the
    /// conversation on screen and the conversation the model replays cannot drift apart.
    /// </para>
    /// <para>
    /// How much the model may <b>think</b> before it writes is a third bound, and it is decided
    /// before the call for the same reason the ceiling is: it cannot be undone afterwards. Every
    /// turn is sent the reasoning effort <see cref="AssistantChatLimits.ReasoningEffortFor"/>
    /// resolves — <c>none</c> unless the deployment asks for more, so a chat reply is not spent
    /// deliberating first. See <c>Assistant:ReasoningEffort</c>.
    /// </para>
    /// <para>
    /// There are therefore <b>two</b> truncations, decided in the opposite order, and each says so.
    /// The <b>model's</b> own ceiling is set before the call
    /// (<see cref="AssistantChatLimits.MaxOutputTokens"/>, with reasoning headroom on a provider that
    /// charges thinking to the same budget) and what it cuts is reported by the provider itself as
    /// <c>finishReason=length</c>; the endpoint turns that into a visible marker on the reply rather
    /// than streaming half a sentence as though it were whole, and reports the real reason in the
    /// <c>finish</c> frame instead of the hardcoded <c>stop</c> that used to be there regardless of
    /// what happened. The <b>character</b> bound runs after it and behaves as described above. A
    /// reader of the thread can always tell which one bit.
    /// </para>
    /// </remarks>
    [HttpPost("chat")]
    public async Task ChatStream([FromBody] AssistantChatRequest request, CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;

        // A re-attach carries no message and starts no turn: it subscribes this response to the turn the
        // process already owns for the thread — the reply the panel lost when its connection dropped.
        if (request.Attach)
        {
            await AttachToTurnAsync(request, userId, ct);
            return;
        }

        // A supplied thread id, or none. An ABSENT threadId binds as Guid.Empty (System.Text.Json
        // gives a struct constructor parameter its default), and Guid.Empty means "no thread yet" —
        // the turn below allocates one instead of 404ing. Only a thread id that was actually
        // SUPPLIED can be unknown, and that is a 404. (A threadId that is present but unbindable —
        // null, "", or a non-Guid string — never reaches here: the JSON input formatter rejects the
        // body and the [ApiController] pipeline answers 400 first.)
        Guid? suppliedThreadId = request.ThreadId == Guid.Empty ? null : request.ThreadId;

        // Ownership of the THREAD is the authority here. Another user's thread is reported as
        // "not found", never "forbidden" — a 403 would confirm it exists.
        AssistantThread? thread = null;

        if (suppliedThreadId is { } threadId)
        {
            thread = await _db.AssistantThreads
                .FirstOrDefaultAsync(t => t.Id == threadId && t.UserId == userId && t.OrganizationId == _currentUser.OrganizationId, ct);

            if (thread is null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                await HttpContext.Response.WriteAsJsonAsync(new { error = "Thread not found" }, ct);
                return;
            }
        }

        // The turn's text, whichever wire shape carried it: the flat `message` when present,
        // otherwise the last `user` entry of the AI-SDK `messages[]` array. This is the ONLY thing
        // taken from `messages[]` — it is a transport fallback for the current turn, never a
        // history source.
        string messageText = request.ResolveMessageText();

        // Both bounds are resolved ONCE here, before anything is written, so a refused turn records
        // nothing at all — the same guarantee the empty-message check below gives.
        IConfiguration configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();

        if (string.IsNullOrWhiteSpace(messageText))
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await HttpContext.Response.WriteAsJsonAsync(
                new
                {
                    error = "No user message was supplied. Send { \"message\": \"...\" } or the AI SDK "
                        + "shape { \"messages\": [ { \"role\": \"user\", \"content\": [ { \"type\": "
                        + "\"text\", \"text\": \"...\" } ] } ] }."
                },
                ct);
            return;
        }

        // A message is persisted and then replayed into EVERY later prompt of this thread, so its
        // length is bounded at the endpoint rather than left to whatever the model happens to
        // accept: one oversized turn would otherwise inflate this thread's cost permanently. The
        // bound is applied to the RESOLVED text, so an AI-SDK turn is bounded too.
        int maxMessageChars = AssistantChatLimits.MaxMessageChars(configuration);
        if (messageText.Length > maxMessageChars)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await HttpContext.Response.WriteAsJsonAsync(
                new { error = $"Message must not exceed {maxMessageChars} characters" }, ct);
            return;
        }

        // product-analytics: the first accepted chat turn is "assistant used". Best-effort (raw claim
        // statement, independent of the thread save below); once per user.
        if (_productEvents is not null)
        {
            await _productEvents.FirstAsync(
                userId, _currentUser.OrganizationId,
                ReelBolt.Inference.Api.Services.Telemetry.ProductEventNames.AssistantUsed, ct);
        }

        // Project context comes from the BODY. A project outside the caller's active organization is treated as
        // absent rather than rejected: the thread is still usable, and the tools simply see no
        // project context. Never leaks whether the project exists. Resolved BEFORE the thread is
        // created so a new thread records the page context it was opened from, and shared with the
        // tool context below so both see exactly the same value.
        Guid? projectId = null;
        if (request.ProjectId is { } requestedProjectId && requestedProjectId != Guid.Empty)
        {
            // In the caller's ACTIVE organization and viewable by their role; cross-org is
            // indistinguishable from absent.
            bool accessible = (await _projectAccess.GetAsync(requestedProjectId, ProjectPermission.View, ct)).IsFound;

            if (accessible)
                projectId = requestedProjectId;
        }

        // Only NOW, with every bound passed, is a thread allocated for a caller who supplied none.
        // Doing it here is what makes a refused turn leave no trace: a 400 above never reaches this.
        if (thread is null)
        {
            DateTime now = DateTime.UtcNow;
            thread = new AssistantThread
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OrganizationId = ActiveOrganization.Require(_currentUser),
                ProjectId = projectId,
                Title = AssistantThreadTitler.Fallback(messageText),
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.AssistantThreads.Add(thread);
            await _db.SaveChangesAsync(ct);
        }

        // The open workflow is validated by the same rule as the project, so the tool context
        // carries only context the caller actually owns: a workflow outside the project in context
        // (or a workflow while there is no project in context) is treated as ABSENT, never trusted
        // on a guessed id. Tools re-check this per call; doing it here keeps the context itself
        // honest.
        Guid? workflowId = null;
        if (projectId is { } ownedProjectId
            && request.WorkflowId is { } requestedWorkflowId
            && requestedWorkflowId != Guid.Empty
            && await _db.WorkflowDefinitions.AnyAsync(w => w.Id == requestedWorkflowId && w.ProjectId == ownedProjectId
                && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot, ct))
        {
            workflowId = requestedWorkflowId;
        }

        // The open picture (the picture editor's document), by the same rule: it must be a document of
        // the project in context, or it is treated as ABSENT. The picture tools re-check it per call.
        Guid? imageDocumentId = null;
        if (projectId is { } pictureProjectId
            && request.ImageDocumentId is { } requestedDocumentId
            && requestedDocumentId != Guid.Empty
            && await _db.EditImageDocuments.AnyAsync(d => d.Id == requestedDocumentId && d.ProjectId == pictureProjectId, ct))
        {
            imageDocumentId = requestedDocumentId;
        }

        // Persist the user turn BEFORE anything is written to the response, so a refused request
        // records nothing and a dropped stream still records what the user said. The RESOLVED text
        // is persisted, which is what makes both wire shapes converge on one stored turn.
        // The first turn names the thread immediately (truncated message) so the list never shows a
        // wall of "New chat"; a model-written title replaces it after the exchange.
        //
        // A thread whose newest turn is this SAME user message with no reply — its request died (the
        // tab closed past the detach bound, the provider failed, the process restarted) — is a RETRY,
        // not a new question: the panel's "Ask again" re-sends exactly that text, and appending it a
        // second time would put the question twice into the thread and into every later prompt. A
        // turn that is still being answered is not a retry, so a double-send while it runs appends.
        AssistantTurnRegistry? turnRegistry = HttpContext.RequestServices.GetService<AssistantTurnRegistry>();
        AssistantMessage? lastTurn = await _db.AssistantMessages
            .Where(m => m.ThreadId == thread.Id)
            .OrderByDescending(m => m.Sequence)
            .FirstOrDefaultAsync(ct);
        // A wake turn (started by the platform, never by a client: see AssistantChatRequest.Wake) only ever reuses an
        // unanswered row of the SAME wake, never an equal-looking human message.
        string? wakeMetadata = request.Wake?.ToMetadataJson();
        bool retryingUnanswered = lastTurn is not null
            && lastTurn.Role == "user"
            && string.Equals(lastTurn.Content, messageText, StringComparison.Ordinal)
            && (request.Wake is null || request.Wake.Matches(lastTurn.MetadataJson))
            && !(turnRegistry?.IsInFlight(thread.Id) ?? false);

        // "First exchange" = no reply yet on this thread, so a thread whose first question was never
        // answered still gets its model-written title once a reply finally lands.
        bool firstExchange = !await _db.AssistantMessages.AnyAsync(m => m.ThreadId == thread.Id && m.Role == "assistant", ct);
        if (firstExchange && AssistantThreadTitler.IsPlaceholder(thread.Title))
            thread.Title = AssistantThreadTitler.Fallback(messageText);

        AssistantMessage userTurn = retryingUnanswered
            ? lastTurn!
            : await AppendTurnAsync(thread, "user", messageText, ct, metadataJson: wakeMetadata);

        // From here on the turn is IN FLIGHT: the thread list reports "Still answering…" instead of
        // "No reply yet", and background summarization/indexing on the shared providers yields to it
        // (AssistantActivityMonitor). Both are optional so hosts that do not register them still work.
        using IDisposable? inFlight = turnRegistry?.Begin(thread.Id);
        using IDisposable? interactive = HttpContext.RequestServices.GetService<AssistantActivityMonitor>()?.BeginTurn();

        // The turn runs on its OWN token, not the request's: a browser that goes away (closed panel,
        // navigation, reload, a phone screen switching off) no longer kills the answer half-way. The reply
        // is still persisted, so reopening the conversation shows it — and re-attaching (POST …/chat with
        // `attach: true`) streams the live turn. A permission card is NOT settled by the disconnect either:
        // it stays answerable by a screen that comes back (the gate no longer watches the connection).
        // But "survives a disconnect" must NOT mean "keeps a workspace's turn-concurrency slot and its credit
        // hold forever": a turn nobody is attached to, and that is not waiting on a card, is cancelled after
        // Assistant:DetachedTurnSeconds of that (see WatchDetachedAsync below). A turn a screen re-attached to,
        // or one held on a card, is never cut by that clock; only the hard wall clock bounds it.
        int maxTurnSeconds = AssistantChatLimits.MaxTurnSeconds(configuration);
        int detachedSeconds = AssistantChatLimits.DetachedTurnSeconds(configuration);
        using var wallClock = new CancellationTokenSource(TimeSpan.FromSeconds(maxTurnSeconds));
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(wallClock.Token);
        CancellationToken runCt = runCts.Token;

        // Register the turn with the process-wide hub BEFORE the first frame, so the SSE request is only a
        // SUBSCRIBER to a turn the process owns (CLAUDE.md § Workflow Execution Liveness): the connection
        // dropping detaches this subscriber, never the work.
        _live = ResolveTurnHub().Begin(thread.Id, userId, _currentUser.OrganizationId, TimeSpan.FromSeconds(maxTurnSeconds));
        _live.Kind = request.Wake is null ? "reply" : "run_wake";

        // Metering scope (B3b): the assistant's model calls (the turn itself, tool loops, the title call) are
        // attributed to the caller's active organization with source=Assistant through the metering chat client.
        using IDisposable usageScope = UsageScope.Begin(
            _currentUser.OrganizationId, projectId, null, null, userId, UsageSource.Assistant);

        // Set on the SUCCESS path only — a 400 or 404 above must never advertise a thread. It is set
        // whether or not the id was supplied, so a client that had none can bind to the allocated
        // thread idempotently.
        HttpContext.Response.Headers["X-Assistant-Thread-Id"] = thread.Id.ToString();

        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers["Cache-Control"] = "no-cache";
        HttpContext.Response.Headers["Connection"] = "keep-alive";

        // A clear-cut attempt to pull out the assistant's instructions or talk it out of its rules is answered
        // with a fixed, friendly reply and no model call (AssistantInputGuard). Everything subtler reaches the
        // model, whose own prompt carries the same boundaries; the guard only saves the obvious attacks a trip.
        AssistantInputVerdict verdict = AssistantInputGuard.Screen(messageText);
        if (verdict.IsRefused)
        {
            await StreamCannedReplyAsync(thread, userTurn, verdict.Reply!, firstExchange, runCt, ct);
            return;
        }

        // Credits (decision 9): the turn holds Billing:NonWorkflow:AssistantTurnHoldCredits of the month's included
        // assistant credits, then of the workspace's credits, before any model call. The hold is taken under the
        // workspace's ledger lock against the authoritative allowance counter, so concurrent turns cannot share the
        // same allowance left. Refused when allowance left + balance cannot cover it, and then answered as an
        // ordinary reply, never a broken stream. Settled at what the turn metered; released if it never gets there.
        // The provider is resolved first: a turn on the organization's own provider (BYO) is not refused by the
        // in-flight cap, since its model calls are not charged.
        // An unpriced Platform chat model (ModelUnpricedException) is refused in plain words before any hold is taken.
        ResolvedInferenceProvider? resolvedProvider;
        try
        {
            resolvedProvider = await TryResolveProviderAsync(runCt);
        }
        catch (ModelUnpricedException unpriced)
        {
            await StreamCannedReplyAsync(thread, userTurn, unpriced.Message, firstExchange, runCt, ct);
            return;
        }

        UsageBillingMode turnMode = resolvedProvider is null
            ? UsageBillingMode.Platform
            : ReelBolt.Shared.Metering.MeteringChatClient.BillingModeOf(resolvedProvider.OrganizationId);
        TurnHold? credits = _charges is null
            ? null
            : await _charges.BeginTurnAsync(_currentUser.OrganizationId, UsageSource.Assistant, Guid.NewGuid(), turnMode, ct);
        if (credits is { Allowed: false, Code: NonWorkflowChargeCodes.TurnConcurrencyLimit })
        {
            // Too many turns of this workspace in flight: nothing has been streamed yet, so this is a plain 429.
            HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            HttpContext.Response.ContentType = "application/json";
            await HttpContext.Response.WriteAsJsonAsync(
                new { code = NonWorkflowChargeCodes.TurnConcurrencyLimit, message = NonWorkflowChargeResults.AssistantTurnConcurrencyText }, ct);
            _live?.Complete(AssistantTurnState.Failed);
            return;
        }

        if (credits is { Allowed: false })
        {
            bool outOfCredits = credits.Code == NonWorkflowChargeCodes.InsufficientCredits;
            await StreamCannedReplyAsync(
                thread,
                userTurn,
                NonWorkflowChargeResults.AssistantRefusalText(credits),
                firstExchange,
                runCt,
                ct,
                outOfCredits
                    ? new
                    {
                        type = "data-credits",
                        data = new { code = NonWorkflowChargeCodes.InsufficientCredits, balance = NonWorkflowChargeResults.ToCredits(credits.BalanceMillis) }
                    }
                    : null);
            return;
        }

        await using TurnCharge turnCharge = new(_charges, credits, HttpContext.RequestServices.GetService<ILogger<AssistantChatController>>());

        // The in-turn cut-off: every model round-trip (the guarded provider below) and
        // every tool call (the gated tools) is pre-claimed at its worst case before it runs, and refused past the turn's
        // ceiling or what it can pay for. A round-trip's input and a model step's tool calls are capped, and a BYO-chat
        // turn's first Platform-billed tool is admitted under the workspace's in-flight cap.
        // the guard's hooks (the vision resolver, the look sizer and the picture tools' worst case) are
        // bound to this request's picture tools and to the per-call tool context the tools run with, exactly as the MCP
        // bridge binds them, so a look or a picture tool is sized by the input it will actually send.
        var toolContext = new AssistantToolContext(userId, projectId, workflowId, imageDocumentId, ChatApproved: true, ThreadId: thread.Id, FromWake: request.Wake is not null);
        AssistantTurnGuard turnGuard = AssistantTurnGuard.ForCall(
            _charges, credits, UsageScope.Current, AssistantTurnBudget.For(configuration, resolvedProvider), HttpContext.RequestServices, toolContext);
        IAgentChatClientProvider guardedChatClients = turnGuard.Wrap(_chatClientProvider, AssistantChatLimits.MaxToolIterations(configuration));

        var diffService = HttpContext.RequestServices.GetRequiredService<IWorkflowEditDiffService>();
        var executionService = HttpContext.RequestServices.GetRequiredService<IWorkflowExecutionService>();
        var executionGuard = HttpContext.RequestServices.GetRequiredService<IAssistantExecutionGuard>();

        AssistantToolProvider toolProvider = CreateToolProvider(diffService, executionService, executionGuard);

        // The caller's identity and page context travel as ONE per-call value (toolContext, built above for the
        // guard). It carries no authority: the provider reads ICurrentUser (request-scoped) inside each admin-gated
        // tool body, so an admin claim can never be captured here at construction.

        // Per-turn session context (who + what page) rides in the SYSTEM prompt, never in the
        // persisted history: the page can change between turns, so a stored copy would go stale.
        string displayName = await _db.ApplicationUsers
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "";
        Guid? activeOrgId = _currentUser.OrganizationId;
        string? orgName = activeOrgId is { } orgIdValue
            ? await _db.Organizations.Where(o => o.Id == orgIdValue).Select(o => o.Name).FirstOrDefaultAsync(ct)
            : null;
        string sessionBlock = AssistantSessionContext.Build(
            new AssistantSessionContext.Caller(
                userId, _currentUser.Email, displayName, _currentUser.IsPlatformAdmin, orgName, _currentUser.OrgRole),
            request.PageContext,
            projectId,
            workflowId,
            imageDocumentId);

        // Documentation retrieved for THIS message rides in the system prompt beside the session
        // block — never in the persisted history, so it is recomputed per turn and never replayed
        // stale. Best-effort: resolved optionally (absent in hosts that do not register it) and the
        // builder itself degrades to null on any failure or timeout.
        PlatformDocsContextBuilder? docsContext = HttpContext.RequestServices.GetService<PlatformDocsContextBuilder>();
        string? docsBlock = docsContext is null ? null : await docsContext.BuildAsync(messageText, runCt);
        if (docsBlock is not null)
            sessionBlock = sessionBlock + "\n\n" + docsBlock;

        // Stream a plain-language caption each time the assistant starts a tool, so the chat panel's
        // "Thinking" line shows what is actually happening. Writes are serialised (tools may run
        // concurrently) and the SSE preamble is guaranteed to have been sent first: the caption
        // events are only emitted from inside RunAsync, which runs after "start"/"text-start".
        var progressLock = new SemaphoreSlim(1, 1);
        var progressJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        async Task ReportStatusAsync(string caption, CancellationToken toolCt)
        {
            await progressLock.WaitAsync(toolCt);
            try
            {
                await WriteEventAsync(progressJson, new { type = "data-toolStatus", data = new { caption } }, ct);
            }
            finally
            {
                progressLock.Release();
            }
        }

        // Starting a tool says what it is doing; FINISHING one says what is true now ("Thinking", or
        // "Docs index still building — answering without it"), so the label never keeps naming a
        // tool that has long since returned.
        toolProvider.OnToolStarting = ReportStatusAsync;
        toolProvider.FailureReporting = new AssistantToolFailureReporting(
            (ILogger?)HttpContext.RequestServices.GetService<ILogger<AssistantChatController>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "chat",
            AnswerExceptions: true);

        // Outermost: the step claim. Throws AssistantTurnStoppedException before the tool body (and its caption) when the
        // turn may not take the step, so no tool runs after the stop; a call past the step's tool maximum is answered
        // with a plain refusal instead of running.
        toolProvider.WrapTool = turnGuard.Gate;
        toolProvider.OnToolFinished = ReportStatusAsync;

        // A picture tool that changed the open picture says so AT ONCE, mid-turn, so the editor showing it
        // brings the change in and points at the layer while the assistant is still answering.
        toolProvider.OnImageDocumentChanged = async (change, toolCt) =>
        {
            await progressLock.WaitAsync(toolCt);
            try
            {
                await WriteEventAsync(progressJson, new { type = "data-imageDocumentChanged", data = change }, ct);
            }
            finally
            {
                progressLock.Release();
            }
        };

        // The "ask permission" step in front of every tool that writes (docs/design/assistant-approvals.md).
        // Required, not optional: a host that forgot to register it must fail loudly rather than run every
        // write ungated. Cards stream as data-assistantAction frames and wait for the user's click on
        // POST /api/v1/assistant/approvals/{id}; a card is NOT settled by the browser going away: it stays answerable by a screen that re-attaches.
        var actionJson = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var interrupted = new AssistantInterruptedTurn();
        toolProvider.ApprovalGate = new AssistantApprovalGate(
            toolContext,
            _currentUser.OrganizationId,
            _db,
            HttpContext.RequestServices.GetRequiredService<AssistantApprovalBroker>(),
            HttpContext.RequestServices.GetRequiredService<AssistantApprovalCardBuilder>(),
            configuration,
            emit: async (frame, toolCt) =>
            {
                // The card's state is what the re-attach entry point reports: a pending card means the turn
                // is awaiting_approval (and a re-attach replays that same card, from the turn's frame log),
                // and any settled card means it is answering again.
                if (frame.State == AssistantActionState.Pending)
                    _live?.MarkAwaitingApproval(frame.Id);
                else
                    _live?.MarkDecided();

                // What the turn did, kept so an interrupted turn can say so instead of leaving no trace.
                interrupted.Note(frame);

                await progressLock.WaitAsync(toolCt);
                try
                {
                    await WriteEventAsync(actionJson, new { type = "data-assistantAction", data = frame }, ct);
                }
                finally
                {
                    progressLock.Release();
                }
            },
            caption: ReportStatusAsync,
            logger: HttpContext.RequestServices.GetService<ILogger<AssistantApprovalGate>>());

        // Names, never ids: learn the id -> name pairs every tool result carries, so a reply that still
        // mentions an id is rewritten with the name (or has the id removed) before anyone reads or stores it.
        var replySanitizer = new AssistantReplySanitizer();
        toolProvider.OnToolResult = (_, result) => replySanitizer.Learn(ToolResultText(result));

        var assistantAgent = new AssistantAgent(
            guardedChatClients,
            configuration,
            toolProvider,
            toolContext,
            sessionBlock);

        // The detached ceiling: once the original connection is gone, the turn is cancelled only after it has been
        // unattended (no re-attached subscriber, no open card) for Assistant:DetachedTurnSeconds. Declared before
        // the try so the finally can stop it and wait for it before the token sources are disposed.
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
        Task? detachedWatch = null;
        using CancellationTokenRegistration detached = ct.Register(() =>
        {
            _live?.Touch();
            detachedWatch = WatchDetachedAsync(_live, TimeSpan.FromSeconds(detachedSeconds), runCts, watchCts.Token);
        });

        // Whether the turn ends as "failed" for the re-attach state. A turn that answered, or that the wall
        // clock stopped with a plain-language notice, is completed; anything that escaped as an exception is not.
        bool turnFailed = false;
        // True once the assistant's reply (or a stop notice) is stored: a cancel after that (the title call) is
        // not a lost reply, so it is neither "failed" nor given an interrupted message.
        bool replyStored = false;

        try
        {
            var messageId = Guid.NewGuid();
            var textId = Guid.NewGuid().ToString();
            _live?.SetMessageId(messageId);

            // Create JsonSerializerOptions matching WorkflowEditDiffService.JsonOptions (camelCase)
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // Emit start message
            await WriteEventAsync(jsonOptions, new { type = "start", messageId }, ct);

            // Emit text-start message
            await WriteEventAsync(jsonOptions, new { type = "text-start", id = textId }, ct);

            // The conversation history is the thread's PERSISTED turns, in sequence order — the
            // client no longer ships it. Only a bounded window of the NEWEST turns is replayed, so a
            // long thread cannot make the assistant progressively more expensive to talk to. The
            // optional open-workflow context is then injected into the first user turn of that
            // window so it stays visible across the whole exchange.
            List<ChatMessage> history = await LoadHistoryAsync(
                thread.Id, AssistantChatLimits.MaxReplayMessages(configuration), runCt);
            history = await InjectWorkflowContextAsync(history, request.WorkflowId, projectId, runCt);

            // Run the agent against the full history so it retains multi-turn context.
            // Heartbeat: an SSE comment every 15s while the model works, so no proxy treats a long,
            // quiet tool-calling turn as a dead connection. Shares the progress lock so a ping can
            // never interleave with a caption event. The ping reaches every re-attached subscriber too
            // (and is never logged, so a replay carries only the turn's real frames).
            // The heartbeat is bound to the TURN, not the first connection, so a screen that re-attaches after
            // the original one went away still gets its pings.
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
            Task heartbeat = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (await timer.WaitForNextTickAsync(heartbeatCts.Token))
                {
                    await progressLock.WaitAsync(heartbeatCts.Token);
                    try
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            try
                            {
                                await HttpContext.Response.WriteAsync(": ping\n\n", heartbeatCts.Token);
                                await HttpContext.Response.Body.FlushAsync(heartbeatCts.Token);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException || !heartbeatCts.IsCancellationRequested)
                            {
                                // The original connection is gone; the turn and its re-attached readers go on.
                            }
                        }

                        _live?.Ping();
                    }
                    finally
                    {
                        progressLock.Release();
                    }
                }
            }, heartbeatCts.Token);

            // The bounds decided BEFORE the model call rather than after it: the output budget, and
            // how much the model may think before it writes anything at all. The second one is the
            // reported "it thinks too much in chat" (issue #228) — the assistant used to send no
            // reasoning configuration, so every reply ran at the provider's own default, the most
            // deliberation a reasoning-capable backend will do. Resolved here, where the provider
            // kind is known, and the budget is reused below to say which number a truncated reply hit.
            InferenceProviderKind? providerKind = resolvedProvider?.Kind;
            int? maxOutputTokens = AssistantChatLimits.MaxOutputTokens(configuration, providerKind);
            ChatOptions? runOptions = AssistantChatLimits.BuildTurnOptions(configuration, providerKind);

            // A turn the cut-off stopped, or that ran past the wall clock while its client is still there, ends as
            // an ordinary reply that says so (plus data-credits for the credit stops), never a broken stream.
            async Task EndTurnWithNoticeAsync(string notice, string? creditsCode)
            {
                await AppendTurnAsync(thread, "assistant", notice, CancellationToken.None);
                await WriteEventAsync(jsonOptions, new { type = "text-delta", textDelta = notice }, ct);
                await WriteEventAsync(jsonOptions, new { type = "text-end" }, ct);
                if (creditsCode is not null)
                {
                    await WriteEventAsync(jsonOptions, new { type = "data-credits", data = new { code = creditsCode } }, ct);
                }

                await WriteEventAsync(jsonOptions, new { type = "finish", finishReason = "stop", usage = new { inputTokens = 0, outputTokens = 0 } }, ct);
                await EmitAsync("data: [DONE]\n\n", ct);
            }

            AgentRunResult result;
            try
            {
                result = await assistantAgent.RunAsync(history, ct: runCt, chatOptions: runOptions);
            }
            catch (Exception ex) when (turnGuard.StoppedCode is { } stoppedCode && (ex is AssistantTurnStoppedException || ex.InnerException is AssistantTurnStoppedException))
            {
                HttpContext.RequestServices.GetService<ILogger<AssistantChatController>>()
                    ?.LogInformation("Assistant turn on thread {ThreadId} cut off ({Code}) at {Millis} millicredits", thread.Id, stoppedCode, UsageScope.RunningCreditsMillis);
                await EndTurnWithNoticeAsync(
                    NonWorkflowChargeResults.AssistantStopText(stoppedCode),
                    stoppedCode is NonWorkflowChargeCodes.TurnLimit or NonWorkflowChargeCodes.InsufficientCredits ? stoppedCode : null);
                return;
            }
            catch (Exception ex) when (UnpricedOf(ex) is { } unpriced)
            {
                // The agent's own resolution found an unpriced Platform model: an ordinary reply that says so.
                await EndTurnWithNoticeAsync(unpriced.Message, null);
                return;
            }
            catch (OperationCanceledException) when (wallClock.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                HttpContext.RequestServices.GetService<ILogger<AssistantChatController>>()
                    ?.LogWarning("Assistant turn on thread {ThreadId} cancelled after Assistant:MaxTurnSeconds ({Seconds}s)", thread.Id, maxTurnSeconds);
                await EndTurnWithNoticeAsync(NonWorkflowChargeResults.AssistantTurnTooLongText, null);
                return;
            }
            finally
            {
                heartbeatCts.Cancel();
                try { await heartbeat; } catch (OperationCanceledException) { }
            }

            // A reply the MODEL cut off is marked before anything else sees it. Truncation by the
            // provider's own output ceiling is silent by construction — a half sentence is a
            // perfectly valid answer — so the only way a reader can tell it from a completed one is
            // if something says so. The marker rides in the same string that is persisted and
            // streamed, so the thread on screen and the thread the model replays as its own past turn
            // both carry it.
            bool modelTruncated = AssistantChatLimits.IsModelTruncation(result.FinishReason);
            if (modelTruncated)
            {
                HttpContext.RequestServices.GetService<ILogger<AssistantChatController>>()
                    ?.LogWarning(
                        "Assistant turn on thread {ThreadId} was truncated by the provider's output limit " +
                        "(finishReason {FinishReason}, maxOutputTokens {MaxOutputTokens}, provider {ProviderKind})",
                        thread.Id, result.FinishReason, maxOutputTokens, providerKind);
            }

            string reply = modelTruncated
                ? result.Output + AssistantChatLimits.ModelTruncationMarker(maxOutputTokens)
                : result.Output;

            // The last line of the "names, never ids" rule: whatever the model wrote, no id or token reaches
            // the screen or the stored thread (which is replayed into every later prompt).
            reply = replySanitizer.Sanitize(reply);

            // The reply is bounded by the SAME cap as a user message, because it is persisted into
            // the same thread and replayed into every later prompt of it — an unbounded assistant
            // turn reopens exactly the hole an unbounded user message did, and the replay window
            // multiplies it rather than containing it. Applied SECOND, over the model-truncation
            // marker above: whichever bound actually bit is the one the reader ends up seeing, and
            // both of them say so.
            string assistantText = AssistantChatLimits.TruncateAssistantReply(reply, maxMessageChars);

            // Persist the assistant turn BEFORE text-end is emitted, so a stream that dies between
            // the two still leaves a complete record of the exchange.
            await AppendTurnAsync(thread, "assistant", assistantText, runCt);
            replyStored = true;

            // The SAME string is streamed, never result.Output: what the user reads on screen and
            // what the thread replays as the assistant's own past turn must not disagree, or a
            // follow-up about "the last thing you said" would cite text the model no longer has.
            await WriteEventAsync(jsonOptions, new { type = "text-delta", textDelta = assistantText }, ct);

            // Emit text-end message
            await WriteEventAsync(jsonOptions, new { type = "text-end" }, ct);

            // After the first exchange, ask a cheap model call for a real title. Any failure keeps
            // the truncated-message title already stored.
            if (firstExchange)
            {
                string? generated = null;
                try
                {
                    IChatClient titleClient = await guardedChatClients.GetAsync(AgentType.Assistant, null, runCt);
                    generated = await AssistantThreadTitler.GenerateAsync(titleClient, messageText, assistantText, runCt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException or AssistantTurnStoppedException)
                {
                    // A title is cosmetic: a cut-off turn simply keeps its fallback title.
                    generated = null;
                }

                if (generated is not null)
                {
                    thread.Title = generated;
                    await _db.SaveChangesAsync(runCt);
                }

                await WriteEventAsync(jsonOptions, new { type = "data-threadTitle", data = new { threadId = thread.Id, title = thread.Title } }, ct);
            }

            // Emit data-workflowDiff message if a workflow was proposed
            if (toolProvider.LastProposedDiff != null)
            {
                await WriteEventAsync(
                    jsonOptions,
                    new { type = "data-workflowDiff", data = toolProvider.LastProposedDiff },
                    ct);
            }

            // Emit finish message with token usage
            var inputTokens = result.InputTokens ?? 0;
            var outputTokens = result.OutputTokens ?? 0;
            // The provider's OWN finish reason, not a hardcoded "stop". Reporting every turn as a
            // clean stop is what made a truncated reply indistinguishable from a finished one in the
            // panel, whatever the text said.
            var finishMessage = new
            {
                type = "finish",
                finishReason = AssistantChatLimits.StreamFinishReason(result.FinishReason),
                usage = new { inputTokens, outputTokens }
            };
            await WriteEventAsync(jsonOptions, finishMessage, ct);

            // Emit [DONE] marker
            await EmitAsync("data: [DONE]\n\n", ct);
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
            // The turn's own bound fired outside the model call: either its hard wall clock
            // (Assistant:MaxTurnSeconds) or, after a disconnect nobody came back from, Assistant:DetachedTurnSeconds.
            // The reply never existed, so the thread gets an "interrupted" message saying what the turn did
            // before it stopped, rather than a user turn with no answer.
            turnFailed = !replyStored;
            HttpContext.RequestServices.GetService<ILogger<AssistantChatController>>()
                ?.LogInformation("Assistant turn on thread {ThreadId} was cancelled (max {MaxTurn}s, detached {Detached}s)", thread.Id, maxTurnSeconds, detachedSeconds);
            if (!replyStored)
                await StoreInterruptedAsync(thread, userTurn, interrupted, wallClock.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected and the cancellation surfaced here: same record, so the reply is never just lost.
            turnFailed = !replyStored;
            if (!replyStored)
                await StoreInterruptedAsync(thread, userTurn, interrupted, wallClockFired: false);
        }
        catch (Exception ex)
        {
            // The SSE headers are already sent, so an exception escaping here makes Kestrel abort
            // the response mid-stream and the browser shows only "Error in input stream". Log the
            // full exception (that is where operators diagnose it) and close the stream with a
            // GENERIC AI SDK `error` chunk: provider/key details are never shown to users, only a
            // reference id that matches the log line.
            turnFailed = true;
            string reference = HttpContext.TraceIdentifier;
            HttpContext.RequestServices.GetService<ILogger<AssistantChatController>>()
                ?.LogError(ex, "Assistant turn failed for thread {ThreadId} (ref {Reference})", thread.Id, reference);
            await TryWriteErrorAsync($"Something went wrong while answering. Please try again in a moment. (ref {reference})");

            // A turn that already DID something, or that nobody was watching, still leaves its record; one that
            // failed in front of a connected client with nothing done keeps its plain error and a retry.
            if (!replyStored && (interrupted.HasActions || ct.IsCancellationRequested))
                await StoreInterruptedAsync(thread, userTurn, interrupted, wallClockFired: false, emitFrames: false);
        }
        finally
        {
            watchCts.Cancel();
            if (detachedWatch is not null)
            {
                try { await detachedWatch; } catch (OperationCanceledException) { }
            }

            // The turn is done: release every re-attach subscriber (a later one still gets the whole log).
            _live?.Complete(turnFailed ? AssistantTurnState.Failed : AssistantTurnState.Completed);

            // The hold settles at everything the turn metered (model calls, tool loops, the title call), whether it
            // finished or not: the usage already happened and is recorded; at least the hold when a Platform usage
            // could not be priced. Nothing metered releases the hold. A settlement that fails is retried once, then
            // logged.
            await turnCharge.SettleAsync(UsageScope.Current);
        }
    }

    /// <summary>
    /// Cancels the turn once nobody has been attached to it, and no card has been waiting on a person, for
    /// <paramref name="limit"/>. Polls instead of arming one timer because "attached" changes while it runs: a
    /// screen that re-attaches, or a card that opens, restarts the clock. Stops when the turn ends or is cancelled.
    /// </summary>
    private static async Task WatchDetachedAsync(AssistantLiveTurn? live, TimeSpan limit, CancellationTokenSource turn, CancellationToken stop)
    {
        if (live is null)
            return;

        TimeSpan poll = TimeSpan.FromMilliseconds(Math.Clamp(limit.TotalMilliseconds / 10, 50, 5_000));
        try
        {
            while (!stop.IsCancellationRequested
                && live.State is AssistantTurnState.Running or AssistantTurnState.AwaitingApproval)
            {
                await Task.Delay(poll, stop);
                if (!live.IsUnattendedFor(limit))
                    continue;

                try { turn.Cancel(); }
                catch (ObjectDisposedException) { }
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The turn ended on its own, or is being torn down.
        }
    }

    /// <summary>
    /// Stores (and, unless the error frame already closed the stream, streams) the assistant message of a turn that
    /// stopped before it could answer: what it did before it stopped, in plain words. Best effort — a failure to
    /// save is logged, never thrown into the turn's cleanup.
    /// </summary>
    private async Task StoreInterruptedAsync(
        AssistantThread thread, AssistantMessage userTurn, AssistantInterruptedTurn interrupted, bool wallClockFired, bool emitFrames = true)
    {
        string text = interrupted.Compose(wallClockFired);
        try
        {
            await AppendTurnAsync(thread, "assistant", text, CancellationToken.None);
        }
        catch (Exception ex)
        {
            HttpContext.RequestServices.GetService<ILogger<AssistantChatController>>()
                ?.LogError(ex, "Could not store the interrupted-turn message for thread {ThreadId}", thread.Id);
            // The user must not lose the reply without notice: say so on the stream (when it is still open).
            if (emitFrames)
                await TryWriteErrorAsync("This reply could not be saved. Please ask again in a moment.");
            return;
        }

        if (!emitFrames)
            return;

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await WriteEventAsync(options, new { type = "text-delta", textDelta = text }, CancellationToken.None);
        await WriteEventAsync(options, new { type = "text-end" }, CancellationToken.None);
        await WriteEventAsync(options, new { type = "finish", finishReason = "stop", usage = new { inputTokens = 0, outputTokens = 0 } }, CancellationToken.None);
        await EmitAsync("data: [DONE]\n\n", CancellationToken.None);
    }

    /// <summary>
    /// Re-attaches a response to the turn the process already owns for a thread — the reply a client lost
    /// when its connection dropped (a phone screen switching off, a navigation, a reload). It appends no
    /// message, starts no turn and takes no credit: it is only a <b>subscriber</b>. The whole turn is
    /// replayed from the start (so a permission card that was on screen comes back with the same arguments,
    /// and a finished reply is shown in full) and then the frames still arriving are streamed until the turn
    /// ends. A thread with no live turn is a 404, so the client falls back to the stored history.
    /// </summary>
    /// <remarks>
    /// <c>POST /api/v1/assistant/chat</c> with <c>{ "threadId": "…", "attach": true }</c>. Deliberately the
    /// same stream path and the same frame contract as a new turn: the client parses both by frame
    /// <c>type</c>, so a re-attach needs no second decoder.
    /// </remarks>
    private async Task AttachToTurnAsync(AssistantChatRequest request, Guid userId, CancellationToken ct)
    {
        Guid? organizationId = _currentUser.OrganizationId;

        if (request.ThreadId == Guid.Empty)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "threadId is required to attach" }, ct);
            return;
        }

        // Ownership of the THREAD is the authority here, exactly as for a new turn: another user's thread is
        // reported as "not found", never "forbidden".
        bool ownsThread = await _db.AssistantThreads
            .AnyAsync(t => t.Id == request.ThreadId && t.UserId == userId && t.OrganizationId == organizationId, ct);

        if (!ownsThread)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Thread not found" }, ct);
            return;
        }

        AssistantTurnHub hub = ResolveTurnHub();
        AssistantLiveTurn? live = hub.FindFor(request.ThreadId, userId, organizationId);

        if (live is null)
        {
            // Nothing to attach to: the thread is idle — its turn never started in this process, or the
            // process was restarted and the (process-local) turn is gone. The client shows the stored history.
            HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "No turn is running on this thread" }, ct);
            return;
        }

        HttpContext.Response.Headers["X-Assistant-Thread-Id"] = request.ThreadId.ToString();
        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers["Cache-Control"] = "no-cache";
        HttpContext.Response.Headers["Connection"] = "keep-alive";

        using AssistantTurnSubscription subscription = live.Subscribe();

        // Bound the read by the turn's OWN remaining wall clock, so a subscriber whose turn never reaches
        // its finally (an exception before it, a reclaimed turn) returns instead of hanging forever.
        using var attachCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A few seconds of grace: the turn's own wall clock fires at the same moment, and the notice that says it was
        // stopped is stored and streamed just after, so a bound that fell exactly on it would drop that notice.
        TimeSpan remaining = live.RemainingLifetime;
        if (remaining <= TimeSpan.Zero)
            attachCts.Cancel();
        else
            attachCts.CancelAfter(remaining + TimeSpan.FromSeconds(5));
        CancellationToken attachCt = attachCts.Token;

        // First the frames already emitted, in order — the card, or the finished reply.
        foreach (string frame in subscription.Backlog)
        {
            if (!await WriteFrameAsync(frame, attachCt))
                return;
        }

        // Then whatever is still arriving, until the turn ends (which completes the reader).
        try
        {
            await foreach (string frame in subscription.Reader.ReadAllAsync(attachCt))
            {
                if (!await WriteFrameAsync(frame, attachCt))
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // The re-attached client left again, or the turn's wall clock ran out; the turn is unaffected.
        }
    }

    /// <summary>
    /// The process-wide turn hub: the host's registered instance when there is one, otherwise
    /// <see cref="AssistantTurnHub.Shared"/>. A turn outliving its connection is process state, so a host
    /// that never registered the hub must still be correct rather than 500 on every turn.
    /// </summary>
    private AssistantTurnHub ResolveTurnHub() =>
        HttpContext.RequestServices.GetService<AssistantTurnHub>() ?? AssistantTurnHub.Shared;

    /// <summary>
    /// The inference provider this turn will talk to (its kind sizes the output budget, its owning organization makes
    /// the turn BYO), or null when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// Null is a real answer, not a failure: it is the legacy-config fallback, a host with no
    /// provider row, and every test double that does not implement
    /// <see cref="IAgentChatClientProvider.ResolveProviderAsync"/>. The budget resolver treats
    /// "unknown kind" exactly as it treats "a kind that needs no ceiling", so a resolution hiccup
    /// can only ever cost the assistant an explicit ceiling — never the turn.
    /// </remarks>
    private async Task<ResolvedInferenceProvider?> TryResolveProviderAsync(CancellationToken ct)
    {
        try
        {
            return await _chatClientProvider.ResolveProviderAsync(AgentType.Assistant, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelUnpricedException)
        {
            // Not a hiccup: the model this turn would use cannot be priced, so the turn must not run.
            throw;
        }
        catch
        {
            // Attribution and sizing are both best-effort: the turn itself is already in flight.
            return null;
        }
    }

    /// <summary>The <see cref="ModelUnpricedException"/> behind <paramref name="ex"/>, directly or as its inner exception.</summary>
    private static ModelUnpricedException? UnpricedOf(Exception ex) =>
        ex as ModelUnpricedException ?? ex.InnerException as ModelUnpricedException;

    private async Task TryWriteErrorAsync(string errorText)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            // Through the turn's log as well, so a screen that re-attaches sees the failure, not a stream that just ends.
            await WriteEventAsync(options, new { type = "error", errorText }, CancellationToken.None);
            await EmitAsync("data: [DONE]\n\n", CancellationToken.None);
        }
        catch (Exception)
        {
            // The connection is already gone; nothing more can be reported.
        }
    }

    /// <summary>
    /// Builds the per-request tool provider. It is handed the request-scoped
    /// <see cref="ICurrentUser"/> rather than a user id, so authority is re-derived from the live
    /// caller on every tool call; tests override it to inject a proposed diff.
    /// </summary>
    /// <remarks>
    /// The per-area tool surfaces come from the request's own scope so they share this
    /// request's DbContext, caller and — critically — its singleton confirmation-token service. The
    /// override signature is unchanged, so a test that wraps this method keeps working; its own
    /// service provider must register the surfaces.
    /// </remarks>
    protected virtual AssistantToolProvider CreateToolProvider(
        IWorkflowEditDiffService diffService,
        IWorkflowExecutionService executionService,
        IAssistantExecutionGuard executionGuard) =>
        // B7: the execute preflight the ExecuteWorkflow tool runs — resolved from the request scope
        // like every other collaborator, so it shares this request's DbContext and the registered
        // estimator (a host that never registers it gets the tool's fallback, which prices through a
        // null estimator and refuses nothing). The composition itself lives in the shared factory so
        // the tool bridge publishes exactly the set this turn runs — see
        // AssistantToolProviderFactory.
        AssistantToolProviderFactory.Create(
            _db,
            diffService,
            executionService,
            executionGuard,
            _currentUser,
            HttpContext.RequestServices);

    /// <summary>
    /// Appends one turn to the thread with the next 1-based sequence number, persists it, and
    /// touches the thread's <c>UpdatedAt</c> so the thread list orders by real activity.
    /// </summary>
    private Task<AssistantMessage> AppendTurnAsync(
        AssistantThread thread,
        string role,
        string content,
        CancellationToken ct,
        string? metadataJson = null)
        => AssistantMessageAppender.AppendAsync(_db, thread, role, content, ct, metadataJson);

    /// <summary>
    /// Answers a turn the input guard (or the credit check) refused: the same stream frames and the same persisted
    /// exchange a model reply would produce, with the fixed text and no model call, plus an optional data part
    /// (<c>data-credits</c> when the workspace is out of credits). The thread keeps its fallback title (no
    /// title call is spent on a refusal).
    /// </summary>
    private async Task StreamCannedReplyAsync(
        AssistantThread thread,
        AssistantMessage userTurn,
        string reply,
        bool firstExchange,
        CancellationToken runCt,
        CancellationToken ct,
        object? dataPart = null)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        try
        {
            var messageId = Guid.NewGuid();
            _live?.SetMessageId(messageId);
            await WriteEventAsync(options, new { type = "start", messageId }, ct);
            string textId = Guid.NewGuid().ToString();
            await WriteEventAsync(options, new { type = "text-start", id = textId }, ct);

            await AppendTurnAsync(thread, "assistant", reply, runCt);

            await WriteEventAsync(options, new { type = "text-delta", textDelta = reply }, ct);
            await WriteEventAsync(options, new { type = "text-end" }, ct);
            if (dataPart is not null)
                await WriteEventAsync(options, dataPart, ct);
            if (firstExchange)
                await WriteEventAsync(options, new { type = "data-threadTitle", data = new { threadId = thread.Id, title = thread.Title } }, ct);
            await WriteEventAsync(options, new { type = "finish", finishReason = "stop", usage = new { inputTokens = 0, outputTokens = 0 } }, ct);
            await EmitAsync("data: [DONE]\n\n", ct);
            _live?.Complete(AssistantTurnState.Completed);
        }
        catch (OperationCanceledException)
        {
            // The client left; the exchange is persisted (or abandoned) exactly as for a model turn.
        }
    }

    /// <summary>A tool result as text, however the invoker handed it back (a JSON string, an element, an object).</summary>
    private static string? ToolResultText(object? result) => result switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(result)
    };

    /// <summary>
    /// The thread's newest <paramref name="maxMessages"/> persisted turns as
    /// <see cref="ChatMessage"/>s, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is taken from the NEWEST end, because the most recent turns are the ones the next
    /// reply actually depends on and they must always survive; the oldest are what get dropped. A
    /// thread shorter than the window is replayed in full, unchanged.
    /// </para>
    /// <para>
    /// The window is then trimmed of any LEADING non-user turns. Cutting a fixed-size window out of
    /// an alternating thread lands on an assistant turn whenever the parity falls that way, and a
    /// provider that requires the first message to use the <c>user</c> role (Anthropic's Messages
    /// API does) would reject the entire prompt rather than the stray turn. This can never empty the
    /// window: <c>ChatStream</c> appends the caller's new user turn before calling this, so the
    /// newest message is always a user turn and the trim always terminates on one.
    /// </para>
    /// </remarks>
    private async Task<List<ChatMessage>> LoadHistoryAsync(Guid threadId, int maxMessages, CancellationToken ct)
    {
        List<AssistantMessage> turns = await _db.AssistantMessages
            .Where(m => m.ThreadId == threadId)
            .OrderByDescending(m => m.Sequence)
            .Take(maxMessages)
            .ToListAsync(ct);

        // Newest-first was only the way to take the TRAILING window in SQL; the prompt needs the
        // window in conversation order.
        turns.Reverse();

        while (turns.Count > 0 && !ToChatRole(turns[0].Role).Equals(ChatRole.User))
            turns.RemoveAt(0);

        return turns
            .Select(m => new ChatMessage(ToChatRole(m.Role), m.Content))
            .ToList();
    }

    private static ChatRole ToChatRole(string role) => role switch
    {
        "assistant" => ChatRole.Assistant,
        "system" => ChatRole.System,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User
    };

    /// <summary>
    /// Writes one SSE <c>data:</c> frame and flushes it. The frame set and its order are a
    /// published contract — the client parses them by <c>type</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="ct"/> is the REQUEST's token. Once the client has gone, a frame has nowhere to
    /// go, so it is skipped (and a write that races the disconnect is swallowed) instead of throwing
    /// into a turn that is still running to persist its reply.
    /// </remarks>
    private Task WriteEventAsync(JsonSerializerOptions jsonOptions, object payload, CancellationToken ct) =>
        EmitAsync($"data: {JsonSerializer.Serialize(payload, jsonOptions)}\n\n", ct);

    /// <summary>
    /// Publishes one frame: it is logged on the turn (so a re-attach replays the turn from the start) and
    /// written to THIS response. The turn is not the connection, so a frame the dead connection cannot take
    /// is still recorded for whoever comes back.
    /// </summary>
    private async Task EmitAsync(string frame, CancellationToken ct)
    {
        _live?.Emit(frame);
        await WriteFrameAsync(frame, ct);
    }

    /// <summary>
    /// Writes one already-logged frame to THIS response only. Never logs, never broadcasts — this is the
    /// re-attach replay path, and the frame has already been recorded by <see cref="AssistantLiveTurn.Emit"/>.
    /// Returns false once the client has gone, so a subscriber stops rather than spinning on a dead stream.
    /// </summary>
    private async Task<bool> WriteFrameAsync(string frame, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        try
        {
            await HttpContext.Response.WriteAsync(frame, ct);
            await HttpContext.Response.Body.FlushAsync(ct);
            return true;
        }
        catch (Exception)
        {
            // The client disconnected mid-write: the turn keeps running and persists its reply.
            return false;
        }
    }

    /// <summary>
    /// Prepends the open-workflow context block to the FIRST user turn of the conversation
    /// history (so it stays visible across the whole multi-turn exchange, exactly as the old
    /// single-message path prepended it to that message). Returns the history unchanged when no
    /// workflowId is supplied, when no project is in context, or when the workflow doesn't belong
    /// to the project in context.
    /// </summary>
    private async Task<List<ChatMessage>> InjectWorkflowContextAsync(
        List<ChatMessage> history,
        Guid? workflowId,
        Guid? projectId,
        CancellationToken ct)
    {
        // If no workflow ID or it's null/empty, just return the history unchanged
        if (!workflowId.HasValue || workflowId == Guid.Empty)
            return history;

        // Without a project in context there is no "belongs to this project" guard to apply, so
        // the workflow is treated as absent rather than trusted — injecting it would echo another
        // user's workflow name and steps into the prompt on nothing more than a guessed id.
        if (!projectId.HasValue)
            return history;

        // Load the workflow, scoped to the project in context (itself already verified to belong
        // to the caller).
        var workflow = await _db.WorkflowDefinitions
            .Where(w => w.Id == workflowId && w.ProjectId == projectId.Value && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot)
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(ct);

        // If workflow not found or belongs to a different project, return the history unchanged
        if (workflow == null)
            return history;

        // Build a compact JSON summary of the workflow steps
        var stepSummaries = workflow.Steps
            .OrderBy(s => s.StepOrder)
            .Select(s => new
            {
                stepOrder = s.StepOrder,
                label = s.Label,
                agentDefinitionId = s.AgentDefinitionId,
                stepType = s.StepType.ToString()
            })
            .ToList();

        var workflowContext = new
        {
            workflowId = workflow.Id,
            workflowName = workflow.Name,
            steps = stepSummaries
        };

        var jsonContext = JsonSerializer.Serialize(
            workflowContext,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Prepend workflow context with a clear label
        var contextBlock = $"""
[WORKFLOW CONTEXT]
{jsonContext}

The workflow above is the currently open workflow. When proposing changes, include all steps from this workflow, marking unchanged steps appropriately.
[END WORKFLOW CONTEXT]
""";

        int firstUserIndex = history.FindIndex(m => m.Role == ChatRole.User);
        if (firstUserIndex >= 0)
        {
            history[firstUserIndex] = new ChatMessage(
                ChatRole.User,
                contextBlock + history[firstUserIndex].Text);
        }
        else
        {
            history.Insert(0, new ChatMessage(ChatRole.User, contextBlock));
        }

        return history;
    }
}
