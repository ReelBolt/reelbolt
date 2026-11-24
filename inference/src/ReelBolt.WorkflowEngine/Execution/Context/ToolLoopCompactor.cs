using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// Pure, deterministic compaction of the message list one model call is about to carry, used by
/// <see cref="ToolLoopCompactingChatClient"/>. No I/O, no randomness, no model call: the same
/// messages always compact to the same result, so a repeated prefix stays byte-stable from one
/// call of the loop to the next.
///
/// <para>
/// What it will and will not do, in order of how much it protects:
/// </para>
/// <list type="bullet">
/// <item>Instructions, the original prompt, every assistant text (including the one that may carry
/// the final answer) and every other kind of content are NEVER touched. Only
/// <see cref="FunctionResultContent"/> payloads and, as a last resort, string arguments of
/// <see cref="FunctionCallContent"/> are rewritten.</item>
/// <item>A call and its result are never removed, reordered or re-identified: contents are
/// replaced in place, with the same call id, so the OpenAI wire format never sees an orphaned
/// tool call or an orphaned tool result.</item>
/// <item>The most recent <see cref="ToolLoopBudgetOptions.RecentToolResultsVerbatim"/> results stay
/// verbatim as long as digesting everything older is enough; the single latest result is never
/// digested at all.</item>
/// <item>On an Anthropic-bound call only, nothing ahead of the latest signed thinking block
/// (<see cref="TextReasoningContent"/> with <see cref="TextReasoningContent.ProtectedData"/>, as the
/// Anthropic SDK maps <c>thinking</c>) is forwarded differently from how it was forwarded when that
/// block was produced: Anthropic binds the block's signature to the conversation before it. OpenAI
/// encrypted reasoning is not bound that way and compacts like any other content. See
/// <see cref="Compact"/>.</item>
/// <item>Input at or under <see cref="ToolLoopBudgetOptions.MaxContextChars"/> is returned as the
/// very same list instance — byte-identical, nothing allocated.</item>
/// <item>The caller's messages are never mutated: <c>FunctionInvokingChatClient</c> keeps the full
/// history to build the final response and the diagnostics from, and that must stay raw. Only the
/// copy forwarded to the provider is compacted.</item>
/// </list>
/// </summary>
public static class ToolLoopCompactor
{
    /// <param name="Messages">What to forward: the input instance itself when nothing changed.</param>
    /// <param name="RawChars">The size of the input, instructions included.</param>
    /// <param name="ForwardedChars">The size of <paramref name="Messages"/>, instructions included.</param>
    public readonly record struct Outcome(IReadOnlyList<ChatMessage> Messages, int RawChars, int ForwardedChars);

    private readonly record struct Slot(int Message, int Content, int Size);

    /// <summary>
    /// Measures <paramref name="messages"/> (plus <paramref name="fixedChars"/> for the
    /// instructions that travel outside the list) and compacts them when over budget.
    /// <paramref name="bindsSignedReasoning"/> is true only for a provider that binds a signed
    /// thinking block to the conversation before it (Anthropic); only then is that prefix frozen.
    /// </summary>
    public static Outcome Compact(
        IReadOnlyList<ChatMessage> messages, int fixedChars, ToolLoopBudgetOptions options, bool bindsSignedReasoning = false)
    {
        int raw = MeasureMessages(messages, fixedChars);
        if (!options.Enabled || options.MaxContextChars <= 0 || raw <= options.MaxContextChars)
            return new Outcome(messages, raw, raw);

        // Signed thinking (Anthropic): the API checks every thinking block it is sent back against
        // the conversation it was created in, and rejects it when "the messages before it have
        // changed since" (Anthropic SDK 12.55.0, BetaThinkingBlockBinding.PrefixMismatchBehavior;
        // ThinkingBlockParam.Signature: "Thinking blocks must be passed back unmodified and in their
        // original order; a modified block results in a 400 invalid_request_error"). Dropping the
        // block instead is not an option either: while thinking is on, the last assistant turn of a
        // tool loop must start with its thinking block. So everything ahead of the latest signed
        // block is forwarded exactly as it was forwarded on the call that produced that block —
        // which, this function being deterministic, is this same compaction applied to that prefix
        // — and only what came after it (the newest tool results) may still be digested. Dropping
        // whole old call/result pairs would change that prefix too, so it is never done.
        //
        // The rule is Anthropic's alone. The OpenAI Responses adapter maps reasoning.encrypted_content
        // to ProtectedData too, but that state is not bound to the messages before it, so for any
        // other provider (bindsSignedReasoning false) a ProtectedData block freezes nothing and the
        // list compacts exactly as it did before the anchor existed.
        int anchor = bindsSignedReasoning ? LastSignedReasoningMessage(messages) : -1;
        if (anchor < 0)
            return CompactFrom(messages, raw, fixedChars, options, editableFrom: 0);

        List<ChatMessage> prefix = [.. messages.Take(anchor)];
        Outcome frozen = Compact(prefix, fixedChars, options, bindsSignedReasoning);
        IReadOnlyList<ChatMessage> basis = ReferenceEquals(frozen.Messages, prefix)
            ? messages
            : [.. frozen.Messages, .. messages.Skip(anchor)];

        Outcome outcome = CompactFrom(basis, raw, fixedChars, options, editableFrom: anchor + 1);
        return outcome.Messages.Count == messages.Count && outcome.Messages.SequenceEqual(messages)
            ? new Outcome(messages, raw, raw)
            : outcome;
    }

    private static int LastSignedReasoningMessage(IReadOnlyList<ChatMessage> messages)
    {
        for (int m = messages.Count - 1; m >= 0; m--)
        {
            if (messages[m].Contents.Any(c => c is TextReasoningContent { ProtectedData.Length: > 0 }))
                return m;
        }

        return -1;
    }

    /// <summary>
    /// The compaction itself, over <paramref name="messages"/>, rewriting only contents of
    /// messages at index <paramref name="editableFrom"/> or later. <paramref name="raw"/> is the
    /// size of the caller's original list, reported as-is.
    /// </summary>
    private static Outcome CompactFrom(
        IReadOnlyList<ChatMessage> messages, int raw, int fixedChars, ToolLoopBudgetOptions options, int editableFrom)
    {
        List<Slot> results = [];
        List<Slot> calls = [];
        long measured = fixedChars;

        for (int m = 0; m < messages.Count; m++)
        {
            IList<AIContent> contents = messages[m].Contents;
            for (int c = 0; c < contents.Count; c++)
            {
                int size = Measure(contents[c]);
                measured += size;
                switch (contents[c])
                {
                    case FunctionResultContent:
                        results.Add(new Slot(m, c, size));
                        break;
                    case FunctionCallContent:
                        calls.Add(new Slot(m, c, size));
                        break;
                }
            }
        }

        int basis = (int)Math.Min(measured, int.MaxValue);
        if (basis <= options.MaxContextChars || results.Count == 0)
            return new Outcome(messages, raw, basis);

        int digestMax = Math.Max(1, options.DigestMaxChars);
        long total = basis;
        Dictionary<(int Message, int Content), AIContent> edits = [];

        void DigestResult(Slot slot)
        {
            if (slot.Message < editableFrom)
                return;

            FunctionResultContent original = (FunctionResultContent)messages[slot.Message].Contents[slot.Content];
            string text = ResultText(original.Result);
            if (text.Length <= digestMax)
                return;

            string digest = DigestText(text, digestMax);
            if (digest.Length >= text.Length)
                return;

            edits[(slot.Message, slot.Content)] = new FunctionResultContent(original.CallId, digest)
            {
                Exception = original.Exception,
                AdditionalProperties = original.AdditionalProperties,
            };
            total -= slot.Size - digest.Length;
        }

        // Phase 1: results older than the verbatim window, oldest first, stopping the moment the
        // context fits. Oldest-first and stop-at-fit make the digested set grow monotonically as
        // the loop advances, so a result digested on one call is digested identically on the next.
        int window = Math.Max(1, options.RecentToolResultsVerbatim);
        int windowStart = Math.Max(0, results.Count - window);
        for (int i = 0; i < windowStart && total > options.MaxContextChars; i++)
            DigestResult(results[i]);

        // Phase 2: the digesting above was not enough, so the verbatim window itself shrinks,
        // oldest of it first. The latest result (the last slot) is the one the model is reacting
        // to and is never digested.
        for (int i = windowStart; i < results.Count - 1 && total > options.MaxContextChars; i++)
            DigestResult(results[i]);

        // Phase 3, opt-out: string arguments of older calls (a whole file written through a tool).
        // The call keeps its id and name, so its result still pairs with it.
        if (options.CompactCallArguments && total > options.MaxContextChars && calls.Count > 1)
        {
            int latestCallMessage = calls[^1].Message;
            foreach (Slot slot in calls)
            {
                if (total <= options.MaxContextChars)
                    break;
                if (slot.Message >= latestCallMessage || slot.Message < editableFrom)
                    continue;

                FunctionCallContent original = (FunctionCallContent)messages[slot.Message].Contents[slot.Content];
                FunctionCallContent? digested = DigestCall(original, digestMax);
                if (digested is null)
                    continue;

                edits[(slot.Message, slot.Content)] = digested;
                total -= slot.Size - MeasureCall(digested);
            }
        }

        if (edits.Count == 0)
            return new Outcome(messages, raw, basis);

        List<ChatMessage> forwarded = new(messages.Count);
        for (int m = 0; m < messages.Count; m++)
        {
            ChatMessage message = messages[m];
            IList<AIContent>? replaced = null;
            for (int c = 0; c < message.Contents.Count; c++)
            {
                if (edits.TryGetValue((m, c), out AIContent? edit))
                {
                    replaced ??= [.. message.Contents];
                    replaced[c] = edit;
                }
            }

            forwarded.Add(replaced is null ? message : Rebuild(message, replaced));
        }

        return new Outcome(forwarded, raw, (int)Math.Max(0, Math.Min(total, int.MaxValue)));
    }

    /// <summary>The size of one message list plus instructions, in characters. Exposed for tests.</summary>
    public static int MeasureMessages(IEnumerable<ChatMessage> messages, int fixedChars = 0)
    {
        long total = fixedChars;
        foreach (ChatMessage message in messages)
        {
            foreach (AIContent content in message.Contents)
                total += Measure(content);
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    // The role, author and timestamps are copied; RawRepresentation deliberately is not. An
    // OpenAI-bound assistant message can carry its provider-native message there, and a provider
    // client that prefers it over Contents would send the UNcompacted call back to the model.
    private static ChatMessage Rebuild(ChatMessage original, IList<AIContent> contents) => new(original.Role, contents)
    {
        AuthorName = original.AuthorName,
        CreatedAt = original.CreatedAt,
        MessageId = original.MessageId,
        AdditionalProperties = original.AdditionalProperties,
    };

    private static int Measure(AIContent content) => content switch
    {
        TextContent text => text.Text?.Length ?? 0,
        TextReasoningContent reasoning => reasoning.Text?.Length ?? 0,
        FunctionCallContent call => MeasureCall(call),
        FunctionResultContent result => ResultText(result.Result).Length,
        // Media and other structured content is never compacted, and its token cost does not follow
        // its length in characters, so it is left out of the measure rather than mis-weighted.
        _ => 0,
    };

    private static int MeasureCall(FunctionCallContent call)
    {
        int size = (call.Name?.Length ?? 0) + (call.CallId?.Length ?? 0);
        if (call.Arguments is not null)
        {
            foreach (KeyValuePair<string, object?> argument in call.Arguments)
                size += argument.Key.Length + ArgumentLength(argument.Value);
        }

        return size;
    }

    private static int ArgumentLength(object? value) => value switch
    {
        null => 4,
        string text => text.Length,
        JsonElement element => element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Length ?? 0
            : element.GetRawText().Length,
        _ => ResultText(value).Length,
    };

    private static string ResultText(object? result)
    {
        switch (result)
        {
            case null:
                return string.Empty;
            case string text:
                return text;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return element.GetString() ?? string.Empty;
            case JsonElement element:
                return element.GetRawText();
            default:
                try
                {
                    return JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);
                }
                catch (Exception)
                {
                    return result.ToString() ?? string.Empty;
                }
        }
    }

    private static FunctionCallContent? DigestCall(FunctionCallContent call, int digestMax)
    {
        if (call.Arguments is null)
            return null;

        Dictionary<string, object?> arguments = new(call.Arguments.Count);
        bool changed = false;
        foreach (KeyValuePair<string, object?> argument in call.Arguments)
        {
            string? text = argument.Value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };

            if (text is { } && text.Length > digestMax)
            {
                string digest = DigestText(text, digestMax);
                if (digest.Length < text.Length)
                {
                    arguments[argument.Key] = digest;
                    changed = true;
                    continue;
                }
            }

            arguments[argument.Key] = argument.Value;
        }

        if (!changed)
            return null;

        return new FunctionCallContent(call.CallId, call.Name, arguments)
        {
            InformationalOnly = call.InformationalOnly,
            Exception = call.Exception,
            AdditionalProperties = call.AdditionalProperties,
        };
    }

    /// <summary>
    /// A bounded stand-in for <paramref name="text"/>: a marker line giving the original size, then
    /// either a structural JSON digest (<see cref="JsonOutputDigest"/>, for JSON tool results) or
    /// the first and last lines with the middle dropped. Callers only pass text longer than
    /// <paramref name="maxChars"/>.
    /// </summary>
    internal static string DigestText(string text, int maxChars)
    {
        int lines = 1;
        foreach (char ch in text)
        {
            if (ch == '\n')
                lines++;
        }

        string marker = $"[compacted: {text.Length} chars, {lines} lines]";
        ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();
        bool looksLikeJson = trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');

        string body = looksLikeJson
            ? JsonOutputDigest.Digest(text, new JsonDigestSettings(maxChars, Math.Max(80, maxChars / 10), 10, 6))
            : HeadAndTail(text, maxChars);

        return marker + "\n" + body;
    }

    private static string HeadAndTail(string text, int maxChars)
    {
        int headLength = Math.Max(1, maxChars * 6 / 10);
        int tailLength = Math.Max(1, maxChars - headLength);

        string head = text[..headLength];
        int lastBreak = head.LastIndexOf('\n');
        if (lastBreak > headLength / 2)
            head = head[..lastBreak];

        string tail = text[^tailLength..];
        int firstBreak = tail.IndexOf('\n');
        if (firstBreak >= 0 && firstBreak < tailLength / 2)
            tail = tail[(firstBreak + 1)..];

        return head + "\n[... middle omitted ...]\n" + tail;
    }
}
