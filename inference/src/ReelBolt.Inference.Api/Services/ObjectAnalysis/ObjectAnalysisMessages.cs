namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>
/// The one place a failed object analysis gets its customer sentence. Plain words only: no model, vendor or
/// provider names, no engine jargon. An unmapped code gets the generic sentence plus the code, so support can ask for it.
/// </summary>
public static class ObjectAnalysisMessages
{
    public const string ObjectNotFound = "OBJECT_NOT_FOUND";
    /// <summary>A tap found no object on the tapped frame; the request fails and is not billed.</summary>
    public const string ObjectNotFoundAtPoint = "OBJECT_NOT_FOUND_AT_POINT";
    public const string ResultUnavailable = "RESULT_UNAVAILABLE";
    public const string ConsentWithdrawn = "CONSENT_WITHDRAWN";
    public const string ConsentRequired = "OBJECT_CONSENT_REQUIRED";
    public const string SourceTooLong = "OBJECT_SOURCE_TOO_LONG";

    /// <summary>Spoken by the assistant when it needs the consent a person must give in the editor.</summary>
    public const string ConsentNeeds =
        "Allow this project's video frames to be sent to our object-detection service. Open the clip in the editor, choose Objects and agree once.";

    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ObjectNotFound] = "We could not find that object in the video. Try describing it differently, or pick another clip.",
        [ObjectNotFoundAtPoint] = "We could not find an object where you tapped. Tap its centre, or draw a box.",
        ["EGRESS_REFUSED"] = "This project has not allowed its video frames to be analysed. Agree to it in the editor and try again.",
        ["SEGMENTATION_EGRESS_UNAVAILABLE"] = "Object detection is not available on this installation, so the video could not be analysed.",
        ["SEGMENTATION_SOURCE_TOO_LONG"] = "This video is longer than object detection can handle. Use a shorter clip.",
        ["SEGMENTATION_FRAME_BUDGET_EXCEEDED"] = "This video has too many frames for object detection. Use a shorter clip or a lower frame rate.",
        ["SEGMENTATION_NO_PROVIDER"] = "Object detection is not set up for this workspace.",
        ["SEGMENTATION_PROVIDER_ERROR"] = "The object-detection service had a problem. Nothing was lost; try again in a few minutes.",
        ["SEGMENTATION_BAD_RESPONSE"] = "The object-detection service returned something we could not use. Try again in a few minutes.",
        ["PLAN_NOT_ENTITLED"] = "Your plan does not include object detection. Upgrade to use it.",
        ["INSUFFICIENT_CREDITS"] = "There were not enough credits to analyse this video.",
        ["SOURCE_UNRESOLVED"] = "We could not open that video. It may have been removed.",
        ["SOURCE_NOT_FOUND"] = "We could not open that video. It may have been removed.",
        [ResultUnavailable] = "The result of this analysis is not available. Try finding the object again.",
        [ConsentWithdrawn] = "Object detection was switched off for this project before the analysis finished.",
        ["RUN_CANCELLED"] = "The analysis was cancelled."
    };

    /// <summary>Every mapped code, for the test that each has text.</summary>
    public static IReadOnlyCollection<string> KnownCodes => Map.Keys.ToList();

    public static string For(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && Map.TryGetValue(code, out string? text)) return text;
        return string.IsNullOrWhiteSpace(code)
            ? "The analysis did not finish. Try again in a few minutes."
            : $"The analysis did not finish. Try again in a few minutes (code {code}).";
    }
}
