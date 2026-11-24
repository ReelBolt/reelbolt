using System.Globalization;
using System.Text;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The media-owned name of a derivative. Deliberately its OWN enum rather than
/// <c>ReelBolt.Shared.Data.Models.MediaDerivativeKind</c>: that shared enum does not yet carry
/// <c>Filmstrip</c>/<c>WaveformPeaks</c>/<c>KeyframeIndex</c>, and the <c>media_derivatives</c>
/// row does not yet carry the cache-identity columns. Both are backend-owned (T-302). Naming
/// derivatives by a media-owned slug keeps this half of the wave independent of a contract that
/// has not landed, and keeps <see cref="MediaDerivativeSpec.ParamsJson"/> stable regardless of how
/// the shared enum ends up being spelled. T-302 maps slugs to the enum and persists rows.
/// </summary>
public enum MediaDerivativeKindSlug
{
    Proxy,
    Filmstrip,
    WaveformPeaks,
    KeyframeIndex,

    /// <summary>Full-size JPEG stills, on demand (T-101). Appended.</summary>
    Stills,

    /// <summary>The low-res stabilised preview proxy (editor-wysiwyg). Appended.</summary>
    StabilizedProxy
}

/// <summary>
/// The closed parameter catalogue for one media derivative: which kind, which variant of it, the
/// canonical JSON of every encoder knob, and the version of the argv builder that turned those
/// knobs into bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trust boundary (plan §3.7 / Global Constraint #12).</b> In this phase every derivative
/// parameter is server-chosen. A caller can select only a <see cref="Slug"/> and a
/// <see cref="Variant"/> index — never a filter name, a number, or a string. Every factory below
/// derives its numbers from first-party constants, from the source's own measured properties
/// (probe output), or from <see cref="VideoEditingOptions"/>. There is no constructor path that
/// accepts free-form text, and no filtergraph is ever assembled from caller input.
/// </para>
/// <para>
/// <b>Canonicalization matters.</b> <see cref="ParamsJson"/> is emitted with keys sorted
/// ordinally, every number formatted with <see cref="CultureInfo.InvariantCulture"/>, and no
/// whitespace, so the hash over it is byte-identical across machines, locales and processes.
/// <see cref="MediaDerivativeCacheKey"/> hashes exactly this string.
/// </para>
/// </remarks>
public sealed record MediaDerivativeSpec(
    MediaDerivativeKindSlug Slug,

    /// <summary>
    /// Filmstrip density index (0 → 1 s, 1 → 5 s, 2 → 30 s) / waveform-peaks LOD index
    /// (0 → 20 ms, 1 → 100 ms, 2 → 1000 ms). Always 0 for a kind that has exactly one variant.
    /// </summary>
    int Variant,

    /// <summary>Canonical, key-sorted, invariant-culture JSON of every encoder knob.</summary>
    string ParamsJson,

    /// <summary>Bump when the argv builder changes in a way that changes produced bytes.</summary>
    int BuilderVersion)
{
    // ---------------------------------------------------------------------------------------
    // Builder versions. Each is independent: changing the proxy argv must not invalidate every
    // filmstrip already in the object store.
    //
    // v3 (hdr-tonemap-v3): the HLG delivery chain moved to mobius knee 0.2 (HdrToneMap.Filter("hlg")).
    // The tone-map version is not part of ParamsJson — the factories below describe encoder knobs,
    // and the kind is resolved from the source's probe when the argv is built — so the builder
    // version is the only component that can invalidate an HLG proxy, filmstrip or still cached
    // under knee 0.5. Bumping it re-produces every cached derivative once (the key is computed
    // before the source is probed, so invalidating only HDR sources is not cheap).
    // ---------------------------------------------------------------------------------------

    public const int ProxyBuilderVersion = 3;
    public const int FilmstripBuilderVersion = 3;
    public const int WaveformPeaksBuilderVersion = 1;
    public const int KeyframeIndexBuilderVersion = 1;
    public const int StillsBuilderVersion = 3;
    public const int StabilizedProxyBuilderVersion = 1;

    // ---------------------------------------------------------------------------------------
    // Proxy knobs (all first-party constants except the two the source itself dictates:
    // target height from the source's own height, and the GOP length from its real fps).
    // ---------------------------------------------------------------------------------------

    /// <summary>Target proxy height when the source is below 2160p.</summary>
    public const int ProxyHeightBelowUhd = 540;

    /// <summary>Target proxy height when the source is 2160p or larger.</summary>
    public const int ProxyHeightUhd = 720;

    /// <summary>
    /// The <c>-2</c> width sentinel: "derive the width from the height, rounded to an even
    /// number, preserving aspect ratio". Never a computed pixel width — a hand-computed width
    /// would break on anamorphic sources.
    /// </summary>
    public const int ProxyWidthSentinel = -2;

    public const string ProxyScaleFlags = "bicubic";
    public const string ProxyVideoCodec = "libx264";
    public const string ProxyPreset = "veryfast";
    public const int ProxyCrf = 23;
    public const string ProxyProfile = "main";
    public const string ProxyPixFmt = "yuv420p";
    public const int ProxyScThreshold = 0;
    public const string ProxyAudioCodec = "aac";
    public const int ProxyAudioBitrateKbps = 128;
    public const int ProxyAudioSampleRate = 48000;
    public const int ProxyAudioChannels = 2;
    public const bool ProxyFaststart = true;

    // ---------------------------------------------------------------------------------------
    // Filmstrip knobs.
    // ---------------------------------------------------------------------------------------

    /// <summary>The three filmstrip densities, indexed by <see cref="Variant"/>.</summary>
    public static readonly int[] FilmstripDensitySeconds = [1, 5, 30];

    public const int FilmstripThumbWidth = 160;
    public const int FilmstripQuality = 5;
    public const string FilmstripCodec = "mjpeg";

    // ---------------------------------------------------------------------------------------
    // Waveform-peaks knobs.
    // ---------------------------------------------------------------------------------------

    /// <summary>The three peak LOD bucket widths in milliseconds, indexed by <see cref="Variant"/>.</summary>
    public static readonly int[] PeaksLodMs = [20, 100, 1000];

    public const int PeaksSampleRate = 48000;

    /// <summary>
    /// Peaks are always decoded as a fixed 2-channel 48 kHz s16le stream, server-chosen rather
    /// than taken from the source: a mono source is upmixed and a 5.1 source downmixed, so one
    /// spec describes one byte layout regardless of what the source happens to carry. This is
    /// what keeps <see cref="ParamsJson"/> — and therefore the cache key — independent of the
    /// source's channel layout.
    /// </summary>
    public const int PeaksChannels = 2;

    // ---------------------------------------------------------------------------------------
    // Keyframe-index knobs.
    // ---------------------------------------------------------------------------------------

    /// <summary>The video stream the keyframe packet probe selects.</summary>
    public const string KeyframeStreamSelector = "v:0";

    // ---------------------------------------------------------------------------------------
    // Stills knobs.
    // ---------------------------------------------------------------------------------------

    /// <summary>How many evenly spaced stills a set without requested times holds (first and last frame included).</summary>
    public const int StillsEvenCount = 12;

    /// <summary>The longest side a still may have, in pixels; a smaller source is never upscaled.</summary>
    public const int StillsLongSide = 1280;

    public const int StillsQuality = 3;
    public const string StillsCodec = "mjpeg";

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the one proxy spec for a source. <paramref name="sourceHeight"/>, the fps rational
    /// and <paramref name="hasAudio"/> all come from the ffprobe result of the source; the rest
    /// are first-party constants. <paramref name="threads"/> is
    /// <see cref="VideoEditingOptions.FfmpegThreads"/>.
    /// </summary>
    public static MediaDerivativeSpec ForProxy(
        int sourceHeight, int fpsNum, int fpsDen, bool hasAudio, int threads, int sourceWidth = 0,
        int cameraProfileVariant = 0)
    {
        // Variant 0 is the plain proxy (byte-identical spec and key to before). 1..N is the converted proxy of
        // CameraProfiles.All[variant - 1], with the recipe token in the spec. A number, never text: the closed catalogue.
        int variant = cameraProfileVariant;
        string? cameraProfileId = null;
        if (variant != 0 && !ReelBolt.Shared.Editing.Color.CameraProfileVariants.TryProfileOf(variant, out cameraProfileId))
            throw new ArgumentOutOfRangeException(nameof(cameraProfileVariant), variant, "Unknown camera profile variant.");

        int gop = ComputeProxyGopFrames(fpsNum, fpsDen);

        if (IsPortraitProxy(sourceWidth, sourceHeight))
        {
            // Portrait: the SHORT side is the width, so the width is the scaled dimension and the
            // height follows with the -2 sentinel. Distinct keys => distinct params hash, so a
            // previously built 304x540 portrait proxy no longer matches and is rebuilt.
            CanonicalJsonObject portrait = new CanonicalJsonObject()
                .AddNumber("audioBitrateKbps", ProxyAudioBitrateKbps)
                .AddNumber("audioChannels", ProxyAudioChannels)
                .AddBool("audioCodec", hasAudio)
                .AddNumber("audioSampleRate", ProxyAudioSampleRate)
                .AddNumber("crf", ProxyCrf)
                .AddBool("faststart", ProxyFaststart)
                .AddNumber("gopFrames", gop)
                .AddNumber("heightSentinel", ProxyWidthSentinel)
                .AddNumber("keyintMin", gop)
                .AddString("pixFmt", ProxyPixFmt)
                .AddString("preset", ProxyPreset)
                .AddString("profile", ProxyProfile)
                .AddString("scaleFlags", ProxyScaleFlags)
                .AddNumber("scThreshold", ProxyScThreshold)
                .AddNumber("targetWidth", ProxyTargetSide(sourceWidth, sourceHeight))
                .AddNumber("threads", threads)
                .AddString("videoCodec", ProxyVideoCodec);

            return new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, variant, WithProfile(portrait, cameraProfileId), ProxyBuilderVersion);
        }

        int targetHeight = sourceHeight >= 2160 ? ProxyHeightUhd : ProxyHeightBelowUhd;

        CanonicalJsonObject json = new CanonicalJsonObject()
            .AddNumber("audioBitrateKbps", ProxyAudioBitrateKbps)
            .AddNumber("audioChannels", ProxyAudioChannels)
            .AddBool("audioCodec", hasAudio)
            .AddNumber("audioSampleRate", ProxyAudioSampleRate)
            .AddNumber("crf", ProxyCrf)
            .AddBool("faststart", ProxyFaststart)
            .AddNumber("gopFrames", gop)
            .AddNumber("keyintMin", gop)
            .AddString("pixFmt", ProxyPixFmt)
            .AddString("preset", ProxyPreset)
            .AddString("profile", ProxyProfile)
            .AddString("scaleFlags", ProxyScaleFlags)
            .AddNumber("scThreshold", ProxyScThreshold)
            .AddNumber("targetHeight", targetHeight)
            .AddNumber("threads", threads)
            .AddString("videoCodec", ProxyVideoCodec)
            .AddNumber("widthSentinel", ProxyWidthSentinel);

        return new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, variant, WithProfile(json, cameraProfileId), ProxyBuilderVersion);
    }

    /// <summary>
    /// The stabilised preview proxy spec: the plain proxy's size, GOP and encoder knobs with the audio dropped,
    /// plus the <c>vidstab</c> recipe (every parameter and <see cref="TimelineRender.StabilizeFilter.RecipeVersion"/>),
    /// so a recipe change is a new cache key. One variant. Numbers and first-party strings only.
    /// </summary>
    public static MediaDerivativeSpec ForStabilizedProxy(
        int sourceHeight, int fpsNum, int fpsDen, int threads, int sourceWidth = 0)
    {
        int gop = ComputeProxyGopFrames(fpsNum, fpsDen);
        bool portrait = IsPortraitProxy(sourceWidth, sourceHeight);

        CanonicalJsonObject json = new CanonicalJsonObject()
            .AddNumber("crf", ProxyCrf)
            .AddBool("faststart", ProxyFaststart)
            .AddNumber("gopFrames", gop)
            .AddNumber("keyintMin", gop)
            .AddString("pixFmt", ProxyPixFmt)
            .AddString("preset", ProxyPreset)
            .AddString("profile", ProxyProfile)
            .AddString("scaleFlags", ProxyScaleFlags)
            .AddNumber("scThreshold", ProxyScThreshold)
            .AddNumber("threads", threads)
            .AddString("videoCodec", ProxyVideoCodec)
            .AddNumber("stabilizeAccuracy", TimelineRender.StabilizeFilter.Accuracy)
            .AddString("stabilizeInterpol", TimelineRender.StabilizeFilter.Interpolation)
            .AddNumber("stabilizeOptzoom", TimelineRender.StabilizeFilter.OptZoom)
            .AddNumber("stabilizeRecipe", TimelineRender.StabilizeFilter.RecipeVersion)
            .AddNumber("stabilizeShakiness", TimelineRender.StabilizeFilter.Shakiness)
            .AddNumber("stabilizeSmoothing", TimelineRender.StabilizeFilter.Smoothing);

        if (portrait)
            json.AddNumber("heightSentinel", ProxyWidthSentinel).AddNumber("targetWidth", ProxyTargetSide(sourceWidth, sourceHeight));
        else
            json.AddNumber("widthSentinel", ProxyWidthSentinel).AddNumber("targetHeight", sourceHeight >= 2160 ? ProxyHeightUhd : ProxyHeightBelowUhd);

        return new MediaDerivativeSpec(MediaDerivativeKindSlug.StabilizedProxy, 0, json.ToJson(), StabilizedProxyBuilderVersion);
    }

    private static string WithProfile(CanonicalJsonObject json, string? cameraProfileId)
    {
        if (cameraProfileId is not null)
        {
            json.AddString("cameraProfile", cameraProfileId)
                .AddString("recipe", ReelBolt.Shared.Editing.Color.CameraProfileVariants.RecipeToken(cameraProfileId));
        }

        return json.ToJson();
    }

    /// <summary>True when the proxy scales the width (portrait source, known width); false keeps the legacy height spec.</summary>
    public static bool IsPortraitProxy(int sourceWidth, int sourceHeight) =>
        sourceWidth > 0 && sourceHeight > sourceWidth;

    /// <summary>The proxy's target for the source's SHORT side: 720 once it is &gt;= 2160, else 540.</summary>
    public static int ProxyTargetSide(int sourceWidth, int sourceHeight)
    {
        int shortSide = IsPortraitProxy(sourceWidth, sourceHeight) ? sourceWidth : sourceHeight;
        return shortSide >= 2160 ? ProxyHeightUhd : ProxyHeightBelowUhd;
    }

    /// <summary>
    /// The GOP length the proxy uses: <c>Math.Max(1, (int)Math.Round(fps * 0.5))</c> — "≈ 0.5 s".
    /// Derived from the source's REAL frame rate, never a constant 15/30: a 120 fps source needs a
    /// 60-frame GOP to keep the same half-second seek granularity a 30 fps source gets from 15.
    /// </summary>
    public static int ComputeProxyGopFrames(int fpsNum, int fpsDen)
    {
        int den = fpsDen <= 0 ? 1 : fpsDen;
        double fps = Math.Max(0, fpsNum) / (double)den;
        return Math.Max(1, (int)Math.Round(fps * 0.5, MidpointRounding.AwayFromZero));
    }

    /// <summary>The filmstrip spec for a density index. Out-of-range variants clamp rather than throw.</summary>
    public static MediaDerivativeSpec ForFilmstrip(int variant)
    {
        int clamped = Math.Clamp(variant, 0, FilmstripDensitySeconds.Length - 1);
        int densitySec = FilmstripDensitySeconds[clamped];

        var json = new CanonicalJsonObject()
            .AddNumber("cols", FilmstripIndex.Cols)
            .AddNumber("densitySec", densitySec)
            .AddNumber("quality", FilmstripQuality)
            .AddNumber("rows", FilmstripIndex.Rows)
            .AddNumber("thumbWidth", FilmstripThumbWidth)
            .AddString("videoCodec", FilmstripCodec)
            .ToJson();

        return new MediaDerivativeSpec(MediaDerivativeKindSlug.Filmstrip, clamped, json, FilmstripBuilderVersion);
    }

    /// <summary>The waveform-peaks spec for an LOD index. Out-of-range variants clamp rather than throw.</summary>
    public static MediaDerivativeSpec ForWaveformPeaks(int variant)
    {
        int clamped = Math.Clamp(variant, 0, PeaksLodMs.Length - 1);

        var json = new CanonicalJsonObject()
            .AddNumber("channels", PeaksChannels)
            .AddString("layout", "ch,min,max")
            .AddRaw("lodsMs", CanonicalJsonObject.NumberArray(PeaksLodMs))
            .AddNumber("sampleRate", PeaksSampleRate)
            .AddString("sampleFormat", "s16le")
            .ToJson();

        return new MediaDerivativeSpec(
            MediaDerivativeKindSlug.WaveformPeaks, clamped, json, WaveformPeaksBuilderVersion);
    }

    /// <summary>The keyframe-index spec. One variant: the packet-level keyframe times of the whole file.</summary>
    public static MediaDerivativeSpec ForKeyframeIndex()
    {
        var json = new CanonicalJsonObject()
            .AddString("source", "packets")
            .AddString("stream", KeyframeStreamSelector)
            .ToJson();

        return new MediaDerivativeSpec(
            MediaDerivativeKindSlug.KeyframeIndex, 0, json, KeyframeIndexBuilderVersion);
    }

    /// <summary>
    /// The stills spec. Variant 0 is the evenly spaced set; variant 1 is a set at
    /// <paramref name="timesMs"/> — whole milliseconds, already normalized by
    /// <c>MediaDerivativeStillTimes.NormalizeToMs</c> (numbers only, never text, so nothing a caller
    /// sends can reach argv as anything but a formatted seek time). The requested times are in the
    /// params JSON as asked, before the writer clamps them to the source's duration, so the cache is
    /// keyed by (source, times) exactly as a caller can name them.
    /// </summary>
    public static MediaDerivativeSpec ForStills(IReadOnlyList<int> timesMs)
    {
        ArgumentNullException.ThrowIfNull(timesMs);
        int[] times = [.. timesMs.Where(t => t >= 0).Distinct().Order().Take(StillsEvenCount)];

        var json = new CanonicalJsonObject()
            .AddNumber("longSide", StillsLongSide)
            .AddNumber("quality", StillsQuality)
            .AddString("videoCodec", StillsCodec);

        if (times.Length == 0)
            json.AddNumber("evenCount", StillsEvenCount);
        else
            json.AddRaw("timesMs", CanonicalJsonObject.NumberArray(times));

        return new MediaDerivativeSpec(
            MediaDerivativeKindSlug.Stills, times.Length == 0 ? 0 : 1, json.ToJson(), StillsBuilderVersion);
    }

    /// <summary>The requested times of a stills spec, in milliseconds; empty for the evenly spaced set (or any other kind).</summary>
    public static IReadOnlyList<int> StillTimesMsOf(MediaDerivativeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Slug != MediaDerivativeKindSlug.Stills) return [];

        using var document = System.Text.Json.JsonDocument.Parse(spec.ParamsJson);
        if (!document.RootElement.TryGetProperty("timesMs", out System.Text.Json.JsonElement times)) return [];
        return [.. times.EnumerateArray().Select(t => t.GetInt32())];
    }
}

/// <summary>
/// Minimal canonical-JSON writer: keys sorted ordinally, values emitted as JSON literals, no
/// whitespace. Purpose-built for <see cref="MediaDerivativeSpec.ParamsJson"/> so the hash over it
/// is stable across machines and locales — a <c>Dictionary</c>'s or an anonymous type's member
/// order would make that a property of the runtime rather than of this file.
/// </summary>
internal sealed class CanonicalJsonObject
{
    private readonly SortedDictionary<string, string> _members = new(StringComparer.Ordinal);

    public CanonicalJsonObject AddString(string key, string value) =>
        AddRaw(key, "\"" + Escape(value) + "\"");

    public CanonicalJsonObject AddNumber(string key, int value) =>
        AddRaw(key, value.ToString(CultureInfo.InvariantCulture));

    public CanonicalJsonObject AddBool(string key, bool value) =>
        AddRaw(key, value ? "true" : "false");

    public CanonicalJsonObject AddRaw(string key, string literal)
    {
        _members[key] = literal;
        return this;
    }

    public static string NumberArray(IEnumerable<int> values) =>
        "[" + string.Join(',', values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

    public string ToJson()
    {
        var builder = new StringBuilder("{");
        bool first = true;
        foreach (KeyValuePair<string, string> member in _members)
        {
            if (!first)
                builder.Append(',');
            first = false;
            builder.Append('"').Append(Escape(member.Key)).Append("\":").Append(member.Value);
        }

        return builder.Append('}').ToString();
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
