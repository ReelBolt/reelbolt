using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

using System.Globalization;
using System.Text;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The argv, the time plan and the object layout of the <see cref="MediaDerivativeKindSlug.Stills"/>
/// derivative: one ffmpeg invocation per still (an input seek and one frame), so the derivative slot's
/// low-priority gate is released between stills exactly as it is between filmstrip passes.
/// </summary>
/// <remarks>
/// Like a filmstrip, a stills set is ONE row whose storage key is its first still; the others are
/// sibling objects listed in a JSON index (<see cref="IndexLeaf"/>) the row's
/// <c>IndexStorageKey</c> points at, each entry carrying its index, time and bare leaf.
/// </remarks>
public static class StillsArgvBuilder
{
    /// <summary>How far before the measured end the "last frame" is taken: a seek to the exact end lands past every frame.</summary>
    public const int LastFrameMarginMs = 100;

    /// <summary>
    /// The times to extract, in milliseconds. With no <paramref name="requestedMs"/>: twelve evenly
    /// spaced from the first frame (0) to the last (the duration less <see cref="LastFrameMarginMs"/>),
    /// rounded to a tenth of a second. Otherwise the requested times, clamped into that same range.
    /// Either way deduplicated and ascending; a source with no measured duration gets its first frame.
    /// </summary>
    public static IReadOnlyList<int> PlanTimesMs(double durationSec, IReadOnlyList<int> requestedMs)
    {
        ArgumentNullException.ThrowIfNull(requestedMs);

        int lastMs = durationSec > 0 && double.IsFinite(durationSec)
            ? Math.Max(0, (int)Math.Floor(durationSec * 10) * 100 - LastFrameMarginMs)
            : 0;

        IEnumerable<int> times;
        if (requestedMs.Count > 0)
        {
            times = requestedMs.Select(t => Math.Clamp(t, 0, lastMs));
        }
        else
        {
            int count = MediaDerivativeSpec.StillsEvenCount;
            times = Enumerable.Range(0, count)
                .Select(i => (int)Math.Round(lastMs * (double)i / (count - 1) / 100, MidpointRounding.AwayFromZero) * 100);
        }

        return [.. times.Distinct().Order()];
    }

    /// <summary>One still: an input seek to <paramref name="atMs"/>, one frame, long side capped at <see cref="MediaDerivativeSpec.StillsLongSide"/> (never upscaled).</summary>
    public static string[] Build(string inputPath, string outputPath, int atMs, string? hdrKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string side = FfmpegArgvFormat.Number(MediaDerivativeSpec.StillsLongSide);

        // min(side, iw/ih) keeps a small source at its own size; decrease keeps the aspect ratio inside
        // the side x side box; divisible-by-2 keeps MJPEG's chroma subsampling happy.
        string filter =
            $"scale=w='min({side},iw)':h='min({side},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2" +
            HdrToneMap.Suffix(hdrKind);

        return
        [
            "-y",
            "-nostdin",
            "-hide_banner",
            // The customer's source (or its proxy): the same per-input whitelists as every other user-media input (security ENG-3).
            .. UserMediaInputFormats.InputOptions(),
            "-ss", FormatSeconds(atMs),
            "-i", inputPath,
            "-an",
            "-frames:v", "1",
            "-vf", filter,
            "-c:v", MediaDerivativeSpec.StillsCodec,
            "-q:v", FfmpegArgvFormat.Number(MediaDerivativeSpec.StillsQuality),
            "-f", "image2",
            "-update", "1",
            outputPath
        ];
    }

    /// <summary>Milliseconds as invariant seconds with no trailing zeros (12500 → "12.5", 0 → "0").</summary>
    public static string FormatSeconds(int ms) => (ms / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The object leaf of still <paramref name="index"/> (0-based) of a set.</summary>
    public static string StillLeaf(string paramsHashShort, int index) =>
        $"stills-{paramsHashShort}-{(index + 1).ToString("00", CultureInfo.InvariantCulture)}.jpg";

    /// <summary>The object leaf of a set's JSON index.</summary>
    public static string IndexLeaf(string paramsHashShort) => $"stills-{paramsHashShort}.json";

    /// <summary>
    /// The index: <c>{"count":N,"stills":[{"atSec":s,"index":i,"storageLeaf":"..."}]}</c> — the bare
    /// leaves only, never a key, so a reader derives each object from the row's own directory.
    /// </summary>
    public static string BuildIndexJson(IReadOnlyList<(int Index, int AtMs, string Leaf)> stills)
    {
        ArgumentNullException.ThrowIfNull(stills);

        var builder = new StringBuilder("{\"count\":")
            .Append(stills.Count.ToString(CultureInfo.InvariantCulture))
            .Append(",\"stills\":[");
        for (int i = 0; i < stills.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append("{\"atSec\":").Append(FormatSeconds(stills[i].AtMs))
                .Append(",\"index\":").Append(stills[i].Index.ToString(CultureInfo.InvariantCulture))
                .Append(",\"storageLeaf\":\"").Append(stills[i].Leaf).Append("\"}");
        }

        return builder.Append("]}").ToString();
    }
}
