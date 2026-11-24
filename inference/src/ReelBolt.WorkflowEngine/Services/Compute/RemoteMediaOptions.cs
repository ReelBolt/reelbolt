namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// A remote media session's own tunables (WP D11), bound from <c>Compute:MediaSession</c>. Everything
/// here is a fabric-only bound: with <c>Compute:Mode=Local</c> no remote session is ever opened and
/// nothing in this file is read.
/// </summary>
public sealed class RemoteMediaOptions
{
    public const string SectionName = "Compute:MediaSession";

    /// <summary>
    /// The cap the engine declares on a <c>stage</c>. The worker enforces it while streaming, and the
    /// engine would rather be told a download was refused than let a runner pull an unbounded object.
    /// Left at 0 it follows <c>VideoSources:MaxBytes</c> (the platform's source limit, see
    /// <c>VideoSourceLimits</c>) so a source the gate admitted can be staged; set a value to cap it lower.
    /// </summary>
    public long MaxStageBytes { get; set; }

    /// <summary>The cap in force: the configured one, else the platform default source limit.</summary>
    public long EffectiveMaxStageBytes => MaxStageBytes > 0 ? MaxStageBytes : ReelBolt.Shared.Workflows.VideoSourceLimits.DefaultMaxBytes;

    /// <summary>
    /// The cap the engine declares on a <c>publish</c>. Default 4 GiB; D12's verifier enforces the job
    /// type's own cap again after the upload, which is the check that can actually be enforced.
    /// </summary>
    public long MaxPublishBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// A GET URL's lifetime. Minted when the op is issued, never stored. The protocol caps GETs at 15
    /// minutes, so a value above that is refused by the presigner rather than silently honoured.
    /// </summary>
    public TimeSpan PresignGetTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A PUT URL's lifetime; the protocol caps PUTs at 30 minutes.</summary>
    public TimeSpan PresignPutTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the engine waits for a runner to be ASSIGNED a media job before it gives up
    /// (<c>RUNNER_JOB_UNCLAIMED</c>). The selector already checked that an eligible runner was online,
    /// so this bounds the queue, not the queue's absence; it is deliberately generous, because a pool
    /// worker finishing a previous job is the ordinary case.
    /// </summary>
    public int AssignWaitSeconds { get; set; } = 300;

    /// <summary>
    /// The deadline of an op the caller gave none (<c>stage</c>, <c>stat</c>, <c>publish</c>, <c>close</c>).
    /// Always sent to the gateway as an explicit <c>timeoutSec</c>, so the HTTP request outlasts it and the
    /// gateway answers <c>504 op_timeout</c> at the deadline; an op with no deadline used to fall back to the
    /// 30 s request timeout, which abandoned a long stage while the worker was still running it and made the
    /// next op <c>409 op_in_flight</c>. Default 900 s (a 30 GiB source at 35 MB/s).
    /// </summary>
    public int OpTimeoutSeconds { get; set; } = 900;

    /// <summary>The op deadline in force: the configured one, else 900 s.</summary>
    public TimeSpan EffectiveOpTimeout => TimeSpan.FromSeconds(OpTimeoutSeconds > 0 ? OpTimeoutSeconds : 900);

    /// <summary>
    /// How long past an op's own deadline the engine keeps asking for its answer (same <c>opId</c>) while the
    /// gateway says <c>409 op_in_flight</c> or does not answer, before it gives up. A hard ceiling: no op can
    /// hold a step longer than <c>deadline + this</c>.
    /// </summary>
    public int OpSettleGraceSeconds { get; set; } = 60;

    /// <summary>The pause between two asks for an in-flight op's answer, in milliseconds. Default 2000.</summary>
    public int OpPollIntervalMilliseconds { get; set; } = 2000;

    /// <summary>
    /// The deadline of a small op (<c>stat</c>, <c>read</c>, <c>close</c>) the caller gave none: these move no
    /// bulk data, so one that has not answered in this long is a silent session, not a slow one. Default 120 s;
    /// <c>stage</c>/<c>publish</c> keep <see cref="OpTimeoutSeconds"/> (their node-side download also has a
    /// connect/header/stall deadline) and ffmpeg ops carry their own timeout.
    /// </summary>
    public int SmallOpTimeoutSeconds { get; set; } = 120;

    /// <summary>The small-op deadline in force.</summary>
    public TimeSpan EffectiveSmallOpTimeout => TimeSpan.FromSeconds(SmallOpTimeoutSeconds > 0 ? SmallOpTimeoutSeconds : 120);
}
