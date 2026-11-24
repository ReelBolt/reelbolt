namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// The one line every sync path calls after it has committed an upsert, to ask for a drift check
/// (SPEC 13.2). It only enqueues: it never awaits, never runs a check itself, and never throws into the
/// sync that produced the change — a full or broken queue loses a check, not a pull, a sync or an
/// upload.
/// </summary>
public static class DriftHooks
{
    public static void Notify(
        DesignDriftQueue? queue,
        Guid projectId,
        DesignDriftTrigger trigger,
        IReadOnlyCollection<string>? changedPaths,
        ILogger? logger = null,
        Guid? organizationId = null)
    {
        if (queue is null)
            return;

        try
        {
            // The organization is charged for the slot so one tenant cannot fill the queue.
            if (organizationId is { } organization)
                queue.Enqueue(projectId, trigger, changedPaths, organization);
            else
                queue.Enqueue(projectId, trigger, changedPaths);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "The design-system drift check of project {ProjectId} could not be queued", projectId);
        }
    }
}
