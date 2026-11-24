using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Pure parser for the JSON produced by
/// <c>ffprobe -v error -print_format json -show_format -show_streams</c>. Deliberately has no
/// dependency on <see cref="IVideoToolRunner"/> or any process I/O, so it is unit-testable
/// against a captured/constructed fixture string with no ffmpeg installation required.
/// </summary>
public static class FfprobeOutputParser
{
    public static MediaProbeResult Parse(string ffprobeJson)
    {
        using JsonDocument doc = JsonDocument.Parse(ffprobeJson);
        JsonElement root = doc.RootElement;

        JsonElement? videoStream = null;
        JsonElement? audioStream = null;
        if (root.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                string? codecType = GetString(stream, "codec_type");
                if (videoStream is null && codecType == "video")
                    videoStream = stream;
                else if (audioStream is null && codecType == "audio")
                    audioStream = stream;
            }
        }

        double durationSec = 0;
        if (root.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement formatDuration) &&
            TryParseInvariantDouble(formatDuration, out double parsedFormatDuration))
        {
            durationSec = parsedFormatDuration;
        }
        else if (videoStream is { } vs0 &&
                 vs0.TryGetProperty("duration", out JsonElement streamDuration) &&
                 TryParseInvariantDouble(streamDuration, out double parsedStreamDuration))
        {
            durationSec = parsedStreamDuration;
        }

        // ---- Container + bitrate: both live on ffprobe's `format` object, not on a stream. ----
        // `format_name` is carried VERBATIM: ffprobe reports a comma-separated PROBE list for a
        // container it had to sniff rather than one it read from a header (e.g.
        // "mov,mp4,m4a,3gp,3g2,mj2" for a plain .mp4), and splitting or normalizing it here would
        // invent a canonical name this parser has no authority to choose. A consumer that wants one
        // name can split it itself; a consumer that loses the list can never recover it.
        string? container = null;
        long? bitrateBps = null;
        if (root.TryGetProperty("format", out JsonElement formatElement) &&
            formatElement.ValueKind == JsonValueKind.Object)
        {
            string? formatName = GetString(formatElement, "format_name");
            if (!string.IsNullOrWhiteSpace(formatName))
                container = formatName;

            bitrateBps = GetIntLikeLong(formatElement, "bit_rate");
        }

        int fpsNum = 0;
        int fpsDen = 1;
        int width = 0;
        int height = 0;
        string? videoCodec = null;
        string? pixFmt = null;
        string? alphaModeTag = null;
        string? colorTransfer = null;
        string? colorPrimaries = null;
        string? colorSpace = null;
        string? colorRange = null;
        int? bitDepth = null;
        string? make = null;
        string? model = null;
        string? software = null;

        bool altersPixels = false;
        if (videoStream is { } vs)
        {
            width = GetInt(vs, "width") ?? 0;
            height = GetInt(vs, "height") ?? 0;

            // ffprobe reports CODED dimensions; ffmpeg autorotates every decode, so each consumer
            // (proxy/filmstrip sizing, frame grids, verifiers) sees the DISPLAY frame. A phone
            // portrait stored as coded 1920x1080 + a 90/270 rotation is therefore 1080x1920.
            altersPixels = AltersPixels(vs);
            if (FfmpegTransposes(vs))
                (width, height) = (height, width);

            videoCodec = GetString(vs, "codec_name");
            pixFmt = GetString(vs, "pix_fmt");
            colorTransfer = NullIfUnknown(GetString(vs, "color_transfer"));
            colorPrimaries = NullIfUnknown(GetString(vs, "color_primaries"));
            colorSpace = NullIfUnknown(GetString(vs, "color_space"));
            colorRange = NullIfUnknown(GetString(vs, "color_range"));
            bitDepth = GetIntLikeInt(vs, "bits_per_raw_sample") is > 0 and var raw ? raw : BitDepthFromPixFmt(pixFmt);

            // WebM/VP9's alpha encoding doesn't show up in pix_fmt at all — ffprobe reports the
            // plain yuv420p base-plane format and carries the actual alpha signal as this stream
            // tag instead (confirmed live: encoding a genuinely transparent PNG sequence with
            // `-pix_fmt yuva420p -c:v libvpx-vp9` produces pix_fmt=yuv420p + tags.alpha_mode="1",
            // and decoding it back with `-pix_fmt rgba` correctly reconstructs the alpha channel).
            // See AlphaPixelFormats, whose pix_fmt-only allowlist was silently rejecting every
            // real VP9/WebM overlay this feature has ever rendered.
            // The tag's KEY case is the muxer's, not ffprobe's: a plain stream-copy remux of that
            // same file (`ffmpeg -i a.webm -c copy b.webm`, the mux step a Remotion render ends
            // with) reports only tags.ALPHA_MODE="1", so the key is matched case-insensitively
            // (#340). Structural keys above (pix_fmt, codec_type, ...) are ffprobe's own and stay exact.
            if (vs.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Object)
            {
                alphaModeTag = GetTagString(tags, "alpha_mode");
            }

            string? rFrameRate = GetString(vs, "r_frame_rate");
            if (!string.IsNullOrWhiteSpace(rFrameRate) && TryParseRational(rFrameRate, out int num, out int den))
            {
                fpsNum = num;
                fpsDen = den;
            }
        }

        string? audioCodec = null;
        int? audioSampleRate = null;
        int? audioChannels = null;
        if (audioStream is { } asElem)
        {
            audioCodec = GetString(asElem, "codec_name");
            string? sampleRateRaw = GetString(asElem, "sample_rate");
            if (sampleRateRaw is not null && int.TryParse(sampleRateRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int sr))
                audioSampleRate = sr;

            // The FIRST audio stream's channel count — the same stream the codec and sample rate
            // above come from, so the three always describe one stream rather than a mix of two.
            audioChannels = GetIntLikeInt(asElem, "channels");
        }

        // Maker tags: stream tags first, then format tags (first allowlisted key with a clean value wins).
        JsonElement? streamTags = videoStream is { } vst && vst.TryGetProperty("tags", out JsonElement st) ? st : null;
        JsonElement? formatTags = root.TryGetProperty("format", out JsonElement fmt) && fmt.TryGetProperty("tags", out JsonElement ft) ? ft : null;
        make = FirstTag(streamTags, formatTags, MakeKeys);
        model = FirstTag(streamTags, formatTags, ModelKeys);
        software = FirstTag(streamTags, formatTags, SoftwareKeys);

        return new MediaProbeResult(durationSec, fpsNum, fpsDen, width, height, videoCodec, audioCodec, audioSampleRate, pixFmt, alphaModeTag, container, audioChannels, bitrateBps, colorTransfer, colorPrimaries, colorSpace, altersPixels, colorRange, bitDepth, make, model, software);
    }

    private static readonly string[] MakeKeys = ["com.apple.quicktime.make", "make", "com.android.manufacturer"];
    private static readonly string[] ModelKeys = ["com.apple.quicktime.model", "model", "com.android.model"];
    private static readonly string[] SoftwareKeys = ["com.apple.quicktime.software", "encoder"];

    private static string? FirstTag(JsonElement? streamTags, JsonElement? formatTags, string[] keys)
    {
        foreach (JsonElement? tags in new[] { streamTags, formatTags })
        {
            if (tags is not { } t) continue;
            foreach (string key in keys)
                if (SanitizeTag(GetTagString(t, key)) is { } clean) return clean;
        }

        return null;
    }

    /// <summary>Control characters removed, trimmed, capped at 64 chars; null when nothing is left.</summary>
    internal static string? SanitizeTag(string? value)
    {
        if (value is null) return null;
        string clean = new(value.Where(c => !char.IsControl(c)).ToArray());
        clean = clean.Trim();
        if (clean.Length > 64) clean = clean[..64].Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>Bit depth from a pix_fmt such as yuv420p10le (10), p010le (10), yuv420p (8); null when it cannot be read.</summary>
    internal static int? BitDepthFromPixFmt(string? pixFmt)
    {
        if (string.IsNullOrWhiteSpace(pixFmt)) return null;
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(pixFmt, @"p(9|10|12|14|16)(?:le|be)?$");
        if (m.Success) return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (pixFmt.StartsWith("p0", StringComparison.Ordinal) && pixFmt.Length >= 4 && int.TryParse(pixFmt.AsSpan(1, 3), out int pv) && pv is 10 or 12 or 16) return pv;
        return pixFmt.Contains('p') || pixFmt is "nv12" or "nv21" ? 8 : null;
    }

    /// <summary>
    /// True when ffmpeg's autorotate transposes the first video stream of this ffprobe JSON (so a decode
    /// is H x W of the coded size). Malformed or empty JSON, or no video stream, is false.
    /// </summary>
    public static bool HasVideoStream(string? ffprobeJson)
    {
        if (string.IsNullOrWhiteSpace(ffprobeJson)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ffprobeJson);
            return doc.RootElement.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array &&
                   streams.EnumerateArray().Any(st => GetString(st, "codec_type") == "video");
        }
        catch (JsonException) { return false; }
    }

    public static bool TransposesOnDecode(string? ffprobeJson)
    {
        if (string.IsNullOrWhiteSpace(ffprobeJson)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ffprobeJson);
            if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.ValueKind != JsonValueKind.Array)
                return false;
            foreach (JsonElement st in streams.EnumerateArray())
                if (GetString(st, "codec_type") == "video")
                    return FfmpegTransposes(st);
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>
    /// True exactly when ffmpeg's autorotate TRANSPOSES this stream (so decoded frames are H x W).
    /// Derived from the raw "Display Matrix" (<c>displaymatrix</c> text, nine ints) and copied from
    /// ffmpeg 8.0 fftools (ffmpeg_demux.c get_rotation + ffmpeg_filter.c insert_filter autorotate):
    ///   theta = av_display_rotation_get(matrix)      // libavutil/display.c: -atan2(m1/s1, m0/s0) in degrees
    ///   theta = -round(theta); theta -= 360 * floor(theta / 360 + 0.9 / 360);
    ///   transpose iff fabs(theta - 90) &lt; 1.0 || fabs(theta - 270) &lt; 1.0.
    /// ffprobe's printed "rotation" is not used (it is rounded differently: 90.7 prints 90 but ffmpeg
    /// does not transpose). The legacy <c>tags.rotate</c> (mkv/webm ROTATE) is NOT autorotated by
    /// ffmpeg, so it is ignored. No matrix, or an unparseable one, means no swap. Verified empirically
    /// against ffmpeg 8.0.1 (see TestData/ffprobe-rotation/*.decoded).
    /// </summary>
    private static bool FfmpegTransposes(JsonElement videoStream)
    {
        if (!videoStream.TryGetProperty("side_data_list", out JsonElement sideData) || sideData.ValueKind != JsonValueKind.Array)
            return false;
        foreach (JsonElement entry in sideData.EnumerateArray())
        {
            if (GetString(entry, "side_data_type") != "Display Matrix")
                continue;
            // The first Display Matrix wins (ffmpeg reads the first matching side data).
            string? text = GetString(entry, "displaymatrix");
            return text is not null && TryParseMatrix(text, out int[] m) && TransposesFor(m);
        }
        return false;
    }

    private static bool TryParseMatrix(string text, out int[] matrix)
    {
        // Lines look like "00000000:            0      -65536           0".
        var values = new List<int>(9);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) { matrix = Array.Empty<int>(); return false; }
            foreach (string tok in line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(tok, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int v))
                { matrix = Array.Empty<int>(); return false; }
                values.Add(v);
            }
        }
        matrix = values.ToArray();
        return matrix.Length == 9;
    }

    /// <summary>ffmpeg's normalised autorotate angle for the matrix, or null when it is NaN (degenerate matrix).</summary>
    private static double? ThetaFor(int[] m)
    {
        // av_display_rotation_get: fixed-point 16.16 for entries 0,1,3,4.
        double s0 = Math.Sqrt(Math.Pow(m[0] / 65536.0, 2) + Math.Pow(m[3] / 65536.0, 2));
        double s1 = Math.Sqrt(Math.Pow(m[1] / 65536.0, 2) + Math.Pow(m[4] / 65536.0, 2));
        if (s0 == 0 || s1 == 0)
            return null; // NaN in ffmpeg: theta stays 0, no transform
        double theta = -(Math.Atan2(m[1] / 65536.0 / s1, m[0] / 65536.0 / s0) * 180.0 / Math.PI);
        if (double.IsNaN(theta) || double.IsInfinity(theta))
            return null;
        // C round(): half away from zero.
        theta = -Math.Round(theta, MidpointRounding.AwayFromZero);
        theta -= 360 * Math.Floor(theta / 360 + 0.9 / 360);
        return theta;
    }

    private static bool TransposesFor(int[] m) =>
        ThetaFor(m) is { } t && (Math.Abs(t - 90) < 1.0 || Math.Abs(t - 270) < 1.0);

    /// <summary>A mirrored matrix (negative 2x2 determinant): ffmpeg's autorotate flips it on decode.</summary>
    private static bool FlippedFor(int[] m) => (long)m[0] * m[4] - (long)m[1] * m[3] < 0;

    private static bool AltersPixelsFor(int[] m) =>
        // ffmpeg autorotate: flip, or rotate whenever fabs(theta) > 1.0 after normalising to [-0.9, 359.1).
        FlippedFor(m) || (ThetaFor(m) is { } t && Math.Abs(t) > 1.0);

    private static bool AltersPixels(JsonElement videoStream)
    {
        if (!videoStream.TryGetProperty("side_data_list", out JsonElement sd) || sd.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement entry in sd.EnumerateArray())
        {
            if (GetString(entry, "side_data_type") != "Display Matrix") continue;
            string? text = GetString(entry, "displaymatrix");
            return text is not null && TryParseMatrix(text, out int[] m) && AltersPixelsFor(m);
        }

        return false;
    }

    /// <summary>
    /// True when ffmpeg's autorotate changes the decoded pixels in ANY way: a quarter turn, a 180, any other
    /// angle, or a mirror (hflip/vflip). Wider than <see cref="TransposesOnDecode"/> (which is only about the
    /// frame size swapping). Malformed JSON or no matrix is false.
    /// </summary>
    public static bool AltersPixelsOnDecode(string? ffprobeJson)
    {
        if (string.IsNullOrWhiteSpace(ffprobeJson)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ffprobeJson);
            if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.ValueKind != JsonValueKind.Array)
                return false;
            foreach (JsonElement st in streams.EnumerateArray())
            {
                if (GetString(st, "codec_type") != "video") continue;
                return AltersPixels(st);
            }
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>ffprobe prints "unknown" for an untagged colour property; that is "not known", not a value.</summary>
    private static string? NullIfUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : value;

    /// <summary>
    /// Parses an exact rational like <c>"30000/1001"</c> or <c>"30/1"</c> into its numerator and
    /// denominator without ever collapsing through a <c>double</c> (R9). A malformed or
    /// zero-denominator value is rejected rather than guessed at.
    /// </summary>
    public static bool TryParseRational(string value, out int numerator, out int denominator)
    {
        numerator = 0;
        denominator = 1;

        string[] parts = value.Split('/');
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int num))
            return false;
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int den))
            return false;
        if (den == 0)
            return false;

        numerator = num;
        denominator = den;
        return true;
    }

    private static bool TryParseInvariantDouble(JsonElement element, out double value)
    {
        value = 0;
        string? raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// A stream/format <c>tags</c> value by key, ignoring the key's case: tags are free text written
    /// by whichever muxer produced the file (matroska writes <c>ALPHA_MODE</c> on a remux,
    /// <c>alpha_mode</c> straight out of the encoder). The exact key wins when several cases are
    /// present, so the result is deterministic; otherwise the first case-insensitive match. String
    /// values only, as with <see cref="GetString"/>.
    /// </summary>
    private static string? GetTagString(JsonElement tags, string name)
    {
        if (tags.ValueKind != JsonValueKind.Object)
            return null;

        if (tags.TryGetProperty(name, out JsonElement exact))
            return exact.ValueKind == JsonValueKind.String ? exact.GetString() : null;

        foreach (JsonProperty property in tags.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }

        return null;
    }

    /// <summary>
    /// The raw text of a numeric ffprobe field that may arrive EITHER as a JSON number
    /// (<c>channels</c>) or as a JSON string (<c>sample_rate</c>, <c>bit_rate</c>) — ffprobe is
    /// consistent about neither, and a bare <c>TryGetInt32</c> silently yields null for the string
    /// form. Null when the field is absent or is of any other JSON kind.
    /// </summary>
    private static string? GetNumericRaw(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }

    /// <summary>
    /// A channel count. A missing field, a non-numeric value (including ffprobe's literal
    /// <c>"N/A"</c>) or a non-positive reading all yield null, <b>never 0</b>: this parser runs on
    /// every analyzed source and must neither throw nor turn "ffprobe did not say" into a number a
    /// downstream consumer would present as a fact.
    /// </summary>
    private static int? GetIntLikeInt(JsonElement element, string propertyName)
    {
        string? raw = GetNumericRaw(element, propertyName);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : null;
    }

    /// <summary>
    /// A bit rate in bits per second. Same absent/non-numeric/non-positive → null rule as
    /// <see cref="GetIntLikeInt"/> — a rate is never legitimately zero, and <c>long</c> because a
    /// high-bitrate multi-megabit stream can exceed <see cref="int"/>'s range.
    /// </summary>
    private static long? GetIntLikeLong(JsonElement element, string propertyName)
    {
        string? raw = GetNumericRaw(element, propertyName);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int i)
            ? i
            : null;
}
