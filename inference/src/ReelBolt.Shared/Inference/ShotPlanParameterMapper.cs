using ReelBolt.Shared.Data.OutputSchemas;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Turns the enum WORDS a <c>ShotDirector</c> plan emits into concrete provider parameters. This is
/// the first-party C# that makes the rushcut invariant hold: the planner never emits a duration in
/// seconds, an aspect ratio or a camera directive — it emits a word, and everything numeric that
/// reaches a provider is decided here.
/// </summary>
/// <remarks>
/// Pure functions only — no I/O, no <see cref="HttpClient"/>, no provider call. Every method here
/// is safe to run in a unit test and costs nothing.
/// </remarks>
public static class ShotPlanParameterMapper
{
    /// <summary>
    /// The canonical camera-move vocabulary. Deliberately pinned here as the single source of truth
    /// and NOT restated on <see cref="ShotPlan"/> or in the agent's schema, so the prompt the model
    /// is given, this table, and the <c>ShotDirectorPromptConsistencyTests</c> vocabulary assertion
    /// cannot drift apart into three near-copies.
    /// </summary>
    /// <remarks>
    /// These are editorial words, not camera hardware: they say what the shot should LOOK like.
    /// Mapping them to vendor phrasing is this class's job, and the phrases below are our own prose
    /// — no vendor-specific directive syntax is ever emitted.
    /// </remarks>
    public static IReadOnlyList<string> CameraWords { get; } = new[]
    {
        "Static",
        "PushIn",
        "PullOut",
        "PanLeft",
        "PanRight",
        "TiltUp",
        "TiltDown",
        "ZoomIn",
        "ZoomOut",
        "Handheld",
        "Orbit"
    };

    /// <summary>
    /// The first-party English clause appended to a shot's prompt for each canonical camera word.
    /// Keys are exactly <see cref="CameraWords"/>, compared case-insensitively after trimming.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CameraPhrases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Static"] = "The camera is locked off and completely still; the frame does not move.",
            ["PushIn"] = "The camera pushes in slowly toward the subject.",
            ["PullOut"] = "The camera pulls back slowly, revealing more of the surroundings.",
            ["PanLeft"] = "The camera pans smoothly to the left.",
            ["PanRight"] = "The camera pans smoothly to the right.",
            ["TiltUp"] = "The camera tilts smoothly upward.",
            ["TiltDown"] = "The camera tilts smoothly downward.",
            ["ZoomIn"] = "The camera zooms in, narrowing the field of view on the subject.",
            ["ZoomOut"] = "The camera zooms out, widening the field of view.",
            ["Handheld"] = "The camera is handheld, with the natural slight movement of a hand-held shot.",
            ["Orbit"] = "The camera orbits around the subject in a smooth arc.",
        };

    /// <summary>
    /// Resolves <paramref name="durationWord"/> (one of <c>Short</c>, <c>Medium</c>, <c>Long</c>) to
    /// a concrete number of seconds the resolved model actually supports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY this throws instead of defaulting: the snapped duration is what the pre-submit estimate
    /// multiplies the per-second price by, so it drives BOTH the spend gate and the ledger. A
    /// silent fallback to "some duration" would persist a cost that does not describe what was
    /// ordered — under-quoting passes a cap it should not, over-quoting refuses work that is
    /// affordable. There is no safe guess, so an unreadable word fails closed.
    /// </para>
    /// <para>
    /// Comparison trims surrounding whitespace and ignores case: <c>"short "</c> and <c>"short"</c>
    /// are the same word, and tolerating that noise cannot invent a duration because the word must
    /// still be one of the three pinned spellings.
    /// </para>
    /// </remarks>
    /// <param name="capabilities">The resolved model's capabilities (<c>Describe(model)</c>).</param>
    /// <param name="durationWord">The plan's duration word.</param>
    /// <returns>
    /// <c>Short</c> → the model's minimum, <c>Long</c> → its maximum, <c>Medium</c> → the midpoint
    /// <c>min + (max - min) / 2</c> (integer division), clamped into <c>[min, max]</c> in every case.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="durationWord"/> is null, blank, or not one
    /// of the three pinned words.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The model's own duration range is unusable: <c>MaxDurationSeconds</c> below
    /// <c>MinDurationSeconds</c>, or <c>MinDurationSeconds</c> below 1. Both describe a model that
    /// cannot produce a billable clip, so pricing it would be fiction.
    /// </exception>
    public static int SnapDurationSeconds(VideoGenerationCapabilities capabilities, string? durationWord)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        int min = capabilities.MinDurationSeconds;
        int max = capabilities.MaxDurationSeconds;

        if (max < min)
        {
            throw new ArgumentException(
                $"The model's duration range is inverted (min {min}s, max {max}s); no duration can be snapped.",
                nameof(capabilities));
        }

        if (min < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                min,
                "The model's minimum duration is below 1 second, which cannot be billed or generated.");
        }

        int target = durationWord?.Trim() switch
        {
            { } word when word.Equals("Short", StringComparison.OrdinalIgnoreCase) => min,
            { } word when word.Equals("Long", StringComparison.OrdinalIgnoreCase) => max,
            { } word when word.Equals("Medium", StringComparison.OrdinalIgnoreCase) => min + ((max - min) / 2),
            _ => throw new ArgumentException(
                $"Duration word '{durationWord}' is not one of Short, Medium, or Long. The snapped "
                + "duration drives the cost estimate, so it is never defaulted.",
                nameof(durationWord)),
        };

        return Math.Clamp(target, min, max);
    }

    /// <summary>
    /// The camera clause to append to a shot's prompt, or <c>null</c> when the word is unknown.
    /// </summary>
    /// <remarks>
    /// An unrecognised or blank camera word is an EXPLICIT, DOCUMENTED degradation: the clause is
    /// simply omitted and the rest of the prompt is sent unchanged, because a missing camera note
    /// costs a little directorial control while guessing at one would put a camera move into the
    /// clip that no human or agent ever asked for. Never throws — unlike
    /// <see cref="SnapDurationSeconds"/>, nothing downstream depends on this value.
    /// </remarks>
    /// <param name="cameraWord">The plan's camera word; null and blank are both "no clause".</param>
    /// <returns>The clause for a canonical word, otherwise <c>null</c>.</returns>
    public static string? CameraPhraseFor(string? cameraWord)
    {
        if (string.IsNullOrWhiteSpace(cameraWord))
        {
            return null;
        }

        return CameraPhrases.TryGetValue(cameraWord.Trim(), out var phrase) ? phrase : null;
    }

    /// <summary>
    /// Builds the provider request for one planned shot.
    /// </summary>
    /// <remarks>
    /// <paramref name="sanitizedPrompt"/> is taken as already sanitized — the caller runs it through
    /// <see cref="GeneratedPromptSanitizer"/> so that the empty/over-length decision (and its
    /// <c>INVALID_PROMPT</c> failure) is made in one place rather than being second-guessed here.
    /// The camera clause is our own prose appended to the prompt text; no vendor directive syntax
    /// is emitted, so a provider swap cannot leave a stale command in the text.
    /// </remarks>
    /// <param name="capabilities">The resolved model's capabilities.</param>
    /// <param name="model">The provider model name.</param>
    /// <param name="resolution">The provider resolution token (validated by the step config, not here).</param>
    /// <param name="ratio">The provider aspect-ratio token.</param>
    /// <param name="shot">The planned shot.</param>
    /// <param name="sanitizedPrompt">The shot's sanitized prompt text.</param>
    /// <returns>A request whose duration is snapped and whose prompt carries the camera clause.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> or <paramref name="shot"/> is null.</exception>
    /// <exception cref="ArgumentException">The shot's duration word is not one of the pinned words.</exception>
    public static VideoGenerationRequest BuildRequest(
        VideoGenerationCapabilities capabilities,
        string model,
        string resolution,
        string ratio,
        ShotPlan shot,
        string sanitizedPrompt)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(shot);

        int durationSeconds = SnapDurationSeconds(capabilities, shot.Duration);

        string promptText = sanitizedPrompt ?? string.Empty;
        string? cameraPhrase = CameraPhraseFor(shot.Camera);
        if (cameraPhrase is not null)
        {
            promptText = string.IsNullOrWhiteSpace(promptText)
                ? cameraPhrase
                : $"{promptText} {cameraPhrase}";
        }

        return new VideoGenerationRequest(model, promptText, durationSeconds, resolution, ratio);
    }
}
