using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>Builds the one-step VideoAnalyze config of the hidden object-analysis run: the cheapest config that still segments.</summary>
public static class ObjectAnalysisConfig
{
    public const int MaxSourceSeconds = SegmentationLimits.MaxSeconds;

    /// <summary>The cap used when the probe has no length: the maximum, so the hold is an upper bound.</summary>
    public const int UnknownDurationSeconds = SegmentationLimits.MaxSeconds;

    /// <summary>
    /// The seconds cap of the run in 30-fps-equivalent seconds: <c>ceil(duration * max(fps, 30) / 30)</c>, because the vendor bills (and
    /// the engine refuses past) the frame count the cap prices at 30 fps. Unknown length = the maximum (600). Null when the video needs
    /// more than 600 such seconds, which the caller refuses up front. Unknown or non-positive fps counts as 30.
    /// </summary>
    public static int? MaxSecondsFor(double? durationSec, double? fps = null)
    {
        if (durationSec is not { } d || d <= 0 || !double.IsFinite(d)) return UnknownDurationSeconds;
        double rate = fps is { } f && f > 30 && double.IsFinite(f) ? f : 30;
        double seconds = Math.Ceiling(d * rate / 30.0 - 1e-9);
        return seconds > MaxSourceSeconds ? null : SegmentationLimits.ClampSeconds((int)seconds);
    }

    public static VideoAnalyzeStepConfig Build(
        VideoSourceRef source, string prompt, ObjectMatchScope scope, int maxSeconds, ObjectSeed? seed = null, bool noWords = false) =>
        new(
            Version: 1,
            Source: source,
            DetectSilence: false,
            DetectShots: false,
            Transcription: VideoTranscriptionMode.Off,
            AnalyzeVisuals: false,
            AnalyzeAudioLevels: false,
            DetectLetterbox: false,
            DetectNearDuplicates: false,
            Vision: VideoVisionMode.Off,
            DerushPrePass: VideoDerushPrePassMode.Off,
            ScreenInstructionLikeSpans: false,
            LocateSubjects: false,
            TrackObjects: [seed is null
                ? new ObjectTrackTarget(prompt, Method: ObjectTrackMethod.Segmentation, MatchScope: scope)
                : new ObjectTrackTarget(
                    prompt,
                    AtSec: seed.AtSec,
                    Region: seed.BoxAsQuad(),
                    Method: ObjectTrackMethod.Segmentation,
                    MatchScope: ObjectMatchScope.SingleTrack,
                    SeedPoint: seed.Point,
                    NoWords: noWords)],
            AllowSourceMediaEgress: true,
            MaxSegmentationSeconds: SegmentationLimits.ClampSeconds(maxSeconds));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(VideoAnalyzeStepConfig config) => JsonSerializer.Serialize(config, Json);
}
