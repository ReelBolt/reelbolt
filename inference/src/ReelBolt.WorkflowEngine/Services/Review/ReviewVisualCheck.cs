using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>Runs one iteration's visual check. Never throws except for the caller's own cancellation.</summary>
public interface IReviewVisualCheck
{
    Task<VisualCheckOutcome> RunAsync(StepExecutionContext context, VisualCheckConfig config, CancellationToken ct);

    /// <summary>
    /// One targeted look (<c>visualCheck.targetedLooks</c>): the frames the reviewer asked for, grabbed by
    /// <paramref name="facts"/> and judged by the same vision checklist with the reviewer's request stated. Frames are
    /// numbered 1..n in this outcome. The default answers "unavailable", so a host without looks wired never runs one.
    /// </summary>
    Task<VisualCheckOutcome> LookAsync(
        StepExecutionContext context, VisualCheckConfig config, IReadOnlyList<ReviewLookFrame> frames, string? focus,
        IReviewFilmFacts? facts, CancellationToken ct) =>
        Task.FromResult(VisualCheckOutcome.Unavailable("targeted looks are not available in this host"));
}

/// <summary>
/// One iteration's visual check, in the order that spends nothing it does not have to: find the output the
/// loop reviews (<see cref="VisualCheckSourceResolver"/>), resolve the Vision provider (the configured id, else
/// the default Vision provider, else none — "unavailable", no frames extracted), sample the frames
/// (<see cref="IVisualCheckFrameGrabber"/>), then make the one vision call (<see cref="IVisualFrameReviewer"/>).
/// Every failure is an <see cref="VisualCheckOutcome.Unavailable"/> outcome with a fixed reason, so the visual
/// check can never fail the review step and never caps a score it did not measure.
/// </summary>
public sealed class ReviewVisualCheck : IReviewVisualCheck
{
    private readonly IInferenceProviderResolver _providers;
    private readonly IVisualCheckFrameGrabber _frames;
    private readonly IVisualFrameReviewer _reviewer;
    private readonly ILogger<ReviewVisualCheck> _logger;

    public ReviewVisualCheck(
        IInferenceProviderResolver providers, IVisualCheckFrameGrabber frames, IVisualFrameReviewer reviewer, ILogger<ReviewVisualCheck> logger)
    {
        _providers = providers;
        _frames = frames;
        _reviewer = reviewer;
        _logger = logger;
    }

    /// <summary>
    /// The check composed from services every engine host already registers (the Vision resolver, the
    /// metered chat factory, the media workspace factory, the probe and the keyframe extractor), so no new
    /// registration is needed; an explicitly registered <see cref="IReviewVisualCheck"/> wins. Null when any
    /// piece is missing, which the executor records as "visual check unavailable".
    /// </summary>
    public static IReviewVisualCheck? TryCreate(IServiceProvider services)
    {
        if (services.GetService<IReviewVisualCheck>() is { } registered)
            return registered;

        IInferenceProviderResolver? providers = services.GetService<IInferenceProviderResolver>();
        IChatClientFactory? chat = services.GetService<IChatClientFactory>();
        IMediaWorkspaceFactory? workspaces = services.GetService<IMediaWorkspaceFactory>();
        IMediaProbe? probe = services.GetService<IMediaProbe>();
        IKeyframeExtractor? keyframes = services.GetService<IKeyframeExtractor>();
        if (providers is null || chat is null || workspaces is null || probe is null || keyframes is null)
            return null;

        ILoggerFactory loggers = services.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        return new ReviewVisualCheck(
            providers,
            new FfmpegVisualCheckFrameGrabber(workspaces, probe, keyframes, loggers.CreateLogger<FfmpegVisualCheckFrameGrabber>()),
            new VisualFrameReviewer(chat, loggers.CreateLogger<VisualFrameReviewer>()),
            loggers.CreateLogger<ReviewVisualCheck>());
    }

    public async Task<VisualCheckOutcome> RunAsync(StepExecutionContext context, VisualCheckConfig config, CancellationToken ct)
    {
        VisualCheckSource? source = VisualCheckSourceResolver.Resolve(context);
        if (source is null)
            return VisualCheckOutcome.Unavailable("no playable render or compile output in the loop's window");

        try
        {
            ResolvedInferenceProvider? provider = await _providers.ResolveVisionAsync(config.VisionProviderId, ct).ConfigureAwait(false);
            if (provider is null)
                return VisualCheckOutcome.Unavailable("no vision provider is configured");

            VisualCheckFrameGrab grab = await _frames.GrabAsync(context, source, config.ClampedFrames, ct).ConfigureAwait(false);
            if (grab.Frames.Count == 0)
                return VisualCheckOutcome.Unavailable(grab.Failure ?? "no frame could be extracted from the output under review");

            VisualFrameReview review = await _reviewer.ReviewAsync(provider, grab.Frames, config, ct).ConfigureAwait(false);
            if (review.Findings is null)
                return VisualCheckOutcome.Unavailable(review.Unavailable ?? VisualFrameReviewer.Unreadable);

            return VisualCheckOutcome.Checked(
                source.StepOrder,
                [.. grab.Frames.Select(f => new VisualCheckFrameTime(f.Frame, f.AtSec))],
                review.Findings,
                config.ClampedFailScoreCap);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Logged with its detail; the outcome carries a fixed reason (exception text can carry paths and hosts).
            _logger.LogWarning(ex, "ReviewLoop step {StepOrder}: the visual check failed; continuing without it", context.Step.StepOrder);
            return VisualCheckOutcome.Unavailable("the visual check failed");
        }
    }

    public async Task<VisualCheckOutcome> LookAsync(
        StepExecutionContext context, VisualCheckConfig config, IReadOnlyList<ReviewLookFrame> frames, string? focus,
        IReviewFilmFacts? facts, CancellationToken ct)
    {
        if (facts is null)
            return VisualCheckOutcome.Unavailable("targeted looks are not available in this host");
        if (frames.Count == 0)
            return VisualCheckOutcome.Unavailable("no frame was asked for");
        VisualCheckSource? source = VisualCheckSourceResolver.Resolve(context);
        if (source is null)
            return VisualCheckOutcome.Unavailable("no playable render or compile output in the loop's window");

        try
        {
            ResolvedInferenceProvider? provider = await _providers.ResolveVisionAsync(config.VisionProviderId, ct).ConfigureAwait(false);
            if (provider is null)
                return VisualCheckOutcome.Unavailable("no vision provider is configured");

            VisualCheckFrameGrab grab = await facts.GrabAtAsync(context, source, frames, ct).ConfigureAwait(false);
            if (grab.Frames.Count == 0)
                return VisualCheckOutcome.Unavailable(grab.Failure ?? "no frame could be extracted from the output under review");

            VisualFrameReview review = await _reviewer.ReviewAsync(provider, grab.Frames, config, focus, ct).ConfigureAwait(false);
            if (review.Findings is null)
                return VisualCheckOutcome.Unavailable(review.Unavailable ?? VisualFrameReviewer.Unreadable);

            return VisualCheckOutcome.Checked(
                source.StepOrder,
                [.. grab.Frames.Select(f => new VisualCheckFrameTime(f.Frame, f.AtSec))],
                review.Findings,
                config.ClampedFailScoreCap);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ReviewLoop step {StepOrder}: a targeted look failed; continuing without it", context.Step.StepOrder);
            return VisualCheckOutcome.Unavailable("the targeted look failed");
        }
    }
}
