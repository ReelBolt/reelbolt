using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The delivery presets a <see cref="StepType.TimelineRender"/> step can target. A preset is a
/// closed name the renderer resolves to concrete dimensions and encoder settings from a first-party
/// table (<c>TimelineRenderPresets</c>) — the step config never carries a free-form filter, codec
/// string or encoder flag. These enums bind by name only (see <see cref="TimelineRenderEnumJsonConverter{TEnum}"/>),
/// because the editor's render request sends names.
/// </summary>
[JsonConverter(typeof(TimelineRenderEnumJsonConverter<TimelineRenderPreset>))]
public enum TimelineRenderPreset
{
    /// <summary>1920x1080, H.264, the timeline's own frame rate.</summary>
    Hd1080p16x9,

    /// <summary>3840x2160, H.264.</summary>
    Uhd4k16x9,

    /// <summary>1080x1920 (9:16) for vertical delivery.</summary>
    Vertical9x16,

    /// <summary>1080x1080 (1:1).</summary>
    Square1x1,

    /// <summary>1080x1350 (4:5).</summary>
    Portrait4x5,

    /// <summary>
    /// The timeline's own settings (width, height, rate) at a high-quality H.264 setting — the
    /// "master" a re-encode starts from.
    /// </summary>
    Master,

    /// <summary>No video stream: the mixed program audio only, as AAC in an .m4a container.</summary>
    AudioOnly,

    /// <summary>
    /// R10.4 "Preview 3 s": the timeline's own aspect at a short side of 540 (never upscaled), fast H.264 + AAC.
    /// Server-only: the public render route refuses it by name; only the preview endpoint builds it.
    /// </summary>
    Preview
}

/// <summary>The video codec allowlist. Nothing outside it ever reaches ffmpeg.</summary>
[JsonConverter(typeof(TimelineRenderEnumJsonConverter<TimelineRenderVideoCodec>))]
public enum TimelineRenderVideoCodec
{
    H264,
    H265
}

/// <summary>The encoder speed/quality trade, mapped to a fixed x264/x265 preset name.</summary>
[JsonConverter(typeof(TimelineRenderEnumJsonConverter<TimelineRenderEncoderSpeed>))]
public enum TimelineRenderEncoderSpeed
{
    Fast,
    Medium,
    Slow
}

/// <summary>How the delivery encode spends bits. Appended; names only.</summary>
[JsonConverter(typeof(TimelineRenderEnumJsonConverter<TimelineRenderRateControl>))]
public enum TimelineRenderRateControl
{
    /// <summary>Constant quality (CRF) with no bitrate limit: today's behaviour.</summary>
    Quality,

    /// <summary>Constant quality (CRF) with a maximum video bitrate.</summary>
    Capped,

    /// <summary>A target average video bitrate (single pass).</summary>
    Average
}

/// <summary>
/// The wire encoding of <see cref="TimelineRenderPreset"/>, <see cref="TimelineRenderVideoCodec"/> and
/// <see cref="TimelineRenderEncoderSpeed"/>: names, read case-insensitively (the Deliver workspace sends
/// <c>"Hd1080p16x9"</c>, <c>"H264"</c>, <c>"Medium"</c>; <c>"hd1080p16x9"</c> binds too) and written as
/// declared. <b>Integers are rejected</b>, so a numeric <c>preset</c> in a render request is MVC's
/// ordinary binding <c>400</c>, never an ordinal that silently picks a preset.
/// </summary>
/// <remarks>
/// Stored step configs are not read through this attribute: every reader of
/// <c>WorkflowStep.TimelineRenderConfigJson</c> (the engine executor, the save validator, the render
/// list) passes options carrying a plain <see cref="JsonStringEnumConverter"/>, which takes
/// precedence over a type-level attribute and still accepts a legacy integer.
/// </remarks>
public sealed class TimelineRenderEnumJsonConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>Names only, case-insensitive on read, written as declared.</summary>
    public TimelineRenderEnumJsonConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// R09.7: how caption items reach the delivery. <c>null</c> on the config (the wire field absent) keeps the
/// pre-R09.7 behaviour: enabled captions are burned in and no sidecar is written.
/// </summary>
[JsonConverter(typeof(TimelineRenderEnumJsonConverter<TimelineRenderCaptions>))]
public enum TimelineRenderCaptions
{
    /// <summary>No captions in the film and no sidecar.</summary>
    None,

    /// <summary>Burn the enabled captions into the picture (same as an absent field).</summary>
    BurnIn,

    /// <summary>No burn-in; write the cue list next to the film as SRT and VTT.</summary>
    Sidecar,

    /// <summary>Burn in and write the SRT and VTT sidecars.</summary>
    Both
}

/// <summary>
/// The frame range to render, in timeline frames, <c>[InFrame, OutFrame)</c>. Omitted means the
/// whole timeline.
/// </summary>
public sealed record TimelineRenderRange(long InFrame, long OutFrame);

/// <summary>
/// Explicit, allowlisted encode settings that refine the chosen preset. Every field is clamped by
/// <see cref="TimelineRenderStepConfig.Validate"/>; a value outside its range is a save-time
/// rejection, never a silently coerced argv.
/// </summary>
/// <param name="Width">Output width override (even, 16..7680). Null keeps the preset's.</param>
/// <param name="Height">Output height override (even, 16..4320). Null keeps the preset's.</param>
/// <param name="Codec">Video codec.</param>
/// <param name="Crf">Constant rate factor, 0..51 (lower is better quality).</param>
/// <param name="Speed">Encoder speed preset.</param>
/// <param name="AudioBitrateKbps">AAC bitrate, 64..320 kbps.</param>
/// <param name="RateControl">Quality (CRF only), Capped (CRF plus a maximum) or Average (target bitrate).</param>
/// <param name="VideoBitrateKbps">Video bitrate in kbps, 200..100000; required by Capped and Average, forbidden by Quality.</param>
public sealed record TimelineRenderEncodeSettings(
    int? Width = null,
    int? Height = null,
    TimelineRenderVideoCodec Codec = TimelineRenderVideoCodec.H264,
    int Crf = 18,
    TimelineRenderEncoderSpeed Speed = TimelineRenderEncoderSpeed.Medium,
    int AudioBitrateKbps = 192,
    TimelineRenderRateControl RateControl = TimelineRenderRateControl.Quality,
    int? VideoBitrateKbps = null);

/// <summary>
/// Optional two-pass <c>loudnorm</c> targets for the program mix (EBU R128 style). Omitted means the
/// mix is measured but not normalised.
/// </summary>
/// <param name="TargetLufs">Integrated loudness target, -36..-5 LUFS.</param>
/// <param name="TruePeakDbtp">True-peak ceiling, -9..0 dBTP.</param>
/// <param name="LoudnessRange">Loudness range target, 1..30 LU.</param>
public sealed record TimelineRenderLoudness(
    double TargetLufs = -16.0,
    double TruePeakDbtp = -1.5,
    double LoudnessRange = 11.0);

/// <summary>
/// JSON configuration for a <see cref="StepType.TimelineRender"/> workflow step, deserialised from
/// <c>WorkflowStep.TimelineRenderConfigJson</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic and non-LLM: the step renders one editor-v2 <c>EditTimeline</c> document — the one
/// stored under <see cref="EditTimelineId"/> at <see cref="Revision"/> (null = the latest revision
/// when the step starts, which the executor pins into its output so the run is reproducible) — to a
/// delivery preset. The server never applies an op here: the document is re-validated through
/// <c>EditTimelineValidator</c> before a single ffmpeg argument is built.
/// </para>
/// <para>
/// <b>Trust boundary.</b> A human authors the numbers in this record and in the document, so every
/// value that can reach ffmpeg argv comes from a closed catalogue with a clamped range: presets and
/// codecs are enums, CRF/bitrate/dimensions/loudness are range-checked here, text goes through the
/// overlay sanitiser into a <c>textfile=</c>, and assets are server-resolved <c>AssetRef</c>s
/// prefix-checked to the project. See <c>docs/editor.md</c> "Rendering (P4)".
/// </para>
/// </remarks>
[StepCard(
    "Renders an editor timeline at a fixed revision to a delivery format.",
    ["A saved editor timeline"],
    "The rendered delivery video file",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits,
    ReadableBy = ["The project's files"])]
public sealed record TimelineRenderStepConfig(
    Guid EditTimelineId,
    int Version = 1,
    int? Revision = null,
    TimelineRenderRange? Range = null,
    TimelineRenderPreset Preset = TimelineRenderPreset.Hd1080p16x9,
    TimelineRenderEncodeSettings? Encode = null,
    string OutputFileName = "timeline-render.mp4",
    bool RegisterProjectFile = true,
    TimelineRenderLoudness? Loudness = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? AudioJobKind = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    TimelineRenderCaptions? Captions = null)
{
    /// <summary>True when enabled captions are burned into the picture (absent field = true, the legacy behaviour).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool BurnInCaptions => Captions is null or TimelineRenderCaptions.BurnIn or TimelineRenderCaptions.Both;

    /// <summary>True when the SRT/VTT sidecars are written.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WriteCaptionSidecars => Captions is TimelineRenderCaptions.Sidecar or TimelineRenderCaptions.Both;

    public const int CurrentVersion = 1;

    public const int MinDimension = 16;
    public const int MaxWidth = 7680;
    public const int MaxHeight = 4320;
    public const int MinCrf = 0;
    public const int MaxCrf = 51;
    public const int MinAudioBitrateKbps = 64;
    public const int MaxAudioBitrateKbps = 320;
    public const int MinVideoBitrateKbps = 200;
    public const int MaxVideoBitrateKbps = 100000;
    public const int MaxOutputFileNameChars = 120;

    private static readonly Regex SafeFileName = new("^[A-Za-z0-9][A-Za-z0-9._ -]*$", RegexOptions.Compiled);

    /// <summary>
    /// Every reason this config may not be saved or run, in field order; empty means valid. Written
    /// so a 400 body can list all of them at once.
    /// </summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (Version != CurrentVersion)
            errors.Add($"version must be {CurrentVersion}, found {Version}.");

        if (EditTimelineId == Guid.Empty)
            errors.Add("editTimelineId is required.");

        if (Revision is { } revision && revision < 1)
            errors.Add($"revision must be >= 1 when supplied, found {revision}.");

        if (Range is { } range)
        {
            if (range.InFrame < 0)
                errors.Add($"range.inFrame must be >= 0, found {range.InFrame}.");
            if (range.OutFrame <= range.InFrame)
                errors.Add($"range.outFrame ({range.OutFrame}) must be greater than range.inFrame ({range.InFrame}).");
        }

        if (Captions is { } captions && !Enum.IsDefined(captions))
            errors.Add($"captions '{captions}' is not a known captions mode.");

        if (!Enum.IsDefined(Preset))
            errors.Add($"preset '{Preset}' is not a known preset.");

        if (Encode is { } encode)
        {
            if (encode.Width is { } width && (width < MinDimension || width > MaxWidth || width % 2 != 0))
                errors.Add($"encode.width must be an even number between {MinDimension} and {MaxWidth}, found {width}.");
            if (encode.Height is { } height && (height < MinDimension || height > MaxHeight || height % 2 != 0))
                errors.Add($"encode.height must be an even number between {MinDimension} and {MaxHeight}, found {height}.");
            if ((encode.Width is null) != (encode.Height is null))
                errors.Add("encode.width and encode.height must be supplied together.");
            if (!Enum.IsDefined(encode.Codec))
                errors.Add($"encode.codec '{encode.Codec}' is not a known codec.");
            if (encode.Crf < MinCrf || encode.Crf > MaxCrf)
                errors.Add($"encode.crf must be between {MinCrf} and {MaxCrf}, found {encode.Crf}.");
            if (!Enum.IsDefined(encode.Speed))
                errors.Add($"encode.speed '{encode.Speed}' is not a known speed.");
            if (encode.AudioBitrateKbps < MinAudioBitrateKbps || encode.AudioBitrateKbps > MaxAudioBitrateKbps)
                errors.Add($"encode.audioBitrateKbps must be between {MinAudioBitrateKbps} and {MaxAudioBitrateKbps}, found {encode.AudioBitrateKbps}.");
            if (!Enum.IsDefined(encode.RateControl))
                errors.Add($"encode.rateControl '{encode.RateControl}' is not a known rate control.");
            else if (Preset == TimelineRenderPreset.AudioOnly)
            {
                if (encode.RateControl != TimelineRenderRateControl.Quality || encode.VideoBitrateKbps is not null)
                    errors.Add("encode.rateControl and encode.videoBitrateKbps do not apply to an audio-only render.");
            }
            else if (encode.RateControl == TimelineRenderRateControl.Quality)
            {
                if (encode.VideoBitrateKbps is not null)
                    errors.Add("encode.videoBitrateKbps can only be set when rateControl is Capped or Average.");
            }
            else if (encode.VideoBitrateKbps is not { } kbps)
                errors.Add($"encode.videoBitrateKbps is required when rateControl is {encode.RateControl}.");
            else if (kbps < MinVideoBitrateKbps || kbps > MaxVideoBitrateKbps)
                errors.Add($"encode.videoBitrateKbps must be between {MinVideoBitrateKbps} and {MaxVideoBitrateKbps}, found {kbps}.");
        }

        if (string.IsNullOrWhiteSpace(OutputFileName))
            errors.Add("outputFileName is required.");
        else if (OutputFileName.Length > MaxOutputFileNameChars || !SafeFileName.IsMatch(OutputFileName) || OutputFileName.Contains(".."))
            errors.Add("outputFileName may only contain letters, digits, '.', '_', '-' and spaces, must start with a letter or digit, and may not contain '..'.");

        if (Loudness is { } loudness)
        {
            if (loudness.TargetLufs < -36 || loudness.TargetLufs > -5)
                errors.Add($"loudness.targetLufs must be between -36 and -5, found {loudness.TargetLufs}.");
            if (loudness.TruePeakDbtp < -9 || loudness.TruePeakDbtp > 0)
                errors.Add($"loudness.truePeakDbtp must be between -9 and 0, found {loudness.TruePeakDbtp}.");
            if (loudness.LoudnessRange < 1 || loudness.LoudnessRange > 30)
                errors.Add($"loudness.loudnessRange must be between 1 and 30, found {loudness.LoudnessRange}.");
        }

        return errors;
    }
}
