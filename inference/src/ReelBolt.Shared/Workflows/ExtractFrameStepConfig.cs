using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Which frame of the source video an <c>ExtractFrame</c> step takes. Append-only: persisted by name.
/// </summary>
public enum FrameAt
{
    /// <summary>The frame at <see cref="ExtractFrameStepConfig.AtSec"/> on the source's own timeline.</summary>
    Time,

    /// <summary>The last frame of the source (what a "continue from where this clip ended" keyframe wants).</summary>
    Last,

    /// <summary>The first frame of the source.</summary>
    First
}

/// <summary>
/// A crop rectangle as fractions of the source frame (all in 0..1, origin top-left): the step never
/// takes pixel values, so one config is valid for any source resolution.
/// </summary>
public sealed record NormalizedCrop(double X, double Y, double W, double H);

/// <summary>
/// JSON configuration for a <c>StepType.ExtractFrame</c> workflow step, deserialised from
/// <c>WorkflowStep.ExtractFrameConfigJson</c>. Deterministic, non-LLM, free: one ffmpeg frame grab,
/// published as an image and (optionally) registered as an image project file so a later step
/// (e.g. <c>VideoGenerate.FirstFrame</c>) or the user can use it.
/// </summary>
/// <param name="Source">
/// The one video to take the frame from. Reuses <see cref="VideoSourceRef"/> (ProjectFile, StepOutput,
/// PreviousStepOutput, StepResult); <see cref="VideoSourceKind.GeneratedClips"/> names many videos and is refused.
/// </param>
/// <param name="At">Which frame. <see cref="FrameAt.Time"/> needs <paramref name="AtSec"/>.</param>
/// <param name="AtSec">Seconds on the source's own timeline; only valid with <see cref="FrameAt.Time"/>.</param>
/// <param name="Format"><c>png</c> (default, lossless) or <c>jpeg</c>.</param>
/// <param name="MaxLongSide">Downscale so the longer side is at most this many pixels (never upscales); null keeps the source size.</param>
/// <param name="Crop">Optional crop applied before the downscale.</param>
/// <param name="RegisterProjectFile">Also register the image as a project file (default true).</param>
[StepCard(
    "Grabs one frame of a video, the first or the last or at a time, as an image.",
    ["A video from the project or an earlier step"],
    "An image, optionally saved as a project file",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits,
    ReadableBy = ["Later steps", "The project's files"],
    PairsWith = ["VideoGenerate: the frame can start the next shot"])]
public sealed record ExtractFrameStepConfig(
    VideoSourceRef Source,
    [property: AppBindable(AppInputType.Choice, Label = "Which frame")]
    FrameAt At = FrameAt.Last,
    [property: AppBindable(AppInputType.Number, AppInputType.Duration, Label = "At second", Min = 0, Max = 86400)]
    double? AtSec = null,
    string Format = "png",
    int? MaxLongSide = null,
    NormalizedCrop? Crop = null,
    bool RegisterProjectFile = true)
{
    public const int MinLongSide = 16;
    public const int MaxLongSideLimit = 8192;
    public const double MinCropFraction = 0.01;

    /// <summary>One serialiser for run time and save time (Web defaults, enums as strings).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The normalised (lower-case) format, or null when <see cref="Format"/> is not an allowed one.</summary>
    public string? NormalizedFormat => Format?.Trim().ToLowerInvariant() switch
    {
        "png" => "png",
        "jpeg" => "jpeg",
        _ => null
    };

    /// <summary>Every reason this config may not be saved or run, in field order; empty means valid. Never throws.</summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (Source is null)
        {
            errors.Add("Source is required.");
        }
        else
        {
            if (Source.Kind == VideoSourceKind.GeneratedClips)
                errors.Add("Source=GeneratedClips names many videos; ExtractFrame takes exactly one.");
            if (Source.Validate() is { } sourceError)
                errors.Add(sourceError);
        }

        if (!Enum.IsDefined(At))
            errors.Add($"At must be Time, Last or First (found {At}).");
        else if (At == FrameAt.Time)
        {
            if (AtSec is null || double.IsNaN(AtSec.Value) || double.IsInfinity(AtSec.Value) || AtSec.Value < 0)
                errors.Add("At=Time requires AtSec, a non-negative number of seconds.");
        }
        else if (AtSec is not null)
        {
            errors.Add($"AtSec is only valid with At=Time, not At={At}.");
        }

        if (NormalizedFormat is null)
            errors.Add("Format must be 'png' or 'jpeg'.");

        if (MaxLongSide is { } side && (side < MinLongSide || side > MaxLongSideLimit))
            errors.Add($"MaxLongSide must be between {MinLongSide} and {MaxLongSideLimit} (found {side}).");

        if (Crop is { } crop)
        {
            bool finite = double.IsFinite(crop.X) && double.IsFinite(crop.Y) && double.IsFinite(crop.W) && double.IsFinite(crop.H);
            if (!finite
                || crop.X < 0 || crop.Y < 0
                || crop.W < MinCropFraction || crop.H < MinCropFraction
                || crop.X + crop.W > 1.0000001 || crop.Y + crop.H > 1.0000001)
            {
                errors.Add($"Crop must be a rectangle inside the frame in 0..1 with W and H of at least {MinCropFraction} (x+w and y+h at most 1).");
            }
        }

        return errors;
    }
}
