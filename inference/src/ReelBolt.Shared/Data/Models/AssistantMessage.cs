namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One turn in an <see cref="AssistantThread"/>. Append-only: a turn is written before the model
/// is invoked (for user turns) or before the stream is closed (for assistant turns), so a dropped
/// SSE connection still leaves an accurate record of what was said.
/// </summary>
public class AssistantMessage
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }

    /// <summary>"user" or "assistant".</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>1-based position within the thread; unique per thread.</summary>
    public int Sequence { get; set; }

    /// <summary>Optional structured side-channel (tool calls, attachments) as JSON.</summary>
    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
