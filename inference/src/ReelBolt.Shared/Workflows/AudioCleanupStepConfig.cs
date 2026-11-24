using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// What an <c>AudioCleanup</c> step removes. A CLOSED list, persisted by name: append-only, never rename or reorder.
/// The first five run locally (free, offline, ffmpeg only); the last two are hosted AI (metered per audio minute,
/// the source's audio leaves the host, so they need <c>AllowSourceMediaEgress</c> and the <c>audio_cleanup</c> plan feature).
/// </summary>
public enum AudioCleanupTarget
{
    /// <summary>Local: a gentle voice pass (high-pass, light denoise).</summary>
    VoiceBasic,

    /// <summary>Local: mains hum, only when a mains frequency is actually detected.</summary>
    Hum,

    /// <summary>Local: wind rumble.</summary>
    Wind,

    /// <summary>Local: steady hiss.</summary>
    Hiss,

    /// <summary>Local: room echo.</summary>
    Echo,

    /// <summary>Hosted AI: keep the voice, drop everything else.</summary>
    IsolateVoice,

    /// <summary>Hosted AI: drop the music (the background stem when the model offers one, else the voice stem).</summary>
    RemoveMusic
}

/// <summary>What file an <c>AudioCleanup</c> step writes. Append-only: persisted by name.</summary>
public enum AudioCleanupOutputKind
{
    /// <summary>A source with a picture track becomes <see cref="VideoWithCleanAudio"/>; an audio-only source becomes a WAV.</summary>
    Auto,

    /// <summary>The cleaned audio alone, as a WAV.</summary>
    Audio,

    /// <summary>The source's picture copied untouched with the cleaned audio, so a clip can be switched to it. Needs a source with a picture.</summary>
    VideoWithCleanAudio
}

/// <summary>
/// JSON configuration for a <c>StepType.AudioCleanup</c> workflow step, deserialised from
/// <c>WorkflowStep.AudioCleanupConfigJson</c>. Deterministic, non-LLM: one audio file (or the audio of one video) in,
/// one cleaned file out, published under <c>outputFiles/{execution}/step-{n}-clean.{ext}</c> and (optionally)
/// registered as a project file. Local targets are free; hosted targets are metered per started audio minute.
/// </summary>
/// <param name="Target">What to remove. See <see cref="AudioCleanupTarget"/>.</param>
/// <param name="Source">The one audio or video to clean: a project file, an earlier step's output or a step result. <see cref="VideoSourceKind.GeneratedClips"/> and <see cref="VideoSourceKind.BriefFiles"/> are refused.</param>
/// <param name="ProviderId">The <c>AudioIsolation</c> provider for a hosted target; null resolves the default row. Not valid with a local target.</param>
/// <param name="AllowSourceMediaEgress">Consent for the source's audio to leave this host (hosted targets only). False refuses a hosted target with <c>EGRESS_REFUSED</c> and uploads nothing.</param>
/// <param name="RegisterProjectFile">Also register the result as a project file (default true).</param>
/// <param name="OutputKind">The file written; see <see cref="AudioCleanupOutputKind"/>.</param>
/// <param name="MaxSourceMinutes">Longest source this step may clean, in minutes. Can only LOWER the deployment's cap: <c>Cleanup:Hosted:MaxSourceMinutes</c> (default 30) for a hosted target, <c>Cleanup:Local:MaxSourceMinutes</c> (default 60) for a local one; null uses the cap.</param>
[StepCard(
    "Cleans the audio of one file or video: hum, wind, hiss and echo for free, or AI voice isolation and music removal.",
    ["An audio file or video from the project or an earlier step"],
    "A cleaned audio file, or the video with its audio cleaned, optionally saved as a project file",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCredits,
    ReadableBy = ["Later steps", "The project's files"],
    PairsWith = ["VideoAnalyze: the cleaned file can be analysed or transcribed", "VideoCompile: the cleaned video can be the source"])]
public sealed record AudioCleanupStepConfig(
    [property: Description("What to remove: VoiceBasic, Hum, Wind, Hiss, Echo (free, local) or IsolateVoice, RemoveMusic (hosted AI, metered per audio minute).")]
    [property: AppBindable(AppInputType.Choice, Label = "What to clean", Choices = ["VoiceBasic", "Hum", "Wind", "Hiss", "Echo"])]
    AudioCleanupTarget Target,
    [property: Description("The one audio file or video to clean. A project file, an earlier step's output or a step result of this project.")]
    VideoSourceRef Source,
    [property: Description("AudioIsolation provider id for IsolateVoice/RemoveMusic. Null uses the default provider; not valid with a local target.")]
    Guid? ProviderId = null,
    [property: Description("Consent for the source's audio to be uploaded to a hosted model (presigned GET, 10 minutes). false refuses IsolateVoice/RemoveMusic with EGRESS_REFUSED and uploads nothing. Local targets never upload.")]
    bool AllowSourceMediaEgress = false,
    [property: Description("Also register the cleaned file as a project file (default true).")]
    bool RegisterProjectFile = true,
    [property: Description("Auto (video in, video out with the picture copied; audio in, WAV out), Audio (WAV only) or VideoWithCleanAudio (needs a picture).")]
    AudioCleanupOutputKind OutputKind = AudioCleanupOutputKind.Auto,
    [property: Description("Longest source for this step in minutes; can only lower the deployment's cap (30 hosted, 60 local). Null uses the cap.")]
    int? MaxSourceMinutes = null)
{
    /// <summary>The longest <see cref="MaxSourceMinutes"/> a config may carry (24 h); the effective cap is still the deployment's.</summary>
    public const int MaxSourceMinutesLimit = 24 * 60;

    /// <summary>One serialiser for run time and save time (Web defaults, enums as strings).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>True for <see cref="AudioCleanupTarget.IsolateVoice"/> and <see cref="AudioCleanupTarget.RemoveMusic"/>: the audio leaves the host and credits are spent.</summary>
    [JsonIgnore]
    public bool IsHosted => Target is AudioCleanupTarget.IsolateVoice or AudioCleanupTarget.RemoveMusic;

    /// <summary>The same rule the credit estimate and the run-rate bucket use: only a hosted target calls a provider.</summary>
    [JsonIgnore]
    public bool CallsAnyProvider => IsHosted;

    /// <summary>The local filter chain of a local target, or null for a hosted one.</summary>
    public LocalCleanupTarget? ToLocalTarget() => Target switch
    {
        AudioCleanupTarget.VoiceBasic => LocalCleanupTarget.VoiceBasic,
        AudioCleanupTarget.Hum => LocalCleanupTarget.Hum,
        AudioCleanupTarget.Wind => LocalCleanupTarget.Wind,
        AudioCleanupTarget.Hiss => LocalCleanupTarget.Hiss,
        AudioCleanupTarget.Echo => LocalCleanupTarget.Echo,
        _ => null
    };

    /// <summary>Every reason this config may not be saved or run, in field order; empty means valid. Never throws.</summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (!Enum.IsDefined(Target))
            errors.Add($"Target must be VoiceBasic, Hum, Wind, Hiss, Echo, IsolateVoice or RemoveMusic (found {Target}).");

        if (Source is null)
        {
            errors.Add("Source is required.");
        }
        else
        {
            if (Source.Kind is VideoSourceKind.GeneratedClips or VideoSourceKind.BriefFiles)
                errors.Add($"Source={Source.Kind} is not supported; AudioCleanup takes exactly one audio file or video (a project file, a step output or a step result).");
            if (Source.Validate() is { } sourceError)
                errors.Add(sourceError);
            if (Source.InSec is not null || Source.OutSec is not null)
                errors.Add("Source.InSec/OutSec are not supported by AudioCleanup; it cleans the whole source.");
        }

        if (ProviderId is { } providerId && providerId == Guid.Empty)
            errors.Add("ProviderId must be a provider id or omitted.");
        else if (ProviderId is not null && Enum.IsDefined(Target) && !IsHosted)
            errors.Add($"ProviderId is only valid with IsolateVoice or RemoveMusic, not Target={Target}.");

        if (!Enum.IsDefined(OutputKind))
            errors.Add($"OutputKind must be Auto, Audio or VideoWithCleanAudio (found {OutputKind}).");

        if (MaxSourceMinutes is { } minutes && (minutes < 1 || minutes > MaxSourceMinutesLimit))
            errors.Add($"MaxSourceMinutes must be between 1 and {MaxSourceMinutesLimit} (found {minutes}).");

        return errors;
    }
}
