using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// The code-reading analyzer agents are useless (and wasteful) when the project has no readable source: they
/// run blind and report "unknown". This decides, before any model call, whether such a step can start.
/// "Readable" is the same definition the project readiness facts use: a GitHub file or a text/json/javascript/xml
/// file; an archive is not code.
/// </summary>
internal static class CodeSourceGuard
{
    internal const string NoReadableSourceCode = "NO_READABLE_SOURCE";
    internal const string ArchiveUnreadableCode = "CODE_ARCHIVE_UNREADABLE";

    /// <summary>The agent types that read project code. Kept explicit; a drift test pins it against <see cref="AgentType"/>.</summary>
    internal static readonly IReadOnlySet<AgentType> CodeReadingAgents = new HashSet<AgentType>
    {
        AgentType.CodeStructureAnalyzer,
        AgentType.DependencyAnalyzer,
        AgentType.ComponentInventoryAnalyzer,
        AgentType.RouteAndApiAnalyzer,
        AgentType.StyleAndThemeExtractor
    };

    private static readonly string[] CodeMimeTypes = ["application/json", "application/javascript", "application/xml"];

    /// <summary>Null when the step may run; otherwise the failure code and the customer-facing message.</summary>
    internal static async Task<(string Code, string Message)?> CheckAsync(
        IServiceProvider? services, Guid projectId, AgentType agentType, CancellationToken ct)
    {
        if (services is null || !CodeReadingAgents.Contains(agentType))
            return null;
        IServiceScopeFactory? scopes = services.GetService<IServiceScopeFactory>();
        if (scopes is null)
            return null;

        using IServiceScope scope = scopes.CreateScope();
        WorkflowEngineDbContext? db = scope.ServiceProvider.GetService<WorkflowEngineDbContext>();
        if (db is null)
            return null;

        IQueryable<ProjectFile> files = db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId && f.Category == "userFiles");

        bool readable = await files
            .Where(f => f.CsamStatus != CsamScanStatus.Quarantined
                        && (f.SourceKind == "github" || f.MimeType.StartsWith("text/") || CodeMimeTypes.Contains(f.MimeType)))
            .WhereNotArchive()
            .AnyAsync(ct);
        if (readable)
            return null;

        // Asked of the database, not of a capped page of files: an archive past any file-count cap still counts.
        string? archive = await files.WhereArchive().OrderBy(f => f.OriginalFileName).Select(f => f.OriginalFileName).FirstOrDefaultAsync(ct);

        if (archive is not null)
            return (ArchiveUnreadableCode,
                $"We could not read the code in {archive}. It may still be unpacking, or it could not be unpacked: " +
                "check the Files tab, upload the source as a .zip without node_modules, or connect GitHub.");
        return (NoReadableSourceCode,
            "There is no code to read in this project. Upload the source as a .zip without node_modules, or connect GitHub.");
    }
}
