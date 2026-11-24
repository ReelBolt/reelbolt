using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>What a <see cref="VideoSourceKind.StepResult"/> source resolved to: a storage key, or a code and message.</summary>
internal sealed record StepResultSourceResolution(string? StorageKey, string? Code, string? Message)
{
    public static StepResultSourceResolution Resolved(string key) => new(key, null, null);
}

/// <summary>
/// T-010/T-014/T-023: resolves a <see cref="VideoSourceKind.StepResult"/> source (a step output of an EARLIER run)
/// to its storage key — the one implementation <see cref="VideoAnalyzeStepExecutor"/>,
/// <see cref="VideoCompileStepExecutor"/> and the step cache share, so they can never disagree on what such a
/// source may name.
///
/// <para>
/// Authorization boundary: the row must belong to an execution of THIS execution's project and its output must be
/// one of that project's addressable outputs (<see cref="ProjectOutputKeys.IsAddressableOutput"/>: renders under
/// <c>outputFiles/</c>, VideoGenerate takes under <c>agentFiles/generated/</c>). Another project, another tenant,
/// a missing row, a pruned output, a key outside the two prefixes, or no database all answer the one
/// <see cref="NotFoundMessage"/> under <see cref="NotFoundCode"/>, so an id from another project cannot be told
/// apart from one that never existed. Fails closed.
/// </para>
/// <para>
/// A row of THIS project that cannot be used answers <see cref="NotPlayableCode"/> with the reason: it failed,
/// it has not finished (pending, running or skipped), or its output is not a video (or, where the consumer
/// takes stills, not a video or an image) by extension.
/// </para>
/// </summary>
internal static class StepResultSourceResolver
{
    public const string NotFoundCode = "SOURCE_NOT_FOUND";
    public const string NotPlayableCode = "SOURCE_NOT_PLAYABLE";

    /// <summary>The ONE message a foreign, missing or pruned StepResult source carries.</summary>
    public const string NotFoundMessage = "The step result named by this source was not found in this project.";

    public const string FailedMessage = "The step result named by this source failed, so it has no output to use.";
    public const string UnfinishedMessage = "The step result named by this source has not finished, so it has no output to use yet.";
    public const string NotVideoMessage = "The step result named by this source is not a video.";
    public const string NotMediaMessage = "The step result named by this source is not a video or an image.";

    private static readonly IReadOnlySet<string> VideoExtensions = StepResultMediaExtensions.Video;

    private static readonly IReadOnlySet<string> ImageExtensions = StepResultMediaExtensions.Image;

    private static readonly IReadOnlySet<string> AudioExtensions = StepResultMediaExtensions.Audio;

    private static readonly StepResultSourceResolution NotFound = new(null, NotFoundCode, NotFoundMessage);

    /// <summary>The failure code a resolver message stands for, or null when it is not one of the resolver's.</summary>
    public static string? CodeFor(string? message) => message switch
    {
        NotFoundMessage => NotFoundCode,
        FailedMessage or UnfinishedMessage or NotVideoMessage or NotMediaMessage => NotPlayableCode,
        _ => null
    };

    /// <summary>Resolves <paramref name="source"/> inside <paramref name="projectId"/> through a short-lived scope.</summary>
    public static async Task<StepResultSourceResolution> ResolveAsync(
        IServiceScopeFactory? scopes, Guid projectId, VideoSourceRef source, bool allowImages, CancellationToken ct, bool allowAudio = false)
    {
        if (scopes is null)
            return NotFound;

        using IServiceScope scope = scopes.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await ResolveAsync(db, projectId, source, allowImages, ct, allowAudio);
    }

    /// <summary>Resolves <paramref name="source"/> inside <paramref name="projectId"/> against <paramref name="db"/>.</summary>
    public static async Task<StepResultSourceResolution> ResolveAsync(
        WorkflowEngineDbContext db, Guid projectId, VideoSourceRef source, bool allowImages, CancellationToken ct, bool allowAudio = false)
    {
        if (source.Kind != VideoSourceKind.StepResult || source.Validate() is not null)
            return NotFound;

        Guid stepResultId = source.StepResultId!.Value;
        var row = await db.WorkflowStepResults
            .AsNoTracking()
            .Where(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId)
            .Select(r => new { r.Status, r.OutputStorageKey, r.ExecutedAt, r.SupersededAt, r.WorkflowExecution.FirstStartedAt })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return NotFound;

        // An output that may predate the project's last quarantine may depend on that file; the answer is the same one
        // a missing row gets. A failed read refuses too.
        if (await ProjectQuarantineState.ResultPredatesAsync(db, projectId, row.ExecutedAt, row.FirstStartedAt, ct, row.SupersededAt))
            return NotFound;

        if (row.Status == StepStatus.Failed)
            return new(null, NotPlayableCode, FailedMessage);
        if (row.Status != StepStatus.Completed)
            return new(null, NotPlayableCode, UnfinishedMessage);

        string? key = row.OutputStorageKey;
        if (!ProjectOutputKeys.IsAddressableOutput(projectId, key) || Path.GetFileName(key!).Length == 0)
            return NotFound;

        string extension = Path.GetExtension(key);
        if (VideoExtensions.Contains(extension) || (allowImages && ImageExtensions.Contains(extension)) || (allowAudio && AudioExtensions.Contains(extension)))
            return StepResultSourceResolution.Resolved(key!);

        return new(null, NotPlayableCode, allowImages ? NotMediaMessage : NotVideoMessage);
    }
}
