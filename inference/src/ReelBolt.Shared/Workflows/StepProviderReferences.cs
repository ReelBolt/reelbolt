using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// One explicit inference-provider reference a step's config carries: which capability the step
/// would use it for, the provider id the author named, and the config field it came from (used
/// verbatim in a save-time refusal, so the author is told which field to change).
/// </summary>
/// <param name="Capability">
/// What the step would ask this provider to do. A reference whose row has a different capability can
/// never be used for this field (every resolver filters on its own capability explicitly), so the
/// pair — not the id alone — is what a validator and a cache key both key on.
/// </param>
/// <param name="ProviderId">The <c>inference_providers.id</c> the config names. Never <see cref="Guid.Empty"/>.</param>
/// <param name="Field">The config property's own JSON name (<c>transcriptionProviderId</c>, ...).</param>
public readonly record struct StepProviderReference(
    InferenceProviderCapability Capability,
    Guid ProviderId,
    string Field);

/// <summary>
/// The single place that knows WHICH config fields of WHICH step type name an inference provider,
/// and for which capability (A10). Shared by the two surfaces that must agree on that answer:
/// <list type="bullet">
/// <item>the Inference API's save-time ownership check
/// (<c>StepProviderOwnershipValidator</c>), which refuses a reference to a provider the project's
/// organization cannot see; and</item>
/// <item>the WorkflowEngine's step-cache fingerprint
/// (<c>ResolvedProviderFingerprint</c>), which hashes what each reference RESOLVES to, so switching
/// the provider a step runs on invalidates its cached output.</item>
/// </list>
///
/// <para>
/// Extracted from the raw config JSON with the SAME serializer options every executor deserializes
/// it with (<c>JsonSerializerDefaults.Web</c> plus string-valued enums — see e.g.
/// <c>VideoAnalyzeStepExecutor.ConfigJsonOptions</c>), so a config this method reads is read exactly
/// as the executor will read it at run time.
/// </para>
///
/// <para>
/// <b>Never throws, and a config it cannot read yields no references.</b> Every failure mode here
/// is already owned elsewhere: a missing or non-object config is <c>StepConfigSaveValidator</c>'s
/// rejection, and a config that will not deserialize is the executor's own CONFIG_INVALID. This
/// method is an OVERLAY on top of that validation, so it must not be the thing that decides a save
/// succeeds or fails — and it must not turn a malformed config into a 500.
/// </para>
/// </summary>
public static class StepProviderReferences
{
    /// <summary>
    /// The web defaults plus string-valued enums — the option set every step-config record in this
    /// namespace is deserialized with in production.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Every explicit provider reference <paramref name="stepType"/>'s config carries, in a stable
    /// order. Each config parameter is that step type's own blob
    /// (<c>WorkflowStep.XxxConfigJson</c> / <c>CreateWorkflowStepRequest.XxxConfigJson</c>); a blob
    /// belonging to another step type is ignored, so a caller may pass every column it has.
    /// </summary>
    public static IReadOnlyList<StepProviderReference> Extract(
        StepType stepType,
        string? conditionalConfigJson = null,
        string? reviewLoopConfigJson = null,
        string? videoAnalyzeConfigJson = null,
        string? videoGenerateConfigJson = null,
        string? voiceoverConfigJson = null,
        string? editRoomConfigJson = null,
        string? graphicsRoomConfigJson = null,
        string? colorGradeRoomConfigJson = null,
        string? audioGenerateConfigJson = null,
        string? audioCleanupConfigJson = null,
        string? videoEnhanceConfigJson = null,
        string? imageGenerateConfigJson = null)
    {
        var references = new List<StepProviderReference>();

        switch (stepType)
        {
            case StepType.VideoAnalyze:
                if (Deserialize<VideoAnalyzeStepConfig>(videoAnalyzeConfigJson) is { } analyze)
                {
                    // Transcription, shot captioning and object tracking each resolve through their
                    // OWN resolver call (ResolveTranscriptionAsync / ResolveVisionAsync — the object
                    // tracker passes ObjectVisionProviderId to the Vision resolver), so all three are
                    // real, independent provider choices even though two share one capability.
                    Add(references, InferenceProviderCapability.Transcription, analyze.TranscriptionProviderId, "transcriptionProviderId");
                    Add(references, InferenceProviderCapability.Vision, analyze.VisionProviderId, "visionProviderId");
                    Add(references, InferenceProviderCapability.Vision, analyze.ObjectVisionProviderId, "objectVisionProviderId");

                    // Text-prompted video segmentation resolves through ResolveVideoSegmentationAsync,
                    // its own capability — so a step naming a specific segmenter is fingerprinted by
                    // THAT id (not merely by the capability default) and the save-time ownership check
                    // refuses one the organization cannot see.
                    Add(references, InferenceProviderCapability.VideoSegmentation, analyze.SegmentationProviderId, "segmentationProviderId");
                }

                break;

            case StepType.Voiceover:
                if (Deserialize<VoiceoverStepConfig>(voiceoverConfigJson, VoiceoverStepConfig.JsonOptions) is { } voiceover)
                    Add(references, InferenceProviderCapability.SpeechSynthesis, voiceover.ProviderId, "providerId");

                break;

            case StepType.Conditional:
                if (Deserialize<ConditionalStepConfig>(conditionalConfigJson) is { } conditional)
                    Add(references, InferenceProviderCapability.Decision, conditional.ProviderId, "providerId");

                break;

            case StepType.ReviewLoop:
                if (Deserialize<ReviewLoopStepConfig>(reviewLoopConfigJson) is { } reviewLoop)
                {
                    Add(references, InferenceProviderCapability.Decision, reviewLoop.ProviderId, "providerId");

                    // QA-P1-5 (S-78): the visual check asks its OWN Vision model (the executor resolves
                    // visualCheck.visionProviderId through ResolveVisionAsync, independently of the reviewer's
                    // chat model), so it is a real provider choice and is validated like every other one. Read
                    // even while the check is off: a step that carries a foreign id must not save on the
                    // chance that someone enables it later.
                    Add(references, InferenceProviderCapability.Vision,
                        reviewLoop.VisualCheck?.VisionProviderId, "visualCheck.visionProviderId");
                }

                break;

            case StepType.VideoGenerate:
                if (Deserialize<VideoGenerateStepConfig>(videoGenerateConfigJson, VideoGenerateStepConfig.JsonOptions) is { } generate)
                {
                    // A talking head (avatar audio) names a LipSync row; any other step a VideoGeneration row.
                    Add(references, LipSyncStepResolution.CapabilityFor(generate), generate.ProviderId, "providerId");
                    // A talking head's still model (docs/design/app-inputs.md 27): owned, resolved and fingerprinted like any reference.
                    Add(references, InferenceProviderCapability.ImageEdit, generate.StillProviderId, "stillProviderId");
                }

                break;

            case StepType.AudioGenerate:
                // T-108: one row, of the capability the step's kind is resolved under.
                if (Deserialize<AudioGenerateStepConfig>(audioGenerateConfigJson, AudioGenerateStepConfig.JsonOptions) is { } audio
                    && Enum.IsDefined(audio.Kind))
                    Add(references, Inference.AudioModelCatalog.CapabilityOf(audio.Kind), audio.ProviderId, "providerId");

                break;

            case StepType.ImageGenerate:
                // One row, of the capability the step's mode is resolved under (Generate: ImageGeneration, Edit: ImageEdit).
                if (Deserialize<ImageGenerateStepConfig>(imageGenerateConfigJson, ImageGenerateStepConfig.JsonOptions) is { } image
                    && Enum.IsDefined(image.Mode))
                    Add(references, Metering.ImageGenerateCreditPricing.CapabilityOf(image.Mode), image.ProviderId, "providerId");

                break;

            case StepType.AudioCleanup:
                // A hosted target resolves an AudioIsolation row; a local target names none (a ProviderId on it is refused at save).
                if (Deserialize<AudioCleanupStepConfig>(audioCleanupConfigJson, AudioCleanupStepConfig.JsonOptions) is { IsHosted: true } cleanup)
                    Add(references, InferenceProviderCapability.AudioIsolation, cleanup.ProviderId, "providerId");

                break;

            case StepType.VideoEnhance:
                // A hosted operation resolves a VideoUpscale or FrameInterpolation row; a local one names none (a ProviderId on it is refused at save).
                if (Deserialize<VideoEnhanceStepConfig>(videoEnhanceConfigJson, VideoEnhanceStepConfig.JsonOptions) is { IsHosted: true, HostedCapability: { } enhanceCapability } enhance)
                    Add(references, enhanceCapability, enhance.ProviderId, "providerId");

                break;

            case StepType.EditRoom:
                if (Deserialize<EditRoomStepConfig>(editRoomConfigJson) is { } editRoom)
                    Add(references, InferenceProviderCapability.Decision, editRoom.ConveneProviderId, "conveneProviderId");

                break;

            case StepType.GraphicsRoom:
                if (Deserialize<GraphicsRoomStepConfig>(graphicsRoomConfigJson) is { } graphicsRoom)
                    Add(references, InferenceProviderCapability.Decision, graphicsRoom.ConveneProviderId, "conveneProviderId");

                break;

            case StepType.ColorGradeRoom:
                if (Deserialize<ColorGradeRoomStepConfig>(colorGradeRoomConfigJson) is { } colorGradeRoom)
                    Add(references, InferenceProviderCapability.Decision, colorGradeRoom.ConveneProviderId, "conveneProviderId");

                break;

            default:
                // Agent, ForEach, Parallel, Extract and VideoCompile carry no provider reference at
                // all: an Agent step's provider comes from its AgentDefinition, not its step config.
                break;
        }

        return references;
    }

    private static void Add(
        List<StepProviderReference> references,
        InferenceProviderCapability capability,
        Guid? providerId,
        string field)
    {
        if (providerId is { } id && id != Guid.Empty)
            references.Add(new StepProviderReference(capability, id, field));
    }

    private static T? Deserialize<T>(string? json, JsonSerializerOptions? options = null)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, options ?? JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
