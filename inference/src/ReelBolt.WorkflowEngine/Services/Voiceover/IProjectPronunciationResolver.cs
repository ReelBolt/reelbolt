using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Voiceover;

/// <summary>
/// The engine's read of a project's pronunciation glossary (docs/voiceover.md "Project pronunciation
/// glossary"). The Inference API owns <c>project_pronunciation_entries</c>; this reads it through the
/// engine's read-only mapping.
/// </summary>
public interface IProjectPronunciationResolver
{
    /// <summary>Every glossary entry of the project, ordered by term (ordinal). Throws when the table cannot be read.</summary>
    Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(Guid projectId, CancellationToken ct);
}

public sealed class ProjectPronunciationResolver : IProjectPronunciationResolver
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ProjectPronunciationResolver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(Guid projectId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        var rows = await db.ProjectPronunciationEntries
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .Select(e => new { e.Term, e.Spelling, e.CaseSensitive })
            .ToListAsync(ct);

        return rows
            .OrderBy(r => r.Term, StringComparer.Ordinal)
            .Select(r => new GlossaryEntry(r.Term, r.Spelling, r.CaseSensitive))
            .ToList();
    }
}
