using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Decision 3, the credit HOLD half: which steps render the run's output, and how a chosen output
/// resolution scales their hold. Shared by the API's execute preflight (<c>WorkflowCostEstimator</c>)
/// and the engine's per-step hold (<c>UsageHistoryCreditHoldEstimator</c>), so the two can never
/// disagree about whether the same run may begin.
/// </summary>
public static class ResolutionHoldScaling
{
    /// <summary>
    /// Whether a step renders the run's output at the chosen resolution: a
    /// <see cref="StepType.VideoCompile"/> encode, a <see cref="StepType.TimelineRender"/> delivery
    /// encode of an editor timeline, a <see cref="StepType.Revise"/> re-render, or an
    /// <see cref="StepType.Agent"/> step whose agent is granted <see cref="ToolGroup.SandboxRender"/>
    /// (the Author renders the final Remotion film). An Agent step whose type is unknown is not.
    /// </summary>
    public static bool RendersOutput(StepType stepType, AgentType? agentType) => stepType switch
    {
        StepType.VideoCompile or StepType.TimelineRender or StepType.Revise => true,
        StepType.Agent => agentType is { } type && ToolGroupCatalog.GroupsFor(type).Contains(ToolGroup.SandboxRender),
        _ => false
    };

    /// <summary>
    /// <paramref name="holdMillis"/> multiplied by <see cref="OutputResolution.PixelFactor"/> when the
    /// step renders the output and the factor is above 1 (4K holds four times the 1080p figure);
    /// otherwise unchanged — a smaller choice never shrinks a hold.
    /// </summary>
    public static long ScaleHold(long holdMillis, bool rendersOutput, int? outputResolution) =>
        ScaleHoldByFactor(holdMillis, rendersOutput, OutputResolution.PixelFactor(outputResolution));

    /// <summary>
    /// Whether <paramref name="outputResolution"/> is the reference the plan and the profiles are priced at:
    /// pixel factor 1, i.e. Auto (null) and 1080p. A render step's hold reads only reference samples and
    /// is then scaled (<see cref="HoldAt"/>).
    /// </summary>
    public static bool IsReference(int? outputResolution) => OutputResolution.PixelFactor(outputResolution) == 1.0;

    /// <summary>
    /// QA-15: a step's final hold at <paramref name="outputResolution"/>. <paramref name="referenceHoldMillis"/>
    /// is its hold at the reference (the run plan over reference samples), scaled by <see cref="ScaleHold"/>;
    /// a render step's own history at exactly that resolution (<paramref name="nativeHoldMillis"/>, its p90,
    /// null with too few samples) may only RAISE that figure, never lower it. Holds only grow: a smaller
    /// resolution never drops below the reference hold, and a non-render step is never scaled or raised.
    /// </summary>
    public static long HoldAt(long referenceHoldMillis, bool rendersOutput, int? outputResolution, long? nativeHoldMillis) =>
        Math.Max(
            ScaleHold(referenceHoldMillis, rendersOutput, outputResolution),
            rendersOutput ? nativeHoldMillis ?? 0 : 0);

    /// <summary><see cref="ScaleHold"/> for a caller that already holds the run's pixel factor.</summary>
    public static long ScaleHoldByFactor(long holdMillis, bool rendersOutput, double pixelFactor) =>
        rendersOutput && pixelFactor > 1.0
            ? (long)Math.Round(holdMillis * pixelFactor, MidpointRounding.AwayFromZero)
            : holdMillis;
}
