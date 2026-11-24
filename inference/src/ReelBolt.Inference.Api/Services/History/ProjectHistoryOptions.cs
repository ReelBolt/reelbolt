namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Settings for project versioning, bound from the <c>ProjectHistory</c> configuration section.
/// Every default is the contract's value; a deployment may tighten or loosen them.
/// </summary>
public sealed class ProjectHistoryOptions
{
    public const string SectionName = "ProjectHistory";

    /// <summary>Refuse to version a project with more live files than this (413).</summary>
    public int MaxFilesPerVersion { get; set; } = 20_000;

    /// <summary>Days an automatic (non-named) version is kept; named versions never expire.</summary>
    public int AutoRetentionDays { get; set; } = 30;

    /// <summary>The most HEADs one capture may issue for unhashed files; the rest are unverified.</summary>
    public int MaxHeadsPerCapture { get; set; } = 2_000;

    /// <summary>How many HEADs one capture runs at once.</summary>
    public int HeadConcurrency { get; set; } = 8;

    /// <summary>The most automatic versions one project may hold; the oldest is pruned at capture.</summary>
    public int MaxNonNamedVersions { get; set; } = 200;

    /// <summary>The most named versions one project may hold.</summary>
    public int MaxNamedVersions { get; set; } = 100;

    /// <summary>Master switch for the automatic (best-effort) capture paths.</summary>
    public bool AutoCapture { get; set; } = true;

    /// <summary>How long an automatic capture may run before it is abandoned.</summary>
    public int AutoCaptureTimeoutSeconds { get; set; } = 10;

    /// <summary>How often the idle sweep looks for quietly changed projects (contract section 9).</summary>
    public int IdleSweepIntervalSeconds { get; set; } = 600;

    /// <summary>Projects the idle sweep may capture in one pass.</summary>
    public int IdleSweepBatchSize { get; set; } = 200;

    /// <summary>A project is idle once it has had no change for this many minutes.</summary>
    public int IdleMinutes { get; set; } = 10;

    /// <summary>Only projects changed within this many hours are considered by the idle sweep.</summary>
    public int IdleLookbackHours { get; set; } = 24;

    /// <summary>Master switch for the background content-hash sweeper (contract section 3).</summary>
    public bool HashSweepEnabled { get; set; } = true;

    /// <summary>Seconds between two hash sweeps.</summary>
    public int HashSweepIntervalSeconds { get; set; } = 60;

    /// <summary>Files hashed per sweep, oldest first.</summary>
    public int HashSweepBatchSize { get; set; } = 50;

    /// <summary>Rows (versions, blobs, documents) the retention GC removes per step per pass.</summary>
    public int GcBatchSize { get; set; } = 500;
}
