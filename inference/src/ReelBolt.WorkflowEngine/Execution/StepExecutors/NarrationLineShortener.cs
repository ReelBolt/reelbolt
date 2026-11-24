using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Cuts narration lines that cannot fit their scene down to their word budget: ONE chat call to the
/// NarrationWriter agent's chat client for the whole batch. The call goes through the metering chat
/// client, so its tokens are recorded against the ambient step scope (source workflow-step) and settle
/// inside the Voiceover step's own hold (see <c>StepUsageProfiles</c>: Enforce steps hold one such call).
/// The answer is untrusted text: it is accepted per line only when <see cref="Validate"/> passes.
/// </summary>
public sealed class NarrationLineShortener : INarrationLineShortener
{
    private readonly IAgentChatClientProvider _chatClients;
    private readonly ILogger<NarrationLineShortener> _logger;

    public NarrationLineShortener(IAgentChatClientProvider chatClients, ILogger<NarrationLineShortener> logger)
    {
        _chatClients = chatClients;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<int, string>?> ShortenAsync(
        IReadOnlyList<NarrationLineOverflow> lines, string? language, CancellationToken ct)
    {
        if (lines.Count == 0)
            return null;

        try
        {
            IChatClient client = await _chatClients.GetAsync(AgentType.NarrationWriter, null, ct);
            var request = lines.Select(l => new { index = l.Index, maxWords = l.WordBudget, text = l.Text });
            string instructions =
                "You shorten spoken narration lines so each fits its scene. For every line, rewrite it with at most "
                + "`maxWords` words, keeping its meaning, its language" + (string.IsNullOrWhiteSpace(language) ? string.Empty : $" ({language})")
                + " and every name and product term. Plain spoken sentences only: no URLs, no tags, no markup, no numbers of seconds. "
                + "Answer with JSON only: {\"lines\":[{\"index\":<n>,\"text\":\"...\"}]} with one entry per input line.";
            ChatResponse response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, instructions),
                 new ChatMessage(ChatRole.User, JsonSerializer.Serialize(request))],
                new ChatOptions { MaxOutputTokens = 1500 }, ct);
            return Parse(response.Text, lines);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Narration line shortening failed; the step will report that the narration does not fit");
            return null;
        }
    }

    /// <summary>Reads the model answer and keeps only the lines that pass <see cref="Validate"/>. Null when nothing usable.</summary>
    internal static IReadOnlyDictionary<int, string>? Parse(string? answer, IReadOnlyList<NarrationLineOverflow> lines)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;
        int start = answer.IndexOf('{');
        int end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        var result = new Dictionary<int, string>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(answer[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("lines", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
                return null;
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("index", out JsonElement index) || !index.TryGetInt32(out int i)
                    || !item.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
                    continue;
                NarrationLineOverflow? source = lines.FirstOrDefault(l => l.Index == i);
                if (source is null)
                    continue;
                if (Validate(text.GetString(), source.WordBudget) is string valid)
                    result[i] = valid;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Deterministic acceptance: non-empty after the line sanitizer, at most <paramref name="wordBudget"/> words,
    /// and no URL or tag/markup left. Returns the sanitized text, or null.
    /// </summary>
    internal static string? Validate(string? candidate, int wordBudget)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        string sanitized = VoiceoverTextSanitizer.Sanitize(candidate, int.MaxValue);
        int words = NarrationFitSolver.CountWords(sanitized);
        if (words == 0 || words > wordBudget)
            return null;
        if (sanitized.Contains("://", StringComparison.Ordinal) || sanitized.Contains("www.", StringComparison.OrdinalIgnoreCase)
            || sanitized.Contains('<') || sanitized.Contains('>') || sanitized.Contains('[') || sanitized.Contains(']')
            || sanitized.Contains('{') || sanitized.Contains('}'))
            return null;
        return sanitized;
    }
}
