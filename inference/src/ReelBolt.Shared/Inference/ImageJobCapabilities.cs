using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Which <see cref="InferenceProviderCapability"/> each picture-editor job resolves, and the rules that
/// go with that mapping. One table, read by the editor's dispatch, the admin validation, the provider
/// test and the fal.ai client, so the four cannot disagree on which job a capability means.
/// </summary>
/// <remarks>
/// <para>
/// The job vocabulary is <see cref="FalImageJob"/>: it names the editor's jobs, and fal.ai is simply
/// the one vendor whose catalogue is organised by them. A non-fal row serves a job through the
/// <see cref="IImageClient"/> method the job maps to and refuses one it cannot do with a typed
/// <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// <b>Subject selection and background removal are not here</b>: both resolve
/// <see cref="InferenceProviderCapability.Segmentation"/> through <see cref="IImageAnalysisClient"/>.
/// </para>
/// </remarks>
public static class ImageJobCapabilities
{
    /// <summary>Every capability one of the editor's image jobs resolves, in job order.</summary>
    public static readonly IReadOnlyList<InferenceProviderCapability> All =
    [
        InferenceProviderCapability.ImageErase,
        InferenceProviderCapability.ImageFill,
        InferenceProviderCapability.ImageExpand,
        InferenceProviderCapability.ImageBackgroundReplace,
        InferenceProviderCapability.ImageRelight,
        InferenceProviderCapability.ImageUpscale,
        InferenceProviderCapability.ImageEdit,
        InferenceProviderCapability.ImageGeneration
    ];

    /// <summary>The capability a job resolves.</summary>
    public static InferenceProviderCapability CapabilityFor(FalImageJob job) => job switch
    {
        FalImageJob.Erase => InferenceProviderCapability.ImageErase,
        FalImageJob.Fill => InferenceProviderCapability.ImageFill,
        FalImageJob.Expand => InferenceProviderCapability.ImageExpand,
        FalImageJob.ReplaceBackground => InferenceProviderCapability.ImageBackgroundReplace,
        FalImageJob.Relight => InferenceProviderCapability.ImageRelight,
        FalImageJob.Upscale => InferenceProviderCapability.ImageUpscale,
        FalImageJob.Instruct => InferenceProviderCapability.ImageEdit,
        FalImageJob.Generate => InferenceProviderCapability.ImageGeneration,
        _ => throw new ArgumentOutOfRangeException(nameof(job), job, "No capability is defined for this job.")
    };

    /// <summary>The job a capability means, or <c>null</c> for a capability that is not an image job.</summary>
    public static FalImageJob? JobFor(InferenceProviderCapability capability) => capability switch
    {
        InferenceProviderCapability.ImageErase => FalImageJob.Erase,
        InferenceProviderCapability.ImageFill => FalImageJob.Fill,
        InferenceProviderCapability.ImageExpand => FalImageJob.Expand,
        InferenceProviderCapability.ImageBackgroundReplace => FalImageJob.ReplaceBackground,
        InferenceProviderCapability.ImageRelight => FalImageJob.Relight,
        InferenceProviderCapability.ImageUpscale => FalImageJob.Upscale,
        InferenceProviderCapability.ImageEdit => FalImageJob.Instruct,
        InferenceProviderCapability.ImageGeneration => FalImageJob.Generate,
        _ => null
    };

    /// <summary>True for every capability <see cref="IInferenceProviderResolver.ResolveImageJobAsync"/> accepts.</summary>
    public static bool IsImageJob(InferenceProviderCapability capability) => JobFor(capability) is not null;

    /// <summary>
    /// True for the six capabilities split out of <see cref="InferenceProviderCapability.ImageEdit"/>:
    /// the ones that may fall back to the ImageEdit default row when they have none of their own.
    /// </summary>
    public static bool FallsBackToImageEdit(InferenceProviderCapability capability) =>
        capability is InferenceProviderCapability.ImageErase
            or InferenceProviderCapability.ImageFill
            or InferenceProviderCapability.ImageExpand
            or InferenceProviderCapability.ImageBackgroundReplace
            or InferenceProviderCapability.ImageRelight
            or InferenceProviderCapability.ImageUpscale;

    /// <summary>Whether a provider of <paramref name="kind"/> may serve <paramref name="capability"/> at all.</summary>
    public static bool KindServes(InferenceProviderKind kind, InferenceProviderCapability capability) =>
        ProviderCapabilityUsage.ServableByKind.TryGetValue(kind, out InferenceProviderCapability[]? served)
        && served.Contains(capability);

    /// <summary>
    /// The fal.ai endpoint a row of <paramref name="capability"/> uses when its own model does not name
    /// one for the job, or <c>null</c> when the capability is not an image job.
    /// </summary>
    public static FalImageEndpoint? FalDefaultFor(InferenceProviderCapability capability) =>
        JobFor(capability) is { } job ? FalImageCatalog.Instance.DefaultFor(job) : null;

    /// <summary>
    /// The model a fal.ai row of <paramref name="rowCapability"/> sends for <paramref name="job"/>: the row's
    /// own model when it is a catalogued endpoint FOR THAT JOB, otherwise empty, which the client reads as
    /// "the catalogue's default for this job". So an <c>ImageUpscale</c> row set to a Topaz endpoint
    /// upscales with Topaz, while a legacy <c>ImageEdit</c> row serving an erase by fallback (its model is
    /// an instruction-edit endpoint) never sends that model to the erase job.
    /// </summary>
    public static string FalModelFor(FalImageJob job, string? rowModel)
    {
        FalImageEndpoint? endpoint = FalImageCatalog.Instance.Find(rowModel?.Trim());
        return endpoint is not null && endpoint.Job == job ? endpoint.Id : string.Empty;
    }

    /// <summary>
    /// The save-time rule for a fal.ai image row's model: for a job capability the model must be a
    /// catalogued endpoint of THAT job, because a row named for upscaling that points at a relighting
    /// endpoint would otherwise be quietly ignored at run time. <see cref="InferenceProviderCapability.ImageEdit"/>
    /// keeps its older, looser rule (any catalogued editing endpoint), because before the split one
    /// ImageEdit row served every tool and its model was only the connection test's target.
    /// Returns a refusal sentence, or <c>null</c> when the model is acceptable.
    /// </summary>
    public static string? FalModelProblem(InferenceProviderCapability capability, string? model)
    {
        if (JobFor(capability) is not { } job) return null;

        string id = model?.Trim() ?? string.Empty;
        FalImageEndpoint? endpoint = FalImageCatalog.Instance.Find(id);
        string allowed = string.Join(", ", FalImageCatalog.Instance.All.Where(e => e.Job == job).Select(e => e.Id));

        if (endpoint is null)
        {
            return "'" + id + "' is not a fal.ai endpoint this platform has a read schema for, so it would refuse to " +
                   "send it a request. For " + capability + " use one of: " + allowed + ".";
        }

        if (capability == InferenceProviderCapability.ImageEdit)
        {
            return endpoint.Job == FalImageJob.Generate
                ? "'" + id + "' is a text-to-image endpoint; an ImageEdit row edits an existing picture. Use one of: " + allowed + "."
                : null;
        }

        return endpoint.Job == job
            ? null
            : "'" + id + "' is a " + endpoint.Job + " endpoint, but a " + capability + " row serves the " + job +
              " job. Use one of: " + allowed + ".";
    }
}
