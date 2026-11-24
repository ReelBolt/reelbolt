using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Structured output where the provider supports it, degrading to plain JSON mode and then to no
/// format at all when the provider rejects the request's format. Not every OpenAI-compatible
/// vision backend implements JSON-schema output — DeepSeek answers it with HTTP 400 "This
/// response_format type is unavailable now" — and every caller's prompt already demands strict
/// JSON and parses defensively, so a format rejection is a reason to ask again more plainly, never
/// to give up on the request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Remembered per provider.</b> When a caller names its provider (<see cref="ProviderKey"/>),
/// the format the provider last ACCEPTED after rejecting a richer one is remembered for
/// <see cref="MemoryTtl"/>, and the next call starts there — so a provider that never supports the
/// JSON-schema format pays its rejection round trip once per TTL, not on every vision call. Only a
/// downgrade is ever recorded: a provider that accepts format #0 leaves no entry and behaves exactly
/// as before. A later rejection of the remembered format falls back to the plainer ones normally and
/// remembers the new answer; once the entry expires the richer format is tried again, so a provider
/// that gains JSON-schema support is picked up without a restart.
/// </para>
/// <para>
/// The memory is a process-wide <see cref="ConcurrentDictionary{TKey,TValue}"/>. Two concurrent
/// first calls may both pay the rejection; the last writer wins, and both write the same answer.
/// </para>
/// </remarks>
public static class StructuredChatFallback
{
    /// <summary>How long a learned format downgrade is trusted before the richer format is tried again.</summary>
    public static readonly TimeSpan MemoryTtl = TimeSpan.FromHours(12);

    /// <summary>A bound on distinct remembered providers; past it the memory is cleared (providers are few — this only guards a leak).</summary>
    private const int MaxRememberedProviders = 1024;

    private static readonly ConcurrentDictionary<string, LearnedFormat> Learned = new(StringComparer.Ordinal);

    private readonly record struct LearnedFormat(int Index, DateTime LearnedAtUtc);

    /// <summary>
    /// The memory key for a provider: its row id (or, for the config fallback with no row, its
    /// endpoint) plus its model — two models on one row can differ in what they support.
    /// </summary>
    public static string ProviderKey(ResolvedInferenceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string identity = provider.ProviderId is Guid id ? id.ToString("N") : "endpoint:" + provider.Endpoint;
        return identity + "|" + provider.ModelName;
    }

    /// <summary>Without a provider key nothing is remembered: every call starts at the schema format.</summary>
    public static Task<ChatResponse> GetResponseAsync(
        IChatClient client,
        IReadOnlyList<ChatMessage> messages,
        ChatResponseFormat schemaFormat,
        int maxOutputTokens,
        ILogger logger,
        CancellationToken ct) =>
        GetResponseAsync(client, messages, schemaFormat, maxOutputTokens, logger, providerKey: null, ct);

    public static async Task<ChatResponse> GetResponseAsync(
        IChatClient client,
        IReadOnlyList<ChatMessage> messages,
        ChatResponseFormat schemaFormat,
        int maxOutputTokens,
        ILogger logger,
        string? providerKey,
        CancellationToken ct)
    {
        ChatResponseFormat?[] formats = [schemaFormat, ChatResponseFormat.Json, null];
        int start = RememberedStart(providerKey, formats.Length);
        for (int i = start; ; i++)
        {
            ChatResponse response;
            try
            {
                response = await client.GetResponseAsync(
                    messages,
                    new ChatOptions { ResponseFormat = formats[i], MaxOutputTokens = maxOutputTokens },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (i < formats.Length - 1 && IsFormatRejection(ex) && !ct.IsCancellationRequested)
            {
                logger.LogInformation("Provider rejected response format #{Index}; retrying with a plainer one.", i);
                continue;
            }

            if (providerKey is not null && i != start)
                Remember(providerKey, i);
            return response;
        }
    }

    /// <summary>The format index a provider is remembered to accept, or 0 when unknown or expired.</summary>
    internal static int RememberedStart(string? providerKey, int formatCount)
    {
        if (providerKey is null || !Learned.TryGetValue(providerKey, out LearnedFormat learned))
            return 0;
        if (DateTime.UtcNow - learned.LearnedAtUtc > MemoryTtl)
        {
            Learned.TryRemove(new KeyValuePair<string, LearnedFormat>(providerKey, learned));
            return 0;
        }

        return Math.Clamp(learned.Index, 0, formatCount - 1);
    }

    private static void Remember(string providerKey, int index)
    {
        if (Learned.Count >= MaxRememberedProviders && !Learned.ContainsKey(providerKey))
            Learned.Clear();
        Learned[providerKey] = new LearnedFormat(index, DateTime.UtcNow);
    }

    /// <summary>Test seam: forget one provider (or, with <c>null</c>, everything).</summary>
    internal static void Forget(string? providerKey)
    {
        if (providerKey is null)
            Learned.Clear();
        else
            Learned.TryRemove(providerKey, out _);
    }

    /// <summary>Test seam: back-date a remembered entry to exercise expiry.</summary>
    internal static void BackdateForTests(string providerKey, TimeSpan age)
    {
        if (Learned.TryGetValue(providerKey, out LearnedFormat learned))
            Learned[providerKey] = learned with { LearnedAtUtc = DateTime.UtcNow - age };
    }

    /// <summary>An error that names the request's response format — the one failure a plainer request can fix.</summary>
    public static bool IsFormatRejection(Exception ex)
    {
        string text = ex.Message + " " + ex.InnerException?.Message;
        return text.Contains("response_format", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("json_schema", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("structured output", StringComparison.OrdinalIgnoreCase);
    }
}
