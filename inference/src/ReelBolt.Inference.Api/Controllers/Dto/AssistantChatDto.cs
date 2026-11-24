using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Request to send ONE turn to the platform assistant.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two shapes.</b> The <b>primary, documented</b> shape is flat:
/// <c>{ "threadId": "…", "message": "…", "projectId": "…?", "workflowId": "…?" }</c>. The AI-SDK
/// shape that <c>@assistant-ui/react-data-stream</c> posts —
/// <c>{ "messages": [ { "role": "user", "content": … } ], "tools": {}, "runConfig": {} }</c>,
/// where <c>content</c> is a plain string or an array of <c>{ "type": "text", "text": … }</c>
/// parts — is a <b>tolerated transport fallback</b>, not a second contract.
/// </para>
/// <para>
/// Crucially, <c>messages[]</c> is a fallback for <b>the current turn only</b>: it is <b>not</b> a
/// history source, and whatever it carries is never replayed as conversation history. History stays
/// server-persisted against the thread (<c>AssistantThread</c>/<c>AssistantMessage</c>), so a
/// request carries only the new turn whichever shape it arrives in.
/// </para>
/// <para>
/// <b>A missing <c>threadId</c> is auto-created, not rejected.</b> <see cref="ThreadId"/> carries no
/// C# default on purpose, so a static reader of the published contract still sees it named — but an
/// <b>absent</b> <c>threadId</c> binds as <see cref="Guid.Empty"/>, because System.Text.Json gives a
/// struct constructor parameter its <c>default</c> rather than failing the request.
/// <see cref="Guid.Empty"/> therefore means "no thread yet", and the endpoint allocates one. A
/// <c>threadId</c> that is <b>present but unbindable</b> — <c>null</c>, <c>""</c>, or a non-Guid
/// string — is a different case, and is <b>not</b> silently smoothed over: there is a value to bind
/// and it does not bind, so the serializer rejects the body and the <c>[ApiController]</c> pipeline
/// answers 400 before this action runs (pinned by
/// <c>AssistantChatControllerTests.ChatStream_WithAnUnbindableThreadId_IsRejectedAtTheJsonBoundary</c>).
/// A thread id that is <b>present, well-formed and unknown</b> is still a 404 — never a 403, which
/// would confirm that it exists.
/// </para>
/// <para>
/// The id of the thread the turn was actually appended to — supplied or allocated — is returned in
/// the <c>X-Assistant-Thread-Id</c> response header on <b>every</b> successful <c>POST …/chat</c>
/// turn, so a client can bind to it idempotently.
/// </para>
/// <para>
/// The assistant is mounted globally, so <c>projectId</c> is <b>per-call context</b>, not route
/// state: the page the assistant is open on supplies it, and a page with no project supplies
/// <c>null</c>. That is why this lives in the body rather than in the URL — a route parameter
/// cannot express "no project". A project the caller does not own is treated as <b>absent</b>,
/// never rejected, and never leaks whether it exists.
/// </para>
/// <para>
/// Unknown top-level properties are ignored by the default MVC serializer, so the <c>tools</c>,
/// <c>runConfig</c>, <c>state</c> and <c>system</c> keys the AI-SDK client also sends must not fail
/// validation. None of them is re-declared here: history is server-persisted, and none of them
/// carries anything this endpoint consumes.
/// </para>
/// </remarks>
public record AssistantChatRequest(
    /// <summary>
    /// The server-persisted thread this turn belongs to. Owned by the caller; a thread id that
    /// exists but belongs to another user is reported as 404, never 403. Carries no default so the
    /// published contract keeps naming it; an absent value binds to <see cref="Guid.Empty"/>, which
    /// means "no thread yet" and the endpoint allocates one. A present-but-unbindable value is a 400
    /// at the JSON boundary instead.
    /// </summary>
    Guid ThreadId,
    /// <summary>
    /// The user's new message, in the primary flat shape. Prior turns come from the thread, not
    /// from the request. When empty, the tolerant AI-SDK <see cref="Messages"/> fallback is used.
    /// </summary>
    string? Message = null,
    /// <summary>
    /// Optional project context for this call. Null on a page with no project — the request is
    /// still accepted, and project-scoped tools report that there is no project in context.
    /// A project the caller does not own is treated as absent rather than rejected.
    /// </summary>
    Guid? ProjectId = null,
    /// <summary>
    /// Optional currently-open workflow id, used to inject a workflow context block into the
    /// first user turn of the conversation.
    /// </summary>
    Guid? WorkflowId = null)
{
    /// <summary>
    /// Re-attach to the thread's live (or just-finished) turn instead of starting a new one. Set by a
    /// client that reopened a conversation whose turn is still running server-side — the reply the panel
    /// lost when the connection dropped. An attach carries <b>no</b> message: it appends nothing and
    /// starts no second turn, it only subscribes the response to the turn the process already owns.
    /// A <c>threadId</c> naming no live turn is a 404.
    /// </summary>
    /// <remarks>
    /// Non-positional so every existing C# call site compiles. It is a transport flag, never part of the
    /// conversation: it is not persisted and never reaches the model.
    /// </remarks>
    [JsonPropertyName("attach")]
    public bool Attach { get; init; }

    /// <summary>
    /// Set ONLY in-process by <c>AssistantWakeTurnRunner</c> when the platform wakes a thread because a watched run
    /// finished. <see cref="JsonIgnoreAttribute"/> and <c>internal</c>: a client can never set it over the wire, so
    /// no request can pose as a wake turn.
    /// </summary>
    [JsonIgnore]
    internal AssistantWakeInfo? Wake { get; init; }

    /// <summary>
    /// The AI-SDK <c>messages[]</c> array, posted by <c>@assistant-ui/react-data-stream</c>.
    /// </summary>
    /// <remarks>
    /// Non-positional and nullable so every existing C# call site keeps compiling, and so a body
    /// that carries only the flat <c>message</c> is unaffected. Read it through
    /// <see cref="ResolveMessageText"/> rather than directly: only the LATEST <c>user</c> entry's
    /// text is a turn, and nothing else in the array is a history source.
    /// </remarks>
    [JsonPropertyName("messages")]
    public List<ChatMessageInput>? Messages { get; init; }

    /// <summary>
    /// Where the user is in the web app (route, page kind, title, tab, entity ids, page-supplied
    /// extras). Untrusted HINTS: bounded and sanitised by <c>AssistantSessionContext</c> before the
    /// model sees any of it, and never used for authority — identity and admin rights come from the
    /// JWT, and every id is re-validated by the tools. Non-positional so existing call sites compile.
    /// </summary>
    [JsonPropertyName("pageContext")]
    public AssistantPageContextDto? PageContext { get; init; }

    /// <summary>
    /// The picture (thumbnail) document open in the picture editor, when the turn is sent from it.
    /// Validated like <see cref="WorkflowId"/>: it must belong to the project in context, otherwise it
    /// is treated as ABSENT (never rejected, never a hint that it exists). Non-positional so existing
    /// call sites compile.
    /// </summary>
    [JsonPropertyName("imageDocumentId")]
    public Guid? ImageDocumentId { get; init; }

    /// <summary>
    /// The text of the user turn this request carries, whichever shape it arrived in.
    /// </summary>
    /// <remarks>
    /// Flat <c>message</c> wins when non-empty (it is the documented shape); otherwise the last
    /// <c>user</c> entry of <see cref="Messages"/> supplies the turn. Empty when neither does.
    /// Never throws — see <see cref="ExtractMessageText"/>.
    /// </remarks>
    public string ResolveMessageText() => ExtractMessageText(this);

    /// <summary>
    /// Extracts the current turn's text from <paramref name="request"/>: the flat
    /// <see cref="Message"/> when non-empty, else the text of the <b>last</b> entry in
    /// <see cref="Messages"/> whose role is <c>user</c>, else <see cref="string.Empty"/>.
    /// </summary>
    /// <remarks>
    /// Every branch is total over arbitrary wire input, because this runs before any validation:
    /// a <c>null</c> request, an absent or empty <c>messages</c>, an entry with no <c>role</c>, a
    /// <c>content</c> that is <c>null</c>, a number, or any other JSON token, and a content array
    /// whose parts are not <c>{ "type": "text", "text": … }</c> all resolve to
    /// <see cref="string.Empty"/> rather than throwing. Non-text content parts are ignored, never
    /// guessed at.
    /// </remarks>
    public static string ExtractMessageText(AssistantChatRequest request)
    {
        if (request is null)
            return string.Empty;

        if (!string.IsNullOrEmpty(request.Message))
            return request.Message;

        List<ChatMessageInput>? messages = request.Messages;
        if (messages is null || messages.Count == 0)
            return string.Empty;

        // Newest-first: only the LATEST user turn is "the turn being sent". Earlier entries are
        // the client's own idea of the conversation and are deliberately not replayed — history
        // is the thread's persisted turns.
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            ChatMessageInput? candidate = messages[i];
            if (candidate is null)
                continue;

            if (!string.Equals(candidate.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            return ChatMessageInput.ExtractContentText(candidate.Content);
        }

        return string.Empty;
    }
}

/// <summary>
/// One entry of the AI-SDK <c>messages[]</c> array.
/// </summary>
/// <remarks>
/// Both properties are declared here rather than positionally, and <c>content</c> is a
/// <see cref="JsonElement"/> rather than a custom type with a <c>JsonConverter</c>: System.Text.Json
/// deserializes any JSON token — a string, an array, a number, <c>null</c> — into a
/// <see cref="JsonElement"/> natively, and being a struct it can never be <c>null</c> and therefore
/// never trips the implicit <c>[Required]</c> that a non-nullable reference type would acquire under
/// <c>[ApiController]</c>. A converter would have to re-implement that tolerance and could throw on
/// a shape it did not anticipate.
/// </remarks>
public record ChatMessageInput
{
    /// <summary>
    /// The entry's role. Nullable because a malformed entry may omit it, and a missing role is
    /// simply an entry that is not a user turn — not a request-level validation failure.
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>
    /// The entry's content: a plain string, or an array of content parts. Any other token is
    /// tolerated and contributes no text.
    /// </summary>
    [JsonPropertyName("content")]
    public JsonElement Content { get; init; }

    /// <summary>
    /// The text of a <c>content</c> value: the string itself, or every <c>{ "type": "text",
    /// "text": … }</c> part of an array concatenated in order.
    /// </summary>
    /// <remarks>
    /// Total by construction — every <see cref="JsonElement"/> accessor that throws on a mismatched
    /// <see cref="JsonElement.ValueKind"/> is guarded by that kind first, so a number, an object, a
    /// boolean, <c>null</c> or the default (undefined) element all yield
    /// <see cref="string.Empty"/>. Parts that are not text parts are ignored.
    /// </remarks>
    public static string ExtractContentText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                List<string> parts = [];
                foreach (JsonElement part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!part.TryGetProperty("type", out JsonElement type)
                        || type.ValueKind != JsonValueKind.String
                        || !string.Equals(type.GetString(), "text", StringComparison.Ordinal))
                        continue;

                    if (part.TryGetProperty("text", out JsonElement text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(text.GetString() ?? string.Empty);
                    }
                }

                return string.Concat(parts);

            default:
                return string.Empty;
        }
    }
}

/// <summary>
/// One row of <c>GET /api/v1/assistant/threads</c>.
/// </summary>
/// <remarks>
/// <see cref="AwaitingReply"/> is true when the thread's newest message is the user's and nothing is
/// answering it any more (its request died); <see cref="ReplyInProgress"/> is true while this process
/// is still answering it. The panel shows them as "No reply yet" and "Still answering…" — before
/// both existed, an unanswered question simply vanished from view after navigating away. Appended
/// with defaults, so every existing construction and consumer is unchanged.
/// </remarks>
public record AssistantThreadSummaryDto(
    Guid Id,
    string Title,
    Guid? ProjectId,
    DateTime UpdatedAt,
    bool AwaitingReply = false,
    bool ReplyInProgress = false);

/// <summary>
/// <c>POST /api/v1/assistant/threads</c> response: the id of the newly created thread.
/// </summary>
public record CreatedAssistantThreadResponse(Guid Id);

/// <summary>
/// Optional body of <c>POST /api/v1/assistant/threads</c>. Both fields are optional so a thread
/// can be opened from a page that has no project context.
/// </summary>
public record CreateAssistantThreadRequest(
    Guid? ProjectId = null,
    string? Title = null);

/// <summary>
/// Body of <c>PATCH /api/v1/assistant/threads/{threadId}</c>. A null field is left unchanged, so
/// a rename does not implicitly un-archive and vice versa.
/// </summary>
public record UpdateAssistantThreadRequest(
    string? Title = null,
    bool? Archived = null);

/// <summary>
/// <c>GET /api/v1/assistant/threads/{threadId}/turn</c>: the state of the thread's most recent turn, so a
/// reopening client can tell an idle thread from one whose turn is still running and re-attach to it.
/// </summary>
/// <remarks>
/// <see cref="State"/> is <c>idle</c> when this process is not holding a turn for the thread, and
/// otherwise <c>running</c>, <c>awaiting_approval</c>, <c>completed</c> or <c>failed</c>.
/// <see cref="TurnId"/> is the turn to name when re-attaching; <see cref="MessageId"/> is the assistant
/// message the stream is keyed on; <see cref="PendingApprovalId"/> is the open permission card while the
/// state is <c>awaiting_approval</c>. The optional fields are omitted when absent.
/// </remarks>
public record AssistantTurnStateDto(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? TurnId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? MessageId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PendingApprovalId = null,
    int PendingWatches = 0);

/// <summary>
/// <c>GET /api/v1/assistant/threads/{threadId}</c>: the thread plus its full, ordered history.
/// </summary>
public record AssistantThreadDetailDto(
    Guid Id,
    string Title,
    Guid? ProjectId,
    IReadOnlyList<AssistantMessageDto> Messages,
    bool ReplyInProgress = false,
    IReadOnlyList<AssistantPendingRunWatchDto>? PendingRunWatches = null);

/// <summary>A run the assistant is waiting on in this thread (<c>pendingRunWatches[]</c>): its label and when the watch was armed. Never ids.</summary>
public record AssistantPendingRunWatchDto(string RunLabel, DateTime CreatedAt);

/// <summary>On a platform-written wake message (<c>wake</c>): the finished run's status word and label.</summary>
public record AssistantWakeDto(string Status, string RunLabel);

/// <summary>
/// One persisted turn of a thread. <see cref="Sequence"/> is 1-based and unique per thread.
/// </summary>
public record AssistantMessageDto(
    Guid Id,
    string Role,
    string Content,
    int Sequence,
    DateTime CreatedAt,
    string? Kind = null,
    AssistantWakeDto? Wake = null);


/// <summary>The page the web app says the user is on. Every field is an untrusted hint.</summary>
public sealed record AssistantPageContextDto
{
    public string? Path { get; init; }
    public string? Kind { get; init; }
    public string? Title { get; init; }
    public string? DocumentTitle { get; init; }
    public string? Tab { get; init; }
    public Dictionary<string, string?>? EntityIds { get; init; }
    public JsonElement? Extra { get; init; }
}

/// <summary>What a platform-started wake turn carries: the run it is about, for the stored user turn's metadata.</summary>
internal sealed record AssistantWakeInfo(Guid ExecutionId, string Status, string RunLabel)
{
    /// <summary>
    /// Whether stored metadata describes this same wake, compared by parsed content (kind, execution id, status) and not by
    /// text: Postgres jsonb re-orders keys and re-spaces the text it stores.
    /// </summary>
    internal bool Matches(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(metadataJson);
            JsonElement root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "run_wake"
                && root.TryGetProperty("executionId", out JsonElement id) && id.TryGetGuid(out Guid g) && g == ExecutionId
                && root.TryGetProperty("status", out JsonElement st) && st.GetString() == Status;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The stored form: <c>{"kind":"run_wake","executionId":..,"status":..,"runLabel":..}</c> (deterministic).</summary>
    internal string ToMetadataJson() => JsonSerializer.Serialize(new
    {
        kind = "run_wake",
        executionId = ExecutionId,
        status = Status,
        runLabel = RunLabel
    });
}

/// <summary><c>GET /api/v1/assistant/activity</c>: live turns, unread replies and pending run watches of the caller. Titles only.</summary>
public record AssistantActivityDto(
    IReadOnlyList<AssistantWritingDto> Writing,
    IReadOnlyList<AssistantUnreadDto> Unread,
    int PendingWatches);

/// <summary>A turn being written right now. <c>State</c> is <c>running</c> or <c>awaiting_approval</c>; <c>Kind</c> is <c>reply</c> or <c>run_wake</c>.</summary>
public record AssistantWritingDto(Guid ThreadId, string Title, string State, DateTime StartedAt, string Kind);

/// <summary>A thread whose newest assistant message the person has not seen. <c>Sequence</c> is what to send to <c>POST threads/{id}/read</c>.</summary>
public record AssistantUnreadDto(Guid ThreadId, string Title, int Sequence, string Kind, DateTime At);

/// <summary>Body of <c>POST /api/v1/assistant/threads/{threadId}/read</c>; <c>ThroughSequence</c> defaults to the latest message.</summary>
public record MarkAssistantThreadReadRequest(int? ThroughSequence = null);
