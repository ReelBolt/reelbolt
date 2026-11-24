using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// JSON configuration for a <c>StepType.AudioGenerate</c> workflow step (T-108), deserialised from
/// <c>WorkflowStep.AudioGenerateConfigJson</c>. Deterministic, non-LLM, PAID: generates <see cref="Takes"/> music
/// tracks or sound effects through a <c>MusicGeneration</c>/<c>SoundEffectGeneration</c> provider row
/// (<see cref="IAudioGenerationClient"/>), each budget-gated on the <c>external_generation_jobs</c> ledger, and
/// registers every take as an audio project file so a MusicSupervisor (<c>m{n}</c>) or SoundDesigner (<c>x{n}</c>)
/// is offered it. Positional members are append-only.
/// </summary>
/// <param name="Kind">Music or sound effect; picks the capability the provider is resolved under.</param>
/// <param name="Prompt">The text prompt, at most <see cref="MaxPromptLength"/> characters. Blank with a composition plan.</param>
/// <param name="DurationSec">
/// Seconds per take, inside the catalogue's range for the kind (music 3-300, sound effects 0.5-30; the chosen model's
/// own range is applied at run time). Must be 0 with a composition plan, whose sections set the length.
/// </param>
/// <param name="Instrumental">Music, prompt mode only: no vocals.</param>
/// <param name="CompositionPlanJson">Music only: the vendor's composition plan (validated against the model's schema at run time).</param>
/// <param name="Loop">Sound effect only, where the model supports it: a seamless loop.</param>
/// <param name="PromptInfluence">Sound effect only, 0-1, where the model supports it.</param>
/// <param name="ProviderId">The provider row to use; null = the default row of the kind's capability.</param>
/// <param name="Model">The model / endpoint id; null = the row's model, else the catalogue default for the kind.</param>
/// <param name="Takes">Independent takes, 1-4; each is one ledger row and one project file.</param>
/// <param name="MaxSpendCredits">Step spend limit in credits over every take; null = only the daily caps apply.</param>
/// <param name="RegisterProjectFile">Register each take as an audio project file (default true).</param>
/// <param name="FileNamePrefix">Project file name prefix (letters, digits, <c>-</c>, <c>_</c>; 1-40); null = <c>music</c>/<c>sfx</c>.</param>
[StepCard(
    "Generates music tracks or sound effects through a provider and saves each take as a project audio file.",
    ["A music or sound-effects provider"],
    "Generated audio tracks saved as project files",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCreditsAndAiShots,
    ReadableBy = ["VideoCompile", "The project's files"],
    PairsWith = ["VideoCompile: mixes the track in"])]
public sealed record AudioGenerateStepConfig(
    [property: Description("Music or SoundEffect.")]
    AudioGenerationKind Kind,
    [property: Description("What to generate, at most 4000 characters. Leave empty when CompositionPlanJson is set.")]
    [property: AppBindable(AppInputType.ShortText, AppInputType.LongText, Label = "What it sounds like", MaxLength = AudioGenerateStepConfig.MaxPromptLength)]
    string Prompt,
    [property: Description("Seconds per take: music 3-300, sound effects 0.5-30 (the chosen model's own range applies). 0 with a composition plan.")]
    [property: AppBindable(AppInputType.Duration, AppInputType.Number, Label = "Length", Min = 0.5, Max = 300)]
    double DurationSec,
    [property: Description("Music, prompt mode only: force an instrumental track.")]
    bool? Instrumental = null,
    [property: Description("Music only: the vendor's composition plan JSON, used instead of a prompt.")]
    string? CompositionPlanJson = null,
    [property: Description("Sound effects only, where the model supports it: generate a seamless loop.")]
    bool? Loop = null,
    [property: Description("Sound effects only, 0 to 1, where the model supports it: how closely to follow the prompt.")]
    double? PromptInfluence = null,
    [property: Description("Provider row id; null uses the default MusicGeneration or SoundEffectGeneration row.")]
    Guid? ProviderId = null,
    [property: Description("Model or endpoint id; null uses the provider row's model, else the catalogue default.")]
    string? Model = null,
    [property: Description("Independent takes, 1 to 4. Each is priced, reserved and registered on its own.")]
    [property: AppBindable(AppInputType.Number, AppInputType.Choice, Label = "Takes", Min = 1, Max = AudioGenerateStepConfig.MaxTakes)]
    int Takes = 1,
    [property: Description("Spend limit for the whole step in credits; null leaves only the daily caps.")]
    [property: AppBindable(AppInputType.Number, Label = "Most credits to spend", Min = 1, Max = 100000, SpendCap = true)]
    double? MaxSpendCredits = null,
    [property: Description("Register each take as an audio project file (offered to music and sound-effect steps).")]
    bool RegisterProjectFile = true,
    [property: Description("Project file name prefix (letters, digits, - and _); null uses music or sfx.")]
    string? FileNamePrefix = null)
{
    /// <summary>Longest prompt the step accepts (the catalogue's tightest music limit is 4100).</summary>
    public const int MaxPromptLength = 4000;

    /// <summary>Most takes one step may order.</summary>
    public const int MaxTakes = 4;

    /// <summary>Longest composition plan JSON accepted, a runaway guard well above a 30-section plan.</summary>
    public const int MaxPlanJsonLength = 64 * 1024;

    private const int MaxModelLength = 200;

    private static readonly Regex SafePrefix = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>One serialiser for run time and save time (Web defaults, enums as strings).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>True when the step sends a composition plan instead of a prompt.</summary>
    [JsonIgnore]
    public bool HasPlan => !string.IsNullOrWhiteSpace(CompositionPlanJson);

    /// <summary>The project file name prefix actually used.</summary>
    [JsonIgnore]
    public string EffectivePrefix => FileNamePrefix ?? (Kind == AudioGenerationKind.Music ? "music" : "sfx");

    /// <summary>
    /// The catalogue's widest duration range for <paramref name="kind"/> across every route, so a config valid for
    /// SOME model of the kind saves; the chosen model's own range is <see cref="ValidateFor"/>'s.
    /// </summary>
    public static (double Min, double Max) CatalogueRange(AudioGenerationKind kind)
    {
        AudioEndpoint[] endpoints = [.. AudioModelCatalog.Instance.All.Where(e => e.Kind == kind)];
        return endpoints.Length == 0 ? (0, 0) : (endpoints.Min(e => e.MinDurationSec), endpoints.Max(e => e.MaxDurationSec));
    }

    /// <summary>The model-independent rules; empty when the config is savable.</summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (!Enum.IsDefined(Kind))
        {
            errors.Add($"Kind must be one of: {string.Join(", ", Enum.GetNames<AudioGenerationKind>())}.");
            return errors;
        }

        bool music = Kind == AudioGenerationKind.Music;

        if (CompositionPlanJson is not null)
        {
            if (!music)
            {
                errors.Add("CompositionPlanJson is for music only.");
            }
            else
            {
                ValidatePlanMode(errors);
            }
        }
        else
        {
            ValidatePrompt(errors);
            (double min, double max) = CatalogueRange(Kind);
            if (!double.IsFinite(DurationSec) || DurationSec < min || DurationSec > max)
            {
                errors.Add($"DurationSec must be from {Num(min)} to {Num(max)} seconds for {Kind}; it is {Num(DurationSec)}.");
            }
        }

        if (music)
        {
            if (Loop is not null) errors.Add("Loop is for sound effects only.");
            if (PromptInfluence is not null) errors.Add("PromptInfluence is for sound effects only.");
        }
        else
        {
            if (Instrumental is not null) errors.Add("Instrumental is for music only.");
            if (PromptInfluence is { } influence && (double.IsNaN(influence) || influence < 0 || influence > 1))
            {
                errors.Add($"PromptInfluence must be from 0 to 1; it is {Num(influence)}.");
            }
        }

        if (Takes is < 1 or > MaxTakes)
        {
            errors.Add($"Takes must be from 1 to {MaxTakes}; it is {Takes}.");
        }

        if (MaxSpendCredits is { } cap && (!double.IsFinite(cap) || cap <= 0))
        {
            errors.Add("MaxSpendCredits must be a positive number of credits when set.");
        }

        if (ProviderId == Guid.Empty)
        {
            errors.Add("ProviderId must not be the empty id; leave it null to use the default provider.");
        }

        if (Model is not null)
        {
            string id = Model.Trim();
            if (id.Length == 0 || id.Length > MaxModelLength
                || !AudioModelCatalog.Instance.All.Any(e => e.Kind == Kind && e.Id == id))
            {
                errors.Add($"Model '{Bound(Model)}' is not a catalogued {Kind} model. Use one of: " +
                           string.Join(", ", AudioModelCatalog.Instance.All.Where(e => e.Kind == Kind).Select(e => e.Id).Distinct()) + ".");
            }
        }

        if (FileNamePrefix is not null && !SafePrefix.IsMatch(FileNamePrefix))
        {
            errors.Add("FileNamePrefix must be 1-40 letters, digits, '-' or '_'.");
        }

        return errors;
    }

    /// <summary>
    /// The rules of the one endpoint the step will call: its duration range, its prompt limit, the optional fields
    /// it declares, and (plan mode) its plan schema and the plan's total length. Run before any ledger row exists.
    /// </summary>
    public List<string> ValidateFor(AudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        List<string> errors = [];

        if (endpoint.Kind != Kind)
        {
            errors.Add($"Model '{endpoint.Id}' generates {endpoint.Kind}, not {Kind}.");
            return errors;
        }

        if (HasPlan)
        {
            if (endpoint.PlanShape == AudioPlanShape.None)
            {
                errors.Add($"Model '{endpoint.Id}' takes no composition plan.");
            }
            else
            {
                try
                {
                    (_, double total) = AudioCompositionPlan.Validate(CompositionPlanJson!, endpoint.PlanShape, endpoint.Id);
                    if (total < endpoint.MinDurationSec || total > endpoint.MaxDurationSec)
                    {
                        errors.Add($"The composition plan lasts {Num(total)} s; '{endpoint.Id}' takes " +
                                   $"{Num(endpoint.MinDurationSec)} to {Num(endpoint.MaxDurationSec)} s.");
                    }
                }
                catch (ArgumentException ex)
                {
                    errors.Add("CompositionPlanJson: " + ex.Message);
                }
            }
        }
        else
        {
            if (DurationSec < endpoint.MinDurationSec || DurationSec > endpoint.MaxDurationSec)
            {
                errors.Add($"DurationSec must be from {Num(endpoint.MinDurationSec)} to {Num(endpoint.MaxDurationSec)} " +
                           $"seconds for '{endpoint.Id}'; it is {Num(DurationSec)}.");
            }

            if (endpoint.PromptMaxLength is { } max && (Prompt?.Length ?? 0) > max)
            {
                errors.Add($"Prompt must be at most {max} characters for '{endpoint.Id}'.");
            }
        }

        if (Instrumental is not null && endpoint.InstrumentalField is null)
            errors.Add($"Instrumental is not supported by '{endpoint.Id}'.");
        if (Loop is not null && endpoint.LoopField is null)
            errors.Add($"Loop is not supported by '{endpoint.Id}'.");
        if (PromptInfluence is not null && endpoint.PromptInfluenceField is null)
            errors.Add($"PromptInfluence is not supported by '{endpoint.Id}'.");

        return errors;
    }

    /// <summary>
    /// The model the step calls on a <paramref name="providerKind"/> row whose own model is
    /// <paramref name="rowModel"/>: the step's <see cref="Model"/>, else the row's model when it serves this kind,
    /// else the catalogue default. Null when the provider kind serves no audio of this kind.
    /// </summary>
    public string? ResolveModel(InferenceProviderKind providerKind, string? rowModel)
    {
        AudioModelCatalog catalog = AudioModelCatalog.Instance;
        if (!string.IsNullOrWhiteSpace(Model)) return Model.Trim();
        if (!string.IsNullOrWhiteSpace(rowModel) && catalog.Find(providerKind, rowModel)?.Kind == Kind) return rowModel.Trim();
        return catalog.All.FirstOrDefault(e => e.ProviderKind == providerKind && e.Kind == Kind && e.IsDefault)?.Id;
    }

    /// <summary>The seconds one take is priced at: the plan's total in plan mode, else <see cref="DurationSec"/>.</summary>
    /// <exception cref="ArgumentException">The plan breaks the endpoint's schema.</exception>
    public double PricedDurationSec(AudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return HasPlan
            ? AudioCompositionPlan.Validate(CompositionPlanJson!, endpoint.PlanShape, endpoint.Id).TotalSec
            : DurationSec;
    }

    /// <summary>The vendor request for one take on <paramref name="model"/>.</summary>
    public AudioGenerationRequest ToRequest(string model) => HasPlan
        ? new AudioGenerationRequest(Kind, null, null, model, CompositionPlanJson: CompositionPlanJson)
        : new AudioGenerationRequest(Kind, Prompt, DurationSec, model, Instrumental, null, Loop, PromptInfluence);

    private void ValidatePlanMode(List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(Prompt))
            errors.Add("Prompt must be empty when CompositionPlanJson is set (the vendor takes one or the other).");
        if (DurationSec != 0)
            errors.Add("DurationSec must be 0 when CompositionPlanJson is set: the plan's sections set the length.");
        if (Instrumental is not null)
            errors.Add("Instrumental applies to prompt mode only, not with a composition plan.");

        if (CompositionPlanJson!.Length > MaxPlanJsonLength)
        {
            errors.Add($"CompositionPlanJson must be at most {MaxPlanJsonLength} characters.");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(CompositionPlanJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                errors.Add("CompositionPlanJson must be a JSON object.");
        }
        catch (JsonException ex)
        {
            errors.Add("CompositionPlanJson is not valid JSON: " + ex.Message);
        }
    }

    private void ValidatePrompt(List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            errors.Add("Prompt is required (or a composition plan, for music).");
        }
        else if (Prompt.Length > MaxPromptLength)
        {
            errors.Add($"Prompt must be at most {MaxPromptLength} characters; it is {Prompt.Length}.");
        }
        else if (Prompt.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            errors.Add("Prompt must not contain control characters.");
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Bound(string value) => value.Length <= 60 ? value : value[..60] + "…";
}
