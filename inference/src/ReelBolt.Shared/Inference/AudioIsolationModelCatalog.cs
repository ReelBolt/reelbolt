using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// One hosted voice-isolation endpoint the platform can call, with the request fields its schema declares and its
/// price. Transcribed from fal's public OpenAPI document and <c>llms.txt</c> page for the endpoint, read 2026-11-02;
/// <c>docs/fal-ai-models.md</c> ("Audio isolation") carries the same table and the list of what must still be
/// measured with a real key. A field the schema does not declare is null here and the client REFUSES a request that
/// sets it.
/// </summary>
public sealed record AudioIsolationEndpoint
{
    /// <summary>The provider kind that serves it (always <see cref="InferenceProviderKind.FalAi"/>).</summary>
    public InferenceProviderKind ProviderKind { get; init; } = InferenceProviderKind.FalAi;

    /// <summary>The fal endpoint id (its queue path).</summary>
    public required string Id { get; init; }

    /// <summary>A short human label.</summary>
    public required string Title { get; init; }

    /// <summary>True for the one endpoint used when a row or request names none.</summary>
    public bool IsDefault { get; init; }

    /// <summary>The field that carries the source audio URL.</summary>
    public required string AudioField { get; init; }

    /// <summary>The output property that carries the voice stem (an object with a <c>url</c>).</summary>
    public required string VoiceOutputField { get; init; }

    /// <summary>The output property that carries a background stem, or null when the endpoint returns none.</summary>
    public string? BackgroundOutputField { get; init; }

    /// <summary>The usage kind metered (<see cref="UsageKind.AudioCleanupMinutes"/>).</summary>
    public UsageKind Unit { get; init; } = UsageKind.AudioCleanupMinutes;

    /// <summary>The vendor list price of one billed minute of source audio.</summary>
    public required decimal UsdPerMinute { get; init; }

    /// <summary>Where the price was read, in the vendor's own words.</summary>
    public required string PriceBasis { get; init; }

    /// <summary>True when the billing unit or rounding is unconfirmed and must be measured with a real key.</summary>
    public bool PriceMustBeMeasured { get; init; } = true;

    /// <summary>Whether the endpoint returns a separate background stem.</summary>
    public bool HasBackgroundStem => BackgroundOutputField is not null;
}

/// <summary>
/// The read catalogue of hosted audio-isolation endpoints. A model that is not here has no read schema and no price,
/// so it is unofferable (fail closed, never a default price). The default is the owner-approved choice: the
/// ElevenLabs-based isolator hosted ON FAL. The <see cref="InferenceProviderKind.ElevenLabs"/> kind itself stays
/// dormant; nothing here calls ElevenLabs directly.
/// </summary>
public sealed class AudioIsolationModelCatalog
{
    private const string FalRead =
        "published vendor price, fal.ai/models/fal-ai/elevenlabs/audio-isolation/llms.txt, read 2026-11-02: ";

    private static readonly AudioIsolationEndpoint[] Endpoints =
    [
        new()
        {
            Id = "fal-ai/elevenlabs/audio-isolation",
            Title = "ElevenLabs Audio Isolation (on fal)",
            IsDefault = true,
            AudioField = "audio_url",
            VoiceOutputField = "audio",
            BackgroundOutputField = null,
            UsdPerMinute = 0.10m,
            PriceBasis = FalRead + "\"$0.1 per minutes\"; whether fal rounds a started minute up, bills the input or " +
                         "the output length, and any minimum charge are unconfirmed",
            PriceMustBeMeasured = true,
        },
    ];

    /// <summary>The process-wide catalogue.</summary>
    public static AudioIsolationModelCatalog Instance { get; } = new();

    private readonly Dictionary<string, AudioIsolationEndpoint> _byId =
        Endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);

    /// <summary>Every catalogued endpoint.</summary>
    public IReadOnlyList<AudioIsolationEndpoint> All => Endpoints;

    /// <summary>The endpoint used when none is named.</summary>
    public AudioIsolationEndpoint Default => Endpoints.Single(e => e.IsDefault);

    /// <summary>The endpoint with this id, or null when none was read. A blank id is not the default here.</summary>
    public AudioIsolationEndpoint? Find(InferenceProviderKind kind, string? id) =>
        kind == InferenceProviderKind.FalAi && id is not null && _byId.TryGetValue(id.Trim(), out AudioIsolationEndpoint? e) ? e : null;

    /// <summary>The endpoint ids, comma-separated, for an error message.</summary>
    public string Ids => string.Join(", ", Endpoints.Select(e => e.Id));

    /// <summary>The catalogued endpoint for a request model: blank = the default, unknown = null.</summary>
    public AudioIsolationEndpoint? Resolve(string? model) =>
        string.IsNullOrWhiteSpace(model) ? Default : Find(InferenceProviderKind.FalAi, model);
}
