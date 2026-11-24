using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Enforces the retention deadline on cloned voices (see docs/voiceover.md "Voices and consent"):
/// every <see cref="SweepInterval"/>, each <see cref="ProjectVoiceSource.Cloned"/> row whose
/// <c>RetentionUntil</c> has passed — or whose consent was revoked more than
/// <see cref="RevokedGraceDays"/> ago — is deleted the same way the DELETE endpoint deletes it:
/// vendor model (best effort, logged), reference recordings, then the row. The read path already
/// refuses an expired or revoked voice; this service is what makes "deleted after N days" a fact
/// rather than a sentence in the docs. Each sweep is bounded (<see cref="MaxPerSweep"/>) and every
/// failure is logged and skipped, never thrown — a vendor outage must not stop the next sweep.
/// </summary>
public sealed class ProjectVoiceRetentionService : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    public const int RevokedGraceDays = 30;
    public const int MaxPerSweep = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectVoiceRetentionService> _logger;

    private readonly bool _sweepEnabled;

    public ProjectVoiceRetentionService(IServiceScopeFactory scopeFactory, ILogger<ProjectVoiceRetentionService> logger,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // ProjectVoices:RetentionSweepEnabled (default true). Local staging sets it false: its database
        // is a copy of prod, so a sweep there would delete real cloned voices at the vendor.
        _sweepEnabled = configuration?.GetValue("ProjectVoices:RetentionSweepEnabled", true) ?? true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_sweepEnabled)
        {
            _logger.LogWarning("Voice retention sweep is disabled (ProjectVoices:RetentionSweepEnabled=false)");
            return;
        }

        // Let the API finish migrating/seeding before the first sweep.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int deleted = await SweepOnceAsync(stoppingToken);
                if (deleted > 0)
                    _logger.LogInformation("Voice retention sweep deleted {Count} expired or revoked cloned voice(s)", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Voice retention sweep failed; will retry after the interval");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One sweep, exposed for tests. Returns the number of rows deleted.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        // Retention is platform housekeeping across every organization's voices.
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
        var providers = scope.ServiceProvider.GetRequiredService<IInferenceProviderResolver>();
        var speechFactory = scope.ServiceProvider.GetRequiredService<ISpeechSynthesisClientFactory>();

        DateTime now = DateTime.UtcNow;
        DateTime revokedCutoff = now.AddDays(-RevokedGraceDays);

        List<ProjectVoice> due = await db.ProjectVoices
            .Where(v => v.Source == ProjectVoiceSource.Cloned &&
                        ((v.RetentionUntil != null && v.RetentionUntil <= now) ||
                         (v.ConsentRevokedAt != null && v.ConsentRevokedAt <= revokedCutoff)))
            .OrderBy(v => v.RetentionUntil)
            .Take(MaxPerSweep)
            .ToListAsync(ct);

        int deleted = 0;
        foreach (ProjectVoice voice in due)
        {
            (bool remoteDeleted, string? note) = await ProjectVoicesController.DeleteRemoteModelAsync(voice, providers, speechFactory, _logger, ct);
            int references = await ProjectVoicesController.DeleteReferenceFilesAsync(
                voice, db, storage, _logger, ct, scope.ServiceProvider.GetService<ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser>());
            db.ProjectVoices.Remove(voice);
            await db.SaveChangesAsync(ct);
            deleted++;
            _logger.LogInformation(
                "Retention deleted cloned voice {VoiceId} of project {ProjectId}: vendor model deleted={RemoteDeleted} ({Note}), reference recordings deleted={References}",
                voice.Id, voice.ProjectId, remoteDeleted, note ?? "ok", references);
        }

        return deleted;
    }
}
