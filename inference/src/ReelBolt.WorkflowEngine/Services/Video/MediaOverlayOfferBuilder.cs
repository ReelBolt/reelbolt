using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Builds the bounded, deterministic sets a <c>VideoAnalyze</c> step offers a downstream
/// <c>OverlayPlanner</c> (initiative "picture-overlay", design §5.3): the pictures/videos the run
/// may show (<c>m{n}</c>) and the moments it may show them over (<c>o{n}</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Offered is the authority.</b> A media offer snapshots the <see cref="VideoAnalysisMediaOffer.ProjectFileId"/>
/// and the facts known at analysis time, so a file added, moved or replaced after the analysis can
/// never be picked by a later compile of this run — the compile resolves the snapshot's file id
/// against the artifact, not a shifted live list.
/// </para>
/// <para>
/// <b>Nothing here probes a provider or runs ffprobe.</b> Images take their size from the file's
/// own header (<see cref="ImageHeader"/>); videos take the probe the ingest derivative run already
/// stored (<c>media_derivatives.source_probe_json</c>) and carry no dimensions when there is none.
/// </para>
/// <para>
/// <b>The brief's named set binds.</b> When the run brief carries files for its <c>overlay</c>
/// role, ONLY those are offered, in the brief's order — the planner may not reach outside what the
/// user named. With no named set, every usable project <c>image/*</c> and <c>video/*</c> file is
/// offered, oldest first, then by id. Either way quarantined files are already absent from
/// <see cref="IProjectFileWorkspace.ListFilesAsync"/>, the project's own read path.
/// </para>
/// </remarks>
public static class MediaOverlayOfferBuilder
{
    /// <summary>
    /// The stored name of the run-brief role that names the files to show.
    /// </summary>
    /// <remarks>
    /// T-007 appends <c>RunBriefFileRole.Overlay</c> to the enum; this ticket does not own that
    /// file, so the role is matched by the name it will persist (roles are persisted by name). The
    /// moment the member exists this comparison is exactly <c>brief.FilesFor(RunBriefFileRole.Overlay)</c>.
    /// </remarks>
    public const string OverlayRoleName = "Overlay";

    /// <summary>
    /// Largest image object downloaded just to read its header — the same 16 MiB bound
    /// <see cref="ImageHeader.ReadDisplaySizeAsync"/> scans a JPEG to its frame header. A bigger
    /// image is offered without dimensions rather than copied whole just to learn its size.
    /// </summary>
    internal const long MaxImageHeaderDownloadBytes = ImageHeader.MaxJpegScanBytes;

    /// <summary>The largest media-offer count any config or caller can produce.</summary>
    public const int MaxMediaCap = 100;

    /// <summary>The largest overlay-window count any config or caller can produce.</summary>
    public const int MaxWindowCap = 200;

    /// <summary>The <c>m{n}</c> kind word for an image file.</summary>
    public const string ImageKind = "Image";

    /// <summary>The <c>m{n}</c> kind word for a video file.</summary>
    public const string VideoKind = "Video";

    /// <summary>
    /// The project-file ids the brief named for its <c>overlay</c> role, in the brief's own group
    /// and file order, de-duplicated (first occurrence wins). Empty when it named none.
    /// </summary>
    public static IReadOnlyList<Guid> NamedOverlayFileIds(StoredRunBrief? brief)
    {
        if (brief?.SourceFiles is not { Count: > 0 } groups)
            return [];

        List<Guid> ids = [];
        HashSet<Guid> seen = [];
        foreach (RunBriefSourceFiles group in groups)
        {
            if (group is null)
                continue;

            // See OverlayRoleName's remarks: matched by persisted name until T-007 appends the enum member.
            if (!string.Equals(group.Role?.ToString(), OverlayRoleName, StringComparison.Ordinal))
                continue;

            foreach (Guid id in group.FileIds ?? [])
            {
                if (seen.Add(id))
                    ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Builds the media offers for a step that asked for them: the brief's named set when it has
    /// one, else every usable project picture/video. Never throws for a missing/unreadable file —
    /// such a candidate simply carries no dimensions, and a named file the project no longer has
    /// is not offered at all (the named set still binds: it never falls back to the whole project).
    /// </summary>
    public static async Task<IReadOnlyList<VideoAnalysisMediaOffer>> BuildAsync(
        Guid projectId,
        StoredRunBrief? brief,
        IProjectFileWorkspace workspace,
        IServiceScopeFactory? scopeFactory,
        int maxMedia,
        CancellationToken ct)
    {
        IReadOnlyList<Guid> named = NamedOverlayFileIds(brief);
        IReadOnlyList<ProjectWorkspaceFile> files = await workspace.ListFilesAsync(projectId, ct);
        int cap = Math.Clamp(maxMedia, 0, MaxMediaCap);

        // Cap BEFORE the probe lookup: only the files that can actually be offered are ever
        // probed, so a huge project cannot turn one analysis into hundreds of storage-key reads.
        IReadOnlyList<ProjectWorkspaceFile> candidates = SelectCandidates(files, named, cap);
        IReadOnlyDictionary<string, MediaSourceProbe> probes =
            await LoadStoredProbesAsync(scopeFactory, projectId, candidates, named, ct);
        return await BuildAsync(projectId, named, workspace, candidates, probes, cap, ct);
    }

    /// <summary>
    /// The capped, ordered candidate list: the brief's named files in brief order when it named
    /// any (a missing one is simply absent), else every usable project picture/video oldest first,
    /// then by id.
    /// </summary>
    private static IReadOnlyList<ProjectWorkspaceFile> SelectCandidates(
        IReadOnlyList<ProjectWorkspaceFile> files, IReadOnlyList<Guid> namedFileIds, int cap)
    {
        if (cap <= 0)
            return [];

        List<ProjectWorkspaceFile> usable = files
            .Where(f => f is not null && IsPictureOrVideo(f.MimeType))
            .ToList();

        if (namedFileIds is { Count: > 0 })
        {
            Dictionary<Guid, ProjectWorkspaceFile> byId = usable.ToDictionary(f => f.Id);
            List<ProjectWorkspaceFile> ordered = [];
            HashSet<Guid> seen = [];
            foreach (Guid id in namedFileIds)
            {
                if (ordered.Count >= cap)
                    break;
                if (byId.TryGetValue(id, out ProjectWorkspaceFile? file) && seen.Add(id))
                    ordered.Add(file);
            }

            return ordered;
        }

        return usable
            .OrderBy(f => f.UploadedAt)
            .ThenBy(f => f.Id)
            .Take(cap)
            .ToList();
    }

    /// <summary>
    /// The pure-ish core of <see cref="BuildAsync(Guid,StoredRunBrief?,IProjectFileWorkspace,IServiceScopeFactory?,int,CancellationToken)"/>:
    /// given the project's listing (already quarantine-filtered by the workspace) and the stored
    /// probes, produce the capped, ordered offers. Exposed so a test can drive the ordering,
    /// binding and cap rules without a database.
    /// </summary>
    /// <param name="namedFileIds">The brief's named set in brief order; null/empty means "every usable project file".</param>
    public static async Task<IReadOnlyList<VideoAnalysisMediaOffer>> BuildAsync(
        Guid projectId,
        IReadOnlyList<Guid>? namedFileIds,
        IProjectFileWorkspace workspace,
        IReadOnlyList<ProjectWorkspaceFile> files,
        IReadOnlyDictionary<string, MediaSourceProbe>? probes,
        int maxMedia,
        CancellationToken ct)
    {
        int cap = Math.Clamp(maxMedia, 0, MaxMediaCap);
        if (cap == 0)
            return [];

        List<ProjectWorkspaceFile> usable = files
            .Where(f => f is not null && IsPictureOrVideo(f.MimeType))
            .ToList();

        bool namedSetBinds = namedFileIds is { Count: > 0 };
        List<ProjectWorkspaceFile> candidates;
        if (namedSetBinds)
        {
            Dictionary<Guid, ProjectWorkspaceFile> byId = usable.ToDictionary(f => f.Id);
            List<ProjectWorkspaceFile> ordered = [];
            HashSet<Guid> seen = [];
            foreach (Guid id in namedFileIds!)
            {
                if (byId.TryGetValue(id, out ProjectWorkspaceFile? file) && seen.Add(id))
                    ordered.Add(file);
            }

            candidates = ordered;
        }
        else
        {
            candidates = usable
                .OrderBy(f => f.UploadedAt)
                .ThenBy(f => f.Id)
                .ToList();
        }

        List<VideoAnalysisMediaOffer> offers = new(Math.Min(cap, candidates.Count));
        foreach (ProjectWorkspaceFile file in candidates.Take(cap))
        {
            (int? width, int? height) = await ResolveSizeAsync(projectId, workspace, file, probes, ct);
            bool isImage = IsPicture(file.MimeType);
            double? duration = isImage ? null : ResolveDuration(file, probes);
            double? aspect = width is > 0 && height is > 0 ? Math.Round(width.Value / (double)height.Value, 4) : null;

            offers.Add(new VideoAnalysisMediaOffer(
                Id: $"m{offers.Count}",
                ProjectFileId: file.Id,
                Name: file.OriginalFileName,
                Kind: isImage ? ImageKind : VideoKind,
                Width: width,
                Height: height,
                Aspect: aspect,
                Orientation: OrientationOf(aspect),
                DurationSec: duration,
                SizeBytes: file.SizeBytes,
                FromUser: namedSetBinds));
        }

        return offers;
    }

    /// <summary>
    /// The overlay windows (<c>o{n}</c>) for every shot with visual data: the shared still-window /
    /// shot-middle helper, split into up to <paramref name="maxTimeSlices"/> non-overlapping
    /// slices, ranked by suitability, capped, then numbered in deterministic shot/start order.
    /// </summary>
    /// <remarks>
    /// Suitability is deterministic and code-only (no model): calm, low motion, a long still
    /// window, and no known on-screen text.
    /// <list type="bullet">
    /// <item><c>calm</c> = 1 − clamp(motionMean / 0.10, 0, 1) — a completely still shot scores 1.</item>
    /// <item><c>still</c> = the longest still window's share of the shot (1 when it covers the shot).</item>
    /// <item><c>noText</c> = 1 when the shot's caption reports no on-screen text, 0 when it does, 0.5 when vision never looked.</item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<VideoAnalysisOverlayWindow> BuildWindows(
        IReadOnlyList<VideoAnalysisShot> shots, int maxWindows, int maxTimeSlices = 3)
    {
        int cap = Math.Clamp(maxWindows, 0, MaxWindowCap);
        int slices = Math.Max(1, maxTimeSlices);
        if (cap == 0 || shots.Count == 0)
            return [];

        var candidates = new List<(int ShotIndex, string ShotId, int SourceIndex, double Start, double End, double Suitability)>();

        for (int shotIndex = 0; shotIndex < shots.Count; shotIndex++)
        {
            VideoAnalysisShot shot = shots[shotIndex];
            VideoAnalysisShotVisual? visual = shot.Visual;
            if (visual is null)
                continue;

            (_, double startSec, double endSec) = OverlayWindowHelper.ChooseTimeWindow(shot, visual);
            if (endSec <= startSec)
                continue;

            double suitability = SuitabilityOf(shot, visual);
            foreach ((double sliceStart, double sliceEnd) in
                     OverlayWindowHelper.SplitIntoTimeSlices(startSec, endSec, slices))
            {
                if (sliceEnd <= sliceStart)
                    continue;
                candidates.Add((shotIndex, shot.Id, shot.SourceIndex, sliceStart, sliceEnd, suitability));
            }
        }

        // Cap: the best moments survive, whatever their position in the film.
        List<(int ShotIndex, string ShotId, int SourceIndex, double Start, double End, double Suitability)> surviving =
            candidates.Count > cap
                ? candidates
                    .OrderByDescending(c => c.Suitability)
                    .ThenBy(c => c.ShotIndex)
                    .ThenBy(c => c.Start)
                    .Take(cap)
                    .ToList()
                : candidates;

        // Then re-sort into deterministic shot/start order before assigning the contiguous ids, so
        // o0..oN read in the order a person watches the film (the placement builder's discipline).
        surviving = surviving
            .OrderBy(c => c.ShotIndex)
            .ThenBy(c => c.Start)
            .ToList();

        List<VideoAnalysisOverlayWindow> windows = new(surviving.Count);
        for (int i = 0; i < surviving.Count; i++)
        {
            var c = surviving[i];
            windows.Add(new VideoAnalysisOverlayWindow(
                Id: $"o{i}",
                ShotId: c.ShotId,
                SourceIndex: c.SourceIndex,
                StartSec: c.Start,
                EndSec: c.End,
                Suitability: Math.Round(c.Suitability, 4)));
        }

        return windows;
    }

    /// <summary>
    /// Reads the ingest probe the engine's derivative run stored on
    /// <c>media_derivatives.source_probe_json</c> for the listed storage keys, newest readable row
    /// per key. Best effort: null scope factory or a query failure yields no probes, which simply
    /// means videos carry no dimensions (never a failed step).
    /// </summary>
    public static async Task<Dictionary<string, MediaSourceProbe>> LoadStoredProbesAsync(
        IServiceScopeFactory? scopeFactory,
        Guid projectId,
        IReadOnlyList<ProjectWorkspaceFile> files,
        IReadOnlyList<Guid> namedFileIds,
        CancellationToken ct)
    {
        Dictionary<string, MediaSourceProbe> byKey = new(StringComparer.Ordinal);
        if (scopeFactory is null)
            return byKey;

        HashSet<Guid>? named = namedFileIds is { Count: > 0 } ? [.. namedFileIds] : null;
        List<string> keys = files
            .Where(f => IsVideo(f.MimeType) && (named is null || named.Contains(f.Id)))
            .Select(f => f.StorageKey)
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
            return byKey;

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var rows = await db.MediaDerivatives
                .AsNoTracking()
                .Where(d => d.ProjectId == projectId &&
                            d.SourceProbeJson != null &&
                            keys.Contains(d.SourceStorageKey))
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new { d.SourceStorageKey, d.SourceProbeJson })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                if (byKey.ContainsKey(row.SourceStorageKey))
                    continue;
                if (MediaSourceProbe.TryParse(row.SourceProbeJson) is { } probe)
                    byKey[row.SourceStorageKey] = probe;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A probe is a fact we may use, never a prerequisite: without it a video is offered
            // with no dimensions and the compile probes it when an overlay actually names it.
        }

        return byKey;
    }

    /// <summary>True for a MIME type this feature may offer as a picture.</summary>
    public static bool IsPicture(string? mimeType) =>
        mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>True for a MIME type this feature may offer as a video.</summary>
    public static bool IsVideo(string? mimeType) =>
        mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPictureOrVideo(string? mimeType) => IsPicture(mimeType) || IsVideo(mimeType);

    private static string? OrientationOf(double? aspect) => aspect switch
    {
        null => null,
        _ when Math.Abs(aspect.Value - 1) < 0.001 => "Square",
        > 1 => "Landscape",
        _ => "Portrait"
    };

    private static double? ResolveDuration(
        ProjectWorkspaceFile file, IReadOnlyDictionary<string, MediaSourceProbe>? probes) =>
        probes is not null && probes.TryGetValue(file.StorageKey, out MediaSourceProbe? probe) && probe.DurationSec > 0
            ? probe.DurationSec
            : null;

    private static async Task<(int? Width, int? Height)> ResolveSizeAsync(
        Guid projectId,
        IProjectFileWorkspace workspace,
        ProjectWorkspaceFile file,
        IReadOnlyDictionary<string, MediaSourceProbe>? probes,
        CancellationToken ct)
    {
        if (IsVideo(file.MimeType))
        {
            if (probes is not null && probes.TryGetValue(file.StorageKey, out MediaSourceProbe? probe)
                && probe.Width > 0 && probe.Height > 0)
            {
                return (probe.Width, probe.Height);
            }

            return (null, null);
        }

        return await ReadImageSizeAsync(projectId, workspace, file, ct);
    }

    /// <summary>
    /// The image's shown-at size, read from its header only: the object is copied to a scratch
    /// file and only the leading bytes are parsed. A missing/unreadable/oversized object is "size
    /// not known", never a failure of the listing.
    /// </summary>
    private static async Task<(int? Width, int? Height)> ReadImageSizeAsync(
        Guid projectId,
        IProjectFileWorkspace workspace,
        ProjectWorkspaceFile file,
        CancellationToken ct)
    {
        string localPath = Path.Combine(Path.GetTempPath(), $"reelbolt-media-offer-{Guid.NewGuid():N}{Path.GetExtension(file.OriginalFileName)}");
        try
        {
            await workspace.DownloadStorageKeyToFileAsync(projectId, file.StorageKey, localPath, MaxImageHeaderDownloadBytes, ct);
            await using FileStream stream = File.OpenRead(localPath);
            if (await ImageHeader.ReadDisplaySizeAsync(stream, ImageHeader.MaxJpegScanBytes, ct) is { } size
                && size.Width > 0 && size.Height > 0)
            {
                return (size.Width, size.Height);
            }

            return (null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null);
        }
        finally
        {
            try
            {
                if (File.Exists(localPath))
                    File.Delete(localPath);
            }
            catch (IOException)
            {
                // A scratch file we could not delete is not a reason to refuse the offer.
            }
        }
    }

    private static double SuitabilityOf(VideoAnalysisShot shot, VideoAnalysisShotVisual visual)
    {
        const double calmMotionCeiling = 0.10;

        double calm = 1 - Math.Clamp(visual.MotionMean / calmMotionCeiling, 0, 1);

        double shotSeconds = Math.Max(0, shot.EndSec - shot.StartSec);
        double still = 0;
        if (shotSeconds > 0 && visual.StillWindows.Count > 0)
        {
            double longest = visual.StillWindows.Max(w => Math.Max(0, w.EndSec - w.StartSec));
            still = Math.Clamp(longest / shotSeconds, 0, 1);
        }

        double noText = shot.Caption is null
            ? 0.5
            : shot.Caption.OnScreenText.Count == 0 ? 1 : 0;

        return (0.45 * calm) + (0.35 * still) + (0.20 * noText);
    }
}
