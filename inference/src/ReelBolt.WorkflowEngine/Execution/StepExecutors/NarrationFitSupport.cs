using System.Globalization;
using System.Text;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>One narration line that does not fit its scene, as the failure message and the shortener see it.</summary>
/// <param name="Index">Zero-based position of the line in the step's line list (scene number is Index + 1).</param>
/// <param name="Text">The sanitized text that was (or would be) spoken.</param>
/// <param name="Words">Word count of <paramref name="Text"/>.</param>
/// <param name="WindowSec">The scene length the line has to fit in.</param>
/// <param name="WordBudget">Words that fit <paramref name="WindowSec"/> (floor(window x 1.8)).</param>
/// <param name="SpeechSec">Seconds of speech, when the line was already synthesized; null before synthesis.</param>
public sealed record NarrationLineOverflow(
    int Index, string Text, int Words, double WindowSec, int WordBudget, double? SpeechSec = null);

/// <summary>
/// Optional seam (T-003b): rewrites overflowing lines shorter, before any synthesis is bought. Null
/// or a null result means "no help available" and the step fails with NARRATION_DOES_NOT_FIT.
/// The model-backed implementation is deliberately not part of T-003a (billing/metering decision pending).
/// </summary>
public interface INarrationLineShortener
{
    /// <returns>Replacement text by <see cref="NarrationLineOverflow.Index"/>, or null when it cannot help.</returns>
    Task<IReadOnlyDictionary<int, string>?> ShortenAsync(
        IReadOnlyList<NarrationLineOverflow> lines, string? language, CancellationToken ct);
}

/// <summary>Plain-words text for a narration that does not fit its scenes (Italian when the run language is Italian).</summary>
internal static class NarrationFitMessages
{
    public const string FailureCode = "NARRATION_DOES_NOT_FIT";

    /// <summary>Pre-flight spend bound: the step's total admitted words exceed the per-step ceiling.</summary>
    public const string TooManyWordsCode = "NARRATION_TOO_MANY_WORDS";

    /// <summary>Plain-words text for a step whose admitted narration words exceed the spend bound.</summary>
    public static string TooManyWords(string? language, int words, int ceiling)
    {
        bool italian = language is not null && language.StartsWith("it", StringComparison.OrdinalIgnoreCase);
        string w = words.ToString(CultureInfo.InvariantCulture);
        string c = ceiling.ToString(CultureInfo.InvariantCulture);
        return italian
            ? $"La narrazione di questo passo ha {w} parole, più del limite di {c} parole per passo. Accorcia la sceneggiatura o dividila in più passi."
            : $"This step's narration has {w} words, above the per-step limit of {c} words. Shorten the script or split it across steps.";
    }

    public static string Build(string? language, IReadOnlyList<NarrationLineOverflow> lines)
    {
        bool italian = language is not null && language.StartsWith("it", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (NarrationLineOverflow l in lines)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            string scene = (l.Index + 1).ToString(CultureInfo.InvariantCulture);
            string window = l.WindowSec.ToString("0.0", CultureInfo.InvariantCulture);
            string words = l.Words.ToString(CultureInfo.InvariantCulture);
            if (l.SpeechSec is double speechSec)
            {
                // Measured after synthesis: quote the real speech duration the judge saw.
                string speech = speechSec.ToString("0.0", CultureInfo.InvariantCulture);
                if (italian)
                {
                    sb.Append($"La frase della scena {scene} è troppo lunga per la scena: ha {words} parole e dura {speech} s di voce, ma la scena dura {window} s. Accorcia il testo o allunga la durata.");
                }
                else
                {
                    sb.Append($"The line for scene {scene} is too long for its scene: it has {words} words and takes {speech} s of speech but the scene lasts {window} s. Shorten the text or lengthen the scene.");
                }
            }
            else
            {
                // Pre-synthesis: name the hard physical ceiling, not the 1.8 words/s comfort budget.
                string ceiling = NarrationFitSolver.HardWordCeiling(l.WindowSec).ToString(CultureInfo.InvariantCulture);
                if (italian)
                {
                    sb.Append($"La frase della scena {scene} è troppo lunga per la scena: ha {words} parole, ma la scena dura {window} s e una voce può pronunciarne al massimo circa {ceiling}. Accorcia il testo o allunga la durata.");
                }
                else
                {
                    sb.Append($"The line for scene {scene} is too long for its scene: it has {words} words, but the scene lasts {window} s and a voice can speak at most about {ceiling} words in that time. Shorten the text or lengthen the scene.");
                }
            }
        }

        return sb.ToString();
    }
}
