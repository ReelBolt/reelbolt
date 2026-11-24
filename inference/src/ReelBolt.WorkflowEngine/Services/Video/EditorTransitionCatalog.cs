using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The transitions an editor agent may name on a kept span under
/// <c>VideoTransitionPolicy.Editor</c> (docs/video-editing.md "Editor-chosen transitions"): a
/// preset WORD and a speed WORD, never a duration or an ffmpeg name the model typed. This catalog
/// alone resolves them — a preset to an <c>xfade</c> transition every ffmpeg 7+ ships, a speed to
/// seconds — so the rushcut invariant (no number in the decision) holds.
/// </summary>
public static class EditorTransitionCatalog
{
    /// <summary>The preset that means "no transition": a plain cut.</summary>
    public const string Cut = "Cut";

    /// <summary>Preset word → <c>xfade</c> transition name. Ordered as the prompt lists them.</summary>
    public static readonly IReadOnlyList<(string Preset, string Xfade, string Look)> Presets =
    [
        ("Fade", "fade", "a soft crossfade"),
        ("Dissolve", "dissolve", "a grainy pixel dissolve"),
        ("DipToBlack", "fadeblack", "fades through black — a pause or a new chapter"),
        ("FlashWhite", "fadewhite", "flashes through white — energy, a reveal, a beat hit"),
        ("FadeGray", "fadegrays", "drains to grey, then into the next shot"),
        ("Blur", "hblur", "a horizontal blur, like a whip pan"),
        ("Zoom", "zoomin", "punches into the next shot"),
        ("Pixelize", "pixelize", "pixelates out and back in — digital, techy"),
        ("Radial", "radial", "a clock-hand sweep"),
        ("Distance", "distance", "a liquid warp"),
        ("CircleOpen", "circleopen", "an iris opening onto the next shot"),
        ("CircleClose", "circleclose", "an iris closing on the outgoing shot"),
        ("CircleCrop", "circlecrop", "a circle shrinks to a point and grows again"),
        ("RectCrop", "rectcrop", "a rectangle shrinks to a point and grows again"),
        ("SlideLeft", "slideleft", "the next shot slides in from the right"),
        ("SlideRight", "slideright", "the next shot slides in from the left"),
        ("SlideUp", "slideup", "the next shot slides up from below"),
        ("SlideDown", "slidedown", "the next shot slides down from above"),
        ("PushLeft", "coverleft", "the next shot covers the frame from the right"),
        ("PushRight", "coverright", "the next shot covers the frame from the left"),
        ("PushUp", "coverup", "the next shot covers the frame from below"),
        ("PushDown", "coverdown", "the next shot covers the frame from above"),
        ("RevealLeft", "revealleft", "the outgoing shot slides away left, revealing the next"),
        ("RevealRight", "revealright", "the outgoing shot slides away right"),
        ("RevealUp", "revealup", "the outgoing shot slides away up"),
        ("RevealDown", "revealdown", "the outgoing shot slides away down"),
        ("WipeLeft", "wipeleft", "a hard-edged wipe to the left"),
        ("WipeRight", "wiperight", "a hard-edged wipe to the right"),
        ("WipeUp", "wipeup", "a hard-edged wipe upward"),
        ("WipeDown", "wipedown", "a hard-edged wipe downward"),
        ("SmoothLeft", "smoothleft", "a soft-edged wipe to the left"),
        ("SmoothRight", "smoothright", "a soft-edged wipe to the right"),
        ("SmoothUp", "smoothup", "a soft-edged wipe upward"),
        ("SmoothDown", "smoothdown", "a soft-edged wipe downward"),
        ("DiagonalTopLeft", "diagtl", "a diagonal wipe from the top-left corner"),
        ("DiagonalTopRight", "diagtr", "a diagonal wipe from the top-right corner"),
        ("DiagonalBottomLeft", "diagbl", "a diagonal wipe from the bottom-left corner"),
        ("DiagonalBottomRight", "diagbr", "a diagonal wipe from the bottom-right corner"),
        ("CornerTopLeft", "wipetl", "a corner wipe from the top-left"),
        ("CornerTopRight", "wipetr", "a corner wipe from the top-right"),
        ("CornerBottomLeft", "wipebl", "a corner wipe from the bottom-left"),
        ("CornerBottomRight", "wipebr", "a corner wipe from the bottom-right"),
        ("BarnDoorOpen", "vertopen", "two vertical doors open onto the next shot"),
        ("BarnDoorClose", "vertclose", "two vertical doors close over the outgoing shot"),
        ("BlindsOpen", "horzopen", "two horizontal doors open onto the next shot"),
        ("BlindsClose", "horzclose", "two horizontal doors close over the outgoing shot"),
        ("SliceLeft", "hlslice", "venetian slices sweep left"),
        ("SliceRight", "hrslice", "venetian slices sweep right"),
        ("SliceUp", "vuslice", "venetian slices sweep up"),
        ("SliceDown", "vdslice", "venetian slices sweep down"),
        ("WindLeft", "hlwind", "the picture blows away to the left in streaks"),
        ("WindRight", "hrwind", "the picture blows away to the right in streaks"),
        ("WindUp", "vuwind", "the picture blows away upward in streaks"),
        ("WindDown", "vdwind", "the picture blows away downward in streaks"),
        ("SqueezeHorizontal", "squeezeh", "the outgoing shot squeezes shut sideways"),
        ("SqueezeVertical", "squeezev", "the outgoing shot squeezes shut vertically"),
    ];

    /// <summary>
    /// Whip words the dynamics planner picks on a bar-line seam (docs/design/dynamic-edit.md). Appended apart from
    /// <see cref="Presets"/> so the editor prompt list stays as it was; <see cref="Resolve"/> accepts them by word.
    /// A plain slide: the catalogue has no combined slide+blur chain.
    /// </summary>
    public static readonly IReadOnlyList<(string Preset, string Xfade)> WhipPresets =
    [
        ("WhipLeft", "slideleft"),
        ("WhipRight", "slideright"),
    ];

    /// <summary>Speed word → seconds. "Quick" when the editor names a preset but no speed.</summary>
    public static readonly IReadOnlyList<(string Speed, double Seconds, string Feel)> Speeds =
    [
        ("Snap", 0.2, "barely there — a cut with a texture, for fast beat-driven edits"),
        ("Quick", 0.35, "punchy, the default for social"),
        ("Smooth", 0.6, "relaxed and readable"),
        ("Slow", 1.0, "lingering — a mood change or the close"),
    ];

    public const string DefaultSpeed = "Quick";

    private static readonly Dictionary<string, string> XfadeByPreset =
        Presets.ToDictionary(p => Normalize(p.Preset), p => p.Xfade);

    private static readonly Dictionary<string, (string Preset, string Xfade)> ByXfade =
        Presets.ToDictionary(p => p.Xfade, p => (p.Preset, p.Xfade));

    private static readonly Dictionary<string, (string Speed, double Seconds)> SecondsBySpeed =
        Speeds.ToDictionary(s => Normalize(s.Speed), s => (s.Speed, s.Seconds));

    /// <summary>The editor's choice for one seam, resolved.</summary>
    public sealed record Choice(string Preset, SeamTreatment Treatment, string Xfade, string Speed, double Seconds);

    /// <summary>The whip choice of a seam whose length the compile derived (never a model number); null for an unknown whip word.</summary>
    public static Choice? WhipChoice(string? word, double seconds)
    {
        string key = Normalize(word ?? "");
        foreach ((string preset, string xfade) in WhipPresets)
            if (Normalize(preset) == key)
                return new Choice(preset, SeamTreatment.Dissolve, xfade, "Whip", seconds);
        return null;
    }

    /// <summary>
    /// Resolves a preset and a speed word. Null for an empty preset (the editor left the seam to the
    /// measured rules) and for an unknown one (<paramref name="unknown"/> is set so the compile can
    /// report it). "Cut" resolves to a hard cut. An unknown speed falls back to <see cref="DefaultSpeed"/>.
    /// </summary>
    public static Choice? Resolve(string? preset, string? speed, out bool unknown)
    {
        unknown = false;
        if (string.IsNullOrWhiteSpace(preset))
            return null;

        string key = Normalize(preset);
        if (key == Normalize(Cut) || key is "hardcut" or "none")
            return new Choice(Cut, SeamTreatment.HardCut, "", "", 0);

        if (WhipChoice(preset, SecondsBySpeed[Normalize(DefaultSpeed)].Seconds) is { } whip)
        {
            (string whipSpeed, double whipSeconds) = !string.IsNullOrWhiteSpace(speed) && SecondsBySpeed.TryGetValue(Normalize(speed), out var ws)
                ? ws
                : SecondsBySpeed[Normalize(DefaultSpeed)];
            return whip with { Speed = whipSpeed, Seconds = whipSeconds };
        }

        string? xfade = XfadeByPreset.TryGetValue(key, out string? x) ? x
            : ByXfade.TryGetValue(key, out var raw) ? raw.Xfade
            : null;
        if (xfade is null)
        {
            unknown = true;
            return null;
        }

        string presetName = ByXfade[xfade].Preset;
        (string speedName, double seconds) = !string.IsNullOrWhiteSpace(speed) && SecondsBySpeed.TryGetValue(Normalize(speed), out var s)
            ? s
            : SecondsBySpeed[Normalize(DefaultSpeed)];
        SeamTreatment treatment = xfade == "fadeblack" ? SeamTreatment.DipToBlack : SeamTreatment.Dissolve;
        return new Choice(presetName, treatment, xfade, speedName, seconds);
    }

    /// <summary>The preset and speed lists as prompt text, one line each.</summary>
    public static string PromptList() =>
        "Presets: " + string.Join("; ", Presets.Select(p => $"{p.Preset} ({p.Look})")) +
        $"; {Cut} (a plain cut).\nSpeeds: " + string.Join("; ", Speeds.Select(s => $"{s.Speed} ({s.Feel})")) + ".";

    private static string Normalize(string word) =>
        new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
