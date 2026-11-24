namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The file extensions a step-result source may name, shared by the run-time resolver (which accepts
/// them) and the save-time validator (which predicts it). One list, so the two cannot drift: QA-P0-21
/// found the save path counting <c>.avi</c> as a video the run-time resolver refuses. The API does not
/// reference the WorkflowEngine, so this lives in Shared rather than on either side.
/// </summary>
public static class StepResultMediaExtensions
{
    public static readonly IReadOnlySet<string> Video = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".webm", ".mkv" };

    public static readonly IReadOnlySet<string> Image = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>Audio files a step result may hold; only a consumer that takes audio (<c>AudioCleanup</c>) accepts them.</summary>
    public static readonly IReadOnlySet<string> Audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac" };
}
