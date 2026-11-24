namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// The <c>WebSearch</c> configuration section: the caps on the agent WebSearch tool. Out-of-range values fall
/// back to the defaults rather than disabling the tool or lifting a cap.
/// </summary>
public sealed class WebSearchToolOptions
{
    public const string SectionName = "WebSearch";

    public const int DefaultMaxCallsPerStep = ReelBolt.Shared.Inference.WebSearchLimits.DefaultMaxCallsPerStep;
    public const int DefaultMaxCallsPerRun = 20;
    public const int DefaultMaxResultsPerCall = 8;

    /// <summary>When false the tool answers WEB_SEARCH_UNAVAILABLE without resolving a provider.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// False (default): a restricted provider's response (Gemini grounding, whose terms require displaying its Search
    /// Suggestions) is refused with WEB_SEARCH_RESTRICTED_PROVIDER and none of its text reaches the model. The call was
    /// already billed and counted.
    /// </summary>
    public bool AllowRestrictedGrounding { get; set; }

    /// <summary>Most calls one step attempt may make.</summary>
    public int MaxCallsPerStep { get; set; } = DefaultMaxCallsPerStep;

    /// <summary>Most calls one run may make across all its steps.</summary>
    public int MaxCallsPerRun { get; set; } = DefaultMaxCallsPerRun;

    /// <summary>Most results one call returns to the model.</summary>
    public int MaxResultsPerCall { get; set; } = DefaultMaxResultsPerCall;

    internal int StepCap => MaxCallsPerStep > 0 ? MaxCallsPerStep : DefaultMaxCallsPerStep;
    internal int RunCap => MaxCallsPerRun > 0 ? MaxCallsPerRun : DefaultMaxCallsPerRun;
    internal int ResultsCap => Math.Clamp(
        MaxResultsPerCall > 0 ? MaxResultsPerCall : DefaultMaxResultsPerCall, 1, ReelBolt.Shared.Inference.WebSearchLimits.MaxResults);
}
