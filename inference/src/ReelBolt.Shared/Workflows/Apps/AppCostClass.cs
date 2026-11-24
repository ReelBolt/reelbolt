using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The cost class a card and a run page show, in words a marketer reads as cost. Derived from the step types, never
/// typed by hand: a step that spends model tokens, a voice or generation makes the class say so.
/// </summary>
public static class AppCostClass
{
    public const string NoCredits = "No credits";
    public const string UsesCredits = "Uses credits";
    public const string UsesCreditsAndVoice = "Uses credits and a voice";
    public const string UsesCreditsAndAiShots = "Uses credits and AI shots";

    /// <summary>
    /// Generation (video or audio) wins over a voice, which wins over plain credits, because real money capped per run
    /// is the disclosure that matters most. No step that spends anything: "No credits".
    /// </summary>
    public static string Derive(IEnumerable<AppStepFacts> steps)
    {
        bool credits = false;
        bool voice = false;
        bool generation = false;
        foreach (AppStepFacts step in steps)
        {
            switch (step.StepType)
            {
                case StepType.VideoGenerate:
                case StepType.AudioGenerate:
                case StepType.ImageGenerate:
                    generation = true;
                    break;
                case StepType.Voiceover:
                    voice = true;
                    break;
                case StepType.Agent:
                    // The deterministic built-ins never reach a model.
                    if (step.AgentType is not (AgentType.ExtractTransform or AgentType.VideoTransform))
                        credits = true;
                    break;
                case StepType.VideoAnalyze:
                    // Transcription, captioning, tracking and hosted segmentation are billed; compute-only analysis is not.
                    if (step.CallsProvider)
                        credits = true;
                    break;
                case StepType.AudioCleanup:
                case StepType.VideoEnhance:
                    // Only the AI targets/operations are billed, but a step card names the TYPE: it may use credits.
                    credits = true;
                    break;
                case StepType.Parallel:
                case StepType.ReviewLoop:
                case StepType.EditRoom:
                case StepType.GraphicsRoom:
                case StepType.ColorGradeRoom:
                case StepType.DailiesRoom:
                case StepType.Revise:
                case StepType.DesignGraphic:
                    credits = true;
                    break;
            }
        }

        if (generation)
            return UsesCreditsAndAiShots;
        if (voice)
            return UsesCreditsAndVoice;
        return credits ? UsesCredits : NoCredits;
    }
}
