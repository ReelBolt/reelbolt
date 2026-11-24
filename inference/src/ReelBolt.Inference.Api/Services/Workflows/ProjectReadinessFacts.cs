using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>What a project can do today: the files it holds and the provider capabilities its organization has.</summary>
public sealed record ProjectReadinessSnapshot(
    int VideoFiles,
    int AudioFiles,
    int ImageFiles,
    int CodeFiles,
    IReadOnlySet<InferenceProviderCapability> Capabilities,
    bool HasDesignProfile,
    bool HasSelectableVoice,
    int ArchiveFiles = 0,
    int UnpackingArchives = 0);

/// <summary>One thing a recipe needs that the project does not have, with the action that fixes it.</summary>
public sealed record ReadinessMissing(string Need, string Message, string Action, string? Detail = null);

/// <summary>The facts behind "can this project run that recipe now".</summary>
public interface IProjectReadinessFacts
{
    /// <summary>
    /// Loads the facts of a project the caller has ALREADY been authorized for (one
    /// <c>IProjectAccessService</c> call per action, made by the action). Reads only that project's rows and
    /// its organization's providers.
    /// </summary>
    Task<ProjectReadinessSnapshot> LoadAsync(Project project, CancellationToken ct);
}

/// <summary>
/// The shared readiness facts: file kinds present and provider capabilities available. The workflow-apps gallery uses
/// it for recipe fit; the assistant's project-readiness tool reads the same facts, so the two cannot disagree.
/// </summary>
public sealed class ProjectReadinessFacts : IProjectReadinessFacts
{
    private static readonly string[] CodeMimeTypes =
    [
        "application/json", "application/javascript", "application/xml"
    ];

    /// <summary>Shown instead of "Add your code" when the project holds only archives and no readable code.</summary>
    public const string ArchiveOnlyCodeMessage = "We can't read the code in your zip";

    /// <summary>What to do about <see cref="ArchiveOnlyCodeMessage"/>.</summary>
    public const string ArchiveOnlyCodeDetail = "Upload the source as a .zip without node_modules, connect GitHub, or sync a folder";

    /// <summary>Shown instead of the archive message while a zip's unpack is still queued or running.</summary>
    public const string UnpackingCodeMessage = "Your zip is still unpacking";

    /// <summary>What to do about <see cref="UnpackingCodeMessage"/>.</summary>
    public const string UnpackingCodeDetail = "Its files appear in the project in a moment; try again shortly";

    private readonly InferenceApiDbContext _db;

    public ProjectReadinessFacts(InferenceApiDbContext db) => _db = db;

    /// <summary>
    /// The project's uploaded files by kind (video, audio, image, code). The one definition of "has code" and
    /// "has footage", shared with the pre-run linter's WL010 so the app page and the lint cannot disagree.
    /// </summary>
    public async Task<(int Video, int Audio, int Image, int Code)> CountFilesAsync(Guid projectId, CancellationToken ct)
    {
        (int video, int audio, int image, int code, _) = await CountFilesWithArchivesAsync(projectId, ct);
        return (video, audio, image, code);
    }

    /// <summary>
    /// <see cref="CountFilesAsync"/> plus the number of archive uploads (zip, tar, ...). Archives are never counted
    /// as code; they are reported on their own so "we can't read your zip" can be told apart from "no code".
    /// </summary>
    public async Task<(int Video, int Audio, int Image, int Code, int Archives)> CountFilesWithArchivesAsync(
        Guid projectId, CancellationToken ct)
    {
        IQueryable<ProjectFile> files = _db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId && f.Category == "userFiles");

        int video = await files.CountAsync(f => f.MimeType.StartsWith("video/"), ct);
        int audio = await files.CountAsync(f => f.MimeType.StartsWith("audio/"), ct);
        int image = await files.CountAsync(f => f.MimeType.StartsWith("image/"), ct);
        // Code: synced or text files, never an archive (a synced zip is still a zip) and never a quarantined file.
        int code = await files
            .Where(f => f.CsamStatus != CsamScanStatus.Quarantined
                        && (f.SourceKind == "github" || f.MimeType.StartsWith("text/") || CodeMimeTypes.Contains(f.MimeType)))
            .WhereNotArchive()
            .CountAsync(ct);
        var candidates = await files
            .Where(f => !f.MimeType.StartsWith("video/") && !f.MimeType.StartsWith("audio/") && !f.MimeType.StartsWith("image/"))
            .Select(f => new { f.MimeType, f.OriginalFileName })
            .ToListAsync(ct);
        int archives = candidates.Count(f => ProjectArchiveKinds.IsArchive(f.MimeType, f.OriginalFileName));
        return (video, audio, image, code, archives);
    }

    /// <summary>How many zips of the project are still being unpacked (queued or running).</summary>
    public Task<int> CountUnpackingAsync(Guid projectId, CancellationToken ct) =>
        _db.ProjectArchiveUnpacks.AsNoTracking().CountAsync(
            u => u.ProjectId == projectId
                 && (u.Status == ArchiveUnpackRunStatus.Pending || u.Status == ArchiveUnpackRunStatus.Running), ct);

    public async Task<ProjectReadinessSnapshot> LoadAsync(Project project, CancellationToken ct)
    {
        (int video, int audio, int image, int code, int archives) = await CountFilesWithArchivesAsync(project.Id, ct);

        int unpacking = code == 0 && archives > 0 ? await CountUnpackingAsync(project.Id, ct) : 0;

        Guid organizationId = project.OrganizationId;
        List<InferenceProviderCapability> capabilities = await _db.ModelCapabilities.AsNoTracking()
            .Where(c => c.IsEnabled && (c.OrganizationId == null || c.OrganizationId == organizationId)
                && c.Model.IsEnabled && c.Model.Provider.IsEnabled)
            .Select(c => c.Capability)
            .Distinct()
            .ToListAsync(ct);

        bool profile = await _db.DesignSystems.AsNoTracking()
            .AnyAsync(d => d.ProjectId == project.Id && d.CurrentVersionId != null, ct);

        // Mirror IProjectVoiceResolver.Project's usable rule exactly (do not drift from it): the voice must be
        // selectable, carry a remote id, and either be a Library voice or still have its provider capability row.
        DateTime now = DateTime.UtcNow;
        List<ProjectVoice> voices = await _db.ProjectVoices.AsNoTracking()
            .Where(v => v.ProjectId == project.Id)
            .ToListAsync(ct);
        bool hasSelectableVoice = voices.Any(v =>
            v.IsSelectable(now)
            && !string.IsNullOrWhiteSpace(v.RemoteVoiceId)
            && (v.Source == ProjectVoiceSource.Library || v.InferenceProviderId is not null));

        return new ProjectReadinessSnapshot(video, audio, image, code, capabilities.ToHashSet(), profile, hasSelectableVoice, archives, unpacking);
    }

    /// <summary>
    /// What a workflow with these steps still needs from the project. Pure: a video slot needs video files, a code
    /// analyzer needs code, a voice step needs a speech provider, a generation step needs a video provider.
    /// </summary>
    public static IReadOnlyList<ReadinessMissing> Evaluate(
        IReadOnlyList<AppStepFacts> steps, ProjectReadinessSnapshot facts, bool requiresProfile = false)
    {
        List<ReadinessMissing> missing = [];
        if (BriefFieldConsumers.AcceptsFileRole("video", steps) && facts.VideoFiles == 0)
            missing.Add(new ReadinessMissing("video", "Add clips", "add-files"));
        if (BriefFieldConsumers.AcceptsFileRole("code", steps) && facts.CodeFiles == 0)
            missing.Add(facts.UnpackingArchives > 0
                ? new ReadinessMissing("code", UnpackingCodeMessage, "add-files", UnpackingCodeDetail)
                : facts.ArchiveFiles > 0
                    ? new ReadinessMissing("code", ArchiveOnlyCodeMessage, "add-files", ArchiveOnlyCodeDetail)
                    : new ReadinessMissing("code", "Add your code", "add-files"));
        if (requiresProfile && !facts.HasDesignProfile)
            missing.Add(new ReadinessMissing("profile", "Build your brand profile", "build-profile"));
        // A voice step is covered either by a configured speech provider or by a usable voice the project already
        // owns; only when it has neither does it need the user to connect one.
        if (steps.Any(s => s.StepType == StepType.Voiceover) && !facts.HasSelectableVoice
            && !facts.Capabilities.Contains(InferenceProviderCapability.SpeechSynthesis))
            missing.Add(new ReadinessMissing("voice", "Connect a voice provider", "connect-provider"));
        // A talking head (a VideoGenerate step with avatar speech) runs on a lip-sync model; any other generation step on a video provider.
        IEnumerable<AppStepFacts> generating = steps.Where(s => s.StepType == StepType.VideoGenerate);
        if (generating.Any(s => !s.IsTalkingHead) && !facts.Capabilities.Contains(InferenceProviderCapability.VideoGeneration))
            missing.Add(new ReadinessMissing("videoGeneration", "Connect a video-generation provider", "connect-provider"));
        if (generating.Any(s => s.IsTalkingHead) && !facts.Capabilities.Contains(InferenceProviderCapability.LipSync))
            missing.Add(new ReadinessMissing("lipSync", "Add a lip-sync model", "connect-provider"));
        return missing;
    }
}
