using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>The kinds of output a post can point at: a finished run's step result, or an editor render saved as a project file.</summary>
public static class SocialOutputKinds
{
    public const string StepResult = "step_result";
    public const string ProjectFile = "project_file";

    public static bool IsValid(string? kind) => kind is StepResult or ProjectFile;
}

/// <summary>A finished video as the publisher needs it. Stamps feed the output-hold and quarantine rules.</summary>
public sealed record SocialResolvedOutput(
    Guid ProjectId,
    string StorageKey,
    string MimeType,
    long? SizeBytes,
    double? DurationSeconds,
    int? Width,
    int? Height,
    DateTime? ProducedAt,
    DateTime? RunStartedAt,
    bool IsProjectFile,
    bool FileFlagged);

public interface ISocialOutputResolver
{
    /// <summary>
    /// The output of this kind and id, only when it belongs to <paramref name="projectId"/> and that project belongs to
    /// <paramref name="organizationId"/>; null otherwise (missing, someone else's, not a video). Reads without the tenant
    /// filter (the sweeper has no caller) and compares the organization explicitly instead.
    /// </summary>
    Task<SocialResolvedOutput?> ResolveAsync(Guid organizationId, Guid projectId, string outputKind, Guid outputId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class SocialOutputResolver(InferenceApiDbContext db, IFileStorageService storage) : ISocialOutputResolver
{
    public async Task<SocialResolvedOutput?> ResolveAsync(Guid organizationId, Guid projectId, string outputKind, Guid outputId, CancellationToken ct)
    {
        bool inOrg = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.Id == projectId && p.OrganizationId == organizationId, ct);
        if (!inOrg) return null;

        if (outputKind == SocialOutputKinds.StepResult)
        {
            var row = await db.WorkflowStepResults.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.Id == outputId && r.OutputStorageKey != null && r.WorkflowExecution.ProjectId == projectId)
                .Select(r => new
                {
                    r.OutputStorageKey,
                    r.OutputJson,
                    r.ExecutedAt,
                    r.WorkflowExecution.FirstStartedAt,
                    r.WorkflowExecution.StartedAt
                })
                .FirstOrDefaultAsync(ct);
            if (row?.OutputStorageKey is not { Length: > 0 } key) return null;

            RenderMetadata meta = RenderMetadataReader.Read(row.OutputJson);
            StoredObjectMetadata? head = await storage.GetMetadataAsync(projectId, key, ct);
            return new SocialResolvedOutput(
                projectId, key, head?.ContentType ?? "video/mp4", head?.SizeBytes,
                meta.DurationSec, meta.Width, meta.Height,
                row.ExecutedAt, ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(row.FirstStartedAt, row.StartedAt),
                IsProjectFile: false, FileFlagged: false);
        }

        if (outputKind == SocialOutputKinds.ProjectFile)
        {
            ProjectFile? file = await db.ProjectFiles.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == outputId && f.ProjectId == projectId && f.MimeType.StartsWith("video/"), ct);
            if (file is null) return null;

            return new SocialResolvedOutput(
                projectId, file.StorageKey, file.MimeType, file.SizeBytes,
                DurationSeconds: null, Width: null, Height: null,
                ReelBolt.Shared.Storage.ProjectOutputHold.FileStamp(file.OutputProducedAt, file.UploadedAt), RunStartedAt: null,
                IsProjectFile: true, FileFlagged: file.CsamStatus == CsamScanStatus.Quarantined);
        }

        return null;
    }
}

/// <summary>The moderation verdict on what is about to leave the building.</summary>
public sealed record SocialModerationVerdict(bool Allowed, string Code, string Message);

public interface ISocialPostModeration
{
    /// <summary>
    /// Screens the post text and the video bytes' stored verdict for publication. FAILS CLOSED: a block, an outage,
    /// a verdict still pending and "moderation is not configured while the policy requires it" all refuse.
    /// </summary>
    Task<SocialModerationVerdict> ScreenAsync(SocialPost post, SocialResolvedOutput output, CancellationToken ct);
}

/// <summary>The real screen: <see cref="PublishModeration"/> for the video, text screening for the title and body.</summary>
public sealed class SocialPostModeration(PublishModeration moderation) : ISocialPostModeration
{
    public async Task<SocialModerationVerdict> ScreenAsync(SocialPost post, SocialResolvedOutput output, CancellationToken ct)
    {
        ModerationAttribution text = new(post.OrganizationId, post.ProjectId, post.CreatedByUserId, null, null, ModerationSurface.Publish, ModerationContentKind.Text);
        string content = string.Join("\n", new[] { post.Title, post.Body, post.HashtagsJson }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (content.Length > 0)
        {
            PublishModerationOutcome textVerdict = await moderation.EvaluateTextAsync(content, text, ct);
            if (!textVerdict.Allowed) return new SocialModerationVerdict(false, textVerdict.Code ?? "CONTENT_BLOCKED", textVerdict.Message);
        }

        ModerationAttribution video = new(post.OrganizationId, post.ProjectId, post.CreatedByUserId, null, post.OutputKind == SocialOutputKinds.StepResult ? post.OutputId : null,
            ModerationSurface.Publish, ModerationContentKind.Video);
        PublishModerationOutcome videoVerdict = await moderation.EvaluateObjectAsync(post.ProjectId, output.StorageKey, output.MimeType, video, ct);
        return videoVerdict.Allowed
            ? new SocialModerationVerdict(true, string.Empty, string.Empty)
            : new SocialModerationVerdict(false, videoVerdict.Code ?? "CONTENT_BLOCKED", videoVerdict.Message);
    }
}
