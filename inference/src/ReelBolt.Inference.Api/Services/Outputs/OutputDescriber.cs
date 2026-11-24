using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>One contact sheet (a filmstrip sprite sheet): a link plus the grid geometry needed to time each tile.</summary>
public sealed record OutputContactSheet(OutputLink Link, int Cols, int Rows, double DensitySec, double FirstTimeSec, int ThumbCount);

/// <summary>One full-size still of a video (T-101): its time and a link to the JPEG.</summary>
public sealed record OutputStillLink(double AtSec, OutputLink Link);

/// <summary>Frames of an output: <c>ready</c>, <c>pending</c> (derivatives requested / in flight) or <c>unavailable</c> (not a video).</summary>
public sealed record OutputFrames(string Status, OutputLink? Thumbnail, IReadOnlyList<OutputContactSheet> ContactSheets)
{
    /// <summary>Full-size stills, when a stills set exists (the evenly spaced one preferred). Empty otherwise.</summary>
    public IReadOnlyList<OutputStillLink> Stills { get; init; } = [];
}

/// <summary>
/// An output resolved server-side for looking at it: a render or a project file of one project. The key
/// is for internal use only, never for a response.
/// </summary>
/// <param name="DurationSec">
/// How long the source plays, when its step's output or a derivative row says so (null = unknown: an image, or a
/// video nothing has measured). S-74: the stills times a look asks for are clamped to it, so two out-of-range asks
/// cannot mint two extractions over the same pixels.
/// </param>
public sealed record OutputSource(
    Guid Id, string Kind, string Name, string MimeType, long SizeBytes, string StorageKey, MediaSourceRef SourceRef,
    Guid? SourceProjectFileId, double? DurationSec = null);

/// <summary>One still of a stored stills set (server-side: the key never leaves the process).</summary>
public sealed record OutputStillObject(double AtSec, string StorageKey);

/// <summary>What <c>GetOutput</c> tells a caller about one output. No member is a storage key, bucket or object-store URL.</summary>
public sealed record OutputDescription(
    Guid Id,
    string Kind,
    string Name,
    string MimeType,
    long SizeBytes,
    double? DurationSec,
    int? Width,
    int? Height,
    Guid? ExecutionId,
    int? StepOrder,
    string? StepLabel,
    OutputLink Link,
    OutputFrames Frames);

/// <summary>A render of a project, resolved server-side (the key is for internal use only, never for a response).</summary>
public sealed record OutputRender(Guid StepResultId, string StorageKey, string FileName, string MimeType, long SizeBytes);

/// <summary>Resolves an <c>outputId</c> inside one project — a step result's render first, then a project file.</summary>
public interface IOutputDescriber
{
    /// <summary><c>null</c> when the id is neither a render nor a file of <paramref name="project"/> (or its object is gone).</summary>
    Task<OutputDescription?> DescribeAsync(Project project, Guid outputId, Guid userId, CancellationToken ct);

    /// <summary>
    /// A step result of <paramref name="projectId"/> whose object is one of that project's addressable outputs
    /// (<see cref="ProjectOutputKeys.IsAddressableOutput"/>: a render under <c>outputFiles/</c> or a generated clip
    /// under <c>agentFiles/generated/</c>) and still exists.
    /// </summary>
    Task<OutputRender?> FindRenderAsync(Guid projectId, Guid stepResultId, CancellationToken ct);

    /// <summary>A render (first) or project file of <paramref name="projectId"/> whose object is under that project; null otherwise.</summary>
    Task<OutputSource?> FindSourceAsync(Guid projectId, Guid outputId, CancellationToken ct);

    /// <summary>
    /// The stills of <paramref name="source"/> taken at <paramref name="timesMs"/> (normalized by
    /// <c>MediaDerivativeStillTimes.NormalizeToMs</c>; empty = the evenly spaced set), newest set first;
    /// null when no such set exists yet.
    /// </summary>
    Task<IReadOnlyList<OutputStillObject>?> FindStillsAsync(Guid projectId, OutputSource source, IReadOnlyList<int> timesMs, CancellationToken ct);

    /// <summary>Publishes one <see cref="MediaDerivativesRequested"/> for the stills set at <paramref name="timesMs"/>.</summary>
    Task RequestStillsAsync(Guid projectId, OutputSource source, IReadOnlyList<int> timesMs, CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
/// Frames reuse media derivatives: the newest Thumbnail row, and the sheets of one Filmstrip density
/// (the finest density with at most <see cref="MaxContactSheets"/> sheets, else the coarsest, capped).
/// A video with no Thumbnail/Filmstrip row at all gets exactly one <see cref="MediaDerivativesRequested"/>
/// per call — the same event <c>MediaController</c>'s request publishes — and reports <c>pending</c>.
/// Every link is minted for the calling user and is re-authorized when it is fetched.
/// </remarks>
public sealed class OutputDescriber(
    InferenceApiDbContext db,
    IFileStorageService storage,
    IOutputLinkService links,
    IPublishEndpoint publishEndpoint) : IOutputDescriber
{
    public const int MaxContactSheets = 12;
    public const string RequestedByAssistant = "assistant-get-output";
    public const string RequestedByLook = "assistant-look-at-output";

    /// <summary>
    /// What the frames view asks for when a video has no frame rows at all: the default derivative set,
    /// <b>plus <see cref="MediaDerivativeKind.Thumbnail"/></b>, the single frame shown beside the
    /// filmstrip's contact sheets.
    /// </summary>
    /// <remarks>
    /// The default set no longer advertises <c>thumbnail</c> (RD-15): only the <c>ThumbnailGenerate</c>
    /// step writes a Thumbnail row, so a request that names it on every upload and editor-open
    /// advertised a derivative the run never produces. This path is the exception — it is asking for
    /// the frames of one specific output, and it wants that thumbnail if the run's own thumbnail step
    /// made one. The contact sheets arrive from the filmstrip either way, so a run without a thumbnail
    /// still turns <c>pending</c> into <c>ready</c>.
    /// </remarks>
    private static readonly IReadOnlyList<MediaDerivativeKind> FrameKinds =
        [.. MediaDerivativeRequests.DefaultKinds, MediaDerivativeKind.Thumbnail];

    public async Task<OutputSource?> FindSourceAsync(Guid projectId, Guid outputId, CancellationToken ct)
    {
        OutputRender? render = await FindRenderAsync(projectId, outputId, ct);
        if (render is not null)
        {
            List<MediaDerivative> rows = await DerivativesOf(projectId, MediaSourceKind.StepResult, outputId, ct);
            string? outputJson = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == outputId)
                .Select(r => r.OutputJson)
                .FirstOrDefaultAsync(ct);

            return new OutputSource(
                outputId, "render", render.FileName, render.MimeType, render.SizeBytes, render.StorageKey,
                new MediaSourceRef(MediaSourceKind.StepResult, outputId), null,
                DurationOf(RenderMetadataReader.Read(outputJson).DurationSec, rows));
        }

        ProjectFile? file = await db.ProjectFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == outputId && f.ProjectId == projectId
                                      && f.CsamStatus != CsamScanStatus.Quarantined
                                      && f.ModerationStatus != FileModerationStatus.Quarantined, ct);
        if (file is null || string.IsNullOrEmpty(file.StorageKey) ||
            !file.StorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal)) return null;

        if (await OutputGate.IsFileHeldAsync(db, file, ct)) return null;

        List<MediaDerivative> fileRows = await DerivativesOf(projectId, MediaSourceKind.ProjectFile, file.Id, ct);

        return new OutputSource(
            file.Id, "file", file.OriginalFileName, file.MimeType, file.SizeBytes, file.StorageKey,
            new MediaSourceRef(MediaSourceKind.ProjectFile, file.Id), file.Id,
            DurationOf(null, fileRows));
    }

    /// <summary>The source's own length: what its step reported, else what a derivative (a proxy, a filmstrip) measured.</summary>
    private static double? DurationOf(double? reported, IReadOnlyList<MediaDerivative> rows) =>
        reported is > 0 ? reported : rows.Select(r => r.DurationSec).FirstOrDefault(v => v is > 0);

    public async Task<IReadOnlyList<OutputStillObject>?> FindStillsAsync(
        Guid projectId, OutputSource source, IReadOnlyList<int> timesMs, CancellationToken ct)
    {
        List<MediaDerivative> rows = await DerivativesOf(projectId, source.SourceRef.Kind, source.Id, ct);
        foreach (MediaDerivative row in rows.Where(r => r.Kind == MediaDerivativeKind.Stills).Reverse())
        {
            // A set of an older version of the source does not describe these bytes.
            if (!string.Equals(row.SourceStorageKey, source.StorageKey, StringComparison.Ordinal)) continue;
            if (StillsIndex.RequestedTimesMs(row) is not { } asked || !asked.SequenceEqual(timesMs)) continue;

            IReadOnlyList<StillsIndex.Still> stills = await StillsIndex.ReadAsync(storage, row, ct);
            if (stills.Count > 0) return [.. stills.Select(s => new OutputStillObject(s.AtSec, s.StorageKey))];
        }

        return null;
    }

    public async Task RequestStillsAsync(Guid projectId, OutputSource source, IReadOnlyList<int> timesMs, CancellationToken ct)
    {
        // No still is made from a file before its CSAM verdict (docs/csam.md); the caller asks again later.
        if (!await Csam.CsamScanGate.AllowsDerivativesAsync(db, projectId, source.SourceProjectFileId, ct)) return;

        string? etag = await storage.GetObjectETagAsync(projectId, source.StorageKey, ct);
        await publishEndpoint.Publish(new MediaDerivativesRequested(
            projectId,
            source.SourceRef,
            source.SourceProjectFileId,
            source.StorageKey,
            etag ?? string.Empty,
            source.MimeType,
            [MediaDerivativeKind.Stills],
            RequestedByLook,
            DateTime.UtcNow,
            timesMs.Count == 0 ? null : [.. timesMs.Select(t => t / 1000.0)]), ct);
    }

    public async Task<OutputRender?> FindRenderAsync(Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        string? key = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId)
            .Select(r => r.OutputStorageKey)
            .FirstOrDefaultAsync(ct);
        if (!ProjectOutputKeys.IsAddressableOutput(projectId, key)) return null;
        if (await OutputGate.IsResultHeldAsync(db, stepResultId, ct)) return null;

        StoredObjectMetadata? head = await storage.GetMetadataAsync(projectId, key, ct);
        if (head is null) return null;

        string fileName = Path.GetFileName(key);
        return new OutputRender(stepResultId, key, fileName, OutputMediaTypes.FromFileName(fileName) ?? "application/octet-stream", head.SizeBytes);
    }

    public async Task<OutputDescription?> DescribeAsync(Project project, Guid outputId, Guid userId, CancellationToken ct)
    {
        OutputRender? render = await FindRenderAsync(project.Id, outputId, ct);
        if (render is not null)
        {
            var step = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == outputId)
                .Select(r => new { r.WorkflowExecutionId, r.OutputJson, r.WorkflowStep.StepOrder, r.WorkflowStep.Label })
                .FirstAsync(ct);
            RenderMetadata meta = RenderMetadataReader.Read(step.OutputJson);
            List<MediaDerivative> rows = await DerivativesOf(project.Id, MediaSourceKind.StepResult, outputId, ct);
            (double? duration, int? width, int? height) = FillFromDerivatives(meta.DurationSec, meta.Width, meta.Height, rows);

            OutputFrames frames = await FramesAsync(
                project.Id, userId, new MediaSourceRef(MediaSourceKind.StepResult, outputId), null, render.StorageKey, render.MimeType, rows, ct);

            return new OutputDescription(
                outputId, "render", render.FileName, render.MimeType, render.SizeBytes, duration, width, height,
                step.WorkflowExecutionId, step.StepOrder, string.IsNullOrWhiteSpace(step.Label) ? null : step.Label,
                links.Create(new OutputLinkTarget(project.Id, OutputLinkKind.StepResult, outputId, null), userId, null),
                frames);
        }

        ProjectFile? file = await db.ProjectFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == outputId && f.ProjectId == project.Id
                                      && f.CsamStatus != CsamScanStatus.Quarantined
                                      && f.ModerationStatus != FileModerationStatus.Quarantined, ct);
        if (file is null || !file.StorageKey.StartsWith($"projects/{project.Id}/", StringComparison.Ordinal)) return null;

        if (await OutputGate.IsFileHeldAsync(db, file, ct)) return null;

        List<MediaDerivative> fileRows = await DerivativesOf(project.Id, MediaSourceKind.ProjectFile, file.Id, ct);
        (double? d, int? w, int? h) = FillFromDerivatives(null, null, null, fileRows);
        OutputFrames fileFrames = await FramesAsync(
            project.Id, userId, new MediaSourceRef(MediaSourceKind.ProjectFile, file.Id), file.Id, file.StorageKey, file.MimeType, fileRows, ct);

        return new OutputDescription(
            file.Id, "file", file.OriginalFileName, file.MimeType, file.SizeBytes, d, w, h, null, null, null,
            links.Create(new OutputLinkTarget(project.Id, OutputLinkKind.ProjectFile, file.Id, null), userId, null),
            fileFrames);
    }

    private Task<List<MediaDerivative>> DerivativesOf(Guid projectId, MediaSourceKind kind, Guid sourceId, CancellationToken ct) =>
        db.MediaDerivatives.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.DeletedAt == null && d.SourceKind == kind &&
                        (kind == MediaSourceKind.ProjectFile ? d.SourceProjectFileId == sourceId : d.SourceStepResultId == sourceId))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(ct);

    private static (double?, int?, int?) FillFromDerivatives(double? duration, int? width, int? height, IReadOnlyList<MediaDerivative> rows)
    {
        // A proxy keeps the source's length but not necessarily its frame size, so only the duration is
        // taken from derivatives; width/height come from the step's own report or stay unknown.
        duration ??= rows.Select(r => r.DurationSec).FirstOrDefault(v => v is > 0);
        return (duration, width, height);
    }

    private async Task<OutputFrames> FramesAsync(
        Guid projectId,
        Guid userId,
        MediaSourceRef sourceRef,
        Guid? sourceProjectFileId,
        string sourceKey,
        string mimeType,
        IReadOnlyList<MediaDerivative> rows,
        CancellationToken ct)
    {
        string projectPrefix = $"projects/{projectId}/";

        MediaDerivative? thumbRow = rows.FirstOrDefault(r =>
            r.Kind == MediaDerivativeKind.Thumbnail && r.StorageKey.StartsWith(projectPrefix, StringComparison.Ordinal));
        OutputLink? thumbnail = thumbRow is null
            ? null
            : links.Create(new OutputLinkTarget(projectId, OutputLinkKind.Derivative, thumbRow.Id, null), userId, null);

        List<OutputContactSheet> sheets = await ContactSheetsAsync(projectId, userId, rows, projectPrefix, ct);
        List<OutputStillLink> stills = await StillLinksAsync(projectId, userId, rows, ct);

        if (thumbnail is not null || sheets.Count > 0 || stills.Count > 0)
            return new OutputFrames("ready", thumbnail, sheets) { Stills = stills };
        if (!OutputMediaTypes.IsVideo(mimeType)) return new OutputFrames("unavailable", null, []);

        // A Filmstrip row whose sidecar cannot be read will not become readable by waiting or by asking again.
        if (rows.Any(r => r.Kind == MediaDerivativeKind.Filmstrip)) return new OutputFrames("unavailable", null, []);

        bool anyFrameRows = rows.Any(r => r.Kind is MediaDerivativeKind.Thumbnail or MediaDerivativeKind.Filmstrip);
        if (!anyFrameRows && await Csam.CsamScanGate.AllowsDerivativesAsync(db, projectId, sourceProjectFileId, ct))
        {
            string? etag = await storage.GetObjectETagAsync(projectId, sourceKey, ct);
            await publishEndpoint.Publish(new MediaDerivativesRequested(
                projectId,
                sourceRef,
                sourceProjectFileId,
                sourceKey,
                etag ?? string.Empty,
                mimeType,
                FrameKinds,
                RequestedByAssistant,
                DateTime.UtcNow), ct);
        }

        return new OutputFrames("pending", null, []);
    }

    /// <summary>The newest evenly spaced stills set (else the newest set at requested times), as links.</summary>
    private async Task<List<OutputStillLink>> StillLinksAsync(
        Guid projectId, Guid userId, IReadOnlyList<MediaDerivative> rows, CancellationToken ct)
    {
        IEnumerable<MediaDerivative> candidates = rows
            .Where(r => r.Kind == MediaDerivativeKind.Stills)
            .Reverse()
            .OrderBy(r => StillsIndex.RequestedTimesMs(r) is { Length: 0 } ? 0 : 1);

        foreach (MediaDerivative row in candidates)
        {
            IReadOnlyList<StillsIndex.Still> stills = await StillsIndex.ReadAsync(storage, row, ct);
            if (stills.Count == 0) continue;
            return [.. stills.Select(s => new OutputStillLink(
                s.AtSec, links.Create(new OutputLinkTarget(projectId, OutputLinkKind.Derivative, row.Id, s.Position), userId, null)))];
        }

        return [];
    }

    private async Task<List<OutputContactSheet>> ContactSheetsAsync(
        Guid projectId, Guid userId, IReadOnlyList<MediaDerivative> rows, string projectPrefix, CancellationToken ct)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<(MediaDerivative Row, FilmstripSheets.Sidecar Sidecar)> densities = [];
        foreach (MediaDerivative row in rows.Where(r => r.Kind == MediaDerivativeKind.Filmstrip))
        {
            if (string.IsNullOrWhiteSpace(row.IndexStorageKey) || !seen.Add(row.IndexStorageKey)) continue;
            if (!row.StorageKey.StartsWith(projectPrefix, StringComparison.Ordinal)) continue;
            FilmstripSheets.Sidecar? sidecar = await FilmstripSheets.TryReadAsync(storage, projectId, row.IndexStorageKey, ct);
            if (sidecar?.Sheets is { Count: > 0 }) densities.Add((row, sidecar));
        }

        if (densities.Count == 0) return [];

        (MediaDerivative chosenRow, FilmstripSheets.Sidecar chosen) =
            densities.Where(d => d.Sidecar.Sheets!.Count <= MaxContactSheets).OrderBy(d => d.Sidecar.DensitySec).FirstOrDefault() is { Row: not null } fine
                ? fine
                : densities.OrderByDescending(d => d.Sidecar.DensitySec).First();

        return chosen.Sheets!
            .Take(MaxContactSheets)
            .Select((sheet, index) => new OutputContactSheet(
                links.Create(new OutputLinkTarget(projectId, OutputLinkKind.Derivative, chosenRow.Id, index), userId, null),
                chosen.Cols,
                chosen.Rows,
                chosen.DensitySec,
                sheet.FirstTimeSec,
                sheet.ThumbCount))
            .ToList();
    }
}
