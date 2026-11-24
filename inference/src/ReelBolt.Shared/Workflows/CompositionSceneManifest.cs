using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// One scene of a rendered composition as its AUTHOR declares it at render time: where it sits
/// (in frames of the composition) and what it shows. The author knows this exactly — it wrote the
/// <c>&lt;Sequence&gt;</c>s — so nothing downstream has to guess it back from pixels.
/// </summary>
public sealed record DeclaredScene(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fromFrame")] int FromFrame,
    [property: JsonPropertyName("durationInFrames")] int DurationInFrames,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("onScreenText")] IReadOnlyList<string>? OnScreenText = null);

/// <summary>A validated scene with its engine-assigned handle.</summary>
public sealed record CompositionScene(
    string Handle,
    string Name,
    int FromFrame,
    int DurationInFrames,
    string Description,
    IReadOnlyList<string> OnScreenText)
{
    /// <summary>
    /// False for a scene the composition's own <c>&lt;Sequence&gt;</c> list places but no author
    /// described — a valid, and deliberately visible, state: the SPLIT is known from the code while
    /// the description is not. <see cref="CompositionSceneManifest.FromDeclaration"/> refuses an
    /// empty description, so an empty one can only ever mean this.
    /// </summary>
    public bool Described => !string.IsNullOrWhiteSpace(Description);
}

/// <summary>
/// Where a scene manifest's split and descriptions came from. A string rather than an enum
/// because this value is persisted in the sidecar object and read back by a later step (possibly
/// after a version skew), where an unrecognised value must degrade to "unknown provenance"
/// instead of failing the deserialization and losing the whole manifest.
/// </summary>
public static class CompositionSceneOrigin
{
    /// <summary>The authoring model declared the scenes at render time. The split is a CLAIM.</summary>
    public const string Author = "author";

    /// <summary>Read from the composition's own <c>&lt;Sequence&gt;</c> list. The split is a READING of the code; nothing is described.</summary>
    public const string Structure = "structure";

    /// <summary>The split came from the composition's code and the descriptions from the author's declaration.</summary>
    public const string Reconciled = "reconciled";

    /// <summary>A still image's three moves, declared by the engine itself.</summary>
    public const string Still = "still";

    /// <summary>A generated clip, described by the shot plan that produced it.</summary>
    public const string Generated = "generated";

    /// <summary>Any other value — a manifest written by a build this one does not know.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The author-declared scene list of one rendered video, stored as a sidecar object next to the
/// video (<see cref="SidecarKeyFor"/>). <c>VideoAnalyze</c> reads it instead of detecting shots and
/// instead of captioning them: the scene boundaries become the shots, the author's description
/// becomes each shot's caption, and the source is marked a designed composition.
/// </summary>
/// <remarks>
/// <see cref="IsComposition"/> distinguishes a designed composition (a Remotion render: its own
/// typography, no room for more) from described footage (a generated clip whose description is
/// the prompt it was generated from), which may still take graphics.
/// </remarks>
public sealed record CompositionSceneManifest(
    int Version,
    string? CompositionId,
    int Fps,
    IReadOnlyList<CompositionScene> Scenes,
    bool IsComposition = true,
    string Origin = CompositionSceneOrigin.Author,
    IReadOnlyList<string>? Findings = null)
{
    /// <summary>
    /// Stays at 1: <see cref="Origin"/> and <see cref="Findings"/> are appended, optional and
    /// defaulted, exactly like every Phase-1/2/4 addition to the analysis artifact, so a sidecar
    /// written before they existed still deserializes and reads as <see cref="CompositionSceneOrigin.Author"/>.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>Hard caps keep a model-written manifest bounded.</summary>
    public const int MaxScenes = 200;
    public const int MaxDescriptionChars = 600;
    public const int MaxOnScreenTextItems = 12;
    public const int MaxOnScreenTextChars = 160;

    /// <summary>Comfortable reading speed for on-screen copy in a promo, in words per second.</summary>
    public const double ReadingWordsPerSecond = 3.5;

    /// <summary>Time the eye needs to find a new piece of copy before reading starts.</summary>
    public const double ReadingSettleSeconds = 0.8;

    /// <summary>
    /// How long a scene must hold for a viewer to read <paramref name="onScreenText"/>: a settle
    /// beat plus the words at <see cref="ReadingWordsPerSecond"/>. Zero for a scene with no copy.
    /// </summary>
    public static double MinReadSeconds(IReadOnlyList<string> onScreenText)
    {
        int words = onScreenText.Sum(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        return words == 0 ? 0 : ReadingSettleSeconds + words / ReadingWordsPerSecond;
    }

    /// <summary>
    /// A short description of why a scene of <paramref name="durationSec"/> is too short to read,
    /// or null when it is readable.
    /// </summary>
    public static string? RushedReason(IReadOnlyList<string> onScreenText, double durationSec)
    {
        double needed = MinReadSeconds(onScreenText);
        if (needed <= 0 || durationSec + 1e-6 >= needed)
            return null;

        int words = onScreenText.Sum(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        return $"rushed: {words} words of on-screen copy held for {durationSec:0.0}s, a viewer needs about {needed:0.0}s to read them";
    }

    /// <summary>The storage key of <paramref name="videoStorageKey"/>'s scene sidecar.</summary>
    public static string SidecarKeyFor(string videoStorageKey) => videoStorageKey + ".scenes.json";

    /// <summary>
    /// Validates an author's declaration and assigns handles (<c>scene-1</c>…). Scenes must be in
    /// frame order and must not overlap; gaps are allowed (the engine folds a gap into the scene
    /// before it). Returns the manifest, or null plus every problem found, so the author can fix
    /// the declaration in one go.
    /// </summary>
    public static (CompositionSceneManifest? Manifest, IReadOnlyList<string> Errors) FromDeclaration(
        string? compositionId, int fps, IReadOnlyList<DeclaredScene>? declared)
    {
        var errors = new List<string>();
        if (fps <= 0 || fps > 240)
            errors.Add($"fps must be the composition's frame rate (1..240), got {fps}.");
        if (declared is null || declared.Count == 0)
            errors.Add("scenes must list at least one scene.");
        else if (declared.Count > MaxScenes)
            errors.Add($"scenes may list at most {MaxScenes} scenes, got {declared.Count}.");

        if (errors.Count > 0)
            return (null, errors);

        var scenes = new List<CompositionScene>(declared!.Count);
        int previousEnd = 0;
        for (int i = 0; i < declared.Count; i++)
        {
            DeclaredScene d = declared[i];
            string label = $"scene {i + 1} ('{d.Name}')";

            if (string.IsNullOrWhiteSpace(d.Name))
                errors.Add($"scene {i + 1} needs a name.");
            if (string.IsNullOrWhiteSpace(d.Description))
                errors.Add($"{label} needs a description of what it shows.");
            if (d.FromFrame < 0)
                errors.Add($"{label} starts before frame 0.");
            if (d.DurationInFrames <= 0)
                errors.Add($"{label} must last at least one frame.");
            if (fps > 0 && d.DurationInFrames > 0 &&
                RushedReason(d.OnScreenText ?? [], d.DurationInFrames / (double)fps) is { } rushed)
            {
                int neededFrames = (int)Math.Ceiling(MinReadSeconds(d.OnScreenText ?? []) * fps);
                errors.Add($"{label} is {rushed}. Hold it for at least {neededFrames} frames or cut the copy it shows.");
            }
            // On-screen copy is drawn verbatim, so markdown emphasis shows up as literal asterisks
            // ("Build the edit as a *flowchart.*", customer finding 2026-10-04). Rejected before the
            // render, naming the clean text, so the author fixes the component and the declaration.
            foreach (string text in d.OnScreenText ?? [])
            {
                if (OnScreenCopySanitizer.ContainsMarkdown(text))
                    errors.Add($"{label} shows markdown on screen: \"{Clip(text.Trim(), 80)}\". A video draws those characters literally — write \"{Clip(OnScreenCopySanitizer.Strip(text).Trim(), 80)}\" in the component and in this declaration, and use font weight or colour for emphasis.");
            }
            if (i > 0 && d.FromFrame < previousEnd)
                errors.Add($"{label} starts at frame {d.FromFrame}, inside the previous scene (which ends at frame {previousEnd}); list scenes in order without overlaps.");

            previousEnd = Math.Max(previousEnd, d.FromFrame + Math.Max(0, d.DurationInFrames));

            IReadOnlyList<string> onScreen = (d.OnScreenText ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Take(MaxOnScreenTextItems)
                .Select(t => Clip(t.Trim(), MaxOnScreenTextChars))
                .ToList();

            scenes.Add(new CompositionScene(
                $"scene-{i + 1}",
                Clip((d.Name ?? "").Trim(), 80),
                d.FromFrame,
                d.DurationInFrames,
                Clip((d.Description ?? "").Trim(), MaxDescriptionChars),
                onScreen));
        }

        return errors.Count > 0
            ? (null, errors)
            : (new CompositionSceneManifest(CurrentVersion, compositionId, fps, scenes), errors);
    }

    /// <summary>
    /// The scenes as gapless <c>[start, end)</c> shot spans covering <c>[0, durationSec)</c>: a gap
    /// joins the scene before it (the first scene starts at 0), the last scene runs to the end, and
    /// a scene that starts at or past the end of the video is dropped. Index-aligned with the
    /// returned scene list.
    /// </summary>
    public (IReadOnlyList<(double StartSec, double EndSec)> Spans, IReadOnlyList<CompositionScene> Scenes) ToShotSpans(double durationSec)
    {
        var kept = Scenes
            .Where(s => Fps > 0 && s.FromFrame / (double)Fps < durationSec)
            .OrderBy(s => s.FromFrame)
            .ToList();

        var spans = new List<(double, double)>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
        {
            double start = i == 0 ? 0 : kept[i].FromFrame / (double)Fps;
            double end = i == kept.Count - 1 ? durationSec : kept[i + 1].FromFrame / (double)Fps;
            spans.Add((start, end));
        }

        return (spans, kept);
    }

    /// <summary>
    /// Merge what the author DECLARED at render time with what the composition's own code SAYS, and
    /// report every disagreement instead of absorbing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The code decides the split; the declaration decides the meaning.</b> Where the composition's
    /// sequence list could be read, its frame positions ARE the scene boundaries — they are the
    /// positions the renderer actually used — and a declared scene is matched onto them to supply the
    /// name, description and on-screen copy. Where the code could not be read, the declaration stands
    /// alone and is stamped <see cref="CompositionSceneOrigin.Author"/>, because that is exactly what
    /// it is: a claim, not a measurement.
    /// </para>
    /// <para>
    /// A disagreement is never silently resolved in favour of the declaration, and never silently
    /// resolved at all — it is returned in the findings so the step that persists the manifest can
    /// record it where a human and an agent will see it.
    /// </para>
    /// </remarks>
    public static (CompositionSceneManifest? Manifest, IReadOnlyList<string> Findings) Reconcile(
        CompositionSceneManifest? declared,
        RemotionSceneStructure.Result? structure,
        string? compositionId = null)
    {
        if (structure is not { Resolved: true, Spans.Count: > 0 })
        {
            return declared is null
                ? (null, [])
                : (declared with { Origin = CompositionSceneOrigin.Author }, declared.Findings ?? []);
        }

        List<string> findings = [];
        int fps = structure.Fps ?? declared?.Fps ?? 0;
        if (fps <= 0)
        {
            return (null, findings.Append(
                "the composition's frame rate could not be read from its source and the render declared none, " +
                "so its sequences cannot be placed on a timeline.").ToList());
        }

        if (declared is not null && structure.Fps is { } structuralFps && structuralFps != declared.Fps)
        {
            findings.Add(
                $"the render declared {declared.Fps} fps but the composition registers {structuralFps}; " +
                "the composition's own rate was used to place its sequences in time.");
        }

        // A declared scene describes the composition's scene that STARTS AT THE SAME FRAME.
        //
        // Matching on the start frame alone is deliberate, and it is what lets the two readings of a
        // crossfade agree: a declaration states each scene's VISIBLE HOLD (FromDeclaration refuses an
        // overlap, so each scene ends where the next begins), while the composition's <Sequence>
        // states its own run time — and those differ by exactly the crossfade. Both agree on where a
        // scene STARTS, which is the split, and the split is the part the code owns.
        List<CompositionScene> declaredScenes = declared?.Scenes.ToList() ?? [];
        List<bool> claimed = [.. declaredScenes.Select(_ => false)];
        List<CompositionScene> scenes = [];
        int undescribed = 0;

        for (int i = 0; i < structure.Spans.Count; i++)
        {
            RemotionSceneStructure.Span span = structure.Spans[i];
            int match = -1;
            for (int j = 0; j < declaredScenes.Count; j++)
            {
                if (!claimed[j] && declaredScenes[j].FromFrame == span.FromFrame)
                {
                    match = j;
                    break;
                }
            }

            string handle = $"scene-{i + 1}";

            if (match >= 0)
            {
                claimed[match] = true;
                CompositionScene source = declaredScenes[match];

                scenes.Add(new CompositionScene(
                    handle, source.Name, span.FromFrame, span.DurationInFrames, source.Description, source.OnScreenText));
                continue;
            }

            undescribed++;
            scenes.Add(new CompositionScene(
                handle,
                $"scene {i + 1}",
                span.FromFrame,
                span.DurationInFrames,
                "",
                []));

            // Reported per scene only when there WAS a declaration to fall short: with no declaration at
            // all, every scene is undescribed by construction and one summary line says so, where one
            // line per scene would be noise a caller has to page through.
            if (declared is not null)
            {
                findings.Add(
                    $"the composition places a <Sequence> at frames {span.FromFrame}..{span.EndFrame} that the declaration " +
                    "does not describe; the scene is kept for its cut points and marked as undescribed.");
            }
        }

        for (int j = 0; j < declaredScenes.Count; j++)
        {
            if (claimed[j])
                continue;

            CompositionScene orphan = declaredScenes[j];
            findings.Add(
                $"declared scene '{orphan.Name}' (frames {orphan.FromFrame}..{orphan.FromFrame + orphan.DurationInFrames}) " +
                "matches no <Sequence> in the composition and was not used.");
        }

        string origin = declared is null || undescribed == declaredScenes.Count
            ? CompositionSceneOrigin.Structure
            : CompositionSceneOrigin.Reconciled;

        if (declared is null)
        {
            findings.Add(
                $"the composition's own <Sequence> list supplied the split ({scenes.Count} scenes); no scene description " +
                "was declared, so every scene is marked as undescribed.");
        }

        return (new CompositionSceneManifest(
            CurrentVersion,
            declared?.CompositionId ?? compositionId,
            fps,
            scenes,
            IsComposition: declared?.IsComposition ?? true,
            Origin: origin,
            Findings: findings.Count > 0 ? findings : null), findings);
    }

    /// <summary>
    /// What this manifest does and does not cover of the media it was applied to — the reconciliation
    /// between a CLAIM about a render and the render itself.
    /// </summary>
    /// <remarks>
    /// <see cref="ToShotSpans"/> deliberately stretches the last scene to the end of the video and
    /// drops any scene starting past it, which keeps a slightly stale declaration usable. That is
    /// the right behaviour and it is also exactly the behaviour that would hide a badly wrong
    /// declaration: a 30-second render described by three scenes covering its first 4 seconds would
    /// silently report three scenes and 26 seconds of footage attributed to the last one. This method
    /// is what makes that visible, so a caller can record it rather than discover it in the cut.
    /// </remarks>
    public IReadOnlyList<string> DescribeAgainstMedia(double durationSec, double? mediaFps = null)
    {
        List<string> findings = [];
        if (Fps <= 0 || durationSec <= 0 || Scenes.Count == 0)
            return findings;

        if (mediaFps is { } measured && measured > 0 && Math.Abs(measured - Fps) > 0.5)
        {
            findings.Add(
                $"the scenes are timed at {Fps} fps but the media analysed is {measured:0.###} fps, so each declared " +
                $"boundary lands {(measured > Fps ? "later" : "earlier")} than the scene it describes.");
        }

        double coveredSec = Scenes.Max(s => (s.FromFrame + s.DurationInFrames) / (double)Fps);
        const double Tolerance = 0.5;

        if (coveredSec < durationSec - Tolerance)
        {
            findings.Add(
                $"the declared scenes cover {coveredSec:0.##}s of a {durationSec:0.##}s render; the remaining " +
                $"{(durationSec - coveredSec):0.##}s belongs to no declared scene and is folded into the last one.");
        }
        else if (coveredSec > durationSec + Tolerance)
        {
            findings.Add(
                $"the declared scenes run to {coveredSec:0.##}s, past the end of the {durationSec:0.##}s render; " +
                "boundaries past the end were dropped.");
        }

        int dropped = Scenes.Count(s => Fps > 0 && s.FromFrame / (double)Fps >= durationSec);
        if (dropped > 0)
        {
            findings.Add($"{dropped} declared scene(s) start at or past the end of the render and were not used.");
        }

        return findings;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
