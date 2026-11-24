namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>R10.4 "Preview 3 s" tunables (config section <c>Editing:Preview</c>).</summary>
public sealed record TimelinePreviewOptions
{
    public const string SectionName = "Editing:Preview";

    /// <summary>Previews one user may start per project in a rolling hour (<c>PREVIEW_RATE_LIMITED</c> beyond it).</summary>
    public int MaxPerHourPerUserProject { get; init; } = 30;

    /// <summary>How long a finished preview file is kept before the sweeper deletes it, in minutes.</summary>
    public int RetentionMinutes { get; init; } = 60;

    /// <summary>How often the sweeper runs, in minutes.</summary>
    public int SweepIntervalMinutes { get; init; } = 5;

    public int Limit => Math.Max(1, MaxPerHourPerUserProject);

    public TimeSpan Retention => TimeSpan.FromMinutes(Math.Max(1, RetentionMinutes));
}
