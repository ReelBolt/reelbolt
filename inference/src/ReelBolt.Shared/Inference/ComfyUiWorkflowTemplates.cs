using System.Text.Json.Nodes;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The CLOSED set of ComfyUI workflow graphs ReelBolt will ever submit (issue #93 phase 5). A
/// <c>VideoGenerateStepConfig.Model</c> for a <c>ComfyUI</c> provider is one of these names and
/// nothing else; <see cref="ComfyUiWorkflowTemplates.TryParse"/> is the only way in, and it
/// accepts the exact names only.
/// </summary>
/// <remarks>
/// Both templates run Wan 2.2 TI2V-5B (Apache-2.0 — Wan-AI/Wan2.2-TI2V-5B on Hugging Face, the
/// licence verified 2026-10-04), through the node set docs.comfy.org documents for it
/// (https://docs.comfy.org/tutorials/video/wan/wan2_2, fetched 2026-10-04): Load Diffusion Model
/// (<c>UNETLoader</c>), Load CLIP (<c>CLIPLoader</c>), Load VAE (<c>VAELoader</c>),
/// <c>CLIPTextEncode</c>, <c>Wan22ImageToVideoLatent</c>, plus the core sampler/decoder and the
/// <c>CreateVideo</c>/<c>SaveVideo</c> pair (comfy_extras/nodes_video.py). A first-last-frame
/// template is deliberately NOT shipped: the Wan 2.2 5B latent node documents a start image only,
/// and inventing a last-frame slot would be exactly the unverified wire fact rule 7 forbids.
/// LTX-2 is not shipped either — see docs/video-generation.md "Self-hosted ComfyUI" for its
/// licence facts and why.
/// </remarks>
public enum ComfyUiWorkflowTemplate
{
    /// <summary><c>wan2.2-ti2v-5b-t2v</c>: text-to-video, no image input.</summary>
    Wan22Ti2v5bTextToVideo,

    /// <summary><c>wan2.2-ti2v-5b-i2v</c>: image-to-video seeded from ONE start image (the request's first image).</summary>
    Wan22Ti2v5bImageToVideo
}

/// <summary>
/// The typed values a template's named slots accept — the ONLY thing a caller can vary about a
/// graph. Every other value (node classes, wiring, checkpoint file names, sampler settings) is a
/// compiled-in constant of <see cref="ComfyUiWorkflowTemplates"/>.
/// </summary>
/// <param name="Prompt">The already-sanitized positive prompt text.</param>
/// <param name="Seed">The sampler seed (non-negative).</param>
/// <param name="Width">Output width in pixels (a template constant per resolution/ratio, never caller-chosen).</param>
/// <param name="Height">Output height in pixels.</param>
/// <param name="LengthFrames">Latent length in frames (<c>duration × fps + 1</c>).</param>
/// <param name="Fps">Output frame rate (a template constant).</param>
/// <param name="FilenamePrefix">The <c>SaveVideo</c> prefix (sanitized to a safe token by the builder).</param>
/// <param name="StartImageName">The ComfyUI input-image name returned by <c>POST /upload/image</c>; required by the image-to-video template, ignored by text-to-video.</param>
public sealed record ComfyUiWorkflowSlots(
    string Prompt,
    long Seed,
    int Width,
    int Height,
    int LengthFrames,
    int Fps,
    string FilenamePrefix,
    string? StartImageName = null);

/// <summary>
/// Builds the vetted graphs. Pure functions, no I/O.
/// </summary>
public static class ComfyUiWorkflowTemplates
{
    /// <summary>Exact persisted/config names, one per <see cref="ComfyUiWorkflowTemplate"/>.</summary>
    public const string TextToVideoName = "wan2.2-ti2v-5b-t2v";
    public const string ImageToVideoName = "wan2.2-ti2v-5b-i2v";

    /// <summary>The output frame rate every template renders at (Wan 2.2 TI2V-5B's native 24 fps).</summary>
    public const int OutputFps = 24;

    /// <summary>Checkpoint file names the templates reference, verbatim from docs.comfy.org's Wan 2.2 tutorial.</summary>
    public const string DiffusionModelFile = "wan2.2_ti2v_5B_fp16.safetensors";
    public const string TextEncoderFile = "umt5_xxl_fp8_e4m3fn_scaled.safetensors";
    public const string VaeFile = "wan2.2_vae.safetensors";

    private const string NegativePrompt =
        "blurry, low quality, distorted, watermark, text overlay, subtitles, extra limbs, deformed hands, flicker";

    /// <summary>Every template name, in enum order.</summary>
    public static IReadOnlyList<string> Names { get; } = new[] { TextToVideoName, ImageToVideoName };

    /// <summary>
    /// The node class names a template instantiates. <c>ComfyUiVideoGenerationClient.ProbeAsync</c>
    /// checks each against the server's <c>GET /object_info/{class}</c> so a server missing a node
    /// fails the no-cost probe instead of failing a submission after GPU time was reserved.
    /// </summary>
    public static IReadOnlyList<string> RequiredNodeClasses(ComfyUiWorkflowTemplate template)
    {
        var classes = new List<string>
        {
            "UNETLoader", "CLIPLoader", "VAELoader", "CLIPTextEncode", "Wan22ImageToVideoLatent",
            "ModelSamplingSD3", "KSampler", "VAEDecode", "CreateVideo", "SaveVideo"
        };
        if (template == ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo)
        {
            classes.Add("LoadImage");
        }

        return classes;
    }

    /// <summary>
    /// What a template's GRAPH can carry as input images, and therefore what its name accepts.
    /// <para>
    /// The image-to-video template wires one uploaded frame into
    /// <c>Wan22ImageToVideoLatent.start_image</c> through a <c>LoadImage</c> node; the text-to-video
    /// graph has no image node at all, which is why an image offered to it is refused rather than
    /// dropped. Neither template carries a LAST frame: Wan 2.2 TI2V-5B's latent node documents a start
    /// image only, and inventing a second slot would be exactly the unverified wire fact this class
    /// exists to prevent (see the class remarks).
    /// </para>
    /// </summary>
    public static VideoImageInputSupport ImageInputSupport(ComfyUiWorkflowTemplate template) => template switch
    {
        ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo => VideoImageInputSupport.FirstFrame,
        ComfyUiWorkflowTemplate.Wan22Ti2v5bTextToVideo => VideoImageInputSupport.None,
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown ComfyUI workflow template.")
    };

    /// <summary>
    /// Parses a config/model string into a template. ORDINAL, exact-name match only — no trimming
    /// of anything but surrounding whitespace, no case folding, no prefix matching, and never a
    /// path, a URL or JSON: a value that is not one of <see cref="Names"/> is rejected, which is
    /// the test-proven refusal of arbitrary graphs and template names.
    /// </summary>
    public static bool TryParse(string? model, out ComfyUiWorkflowTemplate template)
    {
        switch (model?.Trim())
        {
            case TextToVideoName:
                template = ComfyUiWorkflowTemplate.Wan22Ti2v5bTextToVideo;
                return true;
            case ImageToVideoName:
                template = ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo;
                return true;
            default:
                template = default;
                return false;
        }
    }

    /// <summary>The persisted name of a template.</summary>
    public static string NameOf(ComfyUiWorkflowTemplate template) => template switch
    {
        ComfyUiWorkflowTemplate.Wan22Ti2v5bTextToVideo => TextToVideoName,
        ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo => ImageToVideoName,
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown ComfyUI workflow template.")
    };

    /// <summary>
    /// What a template offers, in the SAME record every priced kind uses — except that
    /// <c>PricePerSecondByResolution</c> is GPU-SECONDS per output second, not USD. The values are
    /// conservative single-GPU (24 GB class) estimates for Wan 2.2 TI2V-5B at 20 sampling steps;
    /// they drive the GPU-minutes budget gate exactly as a vendor rate drives the USD one, and
    /// are deliberately constants rather than config knobs so the estimate cannot be talked down.
    /// </summary>
    public static VideoGenerationCapabilities Describe(ComfyUiWorkflowTemplate template) =>
        new(
            PricePerSecondByResolution: new Dictionary<string, decimal>
            {
                { "480P", 20m },
                { "720P", 45m }
            },
            MinDurationSeconds: 1,
            MaxDurationSeconds: 5,
            AllowedRatios: new[] { "16:9", "9:16", "1:1" });

    /// <summary>
    /// The output dimensions for a resolution/ratio pair — template constants (multiples of 16, as
    /// the Wan latent requires), never caller-chosen numbers.
    /// </summary>
    public static (int Width, int Height) Dimensions(string resolution, string ratio)
    {
        (int longSide, int shortSide) = resolution switch
        {
            "480P" => (832, 480),
            "720P" => (1280, 704),
            _ => throw new ArgumentException($"Resolution '{resolution}' is not offered by the ComfyUI templates (480P, 720P).", nameof(resolution))
        };

        return ratio switch
        {
            "16:9" => (longSide, shortSide),
            "9:16" => (shortSide, longSide),
            "1:1" => (shortSide, shortSide),
            _ => throw new ArgumentException($"Ratio '{ratio}' is not offered by the ComfyUI templates (16:9, 9:16, 1:1).", nameof(ratio))
        };
    }

    /// <summary>Latent length in frames for a duration: <c>seconds × fps + 1</c> (the Wan 4n+1 convention lands on 24 fps multiples).</summary>
    public static int LengthFrames(int durationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);
        return durationSeconds * OutputFps + 1;
    }

    /// <summary>
    /// Builds the API-format graph (<c>{ "nodeId": { "class_type", "inputs" } }</c>) for a template.
    /// Only the slot values vary; everything else is literal. The prompt is the one caller-authored
    /// string and goes into a <c>CLIPTextEncode.text</c> input, where it is a string value and can
    /// never become a node, a class name or a path.
    /// </summary>
    public static JsonObject Build(ComfyUiWorkflowTemplate template, ComfyUiWorkflowSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentOutOfRangeException.ThrowIfNegative(slots.Seed);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots.Width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots.Height, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots.LengthFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots.Fps, 1);

        bool imageToVideo = template == ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo;
        if (imageToVideo && string.IsNullOrWhiteSpace(slots.StartImageName))
        {
            throw new ArgumentException("The image-to-video template requires an uploaded start image name.", nameof(slots));
        }

        var latentInputs = new JsonObject
        {
            ["width"] = slots.Width,
            ["height"] = slots.Height,
            ["length"] = slots.LengthFrames,
            ["batch_size"] = 1,
            ["vae"] = Link("3", 0)
        };
        if (imageToVideo)
        {
            latentInputs["start_image"] = Link("10", 0);
        }

        var graph = new JsonObject
        {
            ["1"] = Node("UNETLoader", new JsonObject { ["unet_name"] = DiffusionModelFile, ["weight_dtype"] = "default" }),
            ["2"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = TextEncoderFile, ["type"] = "wan", ["device"] = "default" }),
            ["3"] = Node("VAELoader", new JsonObject { ["vae_name"] = VaeFile }),
            ["4"] = Node("CLIPTextEncode", new JsonObject { ["text"] = slots.Prompt, ["clip"] = Link("2", 0) }),
            ["5"] = Node("CLIPTextEncode", new JsonObject { ["text"] = NegativePrompt, ["clip"] = Link("2", 0) }),
            ["6"] = Node("Wan22ImageToVideoLatent", latentInputs),
            ["7"] = Node("ModelSamplingSD3", new JsonObject { ["shift"] = 8.0, ["model"] = Link("1", 0) }),
            ["8"] = Node("KSampler", new JsonObject
            {
                ["seed"] = slots.Seed,
                ["steps"] = 20,
                ["cfg"] = 5.0,
                ["sampler_name"] = "uni_pc",
                ["scheduler"] = "simple",
                ["denoise"] = 1.0,
                ["model"] = Link("7", 0),
                ["positive"] = Link("4", 0),
                ["negative"] = Link("5", 0),
                ["latent_image"] = Link("6", 0)
            }),
            ["9"] = Node("VAEDecode", new JsonObject { ["samples"] = Link("8", 0), ["vae"] = Link("3", 0) }),
            ["11"] = Node("CreateVideo", new JsonObject { ["images"] = Link("9", 0), ["fps"] = slots.Fps }),
            ["12"] = Node("SaveVideo", new JsonObject
            {
                ["video"] = Link("11", 0),
                ["filename_prefix"] = "reelbolt/" + SafePrefix(slots.FilenamePrefix),
                ["format"] = "mp4",
                ["codec"] = "h264"
            })
        };

        if (imageToVideo)
        {
            graph["10"] = Node("LoadImage", new JsonObject { ["image"] = slots.StartImageName });
        }

        return graph;
    }

    private static JsonObject Node(string classType, JsonObject inputs) =>
        new() { ["class_type"] = classType, ["inputs"] = inputs };

    private static JsonArray Link(string nodeId, int output) => new(nodeId, output);

    /// <summary>Keeps a filename prefix to <c>[A-Za-z0-9_-]</c>: never a path separator, never "..".</summary>
    internal static string SafePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "clip";
        }

        var chars = prefix.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-').Take(64).ToArray();
        return chars.Length == 0 ? "clip" : new string(chars);
    }
}
