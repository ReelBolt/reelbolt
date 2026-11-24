using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>What a <see cref="VideoSourceKind.BriefFiles"/> source expanded to: project files in the brief's order, or a code and a plain message.</summary>
public sealed record BriefFilesResolution(IReadOnlyList<VideoSourceRef> Sources, string? Code, string? Message)
{
    public bool Failed => Code is not null;
}

/// <summary>
/// Expands a <see cref="VideoSourceKind.BriefFiles"/> source (the run page's file choice) into one
/// <see cref="VideoSourceKind.ProjectFile"/> source per file named in <c>brief.sourceFiles[role]</c>.
/// The ids come from the run record, but each one is re-checked against the files of the execution's own
/// project (the workspace lists only that project, which is org-scoped), so a foreign, deleted or invented
/// id is refused with the same plain message as an absent one and never read.
/// </summary>
public static class BriefFilesSourceResolver
{
    public const string NoFilesCode = "INPUT_MISSING";

    public static async Task<BriefFilesResolution> ResolveAsync(
        IProjectFileWorkspace workspace, Guid projectId, VideoSourceRef source, RunBriefState? brief, CancellationToken ct)
    {
        if (source.Kind != VideoSourceKind.BriefFiles || source.Role is not { } role)
            return new BriefFilesResolution([], "SOURCE_UNRESOLVED", "Source=BriefFiles requires Role.");

        IReadOnlyList<Guid> ids = brief?.FilesFor(role) ?? [];
        string roleName = role.ToString().ToLowerInvariant();
        if (ids.Count == 0)
        {
            return new BriefFilesResolution([], NoFilesCode,
                $"This step reads the {roleName} files chosen for the run, and none were chosen. Choose at least one {roleName} file when you start the run.");
        }

        IReadOnlyList<ProjectWorkspaceFile> files = await workspace.ListFilesAsync(projectId, ct);
        HashSet<Guid> known = files.Select(f => f.Id).ToHashSet();
        if (ids.Any(id => !known.Contains(id)))
        {
            return new BriefFilesResolution([], NoFilesCode,
                $"One or more of the {roleName} files chosen for this run are not available in this project. Choose them again when you start the run.");
        }

        List<VideoSourceRef> expanded = ids
            .Select(id => new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: id, InSec: source.InSec, OutSec: source.OutSec))
            .ToList();
        return new BriefFilesResolution(expanded, null, null);
    }

    /// <summary>
    /// <paramref name="sources"/> with every BriefFiles source replaced by its project files (other kinds untouched, same
    /// list instance when none is present).
    /// </summary>
    public static async Task<(IReadOnlyList<VideoSourceRef> Sources, BriefFilesResolution? Failure)> ExpandAsync(
        IProjectFileWorkspace workspace, Guid projectId, IReadOnlyList<VideoSourceRef> sources, RunBriefState? brief, CancellationToken ct)
    {
        if (!sources.Any(s => s.Kind == VideoSourceKind.BriefFiles))
            return (sources, null);

        var result = new List<VideoSourceRef>();
        foreach (VideoSourceRef source in sources)
        {
            if (source.Kind != VideoSourceKind.BriefFiles)
            {
                result.Add(source);
                continue;
            }

            BriefFilesResolution resolved = await ResolveAsync(workspace, projectId, source, brief, ct);
            if (resolved.Failed)
                return (sources, resolved);
            result.AddRange(resolved.Sources);
        }

        return (result, null);
    }
}
