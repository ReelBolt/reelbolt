namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>Task payload for file summarization.</summary>
public record FileSummarizationTask(Guid FileId);

/// <summary>
/// Task payload for ingest moderation. <paramref name="Ticket"/> makes two offers for the same file
/// distinct: a re-upload must not be dropped by the queue's "same task" tracking while the first
/// scan is still in flight.
/// </summary>
public record ContentModerationTask(Guid FileId, Guid Ticket)
{
    public ContentModerationTask(Guid fileId) : this(fileId, Guid.NewGuid())
    {
    }
}
