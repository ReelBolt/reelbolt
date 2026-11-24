using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// Step option <c>registerOutputAsProjectFile</c>: after a step result is persisted as Completed, its
/// delivered output (every delivered clip of a VideoGenerate manifest) is registered IN PLACE as a
/// project file — no copy, the same object the step result points at, exactly like
/// <c>VideoCompile.RegisterProjectFile</c> (plan decision 5).
///
/// <list type="bullet">
/// <item>Idempotent per storage key: a key that already has a project-file row (a retried run, a cache
/// hit serving the same key, or a step that registered its own output) is never registered again.</item>
/// <item>Only the execution's own project's addressable outputs (<see cref="ProjectOutputKeys.IsAddressableOutput"/>):
/// renders under <c>outputFiles/</c> and VideoGenerate takes under <c>agentFiles/generated/</c>.</item>
/// <item>Category (T-023): register-in-place keeps the category the key already names — <c>outputFiles</c> for a
/// render, <c>agentFiles</c> for a generated take — so the row and its object never disagree on where it lives.</item>
/// <item>Quota (T-023, QA-P0-6): CLAUDE.md "workflow outputs are never blocked mid-run", so the plan's storage
/// quota never stops a registration or fails the step. The step output (a JSON object) records
/// <c>registration: {registered, overQuota}</c> so the run's read model can warn.</item>
/// <item>Named "{workflow} - {step label}.{ext}" (sanitised), mime from the extension, size from a HEAD.</item>
/// <item>Best effort: any failure is a logged warning and never fails the step.</item>
/// </list>
/// </summary>
public static class StepOutputRegistration
{
    private const int MaxNameLength = 150;

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".gif"] = "image/gif",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac"
    };

    public static async Task TryRegisterAsync(
        IServiceScopeFactory scopes,
        WorkflowExecution execution,
        WorkflowStep step,
        WorkflowStepResult stepResult,
        ILogger logger,
        CancellationToken ct)
    {
        if (!step.RegisterOutputAsProjectFile || stepResult.Status != StepStatus.Completed)
            return;

        try
        {
            using IServiceScope scope = scopes.CreateScope();
            IServiceProvider services = scope.ServiceProvider;
            if (services.GetService<IProjectFileWorkspace>() is not { } workspace
                || services.GetService<WorkflowEngineDbContext>() is not { } db)
            {
                logger.LogWarning(
                    "Execution {ExecutionId} step {StepOrder}: output not registered as a project file (no file workspace)",
                    execution.Id, step.StepOrder);
                return;
            }

            // The object registered here is the one the step result points at, which may be an older one (a cache hit
            // serves a key an earlier run made). A result that may predate the project's quarantine marker is not
            // registered: it is an unavailable output, and the step stands. A failed read of the marker refuses too.
            if (await ProjectQuarantineState.ResultPredatesAsync(
                    db, execution.ProjectId, stepResult.ExecutedAt, execution.FirstStartedAt, ct))
            {
                logger.LogInformation(
                    "Execution {ExecutionId} step {StepOrder}: the output is unavailable and was not registered as a project file",
                    execution.Id, step.StepOrder);
                return;
            }

            IReadOnlyList<string> keys = step.StepType == StepType.VideoGenerate
                ? await DeliveredClipKeysAsync(workspace, execution.ProjectId, stepResult, ct)
                : stepResult.OutputStorageKey is { } output ? [output] : [];

            List<string> eligible = [.. keys
                .Where(k => ProjectOutputKeys.IsAddressableOutput(execution.ProjectId, k))
                .Distinct(StringComparer.Ordinal)];
            if (eligible.Count == 0)
            {
                // QA-P0-20: this path registered nothing and used to be silent; say so, so a run that registers
                // nothing is visible and never read as a successful registration.
                logger.LogInformation(
                    "Execution {ExecutionId} step {StepOrder}: no addressable output to register as a project file "
                    + "(a render under outputFiles/ or a generated clip under agentFiles/generated/)",
                    execution.Id, step.StepOrder);
                return;
            }

            HashSet<string> already = [.. await db.ProjectFiles.AsNoTracking().IgnoreQueryFilters()
                .Where(f => f.ProjectId == execution.ProjectId && eligible.Contains(f.StorageKey))
                .Select(f => f.StorageKey)
                .ToListAsync(ct)];

            List<(string Key, string Name, long Size)> pending = [];
            string baseName = BaseName(execution, step);
            for (int i = 0; i < eligible.Count; i++)
            {
                string key = eligible[i];
                if (!already.Contains(key))
                    pending.Add((key, eligible.Count == 1 ? baseName : $"{baseName} {i + 1}", await SizeOfAsync(services, key, ct)));
            }

            if (pending.Count == 0)
                return;

            bool overQuota = await IsOverQuotaAsync(services, db, execution, pending.Sum(p => p.Size), logger, ct);
            int registered = 0;
            foreach ((string key, string name, long size) in pending)
            {
                if (await RegisterOneAsync(workspace, db, execution, step, key, name, size, logger, ct))
                    registered++;
            }

            if (registered > 0)
                await RecordRegistrationAsync(db, step, stepResult, registered, overQuota, logger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Execution {ExecutionId} step {StepOrder}: registering the output as a project file failed; the step stands",
                execution.Id, step.StepOrder);
        }
    }

    /// <summary>
    /// Registers ONE addressable output of the running step IN PLACE under the caller's own display name (the extension of
    /// the key is kept), the same way <see cref="TryRegisterAsync"/> registers an output: idempotent per storage key (an
    /// already registered key answers its existing row), only the project's addressable outputs, the output-produced stamp,
    /// and the plan storage quota recorded as a flag (CLAUDE.md "workflow outputs are never blocked mid-run": never a
    /// refusal). Used by a step that names its output after its source (<c>VideoEnhance</c>). Best effort: a failure is a
    /// logged warning and a null.
    /// </summary>
    public static async Task<(Guid? FileId, bool OverQuota)> TryRegisterNamedAsync(
        IServiceScopeFactory scopes,
        WorkflowExecution execution,
        WorkflowStep step,
        string storageKey,
        string displayName,
        long sizeBytes,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            if (!ProjectOutputKeys.IsAddressableOutput(execution.ProjectId, storageKey))
                return (null, false);

            using IServiceScope scope = scopes.CreateScope();
            IServiceProvider services = scope.ServiceProvider;
            if (services.GetService<IProjectFileWorkspace>() is not { } workspace
                || services.GetService<WorkflowEngineDbContext>() is not { } db)
                return (null, false);

            Guid? existing = await db.ProjectFiles.AsNoTracking().IgnoreQueryFilters()
                .Where(f => f.ProjectId == execution.ProjectId && f.StorageKey == storageKey)
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return (existing, false);

            bool overQuota = await IsOverQuotaAsync(services, db, execution, sizeBytes, logger, ct);
            string extension = Path.GetExtension(storageKey);
            string baseName = Sanitize(Path.GetFileNameWithoutExtension(displayName));
            string fileName = baseName + extension.ToLowerInvariant();
            string mime = MimeByExtension.GetValueOrDefault(extension, "application/octet-stream");
            ProjectWorkspaceFile file = await workspace.RegisterStoredObjectAsync(
                execution.ProjectId, storageKey, fileName, mime, sizeBytes,
                SummaryStatus.Done, FileIndexingStatus.NotIndexed, ct, category: CategoryOf(execution.ProjectId, storageKey));
            await StampProducedAsync(db, file.Id, execution.FirstStartedAt ?? DateTime.UnixEpoch, ct);
            logger.LogInformation(
                "Execution {ExecutionId} step {StepOrder}: registered output as project file {FileId}",
                execution.Id, step.StepOrder, file.Id);
            return (file.Id, overQuota);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Execution {ExecutionId} step {StepOrder}: the output was not registered as a project file",
                execution.Id, step.StepOrder);
            return (null, false);
        }
    }

    private static async Task<bool> RegisterOneAsync(
        IProjectFileWorkspace workspace, WorkflowEngineDbContext db, WorkflowExecution execution, WorkflowStep step,
        string key, string name, long size, ILogger logger, CancellationToken ct)
    {
        try
        {
            string extension = Path.GetExtension(key);
            string mime = MimeByExtension.GetValueOrDefault(extension, "application/octet-stream");
            ProjectWorkspaceFile file = await workspace.RegisterStoredObjectAsync(
                execution.ProjectId, key, name + extension.ToLowerInvariant(), mime, size,
                SummaryStatus.Done, FileIndexingStatus.NotIndexed, ct, category: CategoryOf(execution.ProjectId, key));
            // The row records when the output was made: the run's first start (an earlier, true bound for a cache hit that
            // serves an older key), written once here and untouched by any later move or rename.
            await StampProducedAsync(db, file.Id, execution.FirstStartedAt ?? DateTime.UnixEpoch, ct);
            logger.LogInformation(
                "Execution {ExecutionId} step {StepOrder}: registered output {StorageKey} as project file {FileId}",
                execution.Id, step.StepOrder, key, file.Id);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Execution {ExecutionId} step {StepOrder}: output {StorageKey} was not registered as a project file",
                execution.Id, step.StepOrder, key);
            return false;
        }
    }

    private static async Task StampProducedAsync(WorkflowEngineDbContext db, Guid fileId, DateTime producedAt, CancellationToken ct)
    {
        ProjectFile? row = await db.ProjectFiles.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (row is null)
            return;

        row.OutputProducedAt = producedAt;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The project-file category the key already names: <c>agentFiles</c> for a generated take, else <c>outputFiles</c>.</summary>
    private static string CategoryOf(Guid projectId, string key) =>
        key.StartsWith(ProjectOutputKeys.GeneratedPrefix(projectId), StringComparison.Ordinal) ? "agentFiles" : "outputFiles";

    /// <summary>
    /// Would <paramref name="incomingBytes"/> more pass the organization's plan storage quota (the same
    /// <see cref="PlanGate.CheckStorage"/> rule uploads use, over the same sum of project-file bytes)? Only ever a flag:
    /// a missing entitlement service, an unlimited plan or an error answers false and nothing is blocked.
    /// </summary>
    private static async Task<bool> IsOverQuotaAsync(
        IServiceProvider services, WorkflowEngineDbContext db, WorkflowExecution execution, long incomingBytes,
        ILogger logger, CancellationToken ct)
    {
        if (incomingBytes <= 0 || execution.OrganizationId == Guid.Empty
            || services.GetService<IOrganizationEntitlements>() is not { } entitlements)
            return false;

        try
        {
            OrganizationEntitlementSet set = await entitlements.GetAsync(execution.OrganizationId, ct);
            if (set.StorageGb == OrganizationEntitlementSet.UnlimitedInt)
                return false;

            Guid organizationId = execution.OrganizationId;
            IQueryable<Project> projects = db.Projects.AsNoTracking().IgnoreQueryFilters();
            long used = await db.ProjectFiles.AsNoTracking().IgnoreQueryFilters()
                .Where(f => projects.Any(p => p.Id == f.ProjectId && p.OrganizationId == organizationId))
                // Built-in library sounds are quota-exempt (single-sourced in ProjectFileQuota).
                .Where(f => f.SourceKind != ReelBolt.Shared.Billing.ProjectFileQuota.SfxLibrarySourceKind)
                .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
            return PlanGate.CheckStorage(used, incomingBytes, set) is not null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Execution {ExecutionId}: storage quota could not be read while registering outputs; registering anyway",
                execution.Id);
            return false;
        }
    }

    /// <summary>
    /// Adds <c>registration: {registered, overQuota}</c> to the persisted step output when it is a JSON object
    /// (VideoGenerate, VideoCompile, TimelineRender). A text output (an Author step's prose) is left as it is.
    /// </summary>
    private static async Task RecordRegistrationAsync(
        WorkflowEngineDbContext db, WorkflowStep step, WorkflowStepResult stepResult, int registered, bool overQuota,
        ILogger logger, CancellationToken ct)
    {
        if (stepResult.OutputJson is not { Length: > 0 } json || JsonNode.Parse(json) is not JsonObject output)
        {
            logger.LogInformation(
                "Step {StepOrder}: registered {Registered} output(s) (overQuota={OverQuota}); its output is not a JSON object, so no note was added",
                step.StepOrder, registered, overQuota);
            return;
        }

        output["registration"] = new JsonObject { ["registered"] = registered, ["overQuota"] = overQuota };
        string patched = output.ToJsonString();
        stepResult.OutputJson = patched;
        if (await db.WorkflowStepResults.FirstOrDefaultAsync(r => r.Id == stepResult.Id, ct) is { } row)
        {
            row.OutputJson = patched;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The stored object's size; 0 when the object store cannot say (the row is still useful).</summary>
    private static async Task<long> SizeOfAsync(IServiceProvider services, string key, CancellationToken ct)
    {
        if (services.GetService<IAmazonS3>() is not { } s3)
            return 0;

        string bucket = services.GetService<IConfiguration>()?["MinIO:BucketName"] ?? "reelforge";
        GetObjectMetadataResponse head = await s3.GetObjectMetadataAsync(
            new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct);
        return head.ContentLength;
    }

    /// <summary>Every succeeded clip of a VideoGenerate manifest (the step result's ArtifactStorageKey).</summary>
    private static async Task<IReadOnlyList<string>> DeliveredClipKeysAsync(
        IProjectFileWorkspace workspace, Guid projectId, WorkflowStepResult stepResult, CancellationToken ct)
    {
        List<string> keys = [];
        if (!string.IsNullOrWhiteSpace(stepResult.ArtifactStorageKey))
        {
            string local = Path.Combine(Path.GetTempPath(), $"rb-register-{Guid.NewGuid():N}.json");
            try
            {
                await workspace.DownloadStorageKeyToFileAsync(projectId, stepResult.ArtifactStorageKey, local, ct);
                if (JsonNode.Parse(await File.ReadAllTextAsync(local, ct)) is JsonObject { } manifest
                    && manifest["clips"] is JsonArray clips)
                {
                    foreach (JsonObject clip in clips.OfType<JsonObject>())
                    {
                        if (string.Equals(clip["status"]?.GetValue<string>(), "succeeded", StringComparison.OrdinalIgnoreCase)
                            && clip["storageKey"]?.GetValue<string>() is { Length: > 0 } key)
                            keys.Add(key);
                    }
                }
            }
            finally
            {
                File.Delete(local);
            }
        }

        // No (readable) manifest: the step's first delivered clip is still its OutputStorageKey.
        if (keys.Count == 0 && stepResult.OutputStorageKey is { } first)
            keys.Add(first);
        return keys;
    }

    private static string BaseName(WorkflowExecution execution, WorkflowStep step)
    {
        string workflow = execution.WorkflowDefinition?.Name is { Length: > 0 } name ? name : "Workflow";
        string label = !string.IsNullOrWhiteSpace(step.Label) ? step.Label
            : step.AgentDefinition?.Name is { Length: > 0 } agent ? agent
            : $"Step {step.StepOrder}";
        return Sanitize($"{workflow} - {label}");
    }

    private static string Sanitize(string name)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];
        string cleaned = new([.. name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c)]);
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        if (cleaned.Length > MaxNameLength)
            cleaned = cleaned[..MaxNameLength].TrimEnd(' ', '.');
        return cleaned.Length == 0 ? "Output" : cleaned;
    }
}
