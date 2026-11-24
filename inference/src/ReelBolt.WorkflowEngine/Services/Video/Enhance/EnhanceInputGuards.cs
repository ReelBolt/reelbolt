using System.Globalization;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

/// <summary>What the first video stream itself reports (not the container).</summary>
internal sealed record StreamFacts(bool HasVideo, double? DurationSec, long? FrameCount)
{
    public int Width { get; init; }

    public int Height { get; init; }

    public int FpsNum { get; init; }

    public int FpsDen { get; init; }

    public double FormatDurationSec { get; init; }

    public string? PixFmt { get; init; }

    public string? ColorTransfer { get; init; }

    public string? ColorPrimaries { get; init; }

    public int? BitDepth { get; init; }

    public bool HasAudio { get; init; }

    /// <summary>Facts from this one whitelisted probe (container duration, size, rate, colour, audio presence).</summary>
    public SourceFacts ToSourceFacts()
    {
        int num = FpsNum > 0 ? FpsNum : 30;
        int den = FpsDen > 0 ? FpsDen : 1;
        double fps = num / (double)den;
        double duration = Math.Max(FormatDurationSec, DurationSec ?? 0);
        int frames = duration > 0 ? (int)Math.Round(duration * fps, MidpointRounding.AwayFromZero) : 0;
        return new SourceFacts(
            Width, Height, num, den, frames, PixFmt, ColorTransfer, ColorPrimaries, BitDepth, duration, HasAudio);
    }
}

/// <summary>
/// Input hardening shared by every ffmpeg/ffprobe call under Enhance. ffmpeg's
/// <c>-protocol_whitelist</c> and <c>-format_whitelist</c> are per-input options, so they must be
/// repeated immediately before EVERY <c>-i</c>; a second input without them is unrestricted.
/// </summary>
internal static class EnhanceInputGuards
{
    /// <summary>The two per-input options, to be placed right before one <c>-i</c>.</summary>
    public static string[] InputOptions() =>
    [
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaDerivativeInputFormats.Whitelist
    ];

    /// <summary>
    /// Probe the first video stream through the runner with the whitelisted probe argv. Returns null
    /// when the probe failed or its output was unreadable.
    /// </summary>
    public static async Task<StreamFacts?> ProbeStreamAsync(
        IVideoToolRunner runner, string path, TimeSpan timeout, CancellationToken ct)
    {
        VideoToolResult? result;
        try
        {
            result = await runner.RunFfprobeAsync(
                MediaDerivativeInputFormats.BuildSourceProbeArgs(path), timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        if (result is null || !result.Succeeded || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        return ParseStream(result.StdOut);
    }

    /// <summary>
    /// What the first video stream of a whitelisted probe's JSON reports (the container's figures are not read here). Null when the
    /// output is not readable JSON.
    /// </summary>
    public static StreamFacts? ParseStream(string? stdOut)
    {
        if (string.IsNullOrWhiteSpace(stdOut))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdOut);
            if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams)
                || streams.ValueKind != JsonValueKind.Array)
            {
                return new StreamFacts(false, null, null);
            }

            JsonElement? video = null;
            bool hasAudio = false;
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                string? kind = stream.TryGetProperty("codec_type", out JsonElement type) ? type.GetString() : null;
                if (kind == "video" && video is null)
                {
                    video = stream;
                }
                else if (kind == "audio")
                {
                    hasAudio = true;
                }
            }

            if (video is not { } v)
            {
                return new StreamFacts(false, null, null) { HasAudio = hasAudio };
            }

            double formatDuration = doc.RootElement.TryGetProperty("format", out JsonElement format)
                ? ReadDouble(format, "duration") ?? 0
                : 0;
            (int num, int den) = ReadRate(v, "avg_frame_rate") is { } avg ? avg : ReadRate(v, "r_frame_rate") ?? (0, 1);
            string? pixFmt = ReadString(v, "pix_fmt");
            return new StreamFacts(true, ReadDouble(v, "duration"), ReadLong(v, "nb_frames"))
            {
                Width = (int)(ReadLong(v, "width") ?? 0),
                Height = (int)(ReadLong(v, "height") ?? 0),
                FpsNum = num,
                FpsDen = den,
                FormatDurationSec = formatDuration,
                PixFmt = pixFmt,
                ColorTransfer = ReadString(v, "color_transfer"),
                ColorPrimaries = ReadString(v, "color_primaries"),
                BitDepth = ReadLong(v, "bits_per_raw_sample") is { } bits ? (int)bits : DepthFromPixFmt(pixFmt),
                HasAudio = hasAudio
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (int Num, int Den)? ReadRate(JsonElement e, string name)
    {
        string? text = ReadString(e, name);
        string[]? parts = text?.Split('/');
        if (parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)
            && n > 0 && d > 0)
        {
            return (n, d);
        }

        return null;
    }

    private static int? DepthFromPixFmt(string? pixFmt) =>
        pixFmt is null ? null
        : pixFmt.Contains("12", StringComparison.Ordinal) ? 12
        : pixFmt.Contains("10", StringComparison.Ordinal) ? 10
        : 8;

    /// <summary>
    /// The probe with BOTH per-input whitelists (the same argv as every first probe), parsed. Throws <see cref="EnhanceException"/>
    /// (<c>ENHANCE_PROBE_FAILED</c>) when ffprobe fails or its output cannot be read: a follow-up probe is never less restricted
    /// than the first one.
    /// </summary>
    public static async Task<MediaProbeResult> ProbeWhitelistedAsync(
        IVideoToolRunner runner, string path, string field, CancellationToken ct)
    {
        try
        {
            VideoToolResult result = await runner.RunFfprobeAsync(
                MediaDerivativeInputFormats.BuildSourceProbeArgs(path), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("ffprobe failed");
            }

            return FfprobeOutputParser.Parse(result.StdOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EnhanceException)
        {
            throw new EnhanceException(EnhanceErrorCodes.ProbeFailed, field, "ffprobe failed on an input file.");
        }
    }

    private static double? ReadDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.String
        && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
        && d > 0 && double.IsFinite(d)
            ? d
            : null;

    private static long? ReadLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long number))
        {
            return number > 0 ? number : null;
        }

        return v.ValueKind == JsonValueKind.String
            && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            && n > 0
                ? n
                : null;
    }
}
