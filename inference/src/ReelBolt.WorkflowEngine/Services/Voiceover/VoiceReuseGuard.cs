using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Voiceover;

/// <summary>
/// Re-checks voice consent when a Voiceover result is REUSED instead of produced (step-result cache hit,
/// resume reuse, composition replay copy — docs/voiceover.md "Voices and consent"). The executor's guard only
/// runs on a live step, so without this a cloned voice whose consent was revoked, whose retention expired or
/// that was deleted could be replayed from a recorded result. A blocked answer means "treat as a MISS": the
/// step then runs live and the executor's own guard fails it with its usual code.
/// </summary>
public interface IVoiceReuseGuard
{
    /// <summary>
    /// True when every voice the recorded <paramref name="outputJson"/> used (and the step's configured one) is
    /// still speakable in <paramref name="projectId"/>. Never throws except for cancellation: an error is a block.
    /// </summary>
    Task<bool> IsReuseAllowedAsync(Guid projectId, string? voiceoverConfigJson, string? outputJson, CancellationToken ct);
}

public sealed class VoiceReuseGuard : IVoiceReuseGuard
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VoiceReuseGuard>? _logger;

    public VoiceReuseGuard(IServiceScopeFactory scopeFactory, ILogger<VoiceReuseGuard>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> IsReuseAllowedAsync(Guid projectId, string? voiceoverConfigJson, string? outputJson, CancellationToken ct)
    {
        try
        {
            var voiceIds = new HashSet<Guid>();
            var remoteIds = new HashSet<string>(StringComparer.Ordinal);
            CollectFromConfig(voiceoverConfigJson, voiceIds, remoteIds);

            // The output's shape lives next to the executor that writes it. A non-blank result that does not look
            // like a Voiceover result cannot be shown to be voice-free, so it is not reused (fail closed).
            VoiceoverOutputVoices recorded = VoiceoverOutputVoices.Read(outputJson);
            if (!recorded.WellFormed)
                return false;

            if (recorded.ProjectVoiceId is Guid pv)
                voiceIds.Add(pv);
            if (recorded.CastProjectVoiceId is Guid cv)
                voiceIds.Add(cv);
            if (recorded.RemoteVoiceId is { } remote)
                remoteIds.Add(remote);

            // A project voice or cast voice recorded WITHOUT its id (results cached before the id was written)
            // can only be found by its vendor id, and must be found: a deleted row must not read as "no voice".
            bool needsRemoteRow =
                (recorded.Source == VoiceoverOutputVoices.ProjectVoiceSource && recorded.ProjectVoiceId is null) ||
                (recorded.Source == VoiceoverOutputVoices.CastSource && recorded.CastProjectVoiceId is null);
            if (needsRemoteRow && recorded.RemoteVoiceId is null)
                return false;

            if (voiceIds.Count == 0 && remoteIds.Count == 0)
                return true;

            using IServiceScope scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            Guid[] ids = voiceIds.ToArray();
            string[] remotes = remoteIds.ToArray();

            // One query (a UNION). A recorded project-voice id resolves only within the execution's project (another
            // project's voice is "not found"); a vendor id is matched against this project's rows and those of other
            // projects of the SAME organization (ProjectVoiceResolver.VendorScope), because VendorVoiceIdRule refuses a
            // clone of a sibling project; other organizations' rows are never read.
            List<ProjectVoice> rows = await db.ProjectVoices
                .AsNoTracking()
                .Where(v => v.ProjectId == projectId && ids.Contains(v.Id))
                .Union(db.ProjectVoices.AsNoTracking().Where(ProjectVoiceResolver.VendorScope(db, projectId, remotes)))
                .ToListAsync(ct);

            DateTime now = DateTime.UtcNow;
            // Same projection the executor's guard resolves through, so the two cannot disagree.
            bool Usable(ProjectVoice r) => ProjectVoiceResolver.Project(r, now).Usable;

            foreach (Guid id in voiceIds)
            {
                ProjectVoice? own = rows.FirstOrDefault(r => r.Id == id && r.ProjectId == projectId);
                if (own is null || !Usable(own))
                    return false;
            }

            // The vendor id of an id-less project/cast voice must be found in THIS project (a deleted row must not read as "no voice").
            if (needsRemoteRow && !rows.Any(r => r.ProjectId == projectId && r.RemoteVoiceId == recorded.RemoteVoiceId))
                return false;

            return !remoteIds.Any(id => VendorVoiceIdRule.IsRefused(projectId, rows.Where(r => r.RemoteVoiceId == id), Usable));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Voice re-check failed for project {ProjectId}; treating the reuse as a miss", projectId);
            return false;
        }
    }

    // Read exactly as the executor reads it (VoiceoverStepConfig.Parse). A config the executor could not parse is not
    // reusable: the live step would fail CONFIG_INVALID, and the guard must not guess at what such a config meant.
    // The vendor ids the executor would synthesize with (DefaultVoiceId, each line's VoiceId) go through the same rule as the output's.
    private static void CollectFromConfig(string? configJson, HashSet<Guid> voiceIds, HashSet<string> remoteIds)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return;
        VoiceoverStepConfig config = VoiceoverStepConfig.Parse(configJson)
            ?? throw new JsonException("VoiceoverConfigJson deserialized to null.");
        if (config.ProjectVoiceId is Guid id)
            voiceIds.Add(id);
        if (!string.IsNullOrWhiteSpace(config.DefaultVoiceId))
            remoteIds.Add(config.DefaultVoiceId);
        foreach (VoiceoverLine line in config.Source?.Lines ?? [])
        {
            if (!string.IsNullOrWhiteSpace(line.VoiceId))
                remoteIds.Add(line.VoiceId);
        }
    }
}
