using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>One catalogued property of a shareable step: where it sits and what happens to it.</summary>
public sealed record StripPlanEntry(string Property, string Treatment, string? SlotKind);

/// <summary>Whether a step type may appear in a public recipe, and how it is cleaned if so.</summary>
public abstract record StepPublishability
{
    private StepPublishability()
    {
    }

    /// <summary>The step may be published; <paramref name="ConfigType"/> is its config record (null when it has none) and <paramref name="Plan"/> every non-numeric property the catalog treats specially.</summary>
    public sealed record Shareable(Type? ConfigType, IReadOnlyList<StripPlanEntry> Plan) : StepPublishability;

    /// <summary>The step refuses to publish; <paramref name="Message"/> says why in words.</summary>
    public sealed record NotShareable(string ReasonCode, string Message) : StepPublishability;
}

/// <summary>
/// The publishability of every <see cref="StepType"/>: an explicit arm for each member, no default arm that shares anything. A new
/// step type does not compile into a verdict until someone decides it here (and <c>MarketplaceCatalogDriftTests</c> checks it).
/// </summary>
public static class MarketplaceStepPublishability
{
    public const string StepTypeNotShareable = "STEP_TYPE_NOT_SHAREABLE";

    public static StepPublishability For(StepType stepType) => stepType switch
    {
        StepType.Agent => Share(null),
        StepType.Conditional => Share(typeof(ConditionalStepConfig)),
        StepType.ForEach => Share(null),
        StepType.ReviewLoop => Share(typeof(ReviewLoopStepConfig)),
        StepType.Parallel => Share(null),
        StepType.Extract => Share(typeof(ExtractStepConfig)),
        StepType.VideoAnalyze => Share(typeof(VideoAnalyzeStepConfig)),
        StepType.VideoCompile => Share(typeof(VideoCompileStepConfig)),
        StepType.EditRoom => Share(typeof(EditRoomStepConfig)),
        StepType.GraphicsRoom => Share(typeof(GraphicsRoomStepConfig)),
        StepType.ColorGradeRoom => Share(typeof(ColorGradeRoomStepConfig)),
        StepType.VideoGenerate => Share(typeof(VideoGenerateStepConfig)),
        StepType.Voiceover => Share(typeof(VoiceoverStepConfig)),
        StepType.ThumbnailGenerate => Share(typeof(ThumbnailGenerateStepConfig)),
        StepType.Revise => new StepPublishability.NotShareable(
            StepTypeNotShareable, "A revision step belongs to a finished film's own system workflow and cannot be shared."),
        StepType.DesignGraphic => new StepPublishability.NotShareable(
            StepTypeNotShareable, "A graphic-design step belongs to a picture document's own system workflow and cannot be shared."),
        StepType.DailiesRoom => Share(typeof(DailiesRoomStepConfig)),
        StepType.TimelineRender => new StepPublishability.NotShareable(
            StepTypeNotShareable, "A timeline render step points at one of your editor timelines, which cannot be shared."),
        StepType.DesignSystem => Share(typeof(DesignSystemStepConfig)),
        StepType.ExtractFrame => Share(typeof(ExtractFrameStepConfig)),
        StepType.AudioGenerate => Share(typeof(AudioGenerateStepConfig)),
        StepType.PinInsert => Share(typeof(PinInsertStepConfig)),
        StepType.AudioCleanup => Share(typeof(AudioCleanupStepConfig)),
        StepType.VideoEnhance => Share(typeof(VideoEnhanceStepConfig)),
        StepType.ImageGenerate => Share(typeof(ImageGenerateStepConfig)),
        _ => throw new ArgumentOutOfRangeException(nameof(stepType), stepType, "Every step type needs an explicit marketplace verdict.")
    };

    /// <summary>The config record of a shareable step type, null for types without one or that never share.</summary>
    public static Type? ConfigTypeOf(StepType stepType) =>
        For(stepType) is StepPublishability.Shareable s ? s.ConfigType : null;

    /// <summary>Config types a step type owns but that never publish (so the drift test can tell "not shared on purpose" from "forgotten").</summary>
    public static readonly IReadOnlyList<Type> RefusedConfigTypes = [typeof(TimelineRenderStepConfig)];

    private static StepPublishability.Shareable Share(Type? configType) =>
        new(configType, configType is null ? [] : PlanFor(configType));

    private static List<StripPlanEntry> PlanFor(Type root)
    {
        List<StripPlanEntry> plan = [];
        foreach (Type type in ConfigReflection.Reachable([root]))
        {
            foreach (ConfigProperty p in ConfigReflection.PropertiesOf(type))
            {
                if (MarketplaceFieldCatalog.Explicit.TryGetValue(p.RuleKey, out FieldRule? rule))
                    plan.Add(new StripPlanEntry(p.RuleKey, rule.Class.ToString(), rule.SlotKind));
            }
        }

        return plan;
    }
}
