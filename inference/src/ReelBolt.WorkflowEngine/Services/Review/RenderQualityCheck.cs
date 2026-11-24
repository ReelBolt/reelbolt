using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Quality;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>One named check's result: it ran (<c>checked</c>) or it could not (<c>unavailable</c>, with a fixed reason).</summary>
public sealed record RenderQualityCheckStatus(string Name, string Status, string? Reason = null)
{
    public bool IsChecked => string.Equals(Status, RenderQualityOutcome.StatusChecked, StringComparison.Ordinal);
}

/// <summary>
/// One iteration's render quality checks on the finished film. Every check fails soft: one that could not run is
/// <c>unavailable</c> and neither fails the step nor caps a score it did not measure.
/// </summary>
public sealed record RenderQualityOutcome(
    IReadOnlyList<RenderQualityCheckStatus> Checks,
    IReadOnlyList<QualityFinding> Findings,
    int FailScoreCap)
{
    public const string StatusChecked = "checked";
    public const string StatusUnavailable = "unavailable";

    public const string CheckFrozenFrames = "frozen_frames";
    public const string CheckLoudness = "loudness";
    public const string CheckTextContrast = "text_contrast";

    /// <summary>True when at least one check measured something.</summary>
    public bool IsChecked => Checks.Any(c => c.IsChecked);

    /// <summary>A definite (high-severity) finding is present: the score is capped at <see cref="FailScoreCap"/>.</summary>
    public bool Capped => Findings.Any(f => string.Equals(f.Severity, QualitySeverity.High, StringComparison.Ordinal));

    public static RenderQualityOutcome AllUnavailable(string reason) => new(
        [
            new(CheckFrozenFrames, StatusUnavailable, reason),
            new(CheckLoudness, StatusUnavailable, reason),
            new(CheckTextContrast, StatusUnavailable, reason),
        ],
        [],
        6);
}

/// <summary>Runs the render quality checks for a review iteration. Never throws but for cancellation.</summary>
public interface IReviewRenderQuality
{
    Task<RenderQualityOutcome> RunAsync(StepExecutionContext context, VisualCheckConfig config, CancellationToken ct);
}

/// <summary>
/// The render quality checks (docs/video-editing.md "Render quality checks"): frozen frames, loudness against the
/// compile's target, and the contrast of burned-in plain text, all measured by the engine's own ffmpeg through the
/// pinned-local media workspace. Free, nothing leaves the host, no model involved. Built on the reusable helpers in
/// <c>Services/Video/Quality</c>.
/// </summary>
public sealed class ReviewRenderQuality : IReviewRenderQuality
{
    /// <summary>Contrast ratio below which burned-in text is reported (advisory).</summary>
    public const double ContrastAdvisoryRatio = 3.0;

    /// <summary>Contrast ratio below which burned-in text shown for at least <see cref="ContrastHighMinSeconds"/> is a high finding.</summary>
    public const double ContrastHighRatio = 2.0;

    public const double ContrastHighMinSeconds = 1.0;

    internal const int GridWidth = 64;
    internal const int GridHeight = 36;
    internal const double GridFps = 4;
    internal const int MaxGridFrames = 1200;
    internal const int MaxOverlaysMeasured = 16;

    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(120);

    private readonly IMediaWorkspaceFactory _workspaces;
    private readonly IMediaProbe _probe;
    private readonly IFrameGridSampler _sampler;
    private readonly IReviewFilmFacts? _facts;
    private readonly ILogger<ReviewRenderQuality> _logger;

    public ReviewRenderQuality(
        IMediaWorkspaceFactory workspaces, IMediaProbe probe, IFrameGridSampler sampler, IReviewFilmFacts? facts, ILogger<ReviewRenderQuality> logger)
    {
        _workspaces = workspaces;
        _probe = probe;
        _sampler = sampler;
        _facts = facts;
        _logger = logger;
    }

    public static IReviewRenderQuality? TryCreate(IServiceProvider services)
    {
        if (services.GetService<IReviewRenderQuality>() is { } registered)
            return registered;
        IMediaWorkspaceFactory? workspaces = services.GetService<IMediaWorkspaceFactory>();
        IMediaProbe? probe = services.GetService<IMediaProbe>();
        IFrameGridSampler? sampler = services.GetService<IFrameGridSampler>();
        if (workspaces is null || probe is null || sampler is null)
            return null;
        ILoggerFactory loggers = services.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        return new ReviewRenderQuality(
            workspaces, probe, sampler, FfmpegReviewFilmFacts.TryCreate(services), loggers.CreateLogger<ReviewRenderQuality>());
    }

    public async Task<RenderQualityOutcome> RunAsync(StepExecutionContext context, VisualCheckConfig config, CancellationToken ct)
    {
        int cap = config.ClampedFailScoreCap;
        VisualCheckSource? film = ReviewFilmResolver.Resolve(context);
        if (film is null)
            return RenderQualityOutcome.AllUnavailable("no playable film to measure") with { FailScoreCap = cap };

        List<RenderQualityCheckStatus> checks = [];
        List<QualityFinding> findings = [];
        JsonObject? compileOutput = CompileOutput(context, film);

        MediaProbeResult? probe = null;
        FrameGridResult? grid = null;
        try
        {
            (probe, grid) = await ProbeAndSampleAsync(context, film, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ReviewLoop step {StepOrder}: the render quality check could not read the film", context.Step.StepOrder);
        }

        double duration = probe is { DurationSec: > 0 } p && double.IsFinite(p.DurationSec)
            ? p.DurationSec
            : grid is { FrameCount: > 0, EffectiveFps: > 0 } ? grid.FrameCount / grid.EffectiveFps : 0;

        // a. Frozen frames.
        if (grid is null || duration <= 0)
        {
            checks.Add(new(RenderQualityOutcome.CheckFrozenFrames, RenderQualityOutcome.StatusUnavailable, "the film's frames could not be sampled"));
        }
        else
        {
            findings.AddRange(Safe(() => FrozenFrameDetector.Assess(FrozenFrameDetector.Find(grid), duration), "frozen frames"));
            checks.Add(new(RenderQualityOutcome.CheckFrozenFrames, RenderQualityOutcome.StatusChecked));
        }

        // b. Loudness.
        checks.Add(await LoudnessAsync(context, film, probe, duration, compileOutput, findings, ct).ConfigureAwait(false));

        // c. Burned-in text contrast.
        checks.Add(TextContrast(compileOutput, grid, findings));

        return new RenderQualityOutcome(checks, findings, cap);
    }

    private async Task<(MediaProbeResult? Probe, FrameGridResult? Grid)> ProbeAndSampleAsync(
        StepExecutionContext context, VisualCheckSource film, CancellationToken ct)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(SampleTimeout);
        MediaWorkspaceOpenResult opened = await _workspaces.OpenAsync(
            new MediaWorkspaceRequest(
                ExecutionId: context.Execution.Id,
                OrganizationId: context.Execution.OrganizationId,
                ProjectId: context.Execution.ProjectId,
                StepId: context.Step.Id,
                StepOrder: context.Step.StepOrder,
                JobType: RunnerJobType.VideoCompile,
                Purpose: $"review.render-quality:{context.Step.StepOrder.ToString(CultureInfo.InvariantCulture)}",
                Iteration: context.IterationCount,
                RunnerEligible: false),
            bounded.Token).ConfigureAwait(false);
        if (opened.Refused || opened.Lease is null)
            return (null, null);

        await using MediaWorkspaceLease lease = opened.Lease;
        string path = "render-quality/film" + Path.GetExtension(film.StorageKey).ToLowerInvariant();
        await lease.Workspace.StageAsync(film.StorageKey, path, bounded.Token).ConfigureAwait(false);
        MediaProbeResult probe = await _probe.ProbeAsync(lease.Workspace, path, bounded.Token).ConfigureAwait(false);
        if (!double.IsFinite(probe.DurationSec) || probe.DurationSec <= 0)
            return (probe, null);

        try
        {
            FrameGridResult grid = await _sampler.SampleAsync(
                lease.Workspace, path, GridFps, GridWidth, GridHeight, probe.DurationSec, MaxGridFrames, bounded.Token).ConfigureAwait(false);
            return (probe, grid.FrameCount > 0 ? grid : null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ReviewLoop step {StepOrder}: the render quality check could not sample frames", context.Step.StepOrder);
            return (probe, null);
        }
    }

    private async Task<RenderQualityCheckStatus> LoudnessAsync(
        StepExecutionContext context, VisualCheckSource film, MediaProbeResult? probe, double duration,
        JsonObject? compileOutput, List<QualityFinding> findings, CancellationToken ct)
    {
        const string name = RenderQualityOutcome.CheckLoudness;
        if (_facts is null)
            return new(name, RenderQualityOutcome.StatusUnavailable, "the sound cannot be measured in this host");
        if (probe is not null && string.IsNullOrWhiteSpace(probe.AudioCodec))
            return new(name, RenderQualityOutcome.StatusUnavailable, "the film has no audio track");

        try
        {
            ReviewAudioMeasurement? measured = await _facts.MeasureAudioAsync(context, film, ct).ConfigureAwait(false);
            if (measured is null)
                return new(name, RenderQualityOutcome.StatusUnavailable, "the sound could not be measured");

            (LoudnessTarget target, bool normalised) = LoudnessOf(compileOutput);
            double length = duration > 0 ? duration : measured.Momentary.Count > 0 ? measured.Momentary[^1].AtSec : 0;
            LoudnessFacts facts = new(
                measured.IntegratedLufs, measured.TruePeakDbtp, measured.RangeLu,
                ReviewAudioFacts.Silences(measured.Momentary), length, HasAudioTrack: true, Normalised: normalised);
            findings.AddRange(Safe(() => LoudnessAssessor.Assess(facts, target), "loudness"));
            return new(name, RenderQualityOutcome.StatusChecked);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ReviewLoop step {StepOrder}: the loudness check failed", context.Step.StepOrder);
            return new(name, RenderQualityOutcome.StatusUnavailable, "the sound could not be measured");
        }
    }

    /// <summary>The compile's loudness target (Social/Web/Broadcast) and whether it was applied; else the loose default.</summary>
    internal static (LoudnessTarget Target, bool Normalised) LoudnessOf(JsonObject? compileOutput)
    {
        if (compileOutput?["loudness"] is not JsonObject node)
            return (LoudnessTarget.Loose, false);
        string? name = node["target"] is JsonValue t && t.TryGetValue(out string? s) ? s : null;
        bool applied = node["applied"] is JsonValue a && a.TryGetValue(out bool b) && b;
        LoudnessTarget target = Enum.TryParse(name, ignoreCase: true, out ProgramLoudness program) ? program switch
        {
            ProgramLoudness.Social => LoudnessTarget.Social,
            ProgramLoudness.Web => LoudnessTarget.Web,
            ProgramLoudness.Broadcast => LoudnessTarget.Broadcast,
            _ => LoudnessTarget.Loose,
        } : LoudnessTarget.Loose;
        return (target, applied && !ReferenceEquals(target, LoudnessTarget.Loose));
    }

    /// <summary>Burned-in plain-text overlays from the compile's <c>graphics.appliedOverlays[]</c>, measured on the film.</summary>
    internal static RenderQualityCheckStatus TextContrast(JsonObject? compileOutput, FrameGridResult? grid, List<QualityFinding> findings)
    {
        const string name = RenderQualityOutcome.CheckTextContrast;
        if (compileOutput?["graphics"]?["appliedOverlays"] is not JsonArray overlays || overlays.Count == 0)
            return new(name, RenderQualityOutcome.StatusChecked, "no burned-in text overlays");
        if (grid is null)
            return new(name, RenderQualityOutcome.StatusUnavailable, "the film's frames could not be sampled");

        int measured = 0;
        int skipped = 0;
        foreach (JsonNode? item in overlays.Take(MaxOverlaysMeasured))
        {
            if (item is not JsonObject overlay)
                continue;
            if (!IsPlainText(overlay))
            {
                skipped++;
                continue;
            }

            QualityFinding? finding = SafeMeasure(() => MeasureOverlay(overlay, grid));
            measured++;
            if (finding is not null)
                findings.Add(finding);
        }

        if (measured == 0)
        {
            return new(name, RenderQualityOutcome.StatusUnavailable,
                skipped > 0 ? "graphic overlays (rendered assets) are not measured" : "no burned-in text overlays could be read");
        }

        return new(name, RenderQualityOutcome.StatusChecked,
            skipped > 0 ? "graphic overlays (rendered assets) are not measured" : null);
    }

    private static bool IsPlainText(JsonObject overlay) =>
        overlay["x"] is not null && overlay["w"] is not null && overlay["textColor"] is not null && overlay["designedWidth"] is null;

    private static QualityFinding? MeasureOverlay(JsonObject overlay, FrameGridResult grid)
    {
        if (Num(overlay["startSec"]) is not { } start || Num(overlay["endSec"]) is not { } end || end <= start
            || Num(overlay["x"]) is not { } x || Num(overlay["y"]) is not { } y
            || Num(overlay["w"]) is not { } w || Num(overlay["h"]) is not { } h
            || Num(overlay["frameWidth"]) is not { } fw || Num(overlay["frameHeight"]) is not { } fh
            || fw <= 0 || fh <= 0 || w <= 0 || h <= 0)
        {
            return null;
        }

        string? colorText = overlay["textColor"] is JsonValue c && c.TryGetValue(out string? cs) ? cs : null;
        if (!ContrastMath.TryParseColor(colorText, out Rgba text))
            return null;

        double mid = (start + end) / 2;
        int frame = Math.Clamp((int)Math.Round(mid * grid.EffectiveFps), 0, grid.FrameCount - 1);
        double[]? median = FrameGridStats.RegionLumaPercentiles(grid, frame, x / fw, y / fh, w / fw, h / fh, 50);
        if (median is not { Length: 1 } || !double.IsFinite(median[0]))
            return null;

        double ratio = ContrastMath.Ratio(ContrastMath.RelativeLuminance(text.R, text.G, text.B), median[0]);
        if (!double.IsFinite(ratio) || ratio >= ContrastAdvisoryRatio)
            return null;

        bool high = ratio < ContrastHighRatio && end - start >= ContrastHighMinSeconds;
        string id = overlay["placementId"] is JsonValue pv && pv.TryGetValue(out string? ps) ? ps ?? string.Empty : string.Empty;
        string detail = VisualCheckFindingsParser.SanitizeDetail(string.Create(CultureInfo.InvariantCulture,
            $"The burned-in text of graphic {id} is hard to read: contrast {ratio:0.0}:1 against what is behind it (aim for at least {ContrastAdvisoryRatio:0}:1)."));
        return new QualityFinding(
            QualityKinds.LowContrast,
            high ? QualitySeverity.High : ratio < ContrastHighRatio ? QualitySeverity.Medium : QualitySeverity.Low,
            Math.Round(start, 2), Math.Round(end, 2), Math.Round(ratio, 2), high ? ContrastHighRatio : ContrastAdvisoryRatio, detail);
    }

    private static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        if (v.TryGetValue(out double d))
            return double.IsFinite(d) ? d : null;
        if (v.TryGetValue(out int i))
            return i;
        return v.TryGetValue(out long l) ? l : null;
    }

    /// <summary>The output of the step that produced the film (last wins after a loop-back), parsed; null when it is not JSON.</summary>
    internal static JsonObject? CompileOutput(StepExecutionContext context, VisualCheckSource film)
    {
        for (int i = context.StepOutputHistory.Count - 1; i >= 0; i--)
        {
            StepOutputHistoryEntry entry = context.StepOutputHistory[i];
            if (entry.StepOrder != film.StepOrder || string.IsNullOrWhiteSpace(entry.Output))
                continue;
            try
            {
                return JsonNode.Parse(entry.Output) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private IReadOnlyList<QualityFinding> Safe(Func<IReadOnlyList<QualityFinding>> assess, string what)
    {
        try
        {
            return assess();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Render quality: the {Check} assessment failed", what);
            return [];
        }
    }

    private static QualityFinding? SafeMeasure(Func<QualityFinding?> measure)
    {
        try
        {
            return measure();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }
}
