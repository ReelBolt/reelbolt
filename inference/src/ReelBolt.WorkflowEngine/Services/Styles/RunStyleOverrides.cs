using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Styles;

/// <summary>
/// Where a run's editing style and output-format override reach the DETERMINISTIC steps
/// (docs/editing-styles.md "What the style changes in the deterministic steps"). Kept out of the
/// executors themselves so each executor carries one call into this class and nothing else.
///
/// <para>
/// <b>Byte-identical without a choice.</b> Every method here returns its input unchanged — the same
/// config record, no annotation, a null cache field — for a run with no style and no format
/// override, so runs made before styles existed (and runs that pick none) behave exactly as before.
/// </para>
/// </summary>
public static class RunStyleOverrides
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The compile-step format a run format stands for; null for <see cref="RunOutputFormat.Original"/>.</summary>
    public static VideoOutputFormat? ToCompileFormat(RunOutputFormat? format) => format switch
    {
        RunOutputFormat.Vertical => VideoOutputFormat.Vertical,
        RunOutputFormat.Square => VideoOutputFormat.Square,
        RunOutputFormat.Landscape => VideoOutputFormat.Landscape,
        RunOutputFormat.Portrait => VideoOutputFormat.Portrait,
        _ => null
    };

    /// <summary>The run format a compile-step format stands for; null for <see cref="VideoOutputFormat.Source"/>.</summary>
    public static RunOutputFormat? FromCompileFormat(VideoOutputFormat format) => format switch
    {
        VideoOutputFormat.Vertical => RunOutputFormat.Vertical,
        VideoOutputFormat.Square => RunOutputFormat.Square,
        VideoOutputFormat.Landscape => RunOutputFormat.Landscape,
        VideoOutputFormat.Portrait => RunOutputFormat.Portrait,
        _ => null
    };

    /// <summary>The canvas aspect (width / height) of a fixed format.</summary>
    public static double AspectOf(RunOutputFormat format) => format switch
    {
        RunOutputFormat.Vertical => 1080.0 / 1920.0,
        RunOutputFormat.Square => 1.0,
        RunOutputFormat.Portrait => 1080.0 / 1350.0,
        _ => 1920.0 / 1080.0
    };

    /// <summary>"portrait", "square" or "landscape" for an aspect (within 5% of 1:1 counts as square).</summary>
    public static string OrientationOf(double aspect) =>
        Math.Abs(aspect - 1.0) <= 0.05 ? "square" : aspect < 1.0 ? "portrait" : "landscape";

    /// <summary>The execution's format override, Original normalized to null.</summary>
    public static RunOutputFormat? FormatOverride(WorkflowExecution execution) =>
        execution.OutputFormatOverride is RunOutputFormat.Original ? null : execution.OutputFormatOverride;

    /// <summary>
    /// The execution's chosen output resolution (short side in pixels), null for Auto. A stored value
    /// that is not one of <see cref="ReelBolt.Shared.Styles.OutputResolution.Allowed"/> is treated as
    /// Auto, so nothing but an allowed value ever sizes a canvas or a render argument.
    /// </summary>
    public static int? OutputResolutionOf(WorkflowExecution execution) =>
        execution.OutputResolution is { } chosen && ReelBolt.Shared.Styles.OutputResolution.Allowed.Contains(chosen) ? chosen : null;

    /// <summary>
    /// The tallest short side this run may produce (<see cref="ReelBolt.Shared.Styles.OutputResolution.EffectiveMaxHeight"/>):
    /// under Auto the plan's cap but never above 1080 (unlimited stays unlimited), with a choice the
    /// choice clamped to the plan.
    /// </summary>
    public static int OutputCap(ReelBolt.Shared.Tenancy.OrganizationEntitlementSet set, WorkflowExecution execution) =>
        ReelBolt.Shared.Styles.OutputResolution.EffectiveMaxHeight(set.MaxOutputHeight, OutputResolutionOf(execution));

    /// <summary>The pace the run's brief chose (<c>calm|medium|fast</c>), null when it chose none or the stored value is unknown.</summary>
    public static StyleDynamics? BriefPace(WorkflowExecution execution) =>
        RunBriefJson.TryParse(execution.RunBriefJson)?.Pace?.Trim().ToLowerInvariant() switch
        {
            RunBrief.PaceCalm => StyleDynamics.Calm,
            RunBrief.PaceMedium => StyleDynamics.Medium,
            RunBrief.PaceFast => StyleDynamics.Fast,
            _ => null
        };

    /// <summary>The dynamics the run asks for: the brief's pace, else the style's dynamics, else null.</summary>
    public static StyleDynamics? EffectiveDynamics(WorkflowExecution execution) =>
        BriefPace(execution) ?? Style(execution)?.Config.Dynamics;

    private static DynamicPace ToPace(StyleDynamics dynamics) => dynamics switch
    {
        StyleDynamics.Calm => DynamicPace.Calm,
        StyleDynamics.Fast => DynamicPace.Fast,
        _ => DynamicPace.Medium
    };

    /// <summary>The execution's style, null when absent, unreadable or empty.</summary>
    public static RunStyleSnapshot? Style(WorkflowExecution execution)
    {
        RunStyleSnapshot? style = StyleJson.TryParseSnapshot(execution.StyleConfigJson);
        return style is null || style.Config.IsEmpty ? null : style;
    }

    /// <summary>
    /// Applies the run's choices to one <c>VideoCompile</c> step's config:
    /// <list type="bullet">
    /// <item>a format override replaces <see cref="VideoCompileStepConfig.OutputFormat"/> (and, since a
    /// fixed canvas needs a re-encode, switches a stream-copy step to <c>Reencode</c>); for a
    /// vertical, square or 4:5 target it also turns the default centre crop into
    /// <see cref="VideoOutputFit.Subject"/> — a step that chose its fit (<c>Crop</c>, <c>Pad</c> or <c>Subject</c>,
    /// <paramref name="outputFitSetByStep"/>) keeps it;</item>
    /// <item><see cref="StyleEditing.SmoothTransitions"/> turns <c>TransitionPolicy = Off</c> into
    /// <c>Auto</c> on a re-encoding step (a policy the step set itself is kept);</item>
    /// <item>a <see cref="StyleCaptions"/> choice replaces the default <c>Subtitle</c> caption style
    /// on a step that already burns captions (it never turns captions on).</item>
    /// </list>
    /// <paramref name="applied"/> names each change made, for the step's log.
    /// </summary>
    /// <param name="outputFitSetByStep">
    /// The step's stored config names <c>outputFit</c> itself (<see cref="OutputFitSetByStep"/>). <c>Crop</c> is the
    /// enum default, so without this an explicit <c>Crop</c> cannot be told from an unset fit.
    /// </param>
    public static VideoCompileStepConfig ApplyToCompile(
        VideoCompileStepConfig config, WorkflowExecution execution, out IReadOnlyList<string> applied, bool outputFitSetByStep = false)
    {
        var changes = new List<string>();
        applied = changes;

        RunOutputFormat? format = FormatOverride(execution);
        if (ToCompileFormat(format) is { } target && target != config.OutputFormat)
        {
            config = config with { OutputFormat = target };
            changes.Add($"outputFormat={target}");
        }

        if (ToCompileFormat(format) is { } fixedFormat)
        {
            if (config.Mode == VideoCompileMode.StreamCopy)
            {
                config = config with { Mode = VideoCompileMode.Reencode };
                changes.Add("mode=Reencode");
            }

            if (fixedFormat is VideoOutputFormat.Vertical or VideoOutputFormat.Square or VideoOutputFormat.Portrait
                && config.OutputFit == VideoOutputFit.Crop && !outputFitSetByStep)
            {
                config = config with { OutputFit = VideoOutputFit.Subject };
                changes.Add("outputFit=Subject");
            }
        }

        // A chosen output resolution sizes the canvas (or caps a source-sized one), which only a
        // re-encode can do: a stream copy keeps the source's pixels.
        if (OutputResolutionOf(execution) is not null && config.Mode == VideoCompileMode.StreamCopy)
        {
            config = config with { Mode = VideoCompileMode.Reencode };
            changes.Add("mode=Reencode");
        }

        // The run's dynamics (brief pace over style) turn the dynamic edit on for a compile that did not set it itself.
        if (EffectiveDynamics(execution) is { } dynamics && config.Dynamics is null && config.Mode == VideoCompileMode.Reencode)
        {
            config = config with
            {
                Dynamics = new VideoDynamicsConfig(Enabled: true, Pace: ToPace(dynamics), KeepSceneBoundaries: true)
            };
            changes.Add($"dynamics={dynamics}");
            if (config.TransitionPolicy == VideoTransitionPolicy.Off)
            {
                config = config with { TransitionPolicy = VideoTransitionPolicy.Editor };
                changes.Add("transitionPolicy=Editor");
            }
        }

        StyleConfig? style = Style(execution)?.Config;
        if (style is not null)
        {
            if (style.Editing == StyleEditing.SmoothTransitions
                && config.TransitionPolicy == VideoTransitionPolicy.Off
                && config.Mode == VideoCompileMode.Reencode)
            {
                config = config with { TransitionPolicy = VideoTransitionPolicy.Auto };
                changes.Add("transitionPolicy=Auto");
            }

            if (config.EnableCaptions && config.CaptionStyle == CaptionStyle.Subtitle)
            {
                CaptionStyle? captionStyle = style.Captions switch
                {
                    StyleCaptions.Karaoke => CaptionStyle.Karaoke,
                    StyleCaptions.Punch => CaptionStyle.Punch,
                    StyleCaptions.Bold => CaptionStyle.Bold,
                    _ => null
                };
                if (captionStyle is { } cs)
                {
                    config = config with { CaptionStyle = cs };
                    changes.Add($"captionStyle={cs}");
                }
            }
        }

        return config;
    }

    /// <summary>
    /// Whether a stored <c>VideoCompile</c> config JSON sets <c>outputFit</c> itself (any casing, a non-null value).
    /// False for absent, null, unreadable or non-object JSON — the same as an unset fit.
    /// </summary>
    public static bool OutputFitSetByStep(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(VideoCompileStepConfig.OutputFit), StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind != JsonValueKind.Null;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The output shape this run is being cut for: the run's override when it has one, else the first
    /// <c>VideoCompile</c> step of the workflow configured with a fixed format, else null (the
    /// program keeps its sources' own shape).
    /// </summary>
    public static RunOutputFormat? ResolveTargetFormat(WorkflowExecution execution, IEnumerable<WorkflowStep> allSteps)
    {
        if (FormatOverride(execution) is { } format)
            return format;

        foreach (WorkflowStep step in allSteps.Where(s => s.StepType == StepType.VideoCompile).OrderBy(s => s.StepOrder))
        {
            if (string.IsNullOrWhiteSpace(step.VideoCompileConfigJson))
                continue;
            try
            {
                VideoCompileStepConfig? config = JsonSerializer.Deserialize<VideoCompileStepConfig>(step.VideoCompileConfigJson, ConfigJsonOptions);
                if (config is not null && FromCompileFormat(config.OutputFormat) is { } fixedFormat)
                    return fixedFormat;
            }
            catch (JsonException)
            {
                // A broken compile config fails its own step; it just says nothing about the target here.
            }
        }

        return null;
    }

    /// <summary>
    /// Adds an <c>"orientation"</c> block to a <c>VideoAnalyze</c> view so the agents reading it know
    /// each source's shape and the shape the run is being cut for — a 9:16 reel built mostly from
    /// 16:9 clips is mostly crop. Added only when there is something to say: a known target, or more
    /// than one source (a single source with no target already shows its width and height in
    /// <c>media</c>), so every other view stays byte-identical. Additive: no offered id is added,
    /// removed or reordered.
    /// </summary>
    public static void AnnotateOrientation(
        JsonObject view, VideoAnalysisArtifact artifact, WorkflowExecution execution, IEnumerable<WorkflowStep> allSteps)
    {
        RunOutputFormat? target = ResolveTargetFormat(execution, allSteps);
        IReadOnlyList<VideoAnalysisSourceInfo> sources = artifact.Sources is { Count: > 0 } s
            ? s
            : [new VideoAnalysisSourceInfo(0, string.Empty, artifact.Media)];
        bool multiSource = sources.Count > 1;

        if (target is null && !multiSource)
            return;

        string? targetOrientation = target is { } t ? OrientationOf(AspectOf(t)) : null;
        var node = new JsonObject();

        if (target is { } tf)
        {
            node["target"] = new JsonObject
            {
                ["format"] = tf.ToString(),
                ["aspect"] = Round2(AspectOf(tf)),
                ["orientation"] = targetOrientation
            };
        }

        JsonObject SourceNode(VideoAnalysisSourceInfo source, bool withIndex)
        {
            var sourceNode = new JsonObject();
            if (withIndex)
                sourceNode["src"] = source.SourceIndex;

            VideoAnalysisMedia media = source.Media;
            if (media.Width > 0 && media.Height > 0)
            {
                double aspect = media.Width / (double)media.Height;
                string orientation = OrientationOf(aspect);
                sourceNode["orientation"] = orientation;
                sourceNode["aspect"] = Round2(aspect);
                if (targetOrientation is not null)
                    sourceNode["matchesTarget"] = MatchesTarget(orientation, targetOrientation);
            }
            else
            {
                sourceNode["orientation"] = "unknown";
            }

            return sourceNode;
        }

        if (multiSource)
        {
            node["sources"] = new JsonArray(sources.Select(src => (JsonNode)SourceNode(src, withIndex: true)).ToArray());
            if (targetOrientation is not null)
            {
                node["preferSrc"] = new JsonArray(sources
                    .Where(src => src.Media.Width > 0 && src.Media.Height > 0
                        && MatchesTarget(OrientationOf(src.Media.Width / (double)src.Media.Height), targetOrientation))
                    .Select(src => (JsonNode)JsonValue.Create(src.SourceIndex))
                    .ToArray());
            }
        }
        else
        {
            node["source"] = SourceNode(sources[0], withIndex: false);
        }

        view["orientation"] = node;
    }

    /// <summary>
    /// The step-cache key component for this run's style and format (see
    /// <c>StepCacheKeyBuilder</c>): null — no component at all, so every key is byte-identical to a
    /// run without styles — when the run has no style, no override and no fixed target format;
    /// otherwise a canonical string covering all three, so a styled run never reuses an unstyled
    /// result, and an analysis whose orientation block names a target is never served to a run
    /// with a different one.
    /// <para>
    /// A chosen output resolution is a <c>|resolution={n}</c> suffix, present only when chosen: a run
    /// without one keeps exactly the component (and so the key) it had before resolutions existed.
    /// </para>
    /// </summary>
    public static string? CacheKeyComponent(WorkflowExecution execution, IEnumerable<WorkflowStep> allSteps)
    {
        RunStyleSnapshot? style = Style(execution);
        RunOutputFormat? format = FormatOverride(execution);
        RunOutputFormat? target = ResolveTargetFormat(execution, allSteps);
        int? resolution = OutputResolutionOf(execution);
        // QA-DYN-61: the run's dynamics are NOT in this shared component (every step type hashes it); a VideoCompile keys them
        // separately through DynamicsCacheKeyComponent.
        if (style is null && format is null && target is null && resolution is null)
            return null;

        // Only the Config is hashed: a style's display name or id changing must not invalidate a
        // cache entry whose prompts it never touched.
        string styleJson = style is null ? "-" : StyleJson.Serialize(style.Config.Sanitized());
        string component = string.Create(CultureInfo.InvariantCulture,
            $"style={styleJson.Length}:{styleJson}|format={format?.ToString() ?? "-"}|target={target?.ToString() ?? "-"}");
        return resolution is { } n
            ? string.Create(CultureInfo.InvariantCulture, $"{component}|resolution={n}")
            : component;
    }

    /// <summary>
    /// QA-DYN-61: the dynamics the run asks for (brief pace over style) as a step-cache key component for a VideoCompile ONLY
    /// (<c>StepCacheKeyInputs.RunDynamics</c>): they turn the dynamic edit on at run time (QA-DYN-53) and a brief's pace is in no
    /// other part of a compile's key. Null when the run asks for none. Other step types never hash it, so a pace-only change
    /// leaves their keys (VideoAnalyze, Voiceover, paid VideoGenerate) alone.
    /// </summary>
    public static string? DynamicsCacheKeyComponent(WorkflowExecution execution) =>
        EffectiveDynamics(execution) is { } d ? d.ToString() : null;

    private static bool MatchesTarget(string orientation, string targetOrientation) =>
        string.Equals(orientation, targetOrientation, StringComparison.Ordinal);

    private static double Round2(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
