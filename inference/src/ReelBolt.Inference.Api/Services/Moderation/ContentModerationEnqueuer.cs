using ReelBolt.Inference.Api.Services.Background;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// The one non-blocking way an upload path asks for a file to be moderated. The row is already
/// committed when this is called, so a full queue simply means the sweeper picks the file up later;
/// a request must never wait for, or fail because of, moderation.
/// </summary>
public interface IContentModerationEnqueuer
{
    /// <summary>Offers the file without waiting. False when the queue is full or the file is already queued.</summary>
    bool TryQueue(Guid fileId);
}

/// <inheritdoc />
public sealed class ContentModerationEnqueuer(IBackgroundTaskQueue<ContentModerationTask> queue)
    : IContentModerationEnqueuer
{
    public bool TryQueue(Guid fileId) => queue.TryQueue(new ContentModerationTask(fileId));
}
