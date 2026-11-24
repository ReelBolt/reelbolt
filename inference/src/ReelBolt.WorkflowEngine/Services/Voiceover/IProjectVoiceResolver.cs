using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Voiceover;

/// <summary>
/// The engine's read of a project's voice library (see docs/voiceover.md "Voices and consent"). The
/// Inference API owns <c>project_voices</c>; this resolver reads the table through the engine's own
/// read-only mapping and applies the SAME rule the API's <c>Selectable</c> flag applies —
/// <see cref="ProjectVoice.IsSelectable"/> — so a cloned voice whose consent is missing, revoked or
/// expired is refused here exactly as it is hidden there. A voice is only ever resolved WITHIN the
/// project that owns it: a <c>ProjectVoiceId</c> from another project resolves to "not found", never
/// to the other project's voice.
/// </summary>
public interface IProjectVoiceResolver
{
    /// <summary>Resolves one voice for the given project; <see cref="ResolvedProjectVoice.Reason"/> says why it is unusable, when it is.</summary>
    Task<ResolvedProjectVoice> ResolveAsync(Guid projectId, Guid projectVoiceId, CancellationToken ct);

    /// <summary>
    /// The subset of <paramref name="remoteVoiceIds"/> that must NOT be spoken in <paramref name="projectId"/>, by
    /// <see cref="VendorVoiceIdRule"/> (one query over every project's rows): the id matches an unusable voice of this
    /// project, or a cloned voice of another project. An id matching nothing is a raw vendor id and is not returned.
    /// </summary>
    Task<IReadOnlyList<string>> FindRefusedVendorVoiceIdsAsync(Guid projectId, IReadOnlyCollection<string> remoteVoiceIds, CancellationToken ct);

    /// <summary>Every voice of the project that may be spoken RIGHT NOW — the offered set for voice casting.</summary>
    Task<IReadOnlyList<ResolvedProjectVoice>> ListSelectableAsync(Guid projectId, CancellationToken ct);
}

/// <summary>
/// One voice as the Voiceover step sees it. <see cref="RemoteVoiceId"/> is the vendor id the
/// synthesis request carries as the voice; it is null whenever <see cref="Usable"/> is false, so a
/// caller cannot speak an unconsented voice by reading the wrong field.
/// </summary>
public sealed record ResolvedProjectVoice(
    Guid Id,
    string Name,
    string? Description,
    ProjectVoiceSource Source,
    Guid? InferenceProviderId,
    string? RemoteVoiceId,
    bool Usable,
    string? Reason)
{
    public static ResolvedProjectVoice NotFound(Guid id) =>
        new(id, string.Empty, null, ProjectVoiceSource.Library, null, null, false, "voice_not_found");
}

public sealed class ProjectVoiceResolver : IProjectVoiceResolver
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ProjectVoiceResolver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<ResolvedProjectVoice> ResolveAsync(Guid projectId, Guid projectVoiceId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        ProjectVoice? voice = await db.ProjectVoices
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == projectVoiceId && v.ProjectId == projectId, ct);

        return voice is null ? ResolvedProjectVoice.NotFound(projectVoiceId) : Project(voice, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<string>> FindRefusedVendorVoiceIdsAsync(
        Guid projectId, IReadOnlyCollection<string> remoteVoiceIds, CancellationToken ct)
    {
        if (remoteVoiceIds.Count == 0)
            return [];

        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        string[] ids = [.. remoteVoiceIds];
        List<ProjectVoice> rows = await db.ProjectVoices
            .AsNoTracking()
            .Where(VendorScope(db, projectId, ids))
            .ToListAsync(ct);

        DateTime now = DateTime.UtcNow;
        return [.. ids.Where(id => VendorVoiceIdRule.IsRefused(
            projectId, rows.Where(r => r.RemoteVoiceId == id), r => Project(r, now).Usable))];
    }

    public async Task<IReadOnlyList<ResolvedProjectVoice>> ListSelectableAsync(Guid projectId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        List<ProjectVoice> voices = await db.ProjectVoices
            .AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .OrderBy(v => v.CreatedAt)
            .ToListAsync(ct);

        DateTime now = DateTime.UtcNow;
        return voices.Select(v => Project(v, now)).Where(v => v.Usable).ToList();
    }

    /// <summary>The rows the vendor-id rule may consult for a project: <see cref="VendorVoiceIdRule.Scope"/>.</summary>
    public static System.Linq.Expressions.Expression<Func<ProjectVoice, bool>> VendorScope(
        WorkflowEngineDbContext db, Guid projectId, string[] remoteVoiceIds) =>
        VendorVoiceIdRule.Scope(db.Projects, db.ModelCapabilities, projectId, remoteVoiceIds);

    /// <summary>The one place the row becomes a usable-or-not answer; shared by both reads so they cannot disagree.</summary>
    public static ResolvedProjectVoice Project(ProjectVoice voice, DateTime utcNow)
    {
        bool usable = voice.IsSelectable(utcNow) && !string.IsNullOrWhiteSpace(voice.RemoteVoiceId) &&
                      (voice.Source == ProjectVoiceSource.Library || voice.InferenceProviderId is not null);
        string? reason = usable
            ? null
            : voice.UnselectableReason(utcNow)
              ?? (string.IsNullOrWhiteSpace(voice.RemoteVoiceId) ? "voice_has_no_remote_id" : "provider_row_deleted");

        return new ResolvedProjectVoice(
            voice.Id, voice.Name, voice.Description, voice.Source, voice.InferenceProviderId,
            usable ? voice.RemoteVoiceId : null, usable, reason);
    }
}
