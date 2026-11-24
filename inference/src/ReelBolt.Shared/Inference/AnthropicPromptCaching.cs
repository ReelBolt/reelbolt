using System.Reflection;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Where an Anthropic call may place prompt-cache breakpoints. Prompt caching is a PREFIX match:
/// a breakpoint caches every byte before it, and any change before a breakpoint invalidates it.
/// The mode is opt-in per call so a one-shot call (a connectivity ping, a vision caption, a
/// decision question) never pays the cache-write premium for a prefix nobody reads back.
/// </summary>
public enum AnthropicPromptCacheMode
{
    /// <summary>No breakpoints. The default, and byte-identical to the pre-caching adapter.</summary>
    Off = 0,

    /// <summary>
    /// One breakpoint at the end of the newest turn. For a multi-turn agent loop whose system
    /// prompt changes per turn (the platform assistant appends a per-turn session/docs block):
    /// the in-turn tool loop still re-reads the conversation prefix, but no marker is written on
    /// the unstable system block.
    /// </summary>
    TurnOnly = 1,

    /// <summary>
    /// A breakpoint at the end of the last stable system block (which caches <c>tools</c> +
    /// <c>system</c> together, because Anthropic renders tools before system) plus the newest-turn
    /// breakpoint. For workflow agents and room seats, whose system prompt is constant for the
    /// lifetime of a run.
    /// </summary>
    SystemAndTurn = 2,
}

/// <summary>
/// The provider-neutral opt-in for Anthropic prompt caching, and the one place that turns a
/// <see cref="ChatOptions"/>/message pair into a request with cache breakpoints.
/// </summary>
/// <remarks>
/// <para>
/// The markers use the official SDK's own content-level API,
/// <see cref="AIContentCacheExtensions.WithCacheControl{T}"/>, which stores
/// <c>CacheControlEphemeral</c> on <see cref="AIContent.AdditionalProperties"/> and is read by
/// <c>AnthropicChatClient</c> when it maps a content block to the wire. Nothing here hand-builds
/// <c>MessageCreateParams</c>: the system prompt arrives as <see cref="ChatOptions.Instructions"/>
/// (not as a message), so a stable instructions string is promoted to its own system block, which
/// is exactly the shape the SDK already gives instructions — only now with a breakpoint on it.
/// </para>
/// <para>
/// Two breakpoints at most, both at <see cref="Ttl.Ttl5m"/>. The reuse this workload can guarantee
/// is the tool loop inside one agent run (seconds to a few minutes), and Anthropic refreshes the
/// five-minute window on every hit. A one-hour TTL costs 2x input on the write (vs 1.25x) and only
/// pays off on cross-run reuse, which the system prefix cannot guarantee: it embeds the run's
/// style/design pins, and each agent type normally runs once per workflow.
/// </para>
/// </remarks>
public static class AnthropicPromptCaching
{
    /// <summary>The <see cref="ChatOptions.AdditionalProperties"/> key carrying the mode.</summary>
    public const string OptionsKey = "anthropic:prompt_cache_mode";

    /// <summary>Both breakpoints use the five-minute TTL. See the type remarks for why.</summary>
    public static readonly Ttl DefaultTtl = Ttl.Ttl5m;

    /// <summary>
    /// Opts <paramref name="options"/> into prompt caching. Mutates and returns the same instance;
    /// the value is an immutable enum, so cloning the options later keeps it.
    /// </summary>
    public static ChatOptions WithMode(this ChatOptions options, AnthropicPromptCacheMode mode)
    {
        ArgumentNullException.ThrowIfNull(options);

        AdditionalPropertiesDictionary properties = options.AdditionalProperties ??= new();
        if (mode == AnthropicPromptCacheMode.Off)
        {
            properties.Remove(OptionsKey);
        }
        else
        {
            properties[OptionsKey] = mode;
        }

        return options;
    }

    /// <summary>The mode carried by <paramref name="options"/>; <see cref="AnthropicPromptCacheMode.Off"/> when absent or unrecognised.</summary>
    public static AnthropicPromptCacheMode ModeOf(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties
            || !properties.TryGetValue(OptionsKey, out object? value))
        {
            return AnthropicPromptCacheMode.Off;
        }

        return value switch
        {
            AnthropicPromptCacheMode mode => mode,
            int number when Enum.IsDefined(typeof(AnthropicPromptCacheMode), number) => (AnthropicPromptCacheMode)number,
            _ => AnthropicPromptCacheMode.Off,
        };
    }

    /// <summary>
    /// Applies the breakpoints this call opted into. Returns the input untouched — same options
    /// instance, same enumerable — when the mode is <see cref="AnthropicPromptCacheMode.Off"/>.
    /// </summary>
    /// <remarks>
    /// Never mutates the caller's options, message list, messages or contents: the list belongs to
    /// <c>FunctionInvokingChatClient</c> and is replayed on every tool-loop iteration, so an
    /// in-place marker would leave one breakpoint per iteration and Anthropic rejects a request
    /// with more than four. The marked message and its marked content are shallow clones.
    /// </remarks>
    internal static (ChatOptions? Options, IEnumerable<ChatMessage> Messages) Apply(
        ChatOptions? options, IEnumerable<ChatMessage> messages)
    {
        AnthropicPromptCacheMode mode = ModeOf(options);
        if (mode == AnthropicPromptCacheMode.Off || options is null)
        {
            return (options, messages);
        }

        // Always copy, even when the caller handed us a List: the list belongs to
        // FunctionInvokingChatClient and is replayed on every iteration, and the instructions
        // promotion below inserts a block — writing it into the caller's list would put one
        // breakpoint into the replayed history per tool-loop iteration.
        List<ChatMessage> list = new(messages);
        ChatOptions preparedOptions = options;
        bool optionsCloned = false;

        if (mode == AnthropicPromptCacheMode.SystemAndTurn)
        {
            if (!string.IsNullOrWhiteSpace(preparedOptions.Instructions))
            {
                // Promote the stable instructions to their own system block, placed where the SDK
                // would have appended them (after every existing system message), and move the
                // breakpoint onto the end of that block. That single marker caches tools + system.
                if (!optionsCloned)
                {
                    preparedOptions = preparedOptions.Clone();
                    optionsCloned = true;
                }

                string instructions = preparedOptions.Instructions!;
                preparedOptions.Instructions = null;

                TextContent content = new(instructions);
                content.WithCacheControl(DefaultTtl);

                int insertAt = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Role == ChatRole.System)
                    {
                        insertAt = i + 1;
                    }
                }

                list.Insert(insertAt, new ChatMessage(ChatRole.System, new List<AIContent> { content }));
            }
            else
            {
                // No instructions to promote (room seats carry their charter as a system message):
                // mark the last cacheable block of the last system message instead.
                list = MarkLastBlock(list, static message => message.Role == ChatRole.System);
            }
        }

        // The newest turn: caches the conversation prefix a tool loop re-sends on its next call.
        list = MarkLastBlock(list, static message => message.Role != ChatRole.System);

        return (preparedOptions, list);
    }

    private static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// Replaces the last message matching <paramref name="predicate"/> with a clone whose last
    /// cacheable content block carries a breakpoint. Returns a new list; the input is not modified.
    /// </summary>
    private static List<ChatMessage> MarkLastBlock(List<ChatMessage> messages, Func<ChatMessage, bool> predicate)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            ChatMessage message = messages[i];
            if (!predicate(message))
            {
                continue;
            }

            int contentIndex = -1;
            for (int j = message.Contents.Count - 1; j >= 0; j--)
            {
                if (IsCacheable(message.Contents[j]))
                {
                    contentIndex = j;
                    break;
                }
            }

            if (contentIndex < 0)
            {
                return messages;
            }

            List<ChatMessage> copy = new(messages);
            ChatMessage clonedMessage = message.Clone();
            clonedMessage.Contents = new List<AIContent>(message.Contents);
            AIContent clonedContent = ShallowClone(message.Contents[contentIndex]);
            clonedContent.WithCacheControl(DefaultTtl);
            clonedMessage.Contents[contentIndex] = clonedContent;
            copy[i] = clonedMessage;
            return copy;
        }

        return messages;
    }

    /// <summary>The marker on <paramref name="content"/>, if any. Exposed for the breakpoint tests.</summary>
    /// <remarks>
    /// The SDK's own reader, <c>AIContentCacheExtensions.GetCacheControl</c>, is internal in
    /// Anthropic 12.55.0 (its writer, <c>WithCacheControl</c>, is public). This reads the same key
    /// the writer stores, so a test sees exactly what the SDK will see on the wire.
    /// </remarks>
    internal static CacheControlEphemeral? CacheControlOf(AIContent content) =>
        content.AdditionalProperties is { } properties
        && properties.TryGetValue("anthropic:cache_control", out object? value)
            ? value as CacheControlEphemeral
            : null;

    /// <summary>
    /// The block types the Anthropic SDK's <c>WithCacheControlFrom</c> maps to a wire block that
    /// accepts <c>cache_control</c>. Anything else is skipped rather than marked-and-ignored.
    /// </summary>
    private static bool IsCacheable(AIContent content) =>
        content is TextContent or DataContent or FunctionResultContent or FunctionCallContent;

    /// <summary>
    /// A field-for-field copy of an <see cref="AIContent"/>. M.E.AI 10.10 has no public
    /// <c>AIContent.Clone</c>, and rebuilding by type would drop <see cref="AIContent.RawRepresentation"/>
    /// (which the Anthropic adapter uses to round-trip provider blocks). <c>MemberwiseClone</c>
    /// preserves every field and we give the copy its own properties dictionary so the marker
    /// cannot leak back into the shared original.
    /// </summary>
    private static AIContent ShallowClone(AIContent content)
    {
        AIContent clone = (AIContent)MemberwiseCloneMethod.Invoke(content, null)!;
        clone.AdditionalProperties = content.AdditionalProperties?.Clone();
        return clone;
    }
}
