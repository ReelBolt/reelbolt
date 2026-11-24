using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// The concrete encode target a <see cref="TimelineRenderStepConfig"/> resolves to. Every value here
/// is either a preset-table constant or a range-checked config field — this record is the LAST
/// place a human-authored number is touched before it becomes argv, and nothing downstream reads
/// the config again.
/// </summary>
/// <param name="Width">Output width (even).</param>
/// <param name="Height">Output height (even).</param>
/// <param name="Rate">Output frame rate, the timeline's own exact rational.</param>
/// <param name="SampleRate">Output audio sample rate.</param>
/// <param name="VideoEnabled">False for the audio-only preset.</param>
/// <param name="Encoder">libx264 / libx265.</param>
/// <param name="EncoderPreset">x264/x265 preset name.</param>
/// <param name="Crf">Constant rate factor.</param>
/// <param name="AudioBitrateKbps">AAC bitrate.</param>
/// <param name="RateControl">Quality, Capped or Average.</param>
/// <param name="VideoBitrateKbps">Video bitrate for Capped/Average; null for Quality.</param>
/// <param name="Extension">Container extension, including the dot.</param>
/// <param name="Preset">The preset the target was resolved from.</param>
public sealed record ResolvedRenderTarget(
    int Width,
    int Height,
    Rational Rate,
    int SampleRate,
    bool VideoEnabled,
    string Encoder,
    string EncoderPreset,
    int Crf,
    int AudioBitrateKbps,
    string Extension,
    TimelineRenderPreset Preset,
    TimelineRenderRateControl RateControl = TimelineRenderRateControl.Quality,
    int? VideoBitrateKbps = null)
{
    /// <summary>Frames per second as a double, from the exact rational.</summary>
    public double Fps => Rate.Den == 0 ? 30.0 : Rate.Num / (double)Rate.Den;

    /// <summary>ffmpeg's <c>num/den</c> spelling of the rate — never a rounded double.</summary>
    public string RateArg => $"{Rate.Num}/{Rate.Den}";

    /// <summary>MIME type of the delivered file.</summary>
    public string MimeType => VideoEnabled ? "video/mp4" : "audio/mp4";
}

/// <summary>
/// The first-party preset table: the ONLY source of output dimensions and encoder flags. A config's
/// explicit <see cref="TimelineRenderEncodeSettings"/> may refine a preset's codec, CRF, speed,
/// bitrate and dimensions — all range-checked by <see cref="TimelineRenderStepConfig.Validate"/> —
/// and nothing else about the encode is configurable.
/// </summary>
public static class TimelineRenderPresets
{
    /// <summary>
    /// The mezzanine every region renders to: all-intra H.264 at CRF 10, yuv420p. Chosen over FFV1
    /// (issue §7 open question 2) on a measured trade: FFV1 is lossless but 8–12x larger per region
    /// and decodes an order of magnitude slower on concat, while intra x264 at CRF 10 sits above
    /// 48 dB PSNR against its source — the one extra generation it introduces is what the
    /// golden-parity gate measures, and it clears the 40 dB floor with margin. All-intra (GOP 1) is
    /// what makes a region file cut-safe at any frame for the concat demuxer.
    /// media-speed T-003 re-measured the alternatives on 3 s of 4K60 (CPU-s region+delivery): this 312, ultrafast intra
    /// 298 (delivery decode +34%), closed-GOP P-only (g 120, bf 0) 332; none beats it by enough to change cached bytes.
    /// </summary>
    public static readonly string[] MezzanineVideoArgs =
    [
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "10", "-g", "1", "-keyint_min", "1",
        "-pix_fmt", "yuv420p", "-x264-params", "scenecut=0:open_gop=0"
    ];

    public static ResolvedRenderTarget Resolve(TimelineRenderStepConfig config, EditTimelineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);

        Rational rate = settings.Rate is { Den: > 0, Num: > 0 } ? settings.Rate : new Rational(30, 1);
        int sampleRate = settings.SampleRate > 0 ? settings.SampleRate : 48000;

        // Dimensions (preset size, the timeline's own for Master/AudioOnly, an explicit encode
        // width+height over both) come from the shared table the API's plan gate reads too.
        (int width, int height) = TimelineRenderSizing.Dimensions(config, settings.Width, settings.Height);
        (int crf, TimelineRenderEncoderSpeed speed, bool video) = Defaults(config.Preset);

        TimelineRenderVideoCodec codec = TimelineRenderVideoCodec.H264;
        int audioBitrate = DefaultAudioBitrateKbps;
        TimelineRenderRateControl rateControl = TimelineRenderRateControl.Quality;
        int? videoBitrate = null;

        if (config.Encode is { } encode)
        {
            codec = encode.Codec;
            crf = encode.Crf;
            speed = encode.Speed;
            audioBitrate = encode.AudioBitrateKbps;
            rateControl = encode.RateControl;
            videoBitrate = encode.VideoBitrateKbps;
        }

        return new ResolvedRenderTarget(
            Width: width,
            Height: height,
            Rate: rate,
            SampleRate: sampleRate,
            VideoEnabled: video,
            Encoder: codec == TimelineRenderVideoCodec.H265 ? "libx265" : "libx264",
            EncoderPreset: speed switch
            {
                TimelineRenderEncoderSpeed.Fast => "veryfast",
                TimelineRenderEncoderSpeed.Slow => "slow",
                _ => "medium"
            },
            Crf: crf,
            AudioBitrateKbps: audioBitrate,
            Extension: video ? ".mp4" : ".m4a",
            Preset: config.Preset,
            RateControl: rateControl,
            VideoBitrateKbps: videoBitrate);
    }

    /// <summary>
    /// <paramref name="config"/> with its encode size rewritten so the picture's short side is at most
    /// <paramref name="maxShortSide"/> (the plan's <c>MaxOutputHeight</c>), aspect-preserving and
    /// even (<see cref="TimelineRenderSizing.CapToShortSide"/>); the same config when it already fits,
    /// has no picture, or the cap is unlimited. Every other encode setting — the preset's own CRF,
    /// speed and bitrate when the config had no <see cref="TimelineRenderEncodeSettings"/> — is kept,
    /// so the clamp changes the size and nothing else. Apply it BEFORE <see cref="Resolve"/> and hand
    /// the result to the renderer, so the region hashes and the encode agree.
    /// </summary>
    public static TimelineRenderStepConfig CapToShortSide(
        TimelineRenderStepConfig config, EditTimelineSettings settings, int maxShortSide, out bool applied)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);
        applied = false;

        int cap = Math.Max(TimelineRenderStepConfig.MinDimension, maxShortSide);
        if (TimelineRenderSizing.ShortSide(config, settings.Width, settings.Height) is not { } shortSide || shortSide <= cap)
            return config;

        (int width, int height) = TimelineRenderSizing.Dimensions(config, settings.Width, settings.Height);
        (int cappedWidth, int cappedHeight) = TimelineRenderSizing.CapToShortSide(width, height, cap);
        (int crf, TimelineRenderEncoderSpeed speed, _) = Defaults(config.Preset);
        TimelineRenderEncodeSettings encode = config.Encode is { } existing
            ? existing with { Width = cappedWidth, Height = cappedHeight }
            : new TimelineRenderEncodeSettings(cappedWidth, cappedHeight, TimelineRenderVideoCodec.H264, crf, speed, DefaultAudioBitrateKbps);

        applied = true;
        return config with { Encode = encode };
    }

    private const int DefaultAudioBitrateKbps = 192;

    private static (int Crf, TimelineRenderEncoderSpeed Speed, bool Video) Defaults(TimelineRenderPreset preset) => preset switch
    {
        TimelineRenderPreset.Hd1080p16x9 or TimelineRenderPreset.Uhd4k16x9 or TimelineRenderPreset.Vertical9x16
            or TimelineRenderPreset.Square1x1 or TimelineRenderPreset.Portrait4x5 => (18, TimelineRenderEncoderSpeed.Medium, true),
        TimelineRenderPreset.Master => (14, TimelineRenderEncoderSpeed.Slow, true),
        TimelineRenderPreset.AudioOnly => (18, TimelineRenderEncoderSpeed.Medium, false),
        TimelineRenderPreset.Preview => (28, TimelineRenderEncoderSpeed.Fast, true),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), $"Unknown preset '{preset}'.")
    };
}
