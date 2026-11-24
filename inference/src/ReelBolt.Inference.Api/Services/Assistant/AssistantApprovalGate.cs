using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Observability;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The server-side "ask permission" step in front of every assistant tool that writes
/// (docs/design/assistant-approvals.md). One gate serves one chat turn.
/// </summary>
public interface IAssistantApprovalGate
{
    /// <summary>
    /// Remembers what a <c>Propose*</c> call returned, so the matching <c>Apply*</c> can describe the real
    /// change on its card. Never throws.
    /// </summary>
    void NoteProposal(string toolName, object? result);

    /// <summary>
    /// Runs a gated tool: asks the user first (unless they switched on auto mode), runs <paramref name="run"/>
    /// only when they allow it, and reports how it went. When the user declines — or does not answer — the
    /// tool never runs and the returned result tells the model so.
    /// </summary>
    Task<object?> RunAsync(string toolName, AIFunctionArguments args, Func<ValueTask<object?>> run, CancellationToken ct);
}

/// <summary>
/// <see cref="IAssistantApprovalGate"/> for one turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authority is unchanged.</b> The gate sits in FRONT of the tool body, which still verifies its
/// confirmation token, the caller's access and role, and the plan. Allowing a card never widens what a tool
/// may do; it only lets a tool that was going to be refused-or-run get that far.
/// </para>
/// <para>
/// <b>Auto mode is read live</b> from the user's row on every gated call and is never held in a field: a user
/// who switches it on mid-turn (or answers a card with "Always allow") changes the very next action.
/// </para>
/// <para>
/// <b>Fail closed.</b> While a card is pending nothing runs. If nobody answers in time, or the turn itself is
/// cancelled, the request expires and the tool does not run. The only way past a card is an explicit
/// <c>allow</c>, or the user's own auto mode.
/// </para>
/// <para>
/// <b>A dropped connection is not an answer.</b> A card does NOT expire because the browser that showed it went
/// away (a phone screen switching off, a navigation): the turn belongs to the process, not the connection
/// (<see cref="AssistantTurnHub"/>), and a screen that re-attaches replays the card and may still answer it
/// through <c>POST /api/v1/assistant/approvals/{id}</c>. Only the card's own timeout
/// (<see cref="TimeoutKey"/>), or the turn being cancelled, settles it as expired — so an expired card always
/// means "nobody answered in time", never "the connection dropped".
/// </para>
/// </remarks>
public sealed class AssistantApprovalGate : IAssistantApprovalGate
{
    public const string TimeoutKey = "Assistant:ApprovalTimeoutSeconds";
    public const int DefaultTimeoutSeconds = 600;

    /// <summary>
    /// Auto-approve mode still shows a card when one action's estimated cost is above this many credits.
    /// A value that is missing, not a number or &lt;= 0 falls back to <see cref="DefaultAutoApproveCostCardCredits"/>:
    /// the limit can be raised but never silently switched off.
    /// </summary>
    public const string AutoApproveCostCardKey = "Assistant:AutoApproveCostCardCredits";
    public const double DefaultAutoApproveCostCardCredits = 25;

    public static double ResolveAutoApproveLimit(IConfiguration? configuration) =>
        double.TryParse(configuration?[AutoApproveCostCardKey], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v > 0
            ? v
            : DefaultAutoApproveCostCardCredits;

    /// <summary>The status caption shown while a card is waiting for the user.</summary>
    public const string WaitingCaption = "Waiting for your OK";

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AssistantToolContext _context;
    private readonly InferenceApiDbContext _db;
    private readonly AssistantApprovalBroker _broker;
    private readonly AssistantApprovalCardBuilder _cards;
    private readonly Func<AssistantActionFrame, CancellationToken, Task> _emit;
    private readonly Func<string, CancellationToken, Task>? _caption;
    private readonly Guid? _organizationId;
    private readonly TimeSpan _timeout;
    private readonly double _autoLimit;
    private readonly ILogger? _logger;
    // T-020: reporting only. The gate observes what it already knows and hands nothing back to the
    // decision, so the card path is byte-identical whether a detector is attached or not.
    private readonly IProductHealthSignalDetector _signals;
    private readonly ConcurrentDictionary<string, ProposalRecord> _proposals = new(StringComparer.Ordinal);

    /// <param name="context">This turn's caller and page context.</param>
    /// <param name="organizationId">The caller's active organization — an answer must come from the same one.</param>
    /// <param name="emit">Writes one <c>data-assistantAction</c> frame to the chat stream; failures are swallowed here.</param>
    /// <param name="caption">Writes a <c>data-toolStatus</c> caption; optional.</param>
    public AssistantApprovalGate(
        AssistantToolContext context,
        Guid? organizationId,
        InferenceApiDbContext db,
        AssistantApprovalBroker broker,
        AssistantApprovalCardBuilder cards,
        IConfiguration? configuration,
        Func<AssistantActionFrame, CancellationToken, Task> emit,
        Func<string, CancellationToken, Task>? caption = null,
        ILogger? logger = null,
        IProductHealthSignalDetector? signals = null)
    {
        _signals = signals ?? ProductHealthSignals.Ambient;
        _context = context;
        _organizationId = organizationId;
        _db = db;
        _broker = broker;
        _cards = cards;
        _emit = emit;
        _caption = caption;
        _logger = logger;

        int seconds = configuration?.GetValue<int?>(TimeoutKey) ?? DefaultTimeoutSeconds;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600));
        _autoLimit = ResolveAutoApproveLimit(configuration);
    }

    public void NoteProposal(string toolName, object? result)
    {
        try
        {
            if (ToObject(result) is not { } json)
                return;

            if (AssistantApprovalCardBuilder.Property(json, "confirmationToken") is { ValueKind: JsonValueKind.String } token
                && token.GetString() is { Length: > 0 } key)
                _proposals[key] = new ProposalRecord(toolName, json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A proposal that cannot be read only costs its card some detail; the Apply still asks.
        }
    }

    public async Task<object?> RunAsync(
        string toolName, AIFunctionArguments args, Func<ValueTask<object?>> run, CancellationToken ct)
    {
        ProposalRecord? proposal = ToolArgs.GetString(args, "confirmationToken") is { } token
            && _proposals.TryGetValue(token, out ProposalRecord? known)
                ? known
                : null;

        ApprovalVerdict verdict;
        try
        {
            verdict = await _cards.BuildAsync(toolName, args, _context, proposal, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail open on a card that could not be built: ask with the generic one instead. A charged
            // action's generic card stays Paid with an unknown cost (SEC-EF-10), never "Normal" while it charges.
            _logger?.LogWarning(ex, "Could not build the permission card for {Tool}; asking with a generic one", toolName);
            verdict = ApprovalVerdict.Ask(_cards.FallbackCard(toolName));
        }

        // A call the builder could not place is REFUSED, never run: "no card" must not mean "no check".
        // (The tool would refuse it too; running it anyway is exactly how a card/tool disagreement turned
        // into a write with no human decision.)
        if (verdict.Refusal is { } refusal)
            return JsonSerializer.Serialize(new { error = refusal });

        // The one call with nothing to approve yet: it can only ask the model a question. The gate answers it
        // itself; the tool is never invoked on the claim that it "will only ask".
        if (verdict.Answer is { } question)
            return question;

        ApprovalCard card = verdict.Card!;

        bool auto = await IsAutoAsync(ct);

        // A restore, a scheduled post and a cancelled post are never auto-approved: their card waits for the
        // user's own click, whatever the mode and whatever the cost limit (so no "over the limit" note either).
        // So is a run that allows sending the customer's media to AI services (in auto mode and on a turn the platform
        // woke): its card names the steps that send, and approving it is the consent, never a standing "always allow".
        // So is a card that shows a statement for the person to confirm (an object avatar's object statement): approving
        // it is their tick, which nothing automatic may give.
        bool alwaysAsk = AssistantApprovalCatalog.AlwaysAsksUser(toolName) || card.AllowsMediaEgress || card.Statement is not null;
        if (alwaysAsk)
            auto = false;

        // Auto mode still asks when this one action costs more than the user's limit (compared against the
        // card's own p90 required figure), or, for a run whose cost could not be worked out, always.
        AssistantAutoApproveNote? note = null;
        if (auto && (card.CostUnestimated || card.CostCredits is { } c && c > _autoLimit))
        {
            note = new AssistantAutoApproveNote(_autoLimit, card.CostUnestimated ? null : card.CostCredits);
            auto = false;
        }

        Guid id = Guid.NewGuid();
        AssistantActionFrame frame = new(
            id, AssistantActionState.Pending, auto ? AssistantActionMode.Auto : AssistantActionMode.Ask,
            card.Kind, card.Risk, card.Title, card.Summary, card.Changes, card.ProjectName, card.Cost,
            AutoNote: note);

        if (!auto)
        {
            ApprovalAnswer answer;
            using (ApprovalTicket ticket = _broker.Open(id, _context.UserId, _organizationId))
            {
                await EmitAsync(frame with { ExpiresAt = DateTime.UtcNow + _timeout }, ct);
                await CaptionAsync(WaitingCaption, ct);
                try
                {
                    answer = await WaitAsync(ticket, ct);
                }
                catch (OperationCanceledException)
                {
                    // The turn was cancelled while the card was open (its wall clock, or nobody came back):
                    // the card can no longer be answered, so say so on the turn's record instead of leaving a
                    // pending card behind for a screen that re-attaches later.
                    await EmitAsync(frame with { State = AssistantActionState.Expired }, CancellationToken.None);
                    throw;
                }
            }

            if (!answer.Allow)
            {
                await EmitAsync(frame with { State = answer.Expired ? AssistantActionState.Expired : AssistantActionState.Declined }, ct);
                await CaptionAsync(AssistantToolProgress.CaptionFor(toolName), ct);
                return DeclinedResult(answer.Expired);
            }

            if (answer.AlwaysAllow && !alwaysAsk)
                await SetAutoAsync(ct);

            await EmitAsync(frame with { State = AssistantActionState.Allowed }, ct);
            await CaptionAsync(AssistantToolProgress.CaptionFor(toolName), ct);
        }

        object? result;
        try
        {
            // Only a card that was shown and allowed marks the call (auto never reaches here for such a card).
            using IDisposable? mediaEgress = card.AllowsMediaEgress && !auto ? AssistantMediaEgressApproval.Enter(card.GrantedStepOrders) : null;
            using IDisposable? statement = card.Statement is { } shown && !auto ? AssistantStatementApproval.Enter(shown) : null;
            result = await run();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The turn ends here without a reply: reporting only, and the failure is still rethrown
            // exactly as before so the caller's own error handling is untouched (T-020).
            ObserveAbandonedTurn();
            await EmitAsync(frame with { State = AssistantActionState.Failed, Outcome = FailedText }, ct);
            throw;
        }

        // The tool's own result, observed and handed straight back: the value returned to the model is
        // the identical object the tool produced, whatever the detector did with its text.
        ObserveToolResult(toolName, result);
        await EmitAsync(Finished(frame, card, toolName, ToObject(result)), ct);
        return result;
    }

    /// <summary>
    /// Reports a tool result to the ambient detectors (T-020): a <c>{"ok": false}</c> payload, and each
    /// workflow step a run report says failed. Deterministic and metadata-only; the result is not altered.
    /// </summary>
    private void ObserveToolResult(string toolName, object? result) =>
        _signals.ObserveToolResult(new ToolCallObservation(
            "assistant.approval_gate",
            _organizationId ?? Guid.Empty,
            _context.UserId,
            _context.ProjectId,
            ExecutionId: null,
            toolName,
            ResultText(result)));

    /// <summary>Reports a turn that ended on an error with no reply (T-020). Reporting only.</summary>
    private void ObserveAbandonedTurn() =>
        _signals.ObserveTurnOutcome(new TurnOutcomeObservation(
            "assistant.approval_gate",
            _organizationId ?? Guid.Empty,
            _context.UserId,
            _context.ProjectId,
            EndedInError: true,
            HadReply: false));

    /// <summary>A tool result as the JSON text the detector reads, however the invoker handed it back.</summary>
    private static string? ResultText(object? result) => result switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(result)
    };

    /// <summary>What the model reads when the user said no, or never answered.</summary>
    public static string DeclinedResult(bool expired) => JsonSerializer.Serialize(new
    {
        declined = true,
        expired,
        message = expired
            ? "The user did not answer in time, so nothing was changed. Do not try again; say so briefly and ask what they would like to do."
            : "The user chose not to allow this, so nothing was changed. Do not try again; say so briefly and ask what they would like instead."
    });

    private const string FailedText = "That didn't go through.";

    private static AssistantActionFrame Finished(AssistantActionFrame frame, ApprovalCard card, string toolName, JsonElement? result)
    {
        if (result is { } json && FailureOf(json) is { } failure)
            return frame with { State = AssistantActionState.Failed, Outcome = failure };

        ApprovalToolInfo info = AssistantApprovalCatalog.Describe(toolName);
        bool already = result is { } r
            && AssistantApprovalCardBuilder.Property(r, "alreadyApplied") is { ValueKind: JsonValueKind.True };

        Guid? workflowId = card.WorkflowId;
        if (result is { } payload
            && AssistantApprovalCardBuilder.Property(payload, "workflowId") is { ValueKind: JsonValueKind.String } w
            && Guid.TryParse(w.GetString(), out Guid created))
            workflowId = created;

        return frame with
        {
            State = AssistantActionState.Done,
            Outcome = already ? "Already up to date." : info.DoneText,
            Refresh = new AssistantActionRefresh(card.ProjectId, workflowId, card.DocumentId)
        };
    }

    /// <summary>A plain-language reason when a tool's result says it did not do the work; null when it did.</summary>
    private static string? FailureOf(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return null;

        string? error = AssistantApprovalCardBuilder.Property(result, "error") is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
        string? code = AssistantApprovalCardBuilder.Property(result, "code") is { ValueKind: JsonValueKind.String } c ? c.GetString() : null;
        bool notExecuted = AssistantApprovalCardBuilder.Property(result, "executed") is { ValueKind: JsonValueKind.False };

        if (string.IsNullOrWhiteSpace(error) && !notExecuted)
            return null;

        if (string.Equals(code, "INSUFFICIENT_CREDITS", StringComparison.Ordinal))
            return "There aren't enough credits for this.";
        if (error?.Contains("stale", StringComparison.OrdinalIgnoreCase) == true)
            return "The proposal was out of date, so nothing was changed.";
        if (error?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true)
            return "Too many runs were started recently. Try again in a little while.";
        if (error?.Contains("verif", StringComparison.OrdinalIgnoreCase) == true || string.Equals(code, "EMAIL_NOT_VERIFIED", StringComparison.Ordinal))
            return "Please confirm your email address first.";
        if (error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            return "It couldn't be found any more, so nothing was changed.";

        return FailedText;
    }

    private async Task<ApprovalAnswer> WaitAsync(ApprovalTicket ticket, CancellationToken ct)
    {
        // No registration on the browser's connection: a dropped connection must not settle the card (see the
        // class remarks). The card ends on an answer, on its own timeout, or when the turn is cancelled.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        Task expired = Task.Delay(Timeout.Infinite, timeout.Token);
        Task finished = await Task.WhenAny(ticket.Answer, expired);
        if (ReferenceEquals(finished, ticket.Answer))
            return await ticket.Answer;

        // The delay ended: either the turn itself was cancelled (abandon it) or the time ran out.
        ct.ThrowIfCancellationRequested();
        ticket.Expire();
        return await ticket.Answer;
    }

    private Task<bool> IsAutoAsync(CancellationToken ct) =>
        _db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == _context.UserId)
            .Select(u => u.AssistantAutoApprove)
            .FirstOrDefaultAsync(ct);

    // Loaded-then-saved rather than ExecuteUpdateAsync, which the InMemory provider the tests use cannot run;
    // only the one column changes either way.
    private async Task SetAutoAsync(CancellationToken ct)
    {
        ApplicationUser? user = await _db.ApplicationUsers.FirstOrDefaultAsync(u => u.Id == _context.UserId, ct);
        if (user is null || user.AssistantAutoApprove)
            return;

        user.AssistantAutoApprove = true;
        await _db.SaveChangesAsync(ct);
    }

    private async Task EmitAsync(AssistantActionFrame frame, CancellationToken ct)
    {
        try
        {
            await _emit(frame, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The chat stream is cosmetic here; the decision does not depend on the frame arriving.
            _logger?.LogDebug(ex, "Could not write an assistant action frame");
        }
    }

    private async Task CaptionAsync(string caption, CancellationToken ct)
    {
        if (_caption is null)
            return;

        try
        {
            await _caption(caption, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Could not write a status caption");
        }
    }

    /// <summary>
    /// A tool result as a JSON element, however the invoker handed it back: the element
    /// <c>AIFunctionFactory</c> serialises it to (a string result arrives as a JSON string holding the JSON
    /// text), a plain string, or an object. Anything that is not a JSON object yields null.
    /// </summary>
    internal static JsonElement? ToObject(object? result)
    {
        string? text = result switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Object } element => element.GetRawText(),
            JsonElement => null,
            _ => JsonSerializer.Serialize(result, Camel)
        };

        if (string.IsNullOrWhiteSpace(text) || text.TrimStart()[0] != '{')
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
