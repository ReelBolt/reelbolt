using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows;

/// <summary>The hourly run-rate bucket a workflow run is counted against.</summary>
public enum RunRateBucket
{
    /// <summary>The run may hold model or vendor credits (10 per hour by default).</summary>
    Paid,

    /// <summary>The run is deterministic and calls no model or vendor (60 per hour by default).</summary>
    Free
}

/// <summary>
/// Chooses the <see cref="RunRateBucket"/> of a workflow. Pure and conservative: a workflow is
/// <see cref="RunRateBucket.Free"/> only when EVERY step is deterministic and calls no model or vendor;
/// anything unknown, new or unreadable is <see cref="RunRateBucket.Paid"/>.
/// </summary>
public static class RunRateClassifier
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// <see cref="RunRateBucket.Free"/> only for a non-empty step list whose every step is provably free. An empty
    /// list (nothing to prove, or steps that could not be read) is <see cref="RunRateBucket.Paid"/>.
    /// </summary>
    public static RunRateBucket Classify(IEnumerable<WorkflowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        List<WorkflowStep> list = [.. steps];
        return list.Count > 0 && list.All(IsFree) ? RunRateBucket.Free : RunRateBucket.Paid;
    }

    /// <summary>
    /// True when re-running <paramref name="step"/> calls no model or vendor (the per-step rule <see cref="Classify"/> applies to every
    /// step). Read by the review loop's adjustments (docs/design/review-loop-adjustments.md) to decide what may be re-entered.
    /// </summary>
    public static bool IsStepFree(WorkflowStep? step) => IsFree(step);

    private static bool IsFree(WorkflowStep? step) => step?.StepType switch
    {
        StepType.Extract or StepType.DesignSystem
            or StepType.VideoCompile or StepType.TimelineRender or StepType.ThumbnailGenerate
            or StepType.ExtractFrame or StepType.PinInsert => true,
        StepType.Conditional => ParsedCallsNoProvider<ConditionalStepConfig>(step.ConditionalConfigJson, c => c.CallsAnyProvider),
        StepType.VideoAnalyze => ParsedCallsNoProvider<VideoAnalyzeStepConfig>(step.VideoAnalyzeConfigJson, c => c.CallsAnyProvider),
        // AudioCleanup: free only when the config parses and the target is a local one; a hosted target (or no config) is paid.
        StepType.AudioCleanup => ParsedCallsNoProvider<AudioCleanupStepConfig>(step.AudioCleanupConfigJson, c => c.CallsAnyProvider),
        // VideoEnhance: free only when the config parses and names a local operation; a hosted one (or no config) is paid.
        StepType.VideoEnhance => ParsedCallsNoProvider<VideoEnhanceStepConfig>(step.VideoEnhanceConfigJson, c => c.CallsAnyProvider),
        // Agent, ForEach, ReviewLoop, Parallel, the rooms, VideoGenerate, Voiceover, Revise, DesignGraphic,
        // AudioGenerate (every take is a paid music/SFX vendor call), ImageGenerate (every picture is a paid vendor call) and any step type added later.
        _ => false
    };

    /// <summary>Free only when the config is present, parses, and says it calls no provider. Missing or unreadable = paid.</summary>
    private static bool ParsedCallsNoProvider<TConfig>(string? json, Func<TConfig, bool> callsAnyProvider)
        where TConfig : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<TConfig>(json, ConfigJsonOptions) is { } config && !callsAnyProvider(config);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
