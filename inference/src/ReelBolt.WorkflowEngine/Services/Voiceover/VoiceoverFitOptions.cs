namespace ReelBolt.WorkflowEngine.Services.Voiceover;

/// <summary>
/// The narration spend bound, bound from the <c>Voiceover</c> configuration section.
///
/// <para>
/// <c>VoiceoverStepConfig.MaxBytes</c> caps the request bytes; it does not cap how many words are
/// synthesized. The 4.0 words/s hard ceiling (<see cref="ReelBolt.Shared.Workflows.NarrationFitSolver.IsUnrescuable"/>)
/// bounds each line, but a pathological script can still place a large number of individually
/// speakable lines in one step, so this per-step word ceiling is the real spend bound: a step whose
/// admitted words exceed it fails with <c>NARRATION_TOO_MANY_WORDS</c> before any provider call.
/// </para>
/// <para>
/// It cannot bite a real ReelBolt script — 5000 words is about 33 minutes of speech at 2.5 words/s,
/// far beyond any promo — while still bounding a runaway one. A non-positive configured value is
/// treated as <see cref="DefaultMaxAdmittedNarrationWords"/>.
/// </para>
/// </summary>
public sealed class VoiceoverFitOptions
{
    public const string SectionName = "Voiceover";

    /// <summary>The generous per-step default: 5000 admitted words.</summary>
    public const int DefaultMaxAdmittedNarrationWords = 5000;

    /// <summary>Most words one Voiceover step may send to the speech provider.</summary>
    public int MaxAdmittedNarrationWords { get; set; } = DefaultMaxAdmittedNarrationWords;
}
