using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Observability;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// Which workspace lineage a capture belongs to and what it continues from. Mutable on purpose: one
/// instance lives on the ambient <c>WorkflowExecutionContext</c> for a whole revision, and each capture
/// moves <see cref="ParentVersionId"/> forward so a second render chains onto the first.
/// </summary>
public sealed class CompositionLineage
{
    public required Guid WorkspaceId { get; init; }

    /// <summary>The version the next capture is made from (the restored base, then each new version).</summary>
    public Guid? ParentVersionId { get; set; }

    /// <summary>The change request every version captured under this lineage records.</summary>
    public string? Instruction { get; init; }

    public Guid? RevisionId { get; init; }

    public Guid? RequestedByUserId { get; init; }

    /// <summary>The last version a capture under this lineage appended; null until one does.</summary>
    public Guid? LastCapturedVersionId { get; set; }
}

/// <summary>What a capture should record beyond the archive itself.</summary>
public sealed record CompositionCaptureRequest
{
    public required Guid ProjectId { get; init; }
    public required Guid OrganizationId { get; init; }

    /// <summary>The execution whose sandbox is captured (and which "created" the version).</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>The revision lineage, or null to capture into the execution's own (origin) lineage.</summary>
    public CompositionLineage? Lineage { get; init; }

    public CompositionVersionSource Source { get; init; } = CompositionVersionSource.RenderCapture;
    public string? CompositionId { get; init; }
    public double? Fps { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationInFrames { get; init; }
    public string? SceneManifestJson { get; init; }
    public string? RenderOutputStorageKey { get; init; }

    /// <summary>Overrides the "created by" execution (recovery records the ORIGINAL run, not the revision that recovered it).</summary>
    public Guid? CreatedByExecutionId { get; init; }
}

/// <summary>What a capture did. <see cref="Version"/> is the appended (or, deduplicated, the existing) version.</summary>
public sealed record CompositionCaptureResult(
    bool Captured,
    CompositionWorkspaceVersion? Version,
    bool Deduplicated,
    string Outcome,
    string? FailureReason)
{
    public static CompositionCaptureResult Failed(string outcome, string reason) => new(false, null, false, outcome, reason);
}

/// <summary>
/// Layer 2, outbound: snapshot a sandbox's workspace into the store. Used after every successful video
/// render (best effort — see <see cref="TryCaptureAsync"/>), by the revision runner when the agent's own
/// render capture did not land, for preview-only revisions, and for recovering a pre-capture run.
/// </summary>
public interface ICompositionWorkspaceCapture
{
    /// <summary>Capture, throwing <see cref="CompositionWorkspaceException"/> on failure.</summary>
    Task<CompositionCaptureResult> CaptureAsync(ISandboxSession session, CompositionCaptureRequest request, CancellationToken ct);

    /// <summary>
    /// Capture without ever throwing (cancellation aside): the render this follows is finished and
    /// recorded, and a missing snapshot must never take it back. Logged and counted instead.
    /// </summary>
    Task<CompositionCaptureResult> TryCaptureAsync(ISandboxSession session, CompositionCaptureRequest request, CancellationToken ct);
}

/// <inheritdoc />
public sealed class CompositionWorkspaceCapture : ICompositionWorkspaceCapture
{
    private readonly ICompositionWorkspaceStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CompositionWorkspaceOptions _options;
    private readonly string _scratch;
    private readonly ILogger<CompositionWorkspaceCapture> _logger;

    public CompositionWorkspaceCapture(
        ICompositionWorkspaceStore store,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<CompositionWorkspaceCapture> logger)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _options = CompositionWorkspaceStore.ReadOptions(configuration);
        _scratch = CompositionWorkspaceStore.ResolveScratch(configuration, _options);
        _logger = logger;
    }

    public async Task<CompositionCaptureResult> TryCaptureAsync(
        ISandboxSession session, CompositionCaptureRequest request, CancellationToken ct)
    {
        try
        {
            return await CaptureAsync(session, request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SandboxOperationNotSupportedException ex)
        {
            Count("unsupported", request.Source);
            _logger.LogInformation(
                "Composition workspace of execution {ExecutionId} not captured: {Reason}", request.ExecutionId, ex.Message);
            return CompositionCaptureResult.Failed("unsupported", ex.Message);
        }
        catch (Exception ex)
        {
            Count("failed", request.Source);
            _logger.LogWarning(
                ex,
                "Composition workspace of execution {ExecutionId} could not be captured; the render is delivered but cannot be revised from this run",
                request.ExecutionId);
            return CompositionCaptureResult.Failed("failed", ex.Message);
        }
    }

    public async Task<CompositionCaptureResult> CaptureAsync(
        ISandboxSession session, CompositionCaptureRequest request, CancellationToken ct)
    {
        Stopwatch clock = Stopwatch.StartNew();
        (Guid? initiator, Guid? workflowId) = await ReadExecutionAsync(request.CreatedByExecutionId ?? request.ExecutionId, ct);

        Guid workspaceId;
        Guid? parentId;
        CompositionWorkspaceVersion? parent;
        if (request.Lineage is { } lineage)
        {
            workspaceId = lineage.WorkspaceId;
            parentId = lineage.ParentVersionId;
            parent = parentId is { } id ? await _store.FindVersionAsync(id, ct) : null;
        }
        else
        {
            CompositionWorkspace workspace = await _store.GetOrCreateOriginWorkspaceAsync(
                request.ProjectId, request.OrganizationId, request.ExecutionId, workflowId, ct);
            workspaceId = workspace.Id;
            parent = await _store.LatestVersionAsync(workspaceId, ct);
            parentId = parent?.Id;
        }

        await using SandboxWorkspaceArchive exported = await session
            .ExportWorkspaceAsync(_options.TransferTimeoutSeconds, ct).ConfigureAwait(false);
        await using CompositionArchiveFile archive = await CompositionArchiveFile
            .SpoolAsync(exported.Content, _scratch, _options.MaxArchiveBytes, ct).ConfigureAwait(false);

        // An origin re-render of an unchanged project (a still after the video, a re-render after a
        // no-op) is the same source: keep one version. A revision always records its own version —
        // it carries an instruction and a new render even when the agent changed nothing.
        if (request.Lineage is null && parent is not null
            && string.Equals(parent.Sha256, archive.Sha256, StringComparison.OrdinalIgnoreCase)
            && (parent.RenderOutputStorageKey is not null || request.RenderOutputStorageKey is null))
        {
            Count("deduplicated", request.Source);
            return new CompositionCaptureResult(true, parent, true, "deduplicated", null);
        }

        CompositionWorkspaceVersion version = await _store.AppendVersionAsync(
            new CompositionVersionDraft
            {
                WorkspaceId = workspaceId,
                ProjectId = request.ProjectId,
                ParentVersionId = parentId,
                Source = request.Source,
                CompositionId = request.CompositionId,
                Fps = request.Fps,
                Width = request.Width,
                Height = request.Height,
                DurationInFrames = request.DurationInFrames,
                SceneManifestJson = request.SceneManifestJson,
                RenderOutputStorageKey = request.RenderOutputStorageKey,
                CreatedByExecutionId = request.CreatedByExecutionId ?? request.ExecutionId,
                CreatedByUserId = request.Lineage?.RequestedByUserId ?? initiator,
                RevisionId = request.Lineage?.RevisionId,
                Instruction = request.Lineage?.Instruction,
                FileCount = exported.Entries ?? 0
            },
            archive,
            ct).ConfigureAwait(false);

        if (request.Lineage is { } advancing)
        {
            advancing.ParentVersionId = version.Id;
            advancing.LastCapturedVersionId = version.Id;
        }

        Count("captured", request.Source);
        _logger.LogInformation(
            "Captured composition workspace {WorkspaceId} v{Version} from execution {ExecutionId} in {ElapsedMs}ms ({SizeBytes} bytes, {Entries} entries, {Skipped} skipped)",
            workspaceId, version.VersionNumber, request.ExecutionId, clock.ElapsedMilliseconds,
            archive.Length, exported.Entries, exported.Skipped);
        return new CompositionCaptureResult(true, version, false, "captured", null);
    }

    private async Task<(Guid? Initiator, Guid? WorkflowId)> ReadExecutionAsync(Guid executionId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        var row = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId)
            .Select(e => new { e.InitiatedByUserId, e.WorkflowDefinitionId })
            .FirstOrDefaultAsync(ct);
        return (row?.InitiatedByUserId, row?.WorkflowDefinitionId);
    }

    private static void Count(string outcome, CompositionVersionSource source) =>
        ReelBoltDiagnostics.CompositionWorkspaceCaptures.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("source", source.ToString()));
}

/// <summary>What a restore did, including anything the reviser must be told (missing packages).</summary>
public sealed record CompositionRestoreResult(
    int Files,
    long Bytes,
    bool DependenciesMatchTemplate,
    IReadOnlyList<string> ExtraDependencies,
    IReadOnlyList<string> InstalledDependencies,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Layer 2, inbound: put a stored version into a FRESH sandbox. The sandbox is created first (its
/// container init copies the runtime image's template, <c>node_modules</c> included), then the archive
/// replaces the source tree on top. Packages are installed only when the archive declares dependencies
/// the template does not already provide — and since the sandbox network normally has no egress, a
/// refused install is a WARNING handed to the agent, never a failure: most revisions never touch them.
/// </summary>
public interface ICompositionWorkspaceRestorer
{
    /// <exception cref="CompositionWorkspaceException">On a missing/corrupt archive or a refused import.</exception>
    Task<CompositionRestoreResult> RestoreAsync(ISandboxSession session, CompositionWorkspaceVersion version, CancellationToken ct);
}

/// <inheritdoc />
public sealed class CompositionWorkspaceRestorer : ICompositionWorkspaceRestorer
{
    private readonly ICompositionWorkspaceStore _store;
    private readonly CompositionWorkspaceOptions _options;
    private readonly ILogger<CompositionWorkspaceRestorer> _logger;
    private readonly IRestoredWorkspaceVideos? _videos;

    /// <param name="videos">
    /// Prepares the restored <c>public/</c> videos (oversized originals replaced by the engine-made copy).
    /// Null (a bare test fixture) restores exactly as before.
    /// </param>
    public CompositionWorkspaceRestorer(
        ICompositionWorkspaceStore store,
        IConfiguration configuration,
        ILogger<CompositionWorkspaceRestorer> logger,
        IRestoredWorkspaceVideos? videos = null)
    {
        _store = store;
        _options = CompositionWorkspaceStore.ReadOptions(configuration);
        _logger = logger;
        _videos = videos;
    }

    public async Task<CompositionRestoreResult> RestoreAsync(
        ISandboxSession session, CompositionWorkspaceVersion version, CancellationToken ct)
    {
        try
        {
            CompositionRestoreResult result = await RestoreCoreAsync(session, version, ct).ConfigureAwait(false);
            ReelBoltDiagnostics.CompositionWorkspaceRestores.Add(1, new KeyValuePair<string, object?>("outcome", "restored"));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ReelBoltDiagnostics.CompositionWorkspaceRestores.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            throw;
        }
    }

    private async Task<CompositionRestoreResult> RestoreCoreAsync(
        ISandboxSession session, CompositionWorkspaceVersion version, CancellationToken ct)
    {
        await session.EnsureAsync(ct).ConfigureAwait(false);

        await using CompositionArchiveFile archive = await _store.DownloadArchiveAsync(version, ct).ConfigureAwait(false);

        SandboxWorkspaceImport imported;
        try
        {
            await using Stream body = archive.OpenRead();
            imported = await session
                .ImportWorkspaceAsync(body, archive.Length, _options.TransferTimeoutSeconds, ct)
                .ConfigureAwait(false);
        }
        catch (SandboxOperationNotSupportedException ex)
        {
            throw new CompositionWorkspaceException(CompositionWorkspaceException.TransferNotSupported, ex.Message, ex);
        }
        catch (SandboxRequestFailedException ex)
        {
            throw new CompositionWorkspaceException(
                CompositionWorkspaceException.RestoreFailed,
                $"The sandbox refused workspace version {version.VersionNumber}: {ex.Message}", ex);
        }

        IReadOnlyList<string> extra = imported.ExtraDependencies ?? [];
        List<string> installed = [];
        List<string> warnings = [];
        if (!imported.DependenciesMatchTemplate && extra.Count > 0)
        {
            try
            {
                await session.InstallPackagesAsync([.. extra.Select(ToInstallSpec)], ct).ConfigureAwait(false);
                installed.AddRange(extra);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                warnings.Add(
                    "These packages were installed when the film was first made but are not in this sandbox and could not be "
                    + $"installed now ({ex.Message}): {string.Join(", ", extra)}. If the build fails on one of them, change the "
                    + "code that imports it rather than trying to install it again.");
                _logger.LogInformation(
                    "Restore of workspace version {VersionId} could not install {Count} extra package(s): {Reason}",
                    version.Id, extra.Count, ex.Message);
            }
        }

        _logger.LogInformation(
            "Restored composition workspace version {VersionId} (v{Version}) into the sandbox of execution {ExecutionId}: {Files} files, {Bytes} bytes",
            version.Id, version.VersionNumber, session.ExecutionId, imported.Files, imported.Bytes);

        // Before anything can render: oversized videos the workspace was captured with (a capture older than
        // the staged-video copies) get the same copy a fresh staging makes. Fail-open; never fails the restore.
        if (_videos is not null)
        {
            try
            {
                await _videos.PrepareRestoredVideosAsync(session, version.ProjectId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Could not prepare the videos of restored workspace version {VersionId} for execution {ExecutionId}; they stay as restored",
                    version.Id, session.ExecutionId);
            }
        }
        return new CompositionRestoreResult(
            imported.Files, imported.Bytes, imported.DependenciesMatchTemplate, extra, installed, warnings);
    }

    /// <summary>
    /// <c>name@spec</c> (as package.json declares it) to what the sandbox's package-name check accepts:
    /// a range like <c>^1.2.3</c> becomes the pinned <c>1.2.3</c>, and a spec that is not a plain version
    /// (a URL, a tag expression) is dropped so the name alone is installed. Scoped names keep their
    /// leading <c>@</c>.
    /// </summary>
    internal static string ToInstallSpec(string dependency)
    {
        int at = dependency.LastIndexOf('@');
        if (at <= 0)
            return dependency;
        string name = dependency[..at];
        string spec = dependency[(at + 1)..].Trim().TrimStart('^', '~', '=', '>', '<', 'v').Trim();
        return spec.Length > 0 && spec.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? $"{name}@{spec}"
            : name;
    }
}
