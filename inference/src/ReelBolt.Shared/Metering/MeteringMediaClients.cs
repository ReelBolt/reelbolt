using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

// The non-chat model clients (B3c). Each factory wraps what it hands out, exactly as ChatClientFactory does for
// chat: the factory is the choke point, so no call site has to remember to meter. Attribution comes from the
// ambient UsageScope; a call outside any scope lands on the platform org as Unattributed.

/// <summary>
/// Meters embedding calls in <see cref="UsageKind.EmbeddingTokens"/>. Uses the provider's own reported input
/// token count; a backend that reports none (several self-hosted servers) is estimated at four characters per
/// token rather than recorded as free, because "never silently free" is the rule for every metered unit.
/// </summary>
public sealed class MeteringEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ResolvedEmbeddingProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        ResolvedEmbeddingProvider provider,
        IUsageRecorder recorder,
        ILogger? logger = null) : base(inner)
    {
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialised once: the estimate below must see the same values the inner generator did.
        IReadOnlyList<string> inputs = values as IReadOnlyList<string> ?? values.ToList();
        GeneratedEmbeddings<Embedding<float>> result = await base.GenerateAsync(inputs, options, cancellationToken);

        try
        {
            long tokens = result.Usage?.InputTokenCount
                ?? result.Usage?.TotalTokenCount
                ?? EstimateTokens(inputs);
            await _recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.EmbeddingTokens, tokens, _provider.ProviderId, _provider.Kind, _provider.ModelName,
                MeteringChatClient.BillingModeOf(_provider.OrganizationId),
                Capability: InferenceProviderCapability.Embedding, AccountId: _provider.AccountId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} embedding call failed", _provider.Kind, _provider.ModelName);
        }

        return result;
    }

    private static long EstimateTokens(IReadOnlyList<string> inputs)
    {
        long chars = 0;
        foreach (string s in inputs) chars += s.Length;
        return (chars + 3) / 4;
    }
}

/// <summary>Meters transcription in <see cref="UsageKind.AsrAudioSeconds"/>, read from the WAV header the platform produced.</summary>
public sealed class MeteringTranscriptionClient : ITranscriptionClient
{
    private readonly ITranscriptionClient _inner;
    private readonly ResolvedTranscriptionProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringTranscriptionClient(
        ITranscriptionClient inner, ResolvedTranscriptionProvider provider, IUsageRecorder recorder, ILogger? logger = null)
    {
        _inner = inner;
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<TranscriptResult> TranscribeAsync(
        Stream wav, string fileName, string? language, bool wordTimestamps, CancellationToken ct)
    {
        // Measured before the call: the inner client consumes the stream, and the header is the platform's own
        // fact about what it sent, not something the vendor reports back.
        bool measured = WavDuration.TryGetSeconds(wav, out double seconds);

        TranscriptResult result = await _inner.TranscribeAsync(wav, fileName, language, wordTimestamps, ct);

        try
        {
            if (!measured)
            {
                _logger?.LogWarning(
                    "Could not read the duration of {FileName} (not a seekable, well-formed WAV); transcription by {Kind}/{Model} was not metered",
                    fileName, _provider.Kind, _provider.ModelName);
            }
            else
            {
                await _recorder.RecordAsync(new UsageRecordRequest(
                    UsageKind.AsrAudioSeconds, (decimal)Math.Round(seconds, 3), _provider.ProviderId, _provider.Kind, _provider.ModelName,
                    MeteringChatClient.BillingModeOf(_provider.OrganizationId),
                    Capability: InferenceProviderCapability.Transcription, AccountId: _provider.AccountId), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} transcription failed", _provider.Kind, _provider.ModelName);
        }

        return result;
    }
}

/// <summary>
/// Meters image generation and editing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The unit is the image RETURNED, not the call.</b> Every vendor we integrate prices per image
/// produced (MiniMax publishes <c>$0.0035 per image</c> for <c>image-01</c>; BFL prices FLUX 3 Image
/// per image and FLUX.2 "from" a per-megapixel rate), so a single call that returns nine images is
/// nine units. Recording one unit per call would under-charge a batch by a factor of <c>n</c>, and
/// recording it before the response arrives would charge for images that were never produced.
/// </para>
/// <para>
/// <b>Generation and editing meter different kinds</b> (<see cref="UsageKind.ImageGenImages"/> and
/// <see cref="UsageKind.ImageEditImages"/>) because the vendors price them differently — BFL lists
/// FLUX.2 [pro] at "from $0.03" text-to-image against "from $0.045" for image editing — and one
/// shared unit would under-charge every edit by a third. The capability is what selects the kind,
/// not the method called, so a MiniMax <c>subject_reference</c> call made through
/// <c>GenerateAsync</c> still meters as generation, which is what the vendor bills it as.
/// </para>
/// <para>
/// A metering failure is logged and swallowed, never thrown: the image was already produced and
/// already billed to us, so losing the ledger row is a smaller problem than failing a step whose
/// expensive work has completed. Every other decorator in this file takes the same posture.
/// </para>
/// </remarks>
public sealed class MeteringImageClient : IImageClient
{
    // The parameter types below are fully qualified on purpose: this file imports
    // Microsoft.Extensions.AI for the embedding types, and that namespace ships its OWN
    // ImageGenerationRequest, so the bare name is ambiguous here. Fully qualifying at this one
    // call site is cheaper than renaming ours to something nobody would recognise.
    private readonly IImageClient _inner;
    private readonly ResolvedImageProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringImageClient(
        IImageClient inner, ResolvedImageProvider provider, IUsageRecorder recorder, ILogger? logger = null)
    {
        _inner = inner;
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<GeneratedImageSet> GenerateAsync(
        ReelBolt.Shared.Inference.ImageGenerationRequest request, CancellationToken ct)
    {
        GeneratedImageSet result = await _inner.GenerateAsync(request, ct);
        await RecordAsync(UsageKind.ImageGenImages, result, request.Params,
            editsASource: false, string.IsNullOrWhiteSpace(request.ReferenceImageUrl) ? 0 : 1, null,
            new SizeHints(request.ExpectedOutputPixels, null), ct);

        return result;
    }

    public async Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct)
    {
        GeneratedImageSet result = await _inner.EditAsync(request, ct);
        await RecordAsync(UsageKind.ImageEditImages, result, request.Params,
            editsASource: true, request.ReferenceImageUrls?.Count ?? 0, request.ReferenceImagePixels,
            new SizeHints(request.ExpectedOutputPixels, null), ct);

        return result;
    }

    public async Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct)
    {
        GeneratedImageSet result = await _inner.InpaintAsync(request, ct);
        await RecordAsync(UsageKind.ImageEditImages, result, request.Params, editsASource: true, 0, null,
            new SizeHints(request.ExpectedOutputPixels, null), ct);

        return result;
    }

    public async Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct)
    {
        GeneratedImageSet result = await _inner.TransformAsync(request, ct);
        long? input = request.Expand is { ImageWidth: > 0, ImageHeight: > 0 } g ? (long)g.ImageWidth * g.ImageHeight : null;
        await RecordAsync(UsageKind.ImageEditImages, result, request.Params, editsASource: true, 0, null,
            new SizeHints(request.ExpectedOutputPixels, input), ct);

        return result;
    }

    public Task<ImageProbeResult> ProbeAsync(CancellationToken ct) => _inner.ProbeAsync(ct);

    public ImageModelCapabilities Describe(string model) => _inner.Describe(model);

    /// <summary>
    /// The model a call is billed under, and its price variant: the model that RAN
    /// (<see cref="GeneratedImageSet.ResolvedModel"/>, set by a client that chooses its endpoint per call), else the
    /// capability's own model - exactly what every client that predates per-call endpoints has always been billed
    /// under. A fal.ai row serves many endpoints, sends an empty model and lets the client choose per operation, so
    /// the row's model name is only the probe target and recording it would price every operation as if it were that
    /// one. A client whose price also depends on a tier (<see cref="IBillingModelKeyed"/>) keys the call
    /// <c>code#tier</c>; the code is the model and the tier is the price variant (docs/design/model-catalog.md §2.2).
    /// </summary>
    private (string Model, string? Variant) BillingModel(
        string? resolvedModel, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters)
    {
        string model = !string.IsNullOrWhiteSpace(resolvedModel) ? resolvedModel : _provider.ModelName;
        return SplitBillingKey(_inner is IBillingModelKeyed keyed ? keyed.BillingModelKey(model, parameters) : model);
    }

    /// <summary>Splits a <c>code#tier</c> billing key into the model code and the tier (null when there is none).</summary>
    public static (string Model, string? Variant) SplitBillingKey(string key)
    {
        int hash = key.IndexOf('#');
        if (hash < 0) return (key, null);
        string tier = key[(hash + 1)..];
        return (key[..hash], tier.Length == 0 ? null : tier);
    }

    /// <summary>
    /// The <c>{n}MP</c> price variant of the largest picture in <paramref name="result"/> (<see cref="OutputSizePricing.MeasuredPixels"/>):
    /// its pixels in whole megapixels, rounded UP. Null when any picture's size is unknown.
    /// </summary>
    public static string? MegapixelVariant(GeneratedImageSet result) =>
        OutputSizePricing.MeasuredPixels(result.Images) is { } pixels ? OutputSizePricing.MegapixelVariant(pixels) : null;

    /// <summary>What the caller knew before the call: the output size it expected, and the input picture's size.</summary>
    private readonly record struct SizeHints(long? ExpectedOutputPixels, long? InputPixels);

    /// <summary>
    /// The price variant a size-priced call is billed at (T-013 F3: the vendor answered, so this never throws and never
    /// bills 0): the measured output, clamped to <see cref="OutputSizePricing.BilledOutputCeiling"/> (rework 3), plus
    /// each measured reference; for an endpoint that bills its input (outpaint) max(input, output) instead
    /// (<see cref="OutputSizePricing.InputAndOutputVariant"/>, the input taken at the output's size when not stated: an
    /// outpaint's input is never larger than its output); else the documented output
    /// ceiling the same way; else none, which is the variant-less row the seed sets at or above every sized row. A caller
    /// that sized a hold settles at no less than it (EditImageOperationsController).
    /// </summary>
    private string? SizeVariant(string model, GeneratedImageSet result, int referenceImages,
        IReadOnlyList<long>? referencePixels, SizeHints hints)
    {
        if (referenceImages > 0 && (referencePixels is null || referencePixels.Count != referenceImages))
        {
            _logger?.LogWarning(
                "The {Model} image call sent {Count} reference picture(s) of unknown size; metered at its variant-less row",
                model, referenceImages);
            return null;
        }

        long? output = OutputSizePricing.MeasuredPixels(result.Images);
        if (output is { } measured
            && OutputSizePricing.BilledOutputCeiling(_provider.Kind, model, hints.ExpectedOutputPixels) is { } ceiling
            && measured > ceiling)
        {
            _logger?.LogWarning(
                "The {Model} image call returned {Measured} pixels, past the {Ceiling} it can plausibly deliver; clamped to {Ceiling}",
                model, measured, ceiling, ceiling);
            output = ceiling;
        }

        if (output is null && OutputSizePricing.MaxOutputPixels(_provider.Kind, model) is { } documented and > 0)
        {
            _logger?.LogWarning(
                "The {Model} image call returned a picture of unknown size; metered at its documented {Ceiling}-pixel ceiling",
                model, documented);
            output = documented;
        }

        if (output is not { } pixels)
        {
            _logger?.LogWarning(
                "The {Model} image call returned a picture of unknown size and the model documents no output ceiling; " +
                "metered at its variant-less (largest) row",
                model);
            return null;
        }

        if (OutputSizePricing.BillsInputMegapixels(_provider.Kind, model))
            return OutputSizePricing.InputAndOutputVariant(pixels, hints.InputPixels is { } input and > 0 ? input : pixels);

        return OutputSizePricing.MegapixelVariant(pixels, referenceImages > 0 ? referencePixels : null);
    }

    private async Task RecordAsync(
        UsageKind kind,
        GeneratedImageSet result,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters,
        bool editsASource,
        int referenceImages,
        IReadOnlyList<long>? referencePixels,
        SizeHints hints,
        CancellationToken ct)
    {
        try
        {
            (string model, string? variant) = BillingModel(result.ResolvedModel, parameters);
            if (variant is null && _inner is IBillingModelKeyed keyed
                && keyed.PricesByOutputMegapixels(model, editsASource, referenceImages))
            {
                variant = SizeVariant(model, result, referenceImages, referencePixels, hints);
            }

            await _recorder.RecordAsync(new UsageRecordRequest(
                kind, result.Images.Count, _provider.ProviderId, _provider.Kind, model,
                MeteringChatClient.BillingModeOf(_provider.OrganizationId),
                Capability: _provider.Capability, Variant: variant, AccountId: _provider.AccountId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} image call failed", _provider.Kind, _provider.ModelName);
        }
    }
}

/// <summary>
/// Meters subject segmentation and depth estimation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The unit is the image RETURNED, not the call</b> — <see cref="UsageKind.SegmentationImages"/> for
/// a cut-out or a matte, <see cref="UsageKind.DepthImages"/> for a depth map. fal charges per
/// successful output and does not charge for queue time or a server error, so counting outputs is the
/// same thing as counting what will be billed, and counting a call that threw would over-charge.
/// </para>
/// <para>
/// A metering failure is logged and swallowed, never thrown: the vendor has already done the work and
/// already billed us, so losing the ledger row is a smaller problem than failing a step whose
/// expensive work completed. Every other decorator in this file takes the same posture.
/// </para>
/// </remarks>
public sealed class MeteringImageAnalysisClient : IImageAnalysisClient
{
    // The request types are fully qualified for the same reason MeteringImageClient's is: this file
    // imports Microsoft.Extensions.AI, which ships types with overlapping names.
    private readonly IImageAnalysisClient _inner;
    private readonly ResolvedImageAnalysisProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringImageAnalysisClient(
        IImageAnalysisClient inner, ResolvedImageAnalysisProvider provider, IUsageRecorder recorder, ILogger? logger = null)
    {
        _inner = inner;
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<SegmentationResult> SegmentAsync(
        ReelBolt.Shared.Inference.SegmentationRequest request, CancellationToken ct)
    {
        SegmentationResult result = await _inner.SegmentAsync(request, ct);
        await RecordAsync(UsageKind.SegmentationImages, result.ResolvedModel, ct);
        return result;
    }

    public async Task<DepthResult> EstimateDepthAsync(
        ReelBolt.Shared.Inference.DepthRequest request, CancellationToken ct)
    {
        DepthResult result = await _inner.EstimateDepthAsync(request, ct);
        await RecordAsync(UsageKind.DepthImages, result.ResolvedModel, ct);
        return result;
    }

    public Task<ImageAnalysisProbeResult> ProbeAsync(CancellationToken ct) => _inner.ProbeAsync(ct);

    public ImageAnalysisModelCapabilities Describe(string model) => _inner.Describe(model);

    private async Task RecordAsync(UsageKind kind, string? resolvedModel, CancellationToken ct)
    {
        try
        {
            // One unit per REQUEST, whatever it returned: fal bills a segmentation call once ($0.01 per
            // request for fal-ai/sam-3-1/image) even when it returns up to 32 masks. The model that RAN is
            // billed when the client reports one (a fal.ai row's own model is empty or only a probe target).
            string model = string.IsNullOrWhiteSpace(resolvedModel) ? _provider.ModelName : resolvedModel;
            await _recorder.RecordAsync(new UsageRecordRequest(
                kind, 1, _provider.ProviderId, _provider.Kind, model,
                MeteringChatClient.BillingModeOf(_provider.OrganizationId),
                Capability: _provider.Capability, AccountId: _provider.AccountId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} analysis call failed", _provider.Kind, _provider.ModelName);
        }
    }
}

/// <summary>
/// Meters <see cref="InferenceProviderCapability.VideoSegmentation"/> in
/// <see cref="UsageKind.VideoSegmentationFrames"/>: the frames of video INPUT the vendor bills for, not
/// the objects it returns and not the number of calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frames, rounded up to the vendor's 16-frame block.</b> fal's sentence is "$0.01 per 16 frames of
/// video input" and it rounds a request up, so the recorded quantity is
/// <see cref="VideoSegmentationBilling.BillableFrames"/>: 120 real frames record 128 frame-units and price
/// at $0.08. Recording the clip's real 120 against a per-frame card would under-charge every request
/// whose length is not a multiple of 16. The clip's own frame count stays visible in
/// <see cref="VideoSegmentationResult.InputFrames"/>.
/// </para>
/// <para>
/// <b>Nothing is recorded for a failed call.</b> The inner call is awaited first, so a throw (an unknown
/// status, a malformed response, a transport failure) records no row at all. fal does not bill a refused
/// or failed request, and a row would make the ledger disagree with the invoice.
/// </para>
/// <para>
/// A metering failure after a successful call is logged and swallowed, exactly like the other decorators
/// in this file: the vendor has already done the work and billed us, so failing the step now would lose
/// the output while keeping the cost.
/// </para>
/// </remarks>
public sealed class MeteringVideoSegmentationClient : IVideoSegmentationClient
{
    private readonly IVideoSegmentationClient _inner;
    private readonly ResolvedImageAnalysisProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringVideoSegmentationClient(
        IVideoSegmentationClient inner,
        ResolvedImageAnalysisProvider provider,
        IUsageRecorder recorder,
        ILogger? logger = null)
    {
        _inner = inner;
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<VideoSegmentationResult> SegmentVideoAsync(VideoSegmentationRequest request, CancellationToken ct)
    {
        VideoSegmentationResult result = await _inner.SegmentVideoAsync(request, ct);

        try
        {
            int frames = result.InputFrames ?? result.Frames.Count;
            int billableFrames = VideoSegmentationBilling.BillableFrames(frames);
            if (billableFrames <= 0)
            {
                _logger?.LogWarning(
                    "Video segmentation by {Kind}/{Model} reported no input frames; nothing was metered",
                    _provider.Kind, _provider.ModelName);
                return result;
            }

            // The model that RAN, which for a fal.ai row is the request's endpoint id rather than the
            // row's probe model.
            string model = string.IsNullOrWhiteSpace(result.Model) ? _provider.ModelName : result.Model;
            await _recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.VideoSegmentationFrames, billableFrames, _provider.ProviderId, _provider.Kind, model,
                MeteringChatClient.BillingModeOf(_provider.OrganizationId)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} video segmentation call failed", _provider.Kind, _provider.ModelName);
        }

        return result;
    }
}

/// <summary>Meters speech synthesis in <see cref="UsageKind.TtsInputBytes"/>: the UTF-8 length of the text sent.</summary>
public sealed class MeteringSpeechSynthesisClient : ISpeechSynthesisClient, IWrappedSpeechSynthesisClient
{
    private readonly ISpeechSynthesisClient _inner;
    private readonly ResolvedSpeechSynthesisProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringSpeechSynthesisClient(
        ISpeechSynthesisClient inner, ResolvedSpeechSynthesisProvider provider, IUsageRecorder recorder, ILogger? logger = null)
    {
        _inner = inner;
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    public ISpeechSynthesisClient Inner => _inner;

    public async Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        SynthesizedSpeech speech = await _inner.SynthesizeAsync(request, ct);

        try
        {
            // A per-request model override changes what the vendor bills, so it is the model on the row.
            string model = string.IsNullOrWhiteSpace(request.ModelOverride) ? _provider.ModelName : request.ModelOverride;
            await _recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.TtsInputBytes, Encoding.UTF8.GetByteCount(request.Text ?? string.Empty), _provider.ProviderId,
                _provider.Kind, model, MeteringChatClient.BillingModeOf(_provider.OrganizationId),
                Capability: InferenceProviderCapability.SpeechSynthesis, AccountId: _provider.AccountId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} synthesis call failed", _provider.Kind, _provider.ModelName);
        }

        return speech;
    }
}
