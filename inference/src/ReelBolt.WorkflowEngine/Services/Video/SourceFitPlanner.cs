using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// How every source clip is fitted into a fixed canvas once <see cref="VideoCompileStepConfig.SourceFits"/>
/// can override <see cref="VideoCompileStepConfig.OutputFit"/> per clip.
/// </summary>
/// <param name="CoverFit">Whether any clip is scaled to COVER the canvas (Crop or Subject).</param>
/// <param name="CropFocus">
/// Where each cover-fitted clip's crop sits (Subject), and — when <paramref name="CoverFit"/> is on —
/// a <see cref="Video.CropFocus.Pad"/> entry for every clip whose own fit is Pad, so every piece of
/// code that already reads <c>CropFocus.Pad</c> pads it.
/// </param>
public sealed record SourceFitPlan(
    bool CoverFit,
    IReadOnlyDictionary<int, CropFocus> CropFocus,
    IReadOnlyDictionary<int, VideoOutputFit> Overrides,
    VideoOutputFit Default,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    /// <summary>The fit that applies to <paramref name="sourceIndex"/>.</summary>
    public VideoOutputFit FitOf(int sourceIndex) => Overrides.GetValueOrDefault(sourceIndex, Default);
}

/// <summary>
/// <see cref="SourceFitPlan"/> from the config. With no <see cref="VideoCompileStepConfig.SourceFits"/>
/// the plan is exactly the pre-override one: cover iff the fit is Crop or Subject at a platform
/// format, crop focus only for Subject (see docs/video-editing.md "Keyframed reframing").
/// </summary>
public static class SourceFitPlanner
{
    private const string PadBasis = "sourceFit";

    public static SourceFitPlan Plan(
        VideoCompileStepConfig config,
        VideoAnalysisArtifact artifact,
        IReadOnlyList<(int SourceIndex, double Start, double End)> keptWindows,
        Func<int, VideoAnalysisMedia> sourceMedia,
        VideoAnalysisMedia canvas,
        bool fixedFormat,
        IReadOnlySet<int>? reframedSources = null)
    {
        bool platform = fixedFormat && config.OutputFormat != VideoOutputFormat.Source;
        IReadOnlyDictionary<int, VideoOutputFit> overrides = config.SourceFits ?? new Dictionary<int, VideoOutputFit>();

        if (overrides.Count > 0)
        {
            HashSet<int> known = (artifact.Sources ?? []).Select(s => s.SourceIndex).ToHashSet();
            if (known.Count == 0)
                known.Add(0);
            foreach (int key in overrides.Keys.Where(k => !known.Contains(k)).Order())
            {
                return new SourceFitPlan(false, new Dictionary<int, CropFocus>(), overrides, config.OutputFit,
                    VideoReframeCodes.SourceFitUnknownSource, $"SourceFits names source {key}, which the analysis does not have.");
            }
        }

        // OutputFormat=Source: every clip is padded into a source-shaped canvas; overrides do not apply.
        if (!platform)
            overrides = new Dictionary<int, VideoOutputFit>();

        VideoOutputFit Fit(int idx) => overrides.GetValueOrDefault(idx, config.OutputFit);
        List<int> used = keptWindows.Select(w => w.SourceIndex).Distinct().ToList();
        bool coverFit = platform && (overrides.Count == 0
            ? config.OutputFit is VideoOutputFit.Crop or VideoOutputFit.Subject
            : used.Any(idx => Fit(idx) is VideoOutputFit.Crop or VideoOutputFit.Subject));

        var focus = new Dictionary<int, CropFocus>();
        if (coverFit)
        {
            List<(int SourceIndex, double Start, double End)> subjectWindows = keptWindows
                .Where(w => Fit(w.SourceIndex) == VideoOutputFit.Subject)
                .ToList();
            if (subjectWindows.Count > 0)
            {
                foreach ((int idx, CropFocus f) in CropFocusResolver.Resolve(artifact, subjectWindows, sourceMedia, canvas))
                {
                    // A source a reframe owns (hand-written, or a followSubject entry) moves its crop over time,
                    // so "no single crop keeps the subject" does not make it a pad: it stays cropped and the
                    // reframe frames it (production run 180c2c62).
                    focus[idx] = f.Pad && reframedSources?.Contains(idx) == true ? f with { Pad = false } : f;
                }
            }

            foreach (int idx in used.Where(idx => Fit(idx) == VideoOutputFit.Pad))
                focus[idx] = new CropFocus(0.5, 0.5, PadBasis, Pad: true);
        }

        return new SourceFitPlan(coverFit, focus, overrides, config.OutputFit);
    }
}
