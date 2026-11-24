namespace ReelBolt.Shared.Moderation;

/// <summary>
/// Configuration for the shared content-moderation path (<c>ContentModeration</c> configuration
/// section). Defaults are the plan's fixed thresholds: block at <c>&gt;= 0.85</c>, review in
/// <c>[0.50, 0.85)</c>, allow below <c>0.50</c>, one 8-second call, one frame every 2 seconds up to
/// 20 frames, and a 30-day audit window.
/// </summary>
/// <remarks>
/// Values read from configuration are clamped at the consumer (policy, client, sampler), not here:
/// a typo in an environment variable must not disable moderation or turn a 1-hour timeout into a
/// stalled upload. A <c>Review</c> verdict never blocks anything; only the configured publish
/// surface fails closed.
/// </remarks>
public sealed class ContentModerationOptions
{
    public const string SectionName = "ContentModeration";

    /// <summary>False by default: an install that has not configured moderation keeps today's behaviour.</summary>
    public bool Enabled { get; set; }

    /// <summary>The pinned snapshot (the alias <c>omni-moderation-latest</c> moves under us).</summary>
    public string Model { get; set; } = "omni-moderation-2024-09-26";

    /// <summary>One short timeout per call; a failed call must not stall a step or an upload.</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>A maximum score at or above this blocks the content.</summary>
    public double BlockThreshold { get; set; } = 0.85;

    /// <summary>A maximum score at or above this (and below the block threshold) creates a review row.</summary>
    public double ReviewThreshold { get; set; } = 0.50;

    /// <summary>
    /// Categories that block regardless of score — never sent to the model as a prompt. Defaults to
    /// empty so configuration REPLACES the list: <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/>
    /// appends to a pre-populated collection property, so a shipped default here would be duplicated
    /// on every bind. Consumers use <see cref="EffectiveHardBlockCategories"/>, which restores the
    /// built-in <c>sexual/minors</c> row when nothing is configured.
    /// </summary>
    public string[] HardBlockCategories { get; set; } = [];

    /// <summary>
    /// The hard-block list actually applied: the configured categories, or the built-in
    /// <c>sexual/minors</c> when the configuration section is absent/empty.
    /// </summary>
    public IReadOnlyList<string> EffectiveHardBlockCategories =>
        HardBlockCategories is { Length: > 0 } ? HardBlockCategories : ["sexual/minors"];

    /// <summary>Characters of text sent per call; longer text is truncated by the caller.</summary>
    public int MaxTextChars { get; set; } = 8000;

    /// <summary>Images attached to one call; further sampled frames are dropped.</summary>
    public int MaxImagesPerRequest { get; set; } = 20;

    /// <summary>20 MiB, the vendor's documented per-image ceiling.</summary>
    public long MaxImageBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// Hard ceiling on the WAV extracted for transcription (the model never receives audio; only
    /// the transcript does). An output above this is refused rather than read into memory.
    /// </summary>
    public long MaxAudioBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Seconds of audio extracted for transcription, from the start of the source.</summary>
    public int MaxAudioSeconds { get; set; } = 600;

    /// <summary>Long side of a sampled image or video frame, before base64 encoding.</summary>
    public int SampleImageMaxLongSide { get; set; } = 512;

    /// <summary>Seconds between sampled video frames (ffmpeg <c>fps=1/N</c>).</summary>
    public int VideoFrameIntervalSeconds { get; set; } = 2;

    /// <summary>Hard ceiling on sampled video frames.</summary>
    public int VideoMaxFrames { get; set; } = 20;

    /// <summary>
    /// A video or audio file that has sound is judged on its frames AND its transcript. With no platform transcription
    /// provider the speech cannot be heard, and by default the file is refused (an outage, never an Allow). Set this
    /// to <c>true</c> only where that is a deliberate choice: the file is then judged on its frames alone, and the
    /// stored verdict says so. A production deployment needs a platform transcription provider instead.
    /// </summary>
    public bool AllowFramesOnlyWithoutTranscription { get; set; }

    /// <summary>Audit rows older than this are deleted by the retention service.</summary>
    public int AuditRetentionDays { get; set; } = 30;

    /// <summary>Only the publish/egress surface refuses when moderation is unavailable.</summary>
    public bool FailClosedAtPublish { get; set; } = true;

    /// <summary>
    /// Seconds an <see cref="ModerationAction.Unavailable"/> outcome is cached for one object before
    /// the pipeline runs again. An outage therefore costs one sample per object per window instead of
    /// one per request; <c>0</c> disables negative caching.
    /// </summary>
    public int UnavailableCacheSeconds { get; set; } = 60;

    /// <summary>The effective negative-cache window: clamped to 0..3600 seconds.</summary>
    public int EffectiveUnavailableCacheSeconds => Math.Clamp(UnavailableCacheSeconds, 0, 3600);

    /// <summary>The effective timeout: an operator value outside 1..30 seconds is clamped.</summary>
    public int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 1, 30);

    /// <summary>The effective frame interval: anything below one second would flood the request.</summary>
    public int EffectiveVideoFrameIntervalSeconds => Math.Max(VideoFrameIntervalSeconds, 1);

    /// <summary>The effective frame ceiling, 1..20.</summary>
    public int EffectiveVideoMaxFrames => Math.Clamp(VideoMaxFrames, 1, 20);

    /// <summary>The effective long side; a non-positive value falls back to the 512 default.</summary>
    public int EffectiveSampleImageMaxLongSide =>
        SampleImageMaxLongSide > 0 ? SampleImageMaxLongSide : 512;
}
