namespace ReelBolt.Shared.Moderation;

/// <summary>
/// Splits generated text into the bounded chunks one moderation call can classify.
/// </summary>
/// <remarks>
/// The client truncates a single call's text at <see cref="ContentModerationOptions.MaxTextChars"/>.
/// A rendered payload longer than that (a long narration plan, many caption cues, many overlays)
/// would otherwise be partly unclassified while the renderer/TTS used all of it. Callers moderate
/// every chunk and refuse to proceed when the text cannot be covered in the bounded number of
/// calls.
/// </remarks>
public static class ModerationTextChunker
{
    /// <summary>Hard ceiling on calls for one payload; beyond it the caller refuses rather than under-checks.</summary>
    public const int DefaultMaxChunks = 64;

    /// <summary>
    /// Chunks <paramref name="text"/> at whitespace boundaries where possible, hard-splitting a token
    /// longer than <paramref name="maxChars"/>. Returns false (and no chunks) when more than
    /// <paramref name="maxChunks"/> would be needed. Concatenating the chunks reproduces the input
    /// exactly, so every character the renderer/TTS will use is classified.
    /// </summary>
    public static bool TryChunk(
        string? text,
        int maxChars,
        out IReadOnlyList<string> chunks,
        int maxChunks = DefaultMaxChunks)
    {
        chunks = Chunk(text, maxChars, maxChunks, out bool complete);
        if (complete) return true;
        chunks = [];
        return false;
    }

    /// <summary>
    /// The same chunking, but what does not fit in <paramref name="maxChunks"/> chunks is left off and
    /// <paramref name="complete"/> is false: the caller judges what fits and says plainly it did not judge the rest.
    /// </summary>
    public static IReadOnlyList<string> Chunk(string? text, int maxChars, int maxChunks, out bool complete)
    {
        complete = true;
        if (string.IsNullOrWhiteSpace(text)) return [];

        int size = Math.Max(1, maxChars);
        int limit = Math.Max(1, maxChunks);
        List<string> result = [];
        int index = 0;
        while (index < text.Length)
        {
            if (result.Count >= limit)
            {
                complete = false;
                break;
            }

            int remaining = text.Length - index;
            int length = Math.Min(size, remaining);
            if (length < remaining)
            {
                int lastBreak = text.LastIndexOfAny([' ', '\n', '\r', '\t'], index + length - 1, length);
                if (lastBreak > index + (length / 2))
                    length = lastBreak - index + 1;
            }

            result.Add(text.Substring(index, length));
            index += length;
        }

        return result;
    }
}
