using System.Globalization;

namespace ReelBolt.Inference.Api.Services.SfxLibrary;

/// <summary>
/// One synthesized sound: identity and metadata plus the ffmpeg filtergraph that renders it. The graph is
/// built by first-party code from the numbers below (seeded noise, fixed filters), so the same recipe
/// version always yields the same argv.
/// </summary>
public sealed record SfxRecipe(
    string Slug,
    string Title,
    string Category,
    IReadOnlyList<string> Tags,
    int DurationMs,
    string Graph);

/// <summary>
/// The first-party synthesized sound-effects pack (docs/sfx-library.md): a pure recipe table, no audio
/// in git. Every item is rendered by ffmpeg from <c>aevalsrc</c> tones and seeded <c>anoisesrc</c> noise,
/// shaped with fixed filters, faded and loudness-normalised to one level. Changing any number or filter
/// here changes the sound: bump <see cref="Version"/> so the seeder re-renders.
/// </summary>
public static class SfxSynthRecipes
{
    /// <summary>Recorded on every synthesized item as its provenance (<c>first-party synthesized, recipe v1</c>).</summary>
    public const string Version = "v1";

    public const string Licence = "ReelBolt-original";

    public const string Provenance = "first-party synthesized, recipe " + Version;

    public const int SampleRate = 48000;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N(double v) => v.ToString("0.######", Inv);

    /// <summary>The recipe table, in library order.</summary>
    public static IReadOnlyList<SfxRecipe> All { get; } = Build();

    /// <summary>
    /// The ffmpeg arguments (no shell, one argv element each) that render <paramref name="recipe"/> to the WAV at
    /// <paramref name="outputPath"/>: mono, 48 kHz, 16-bit PCM, no metadata, bit-exact muxing.
    /// </summary>
    public static IReadOnlyList<string> BuildArgv(SfxRecipe recipe, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-bitexact",
        "-filter_complex", recipe.Graph,
        "-map", "[o]",
        "-ar", SampleRate.ToString(Inv), "-ac", "1", "-c:a", "pcm_s16le",
        "-fflags", "+bitexact", "-flags:a", "+bitexact", "-map_metadata", "-1",
        "-f", "wav", outputPath
    ];

    // ---- graph pieces ----

    /// <summary>A sine tone sweeping f0 to f1 with an exponential decay and a 1.5 ms attack.</summary>
    private static string Tone(string label, double f0, double f1, double dur, double decay, double amp) =>
        $"aevalsrc='{N(amp)}*sin(2*PI*({N(f0)}*t+({N(f1)}-{N(f0)})*t*t/(2*{N(dur)})))*(1-exp(-t*700))*exp(-{N(decay)}*t)':s={SampleRate}:d={N(dur)}[{label}]";

    /// <summary>A sum of partials (freq, relative level) with one decay, optionally started late.</summary>
    private static string Partials(string label, double dur, double decay, double amp, double start, params (double F, double L)[] parts)
    {
        string body = string.Join("+", parts.Select(p => $"{N(p.L)}*sin(2*PI*{N(p.F)}*t)"));
        return $"aevalsrc='{N(amp)}*gt(t,{N(start)})*({body})*exp(-{N(decay)}*max(0,t-{N(start)}))':s={SampleRate}:d={N(dur)}[{label}]";
    }

    /// <summary>Seeded noise shaped by a volume envelope and band limits.</summary>
    private static string Noise(string label, double dur, string color, int seed, string envelope, double hp, double lp, double amp = 0.9) =>
        $"anoisesrc=d={N(dur)}:c={color}:r={SampleRate}:a={N(amp)}:seed={seed}," +
        $"volume='{envelope}':eval=frame,highpass=f={N(hp)},lowpass=f={N(lp)}[{label}]";

    /// <summary>A train of exponentially decaying clicks at the given offsets (seconds).</summary>
    private static string ClickEnvelope(double decay, params double[] offsets) =>
        string.Join("+", offsets.Select(o => $"gt(t,{N(o)})*exp(-{N(decay)}*max(0,t-{N(o)}))"));

    private static string Bell(double dur) => $"pow(sin(PI*t/{N(dur)}),2)";

    /// <summary>Mixes labelled parts, fades the ends, loudness-normalises to the one library level and fixes the format.</summary>
    private static string Finish(double dur, params string[] parts)
    {
        // Every part ends with "[label]"; the labels are mixed together, so callers never repeat a name.
        string labels = string.Concat(parts.Select(p => p[p.LastIndexOf('[')..]));
        double fadeOut = Math.Min(0.06, dur / 4);
        string mix = parts.Length == 1 ? string.Empty : $"amix=inputs={parts.Length}:duration=longest:normalize=0,";
        string tail =
            mix +
            $"afade=t=in:st=0:d=0.003,afade=t=out:st={N(dur - fadeOut)}:d={N(fadeOut)}," +
            "loudnorm=I=-20:TP=-3:LRA=7," +
            $"aresample={SampleRate},aformat=sample_fmts=s16:channel_layouts=mono,atrim=0:{N(dur)},asetpts=N/SR/TB[o]";
        return string.Join(';', parts) + ";" + labels + tail;
    }

    private static SfxRecipe R(string slug, string title, string category, string[] tags, double dur, params string[] parts) =>
        new(slug, title, category, tags, (int)Math.Round(dur * 1000), Finish(dur, parts));

    private static IReadOnlyList<SfxRecipe> Build()
    {
        List<SfxRecipe> all = [];

        // pop: soft, bubbly and bright
        all.Add(R("pop-soft", "Soft pop", "pop", ["pop", "appear", "text_reveal"], 0.18,
            Tone("a", 520, 180, 0.18, 22, 0.9)));
        all.Add(R("pop-bubble", "Bubble pop", "pop", ["pop", "appear", "disappear"], 0.22,
            Tone("a", 300, 900, 0.22, 16, 0.9)));
        all.Add(R("pop-bright", "Bright pop", "pop", ["pop", "appear", "flash"], 0.16,
            Tone("a", 1400, 500, 0.16, 28, 0.8),
            Noise("b", 0.16, "white", 11, ClickEnvelope(260, 0), 3000, 9000, 0.4)));

        // click
        all.Add(R("click-soft", "Soft click", "click", ["click", "pop", "text_reveal"], 0.15,
            Noise("a", 0.15, "pink", 21, ClickEnvelope(220, 0), 1500, 7000)));
        all.Add(R("click-tick", "Sharp tick", "click", ["click", "text_reveal", "hit"], 0.15,
            Noise("a", 0.15, "white", 22, ClickEnvelope(420, 0), 3500, 10000),
            Tone("b", 2600, 2200, 0.15, 90, 0.35)));
        all.Add(R("click-double", "Double click", "click", ["click", "appear", "transition"], 0.25,
            Noise("a", 0.25, "pink", 23, ClickEnvelope(260, 0, 0.09), 1800, 8000)));

        // whoosh, short and long
        all.Add(R("whoosh-short-a", "Short whoosh", "whoosh", ["whoosh", "transition", "appear", "disappear"], 0.5,
            Noise("a", 0.5, "pink", 31, Bell(0.5), 500, 7000),
            Tone("b", 300, 1800, 0.5, 4, 0.12)));
        all.Add(R("whoosh-short-b", "Airy whoosh", "whoosh", ["whoosh", "transition", "text_reveal"], 0.45,
            Noise("a", 0.45, "white", 32, Bell(0.45), 1200, 9000, 0.7)));
        all.Add(R("whoosh-short-c", "Low whoosh", "whoosh", ["whoosh", "transition", "scene_swap"], 0.6,
            Noise("a", 0.6, "brown", 33, Bell(0.6), 120, 2500),
            Tone("b", 140, 520, 0.6, 3, 0.15)));
        all.Add(R("whoosh-long-a", "Long whoosh", "whoosh", ["whoosh", "transition", "scene_swap"], 1.2,
            Noise("a", 1.2, "pink", 34, Bell(1.2), 300, 6500),
            Tone("b", 200, 1500, 1.2, 2, 0.1)));
        all.Add(R("whoosh-long-b", "Slow sweep whoosh", "whoosh", ["whoosh", "transition", "scene_swap", "disappear"], 2.0,
            Noise("a", 2.0, "pink", 35, Bell(2.0), 200, 5000),
            Tone("b", 120, 900, 2.0, 1.5, 0.1)));

        // swoosh in / out
        all.Add(R("swoosh-in", "Swoosh in", "swoosh", ["swoosh", "appear", "text_reveal", "transition"], 0.5,
            Noise("a", 0.5, "pink", 41, "pow(t/0.5,2)", 600, 7500)));
        all.Add(R("swoosh-in-soft", "Soft swoosh in", "swoosh", ["swoosh", "appear", "text_reveal"], 0.4,
            Noise("a", 0.4, "pink", 42, "pow(t/0.4,1.5)", 900, 5000, 0.6)));
        all.Add(R("swoosh-out", "Swoosh out", "swoosh", ["swoosh", "disappear", "transition"], 0.5,
            Noise("a", 0.5, "pink", 43, "pow(1-t/0.5,2)", 600, 7500)));
        all.Add(R("swoosh-out-soft", "Soft swoosh out", "swoosh", ["swoosh", "disappear"], 0.4,
            Noise("a", 0.4, "pink", 44, "pow(1-t/0.4,1.5)", 900, 5000, 0.6)));

        // ding / chime
        all.Add(R("ding-bell", "Bell ding", "ding", ["ding", "appear", "flash", "hit"], 1.2,
            Partials("a", 1.2, 4.5, 0.8, 0, (1318, 1), (3637, 0.35), (7116, 0.12))));
        all.Add(R("ding-soft", "Soft ding", "ding", ["ding", "appear", "text_reveal"], 0.8,
            Partials("a", 0.8, 7, 0.8, 0, (988, 1), (1976, 0.3))));
        all.Add(R("chime-up", "Rising chime", "ding", ["chime", "appear", "flash"], 1.5,
            Partials("a", 1.5, 3.5, 0.5, 0, (784, 1), (1568, 0.2)),
            Partials("b", 1.5, 3.5, 0.5, 0.12, (988, 1), (1976, 0.2)),
            Partials("c", 1.5, 3.5, 0.5, 0.24, (1319, 1), (2637, 0.2))));
        all.Add(R("chime-soft", "Soft chime", "ding", ["chime", "appear", "scene_swap"], 1.4,
            Partials("a", 1.4, 4, 0.6, 0, (659, 1), (1318, 0.25)),
            Partials("b", 1.4, 4, 0.6, 0.16, (880, 1), (1760, 0.25))));

        // stamp / thud / impact
        all.Add(R("stamp-ink", "Ink stamp", "stamp", ["stamp", "hit", "appear"], 0.35,
            Tone("a", 160, 70, 0.35, 14, 0.9),
            Noise("b", 0.35, "white", 51, ClickEnvelope(110, 0), 1200, 6000, 0.6)));
        all.Add(R("stamp-hard", "Hard stamp", "stamp", ["stamp", "hit", "appear"], 0.4,
            Tone("a", 190, 55, 0.4, 11, 0.95),
            Noise("b", 0.4, "white", 52, ClickEnvelope(70, 0), 900, 8000, 0.8)));
        all.Add(R("thud-soft", "Soft thud", "thud", ["thud", "hit", "disappear"], 0.4,
            Tone("a", 110, 48, 0.4, 10, 0.95),
            Noise("b", 0.4, "brown", 53, ClickEnvelope(60, 0), 40, 500, 0.5)));
        all.Add(R("impact-soft", "Soft impact", "impact", ["impact", "hit", "scene_swap"], 0.7,
            Tone("a", 95, 40, 0.7, 6, 0.95),
            Noise("b", 0.7, "brown", 54, ClickEnvelope(30, 0), 40, 900, 0.7)));
        all.Add(R("impact-hard", "Hard impact", "impact", ["impact", "hit", "flash", "scene_swap"], 1.0,
            Tone("a", 120, 36, 1.0, 5, 1.0),
            Noise("b", 1.0, "pink", 55, ClickEnvelope(24, 0), 60, 5000, 0.9)));

        // shutter / camera
        all.Add(R("shutter-click", "Camera shutter", "shutter", ["shutter", "flash", "hit", "appear"], 0.3,
            Noise("a", 0.3, "white", 61, ClickEnvelope(180, 0, 0.08), 900, 7500),
            Tone("b", 1800, 900, 0.3, 40, 0.25)));
        all.Add(R("shutter-flash", "Shutter with flash", "shutter", ["shutter", "flash", "transition"], 0.5,
            Noise("a", 0.5, "white", 62, ClickEnvelope(160, 0, 0.09), 900, 7500),
            Partials("b", 0.5, 8, 0.25, 0.1, (4200, 1), (6300, 0.4))));

        // riser (short)
        all.Add(R("riser-short", "Short riser", "riser", ["riser", "transition", "scene_swap"], 1.5,
            Noise("a", 1.5, "pink", 71, "pow(t/1.5,2)", 400, 8000, 0.8),
            Tone("b", 200, 2400, 1.5, 0.0, 0.18)));
        all.Add(R("riser-tonal", "Tonal riser", "riser", ["riser", "transition", "text_reveal"], 1.2,
            Tone("a", 330, 1320, 1.2, 0.0, 0.5),
            Tone("b", 495, 1980, 1.2, 0.0, 0.25)));

        // glitch / zap
        all.Add(R("glitch-stutter", "Glitch stutter", "glitch", ["glitch", "transition", "hit", "flash"], 0.35,
            $"anoisesrc=d=0.35:c=white:r={SampleRate}:a=0.9:seed=81," +
            "volume='gt(sin(2*PI*38*t),0)*gt(sin(2*PI*11*t),-0.3)':eval=frame," +
            "acrusher=bits=5:mode=log:aa=0,highpass=f=600,lowpass=f=9000[a]"));
        all.Add(R("glitch-digital", "Digital glitch", "glitch", ["glitch", "transition", "disappear"], 0.4,
            Tone("a", 2200, 400, 0.4, 5, 0.5),
            $"anoisesrc=d=0.4:c=pink:r={SampleRate}:a=0.9:seed=82," +
            "volume='gt(sin(2*PI*27*t),0.2)':eval=frame,acrusher=bits=4:mode=lin:aa=0,highpass=f=800,lowpass=f=8000[b]"));
        all.Add(R("zap-laser", "Laser zap", "zap", ["zap", "hit", "flash", "appear"], 0.35,
            Tone("a", 3200, 180, 0.35, 7, 0.9)));
        all.Add(R("zap-short", "Short zap", "zap", ["zap", "flash", "pop"], 0.2,
            Tone("a", 2400, 600, 0.2, 14, 0.9)));

        // sparkle
        all.Add(R("sparkle-shimmer", "Shimmer sparkle", "sparkle", ["sparkle", "appear", "flash", "text_reveal"], 1.0,
            Partials("a", 1.0, 14, 0.5, 0, (3136, 1)),
            Partials("b", 1.0, 14, 0.5, 0.07, (3951, 1)),
            Partials("c", 1.0, 14, 0.5, 0.14, (4699, 1)),
            Partials("d", 1.0, 14, 0.5, 0.22, (5588, 1)),
            Partials("e", 1.0, 14, 0.5, 0.31, (6272, 1))));
        all.Add(R("sparkle-twinkle", "Twinkle", "sparkle", ["sparkle", "appear", "pop"], 0.7,
            Partials("a", 0.7, 18, 0.5, 0, (4186, 1), (8372, 0.2)),
            Partials("b", 0.7, 18, 0.5, 0.1, (5274, 1), (10548, 0.2))));

        // text tick / typewriter
        all.Add(R("text-tick", "Text tick", "text-tick", ["text_reveal", "click", "appear"], 0.15,
            Noise("a", 0.15, "white", 91, ClickEnvelope(500, 0), 2500, 9000),
            Tone("b", 1900, 1700, 0.15, 120, 0.3)));
        all.Add(R("typewriter-short", "Short typewriter", "text-tick", ["text_reveal", "click"], 0.6,
            Noise("a", 0.6, "pink", 92, ClickEnvelope(260, 0, 0.11, 0.19, 0.31, 0.4, 0.52), 1200, 8000)));
        all.Add(R("typewriter-long", "Typewriter run", "text-tick", ["text_reveal", "click"], 1.2,
            Noise("a", 1.2, "pink", 93, ClickEnvelope(260, 0, 0.1, 0.17, 0.29, 0.36, 0.5, 0.58, 0.71, 0.79, 0.93, 1.02), 1200, 8000)));

        // scene-swap sweep
        all.Add(R("scene-swap-sweep", "Scene swap sweep", "transition", ["transition", "scene_swap", "whoosh"], 0.9,
            Noise("a", 0.9, "pink", 101, Bell(0.9), 400, 7000),
            Tone("b", 250, 2200, 0.9, 1.5, 0.14)));
        all.Add(R("scene-swap-hit", "Scene swap hit", "transition", ["transition", "scene_swap", "hit", "impact"], 0.8,
            Noise("a", 0.8, "pink", 102, "pow(t/0.35,2)*lt(t,0.35)+gt(t,0.35)*exp(-9*(t-0.35))", 300, 6500, 0.8),
            Tone("b", 90, 40, 0.8, 7, 0.7)));

        return all;
    }
}
