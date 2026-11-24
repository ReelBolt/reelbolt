namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

/// <summary>Bound from <c>VideoEnhance:Local</c>. Operator configuration only; never from a request.</summary>
public sealed class LocalEnhanceOptions
{
    public const string SectionName = "VideoEnhance:Local";

    /// <summary>Per-invocation deadline, scaled upward by <see cref="SourceTimeoutPolicy"/> for big sources.</summary>
    public int TimeoutSeconds { get; set; } = 1800;

    /// <summary>A source longer than this is refused before any encode (the plan's local cap).</summary>
    public int MaxSourceSeconds { get; set; } = 600;
}
