using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Whether a resolved video-generation provider can take a still image as an INPUT at all — the check
/// that decides whether a plan naming a <c>firstFrame</c>/<c>lastFrame</c> may be submitted, made
/// BEFORE any frame is cut and before any clip is bought.
/// </summary>
/// <remarks>
/// <para>
/// WHY this is a refusal and not a warning. A provider that has no image input cannot honour the
/// frame the plan asked for, and the two available alternatives are both dishonest: submitting the
/// clip anyway drops the frame the operator paid for while the manifest reports the request as
/// satisfied, and extracting a frame that nothing will carry puts the user's own media through
/// ffmpeg for no reason. The whole plan is therefore refused with
/// <see cref="UnsupportedCode"/> before the first ledger row can exist — the same all-or-nothing rule
/// <see cref="GeneratedShotEgressPolicy"/> follows and for the same reason: a partially generated
/// plan is a half-edit the operator cannot use.
/// </para>
/// <para>
/// The set below is exactly the closed set <c>InferenceProvidersController</c> permits for
/// <see cref="InferenceProviderCapability.VideoGeneration"/>, and every one of those vendors
/// documents image-conditioned model variants: MiniMax's v2 request carries the frame as a second
/// content item, Higgsfield's endpoints take a start/end image, ComfyUI's vetted
/// <c>wan2.2-ti2v-5b-i2v</c> graph wires one into its <c>LoadImage</c> node, and fal.ai's
/// image-to-video endpoints take a first frame and, where the endpoint's schema documents one, a
/// last frame. Everything else (a chat kind, an image kind, an analysis kind) has no image-to-video
/// input at all.
/// </para>
/// <para>
/// WHAT THIS TYPE DOES NOT CLAIM. Putting the bytes on
/// <see cref="VideoGenerationRequest.Images"/> is this engine's half; TRANSPORTING them to the vendor
/// is the resolved client's half, and this table says nothing about which clients have finished it. A
/// client that does not read the list drops the frame silently, so a client's image transport and its
/// kind's entry here are two different things to keep true.
/// </para>
/// </remarks>
public static class VideoGenerationImageSupport
{
    /// <summary>
    /// The stable code reported when a plan requests source-media frames and the resolved provider
    /// kind cannot accept an input image. Callers surface it verbatim in the step's failure payload,
    /// so operators and tests match on one constant instead of on prose.
    /// </summary>
    public const string UnsupportedCode = "KEYFRAME_NOT_SUPPORTED";

    /// <summary>
    /// The provider kinds whose video-generation models accept a still frame as the clip's opening or
    /// closing image — see the type remarks for why this is exactly the set the provider controller
    /// already permits for video generation.
    /// </summary>
    private static readonly HashSet<InferenceProviderKind> KindsWithImageInputs = new()
    {
        InferenceProviderKind.MiniMax,
        InferenceProviderKind.Higgsfield,
        InferenceProviderKind.ComfyUI,
        InferenceProviderKind.FalAi
    };

    /// <summary>Whether <paramref name="kind"/> has an image-to-video input to send a frame on.</summary>
    public static bool CanAcceptInputImages(InferenceProviderKind kind) => KindsWithImageInputs.Contains(kind);
}
