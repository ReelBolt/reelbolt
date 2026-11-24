namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A server-persisted assistant conversation. Conversations live on the server, not in a client
/// buffer, so a thread survives a page reload and can be reopened from any page.
/// </summary>
public class AssistantThread
{
    public Guid Id { get; set; }

    /// <summary>The user the thread belongs to. Every read is scoped to this, never to a route value.</summary>
    public Guid UserId { get; set; }

    /// <summary>The workspace the thread was started in.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The project the thread was started from, when the page that opened it had project context.
    /// Deliberately not a foreign key: a thread outlives the project it was about, so deleting a
    /// project must not delete (or block on) the conversation.
    /// </summary>
    public Guid? ProjectId { get; set; }

    public string Title { get; set; } = "New chat";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }

    /// <summary>
    /// The highest message sequence the person has seen (the web client marks it when the panel is visible). An assistant
    /// message above it is "unread". Monotonic: it never goes down. Opening a thread does not move it.
    /// </summary>
    public int ReadThroughSequence { get; set; }

    public ICollection<AssistantMessage> Messages { get; set; } = new List<AssistantMessage>();
}
