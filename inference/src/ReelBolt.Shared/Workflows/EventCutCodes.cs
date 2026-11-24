#nullable enable
namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Cut on an event (<see cref="VideoCompileStepConfig.CutOnEvents"/>): the config-only check and the codes of
/// its compile errors (docs/video-editing.md "Cutting on an event").
/// </summary>
public static class EventCutCodes
{
    public const string RequiresReencode = "EVENT_CUT_REQUIRE_REENCODE";
    public const string NotInSource = "EVENT_CUT_NOT_IN_SOURCE";

    public static readonly IReadOnlyList<string> All = [RequiresReencode, NotInSource];

    /// <summary>Null when fine. A cut on an event is frame-exact, which a stream copy cannot promise.</summary>
    public static VideoReframeValidation? Check(VideoCompileStepConfig config) =>
        config.CutOnEvents && config.Mode == VideoCompileMode.StreamCopy
            ? VideoReframeValidation.Error(RequiresReencode, "CutOnEvents requires Mode=Reencode - a stream copy can only cut on a keyframe, not on an event's frame.")
            : null;
}
