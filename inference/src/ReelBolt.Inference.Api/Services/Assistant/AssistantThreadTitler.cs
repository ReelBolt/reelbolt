using Microsoft.Extensions.AI;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Names a conversation. The fallback (the first message, truncated) is applied the moment the
/// first turn lands; the model-written title replaces it after the first exchange when the cheap
/// call succeeds within a short budget.
/// </summary>
public static class AssistantThreadTitler
{
    public const string DefaultTitle = "New chat";
    public const int MaxTitleChars = 60;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public static bool IsPlaceholder(string? title) =>
        string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), DefaultTitle, StringComparison.OrdinalIgnoreCase);

    /// <summary>First message, whitespace-collapsed and cut on a word boundary.</summary>
    public static string Fallback(string message)
    {
        string flat = string.Join(' ', (message ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length == 0) return DefaultTitle;
        if (flat.Length <= MaxTitleChars) return flat;
        int cut = flat.LastIndexOf(' ', MaxTitleChars - 1);
        if (cut < MaxTitleChars / 2) cut = MaxTitleChars - 1;
        return flat[..cut].TrimEnd(' ', ',', '.', ';', ':') + "…";
    }

    /// <summary>Strips quotes/markdown/newlines from a model title and bounds it. Null when unusable.</summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) line = line[6..];
        line = line.Trim().Trim('"', '\'', '`', '*', '#', '.', ' ');
        if (line.Length == 0) return null;
        return line.Length <= MaxTitleChars ? line : line[..(MaxTitleChars - 1)].TrimEnd() + "…";
    }

    /// <summary>One small model call; never throws (a failure is just "no better title").</summary>
    public static async Task<string?> GenerateAsync(
        IChatClient client, string userMessage, string assistantReply, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Budget);
        try
        {
            string excerpt(string s, int n) => s.Length <= n ? s : s[..n];
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You name chat conversations. Reply with ONLY a title of 2 to 6 words that says what the " +
                    "conversation is about. No quotes, no trailing punctuation, no markdown."),
                new(ChatRole.User,
                    $"User message:\n{excerpt(userMessage, 600)}\n\nAssistant reply:\n{excerpt(assistantReply, 600)}\n\nTitle:")
            };
            ChatResponse response = await client.GetResponseAsync(
                messages, new ChatOptions { MaxOutputTokens = 400 }, cts.Token);
            return Sanitize(response.Text);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
