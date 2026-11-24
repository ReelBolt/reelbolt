namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Configuration for background file summarization (section <c>FileSummarization</c>). See
/// docs/platform-assistant-mcp.md "Background work never blocks an upload or the assistant".
/// </summary>
public sealed class FileSummarizationOptions
{
    public const string SectionName = "FileSummarization";

    /// <summary>
    /// How many summarization LLM calls may run at once. Kept small on purpose: summarization shares
    /// the chat provider with the interactive assistant, and a customer uploading a whole repository
    /// must not be able to occupy it. Clamped to 1..8.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>In-memory queue size. A full queue never blocks anything: the row stays Pending and the sweeper offers it again.</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>
    /// The largest number of bytes read from a stored file into memory for a summary. A larger file
    /// is summarized from its first <see cref="MaxTextBytes"/> bytes only, with a <c>[truncated]</c>
    /// marker appended: a summary of the head of a big file is still useful.
    /// </summary>
    public int MaxTextBytes { get; set; } = 524288;

    /// <summary>How often pending (and stuck) rows are re-offered to the queue. Minimum 5.</summary>
    public int SweepIntervalSeconds { get; set; } = 45;

    /// <summary>Delay before the first sweep after startup, so migrations and seeding finish first.</summary>
    public int InitialSweepDelaySeconds { get; set; } = 10;

    /// <summary>
    /// A row left in <c>Processing</c> by this process's predecessor (a restart mid-summary) is
    /// re-queued once it has been in that state, measured from its upload time, for this long.
    /// </summary>
    public int StuckProcessingMinutes { get; set; } = 15;

    /// <summary>
    /// Before each summarization call, wait up to this long while an assistant turn is being
    /// answered, so the person at the keyboard is served first. 0 disables the yield.
    /// </summary>
    public int InteractiveYieldSeconds { get; set; } = 60;
}
