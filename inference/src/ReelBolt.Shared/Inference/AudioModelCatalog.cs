using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// One audio endpoint (fal) or model (ElevenLabs) the platform can call, with every field its request may carry
/// and its price. Transcribed from research/audio-vendors.md (fal OpenAPI and llms.txt, the ElevenLabs OpenAPI
/// and pricing page, all read 2026-10-17); <c>docs/fal-ai-models.md</c> and <c>docs/elevenlabs-provider.md</c>
/// carry the same table. A field the schema does not declare is null here, and the client REFUSES a request
/// that sets it.
/// </summary>
public sealed record AudioEndpoint
{
    /// <summary>The provider kind that serves it.</summary>
    public required InferenceProviderKind ProviderKind { get; init; }

    /// <summary>fal: the endpoint id (its queue path). ElevenLabs: the <c>model_id</c> sent in the body.</summary>
    public required string Id { get; init; }

    /// <summary>Music or sound effect.</summary>
    public required AudioGenerationKind Kind { get; init; }

    /// <summary>A short human label.</summary>
    public required string Title { get; init; }

    /// <summary>The one endpoint the kind uses for <see cref="Kind"/> unless a row or step names another.</summary>
    public bool IsDefault { get; init; }

    /// <summary>ElevenLabs only: the request path (<c>/v1/music</c>, <c>/v1/sound-generation</c>). Null on fal.</summary>
    public string? Path { get; init; }

    /// <summary>The prompt field (<c>prompt</c> or <c>text</c>).</summary>
    public required string PromptField { get; init; }

    /// <summary>Longest prompt the schema allows, or null when it states none.</summary>
    public int? PromptMaxLength { get; init; }

    /// <summary>The duration field, or null when the endpoint has none.</summary>
    public required string DurationField { get; init; }

    /// <summary>The duration is sent in milliseconds (<c>music_length_ms</c>) rather than seconds.</summary>
    public bool DurationInMilliseconds { get; init; }

    /// <summary>Shortest duration the platform requests, in seconds.</summary>
    public required double MinDurationSec { get; init; }

    /// <summary>
    /// Longest duration the platform requests, in seconds. For music this is the platform's 300 s cap, not the
    /// schema's 600000 ms: the vendor docs say five minutes and which one is enforced is unconfirmed.
    /// </summary>
    public required double MaxDurationSec { get; init; }

    /// <summary>A duration must be sent (prompt-mode music, so the take can be priced).</summary>
    public bool DurationRequired { get; init; }

    /// <summary><c>force_instrumental</c>, or null.</summary>
    public string? InstrumentalField { get; init; }

    /// <summary><c>loop</c>, or null.</summary>
    public string? LoopField { get; init; }

    /// <summary><c>prompt_influence</c> (0-1), or null.</summary>
    public string? PromptInfluenceField { get; init; }

    /// <summary>Which composition-plan schema the endpoint takes (<c>composition_plan</c>).</summary>
    public AudioPlanShape PlanShape { get; init; } = AudioPlanShape.None;

    /// <summary>The endpoint takes an <c>output_format</c> (fal: body field; ElevenLabs: query parameter).</summary>
    public bool HasOutputFormat { get; init; }

    /// <summary>The file type the endpoint returns when it has no <c>output_format</c> (MMAudio: FLAC).</summary>
    public (string MimeType, string Extension)? FixedFileType { get; init; }

    /// <summary>The usage kind metered.</summary>
    public required UsageKind Unit { get; init; }

    /// <summary>USD per <see cref="Unit"/>, the vendor list price. The same figure ModelPriceCatalog.Audio cites.</summary>
    public required decimal UsdPerUnit { get; init; }

    /// <summary>How a duration becomes billed units.</summary>
    public required AudioBillingRounding Rounding { get; init; }

    /// <summary>The vendor's own pricing sentence and where it was read.</summary>
    public required string PriceBasis { get; init; }

    /// <summary>The billing unit is ambiguous on the vendor page and must be measured with a real key.</summary>
    public bool PriceMustBeMeasured { get; init; }

    /// <summary>The pricing view a budget gate reads.</summary>
    public AudioGenerationCapabilities Capabilities => new(
        Id, Kind, Unit, UsdPerUnit, Rounding, MinDurationSec, MaxDurationSec, PriceBasis, PriceMustBeMeasured);
}

/// <summary>
/// The music and sound-effect endpoints of both routes: fal's hosted ElevenLabs Music, ElevenLabs
/// SFX v2 and MMAudio, and the direct ElevenLabs models. One default per (kind, job).
/// </summary>
/// <remarks>
/// <b>Deliberately absent.</b> <c>fal-ai/stable-audio-25/text-to-audio</c> ($0.2 flat per request with a 190 s
/// default: a per-duration card cannot express it), <c>fal-ai/stable-audio</c> (page says "$0 per compute seconds",
/// unpriced, so never offered), fal's SFX v1 (superseded by v2 at the same price) and ElevenLabs <c>music_v1</c>
/// (deprecated, and still the API default, which is why <c>model_id</c> is always sent).
/// </remarks>
public sealed class AudioModelCatalog
{
    private const string FalRead = "published vendor price, fal.ai/models/{0}/llms.txt, read 2026-10-17: ";
    private const string ElevenLabsRead = "published vendor price, elevenlabs.io/pricing/api, read 2026-10-17: ";

    private readonly Dictionary<(InferenceProviderKind, string), AudioEndpoint> _byId;

    private AudioModelCatalog()
    {
        All = Build();
        _byId = All.ToDictionary(e => (e.ProviderKind, e.Id));
    }

    /// <summary>The shared instance.</summary>
    public static AudioModelCatalog Instance { get; } = new();

    /// <summary>Every endpoint, defaults first within a (kind, job).</summary>
    public IReadOnlyList<AudioEndpoint> All { get; }

    /// <summary>The capability an audio job is served under.</summary>
    public static InferenceProviderCapability CapabilityOf(AudioGenerationKind kind) => kind switch
    {
        AudioGenerationKind.Music => InferenceProviderCapability.MusicGeneration,
        AudioGenerationKind.SoundEffect => InferenceProviderCapability.SoundEffectGeneration,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The audio job of a capability, or null when it is not an audio capability.</summary>
    public static AudioGenerationKind? KindOf(InferenceProviderCapability capability) => capability switch
    {
        InferenceProviderCapability.MusicGeneration => AudioGenerationKind.Music,
        InferenceProviderCapability.SoundEffectGeneration => AudioGenerationKind.SoundEffect,
        _ => null,
    };

    /// <summary>The endpoint with this id for this kind, or null when none was read.</summary>
    public AudioEndpoint? Find(InferenceProviderKind kind, string? id) =>
        id is not null && _byId.TryGetValue((kind, id.Trim()), out AudioEndpoint? endpoint) ? endpoint : null;

    /// <summary>The default endpoint of a kind for a job.</summary>
    public AudioEndpoint DefaultFor(InferenceProviderKind kind, AudioGenerationKind job) =>
        All.Single(e => e.ProviderKind == kind && e.Kind == job && e.IsDefault);

    /// <summary>The ids of a kind's endpoints for a job, for a refusal an operator can act on.</summary>
    public string IdsFor(InferenceProviderKind kind, AudioGenerationKind job) =>
        string.Join(", ", All.Where(e => e.ProviderKind == kind && e.Kind == job).Select(e => e.Id));

    /// <summary>
    /// Resolves a request's endpoint: blank = the default for <paramref name="job"/>; otherwise it must be catalogued
    /// for this kind AND this job.
    /// </summary>
    /// <exception cref="NotSupportedException">The model is not catalogued for the kind and job.</exception>
    public AudioEndpoint Require(InferenceProviderKind kind, AudioGenerationKind job, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return DefaultFor(kind, job);
        return ModelProblem(kind, job, model) is { } problem ? throw new NotSupportedException(problem) : Find(kind, model)!;
    }

    /// <summary>
    /// Why <paramref name="model"/> cannot serve <paramref name="job"/> on <paramref name="kind"/>, or null when it can
    /// (a blank model is the job's default). The admin boundary and the clients share this sentence.
    /// </summary>
    public string? ModelProblem(InferenceProviderKind kind, AudioGenerationKind job, string? model)
    {
        string id = model?.Trim() ?? string.Empty;
        if (id.Length == 0) return null;

        AudioEndpoint? endpoint = Find(kind, id);
        if (endpoint is null)
        {
            return "'" + id + "' is not a " + kind + " " + Describe(job) + " model this platform has a read schema and a " +
                   "price for, so it will not be sent a request. Use one of: " + IdsFor(kind, job) + ".";
        }

        if (endpoint.Kind != job)
        {
            return "'" + id + "' generates " + Describe(endpoint.Kind) + ", not " + Describe(job) + ". Use one of: " +
                   IdsFor(kind, job) + ".";
        }

        return null;
    }

    private static string Describe(AudioGenerationKind job) => job == AudioGenerationKind.Music ? "music" : "sound effects";

    private static IReadOnlyList<AudioEndpoint> Build() =>
    [
        // ================================================================ fal.ai (queue.fal.run)
        new()
        {
            ProviderKind = InferenceProviderKind.FalAi, Id = "fal-ai/elevenlabs/music", Kind = AudioGenerationKind.Music,
            IsDefault = true, Title = "ElevenLabs Music (via fal)",
            PromptField = "prompt", PromptMaxLength = 4100,
            DurationField = "music_length_ms", DurationInMilliseconds = true, DurationRequired = true,
            MinDurationSec = 3, MaxDurationSec = 300,
            InstrumentalField = "force_instrumental", PlanShape = AudioPlanShape.Sections, HasOutputFormat = true,
            Unit = UsageKind.MusicGenMinutes, UsdPerUnit = 0.60m, Rounding = AudioBillingRounding.UpToWholeMinute,
            PriceBasis = string.Format(FalRead, "fal-ai/elevenlabs/music") +
                         "\"$0.6 per output audio minute, rounded up to the next minute\" (30 s bills as 1 min)",
        },
        new()
        {
            ProviderKind = InferenceProviderKind.FalAi, Id = "fal-ai/elevenlabs/sound-effects/v2",
            Kind = AudioGenerationKind.SoundEffect, IsDefault = true, Title = "ElevenLabs Sound Effects v2 (via fal)",
            PromptField = "text", PromptMaxLength = 450,
            DurationField = "duration_seconds", MinDurationSec = 0.5, MaxDurationSec = 22,
            LoopField = "loop", PromptInfluenceField = "prompt_influence", HasOutputFormat = true,
            Unit = UsageKind.SoundEffectGenSeconds, UsdPerUnit = 0.002m, Rounding = AudioBillingRounding.UpToWholeSecond,
            PriceBasis = string.Format(FalRead, "fal-ai/elevenlabs/sound-effects/v2") +
                         "\"$0.002 per seconds\"; whether requested or delivered seconds are billed, and what an unset " +
                         "duration bills, is unconfirmed (each started second is the card; an unset duration is " +
                         "estimated at the 22 s maximum)",
        },
        new()
        {
            ProviderKind = InferenceProviderKind.FalAi, Id = "fal-ai/mmaudio-v2/text-to-audio",
            Kind = AudioGenerationKind.SoundEffect, Title = "MMAudio v2 text-to-audio (FLAC, Foley-oriented)",
            PromptField = "prompt",
            DurationField = "duration", MinDurationSec = 1, MaxDurationSec = 30,
            FixedFileType = ("audio/flac", ".flac"),
            Unit = UsageKind.SoundEffectGenSeconds, UsdPerUnit = 0.001m, Rounding = AudioBillingRounding.UpToWholeSecond,
            PriceBasis = string.Format(FalRead, "fal-ai/mmaudio-v2/text-to-audio") + "\"$0.001 per second\"",
        },

        // ================================================================ ElevenLabs direct (api.elevenlabs.io)
        new()
        {
            ProviderKind = InferenceProviderKind.ElevenLabs, Id = "music_v2_5", Kind = AudioGenerationKind.Music,
            IsDefault = true, Title = "Eleven Music v2.5", Path = "/v1/music",
            PromptField = "prompt", PromptMaxLength = 4100,
            DurationField = "music_length_ms", DurationInMilliseconds = true, DurationRequired = true,
            MinDurationSec = 3, MaxDurationSec = 300,
            InstrumentalField = "force_instrumental", PlanShape = AudioPlanShape.Chunks, HasOutputFormat = true,
            Unit = UsageKind.MusicGenMinutes, UsdPerUnit = 0.15m, Rounding = AudioBillingRounding.UpToWholeMinute,
            PriceMustBeMeasured = true,
            PriceBasis = ElevenLabsMusicBasis,
        },
        new()
        {
            ProviderKind = InferenceProviderKind.ElevenLabs, Id = "music_v2", Kind = AudioGenerationKind.Music,
            Title = "Eleven Music v2", Path = "/v1/music",
            PromptField = "prompt", PromptMaxLength = 4100,
            DurationField = "music_length_ms", DurationInMilliseconds = true, DurationRequired = true,
            MinDurationSec = 3, MaxDurationSec = 300,
            InstrumentalField = "force_instrumental", PlanShape = AudioPlanShape.Chunks, HasOutputFormat = true,
            Unit = UsageKind.MusicGenMinutes, UsdPerUnit = 0.15m, Rounding = AudioBillingRounding.UpToWholeMinute,
            PriceMustBeMeasured = true,
            PriceBasis = ElevenLabsMusicBasis,
        },
        new()
        {
            ProviderKind = InferenceProviderKind.ElevenLabs, Id = "eleven_text_to_sound_v2",
            Kind = AudioGenerationKind.SoundEffect, IsDefault = true, Title = "Eleven Sound Effects v2",
            Path = "/v1/sound-generation",
            PromptField = "text",
            DurationField = "duration_seconds", MinDurationSec = 0.5, MaxDurationSec = 30,
            LoopField = "loop", PromptInfluenceField = "prompt_influence", HasOutputFormat = true,
            Unit = UsageKind.SoundEffectGenSeconds, UsdPerUnit = 0.002m, Rounding = AudioBillingRounding.UpToWholeSecond,
            PriceMustBeMeasured = true,
            PriceBasis = ElevenLabsRead + "Sound Effects \"$0.12 per minute\" = $0.002 per second; MUST MEASURE: the same " +
                         "page's FAQ says \"Music and Sound Effects are billed per generation\", so the unit is ambiguous",
        },
    ];

    private const string ElevenLabsMusicBasis =
        ElevenLabsRead + "Eleven Music \"$0.15 per minute\", each started minute as the card (the conservative reading); " +
        "MUST MEASURE: the same page's FAQ says \"Music and Sound Effects are billed per generation\", so the unit is " +
        "ambiguous";
}
