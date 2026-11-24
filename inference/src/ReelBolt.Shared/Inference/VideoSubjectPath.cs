using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// How the subjects of a clip (project avatars of people and of objects) reach ONE video model
/// (docs/design/avatar-objects.md "Subjects in a video generation step"). Decided per model from the model's own
/// declarations, before anything is reserved. Appended-only: the names are recorded in <c>meta.subjectPath</c>.
/// </summary>
public enum VideoSubjectPath
{
    /// <summary>
    /// The model animates one still of a person, driven by a speech track
    /// (<see cref="VideoReferenceInputSupport.AnimatesStill"/> and the step names the speech). One subject, a person.
    /// </summary>
    TalkingHead,

    /// <summary>
    /// The model has its own reference mode: each subject's pictures (and, where the model takes them, videos and a
    /// person's speech samples) are sent as reference items, shared out across the subjects inside the model's counts.
    /// </summary>
    References,

    /// <summary>
    /// The model takes an image input: ONE composed still that shows every subject in the scene of the prompt is sent
    /// as the first frame. Covers every image-capable model.
    /// </summary>
    FirstFrame
}

/// <summary>
/// Why a model cannot show a step's subjects: the failure code, a sentence that names the model, and, for a text-only
/// route, the image-capable model of the same family (<see cref="VideoSubjectSiblings"/>), which a caller may return
/// to its own caller as a machine-readable hint.
/// </summary>
/// <param name="Code">The step failure code (<see cref="VideoReferenceInput.NotSupportedCode"/>).</param>
/// <param name="Message">One sentence, kind-neutral (an avatar or an object), naming the model.</param>
/// <param name="Sibling">The model to use instead, or null when none is known.</param>
public sealed record VideoSubjectRefusal(string Code, string Message, string? Sibling = null);

/// <summary>The answer of <see cref="VideoSubjectPaths.Decide"/>: exactly one of the path or the refusal is set.</summary>
public readonly record struct VideoSubjectPathDecision(VideoSubjectPath? Path, VideoSubjectRefusal? Refusal)
{
    /// <summary>The model carries the subjects this way.</summary>
    public static VideoSubjectPathDecision Via(VideoSubjectPath path) => new(path, null);

    /// <summary>The model cannot carry them.</summary>
    public static VideoSubjectPathDecision Refuse(string message, string? sibling = null) =>
        new(null, new VideoSubjectRefusal(VideoReferenceInput.NotSupportedCode, message, sibling));
}

/// <summary>
/// The ONE place the path table of docs/design/avatar-objects.md is applied, so the engine, the save check, the linter
/// and every estimate give the same answer for the same model:
/// <list type="table">
/// <item><term>TalkingHead</term><description>the model declares <c>AnimatesStill</c> and the step names the speech</description></item>
/// <item><term>References</term><description>the model declares reference pictures and the mode is not <c>FirstFrame</c></description></item>
/// <item><term>FirstFrame</term><description>the model declares an image input</description></item>
/// <item><term>refused</term><description>none of the above (a text-only route); the message names the sibling</description></item>
/// </list>
/// Pure: no network call, ever.
/// </summary>
public static class VideoSubjectPaths
{
    /// <summary>
    /// The path for a step with subjects on a model whose declarations are <paramref name="references"/> and
    /// <paramref name="images"/>.
    /// </summary>
    /// <param name="kind">The resolved provider kind (for the sibling of a text-only route).</param>
    /// <param name="model">The model identity, named in every refusal.</param>
    /// <param name="references">The model's reference declaration.</param>
    /// <param name="images">The model's image-input declaration.</param>
    /// <param name="mode">The step's <see cref="VideoSubjectMode"/>.</param>
    /// <param name="hasDrivingAudio">The step names the speech that drives a person's mouth (<c>AvatarAudio</c>).</param>
    /// <param name="hasFrames">The step also names a first or last frame of its own.</param>
    public static VideoSubjectPathDecision Decide(
        InferenceProviderKind kind,
        string? model,
        VideoReferenceInputSupport references,
        VideoImageInputSupport images,
        VideoSubjectMode mode = VideoSubjectMode.Auto,
        bool hasDrivingAudio = false,
        bool hasFrames = false)
    {
        ArgumentNullException.ThrowIfNull(references);
        string named = string.IsNullOrWhiteSpace(model) ? "(no model)" : $"'{model}'";

        // A talking head, and the two requests such a model cannot take (a frame of the step's own, no speech), are
        // judged by the rule the phase 3 path has always used, word for word.
        if (references.AnimatesStill)
        {
            return VideoReferenceInput.Refusal(references, model ?? string.Empty, hasFrames, hasDrivingAudio) is { } talkingHeadRefusal
                ? VideoSubjectPathDecision.Refuse(talkingHeadRefusal)
                : VideoSubjectPathDecision.Via(VideoSubjectPath.TalkingHead);
        }

        if (hasDrivingAudio && !references.AudioDrivesSpeech)
        {
            return VideoSubjectPathDecision.Refuse(
                $"{named} is not known to make a person say the words of a driving audio track (it may only borrow " +
                "the voice), so the step's avatar audio cannot be used with it. Nothing was submitted.");
        }

        bool hasReferenceMode = references.MaxImages > 0 && references.MaxFiles > 0;
        bool takesFirstFrame = images != VideoImageInputSupport.None;

        if (hasReferenceMode && mode != VideoSubjectMode.FirstFrame)
        {
            return hasFrames && references.ExclusiveWithFrames
                ? VideoSubjectPathDecision.Refuse(
                    $"{named} cannot combine an avatar's reference material with a first or last frame (the vendor " +
                    "documents the two modes as mutually exclusive). Remove the frame or the avatar. Nothing was submitted.")
                : VideoSubjectPathDecision.Via(VideoSubjectPath.References);
        }

        if (mode == VideoSubjectMode.References && takesFirstFrame)
        {
            return VideoSubjectPathDecision.Refuse(
                $"{named} has no reference mode, and this step asks for one (subjectMode References). Set the step's " +
                "subject mode to Auto or FirstFrame, or choose a model that takes reference pictures. Nothing was submitted.");
        }

        if (takesFirstFrame)
        {
            return hasFrames
                ? VideoSubjectPathDecision.Refuse(
                    $"{named} shows an avatar or an object through one composed first frame, so the step cannot also " +
                    "give it a first or last frame. Remove the frame. Nothing was submitted.")
                : VideoSubjectPathDecision.Via(VideoSubjectPath.FirstFrame);
        }

        string? sibling = VideoSubjectSiblings.For(kind, model);
        return VideoSubjectPathDecision.Refuse(TakesNoPictures(model, sibling), sibling);
    }

    /// <summary>The same decision from a resolved client's own declarations (the engine's call).</summary>
    /// <exception cref="NotSupportedException">The client has no such model.</exception>
    /// <exception cref="ArgumentException">The value is not a usable model identity for the client.</exception>
    public static VideoSubjectPathDecision Decide(
        IVideoGenerationClient client,
        InferenceProviderKind kind,
        string model,
        VideoSubjectMode mode = VideoSubjectMode.Auto,
        bool hasDrivingAudio = false,
        bool hasFrames = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Decide(
            kind, model, client.DeclaredReferenceInputSupport(model), client.DeclaredImageInputSupport(model), mode, hasDrivingAudio, hasFrames);
    }

    /// <summary>
    /// The same decision for a provider kind and a model NAME, with no client and no network: the answer the save
    /// check, the linter and an estimate give before a run exists. Null when the model is not one this platform has a
    /// declaration for (an unknown name is "unknown", never a finding).
    /// </summary>
    public static VideoSubjectPathDecision? DecideOffline(
        InferenceProviderKind kind,
        string? model,
        VideoSubjectMode mode = VideoSubjectMode.Auto,
        bool hasDrivingAudio = false,
        bool hasFrames = false) =>
        VideoSubjectModels.TryDeclared(kind, model, out VideoReferenceInputSupport references, out VideoImageInputSupport images)
            ? Decide(kind, model, references, images, mode, hasDrivingAudio, hasFrames)
            : null;

    /// <summary>
    /// The refusal sentence of a text-only route: with a sibling it names it; without one it says what to look for.
    /// Kind-neutral on purpose: the same model is refused for a person and for an object.
    /// </summary>
    public static string TakesNoPictures(string? model, string? sibling)
    {
        string named = string.IsNullOrWhiteSpace(model) ? "(no model)" : $"'{model}'";
        return sibling is null
            ? $"{named} takes no pictures, so it cannot show an avatar or an object. Choose a model that takes pictures."
            : $"{named} takes no pictures. Use '{sibling}' to show an avatar or an object.";
    }
}

/// <summary>
/// What each video model of a provider KIND declares, answered from the same static tables its client reads, so code
/// that holds no client (the Inference API at save, lint and estimate time) can still apply
/// <see cref="VideoSubjectPaths"/>. Pure.
/// </summary>
public static class VideoSubjectModels
{
    /// <summary>
    /// The declarations of <paramref name="model"/> on <paramref name="kind"/>. False for a kind with no video
    /// generation, or a model the kind's client does not know (the caller treats that as unknown).
    /// </summary>
    public static bool TryDeclared(
        InferenceProviderKind kind, string? model, out VideoReferenceInputSupport references, out VideoImageInputSupport images)
    {
        references = VideoReferenceInputSupport.None;
        images = VideoImageInputSupport.None;
        string id = model?.Trim() ?? string.Empty;
        try
        {
            switch (kind)
            {
                case InferenceProviderKind.MiniMax:
                    _ = MiniMaxVideoGenerationClient.DescribeModel(id);
                    references = MiniMaxVideoGenerationClient.ReferenceSupport;
                    images = VideoImageInputSupport.FirstAndLastFrame;
                    return true;

                case InferenceProviderKind.FalAi:
                    FalVideoEndpoint? endpoint = id.Length == 0
                        ? FalVideoCatalog.Instance.DefaultFor(FalVideoJob.ImageToVideo)
                        : FalVideoCatalog.Instance.Find(id);
                    if (endpoint is null)
                    {
                        return false;
                    }

                    references = FalAiVideoGenerationClient.ReferenceInputSupportOf(endpoint);
                    images = FalAiVideoGenerationClient.ImageInputSupportOf(endpoint);
                    return true;

                case InferenceProviderKind.Higgsfield:
                    if (id.Length == 0)
                    {
                        return false;
                    }

                    images = HiggsfieldVideoGenerationClient.DeclaredImageInputSupportOf(id);
                    return true;

                case InferenceProviderKind.ComfyUI:
                    if (!ComfyUiWorkflowTemplates.TryParse(id, out ComfyUiWorkflowTemplate template))
                    {
                        return false;
                    }

                    images = ComfyUiWorkflowTemplates.ImageInputSupport(template);
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The image-capable model of the same family for every text-only route this platform offers (decision AO-3 of
/// docs/design/avatar-objects.md: a video model with no image input at all is refused before anything is charged,
/// with a message that names this model; the platform never changes the model itself).
/// </summary>
public static class VideoSubjectSiblings
{
    /// <summary>
    /// fal.ai: each catalogued text-to-video endpoint and the catalogued endpoint of the same model that takes
    /// pictures (its image-to-video route; for Veo 3.1, whose plain image-to-video route is not catalogued, its
    /// reference-to-video route). <c>VideoSubjectSiblingTests</c> fails if a text-only endpoint is added without one.
    /// </summary>
    private static readonly Dictionary<string, string> FalSiblings = new(StringComparer.Ordinal)
    {
        ["fal-ai/kling-video/v3/turbo/pro/text-to-video"] = "fal-ai/kling-video/v3/turbo/pro/image-to-video",
        ["minimax/h3-max-turbo/text-to-video"] = "minimax/h3-max-turbo/image-to-video",
        ["alibaba/wan-3.0/text-to-video"] = "alibaba/wan-3.0/image-to-video",
        ["fal-ai/veo3.1"] = "fal-ai/veo3.1/reference-to-video",
    };

    private const string HiggsfieldTextSuffix = "/text-to-video";
    private const string HiggsfieldImageSuffix = "/image-to-video";

    /// <summary>
    /// The model to use instead of the text-only <paramref name="model"/> of <paramref name="kind"/>, or null when
    /// the model is not a text-only route this platform knows a sibling for.
    /// </summary>
    public static string? For(InferenceProviderKind kind, string? model)
    {
        string id = model?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            return null;
        }

        switch (kind)
        {
            case InferenceProviderKind.FalAi:
                return FalSiblings.GetValueOrDefault(id);

            case InferenceProviderKind.Higgsfield:
                {
                    // The vendor's endpoint ids come in pairs that differ only in the workflow word.
                    if (!id.EndsWith(HiggsfieldTextSuffix, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    string candidate = id[..^HiggsfieldTextSuffix.Length] + HiggsfieldImageSuffix;
                    try
                    {
                        return HiggsfieldVideoGenerationClient.DeclaredImageInputSupportOf(candidate) != VideoImageInputSupport.None
                            ? candidate
                            : null;
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }
                }

            case InferenceProviderKind.ComfyUI:
                return string.Equals(id, ComfyUiWorkflowTemplates.TextToVideoName, StringComparison.Ordinal)
                    ? ComfyUiWorkflowTemplates.ImageToVideoName
                    : null;

            default:
                return null;
        }
    }
}
