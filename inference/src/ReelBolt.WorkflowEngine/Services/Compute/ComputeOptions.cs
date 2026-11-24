namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// How the engine obtains compute for a workflow execution (WP D6).
/// <para>
/// Append-only: <see cref="Local"/> is the value every install has today and MUST stay the default,
/// so <c>docker compose up</c> for a self-hoster needs no new configuration key at all
/// (docs/design/runner-protocol-adr.md §1: "the protocol is additive and <c>Compute:Mode=Local</c>
/// remains the default").
/// </para>
/// </summary>
public enum ComputeMode
{
    /// <summary>
    /// No compute fabric. Every step runs exactly where it runs today: the sandbox-executor plus
    /// in-process ffmpeg. The selector answers <c>Local</c> without reading the database and without
    /// ever calling the gateway, which is what keeps this the zero-config self-host default.
    /// </summary>
    Local = 0,

    /// <summary>
    /// The compute fabric is deployed. The selector resolves a real target per step: the
    /// organization's own paired runner (OrgRunner) or our worker pool (CloudPool).
    /// </summary>
    Fabric = 1
}

/// <summary>
/// The engine's compute-fabric settings, bound from the <c>Compute</c> configuration section
/// (which already carries D12's <c>Compute:OutputVerification</c> subsection).
/// <para>
/// <b>Nothing here is required.</b> An install that sets no <c>Compute:*</c> key at all gets
/// <see cref="Mode"/> <see cref="ComputeMode.Local"/> and never reaches the gateway, so every value
/// below is a fabric-only tunable and reading it with the fabric switched off is a no-op.
/// </para>
/// </summary>
public sealed class ComputeOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Compute";

    /// <summary>
    /// Whether the compute fabric is in play. <see cref="ComputeMode.Local"/> unless an operator
    /// turns it on, which is what keeps a self-host install working with no new configuration.
    /// </summary>
    public ComputeMode Mode { get; set; } = ComputeMode.Local;

    /// <summary>
    /// The shared secret the gateway's <c>/internal/v1</c> surface requires (WP D5b). The gateway
    /// answers <c>503 internal_api_disabled</c> when IT has no token configured; when the ENGINE has
    /// none, every call is refused with <c>401</c> rather than sent unauthenticated.
    /// <para>
    /// Read from <c>Compute:InternalToken</c> first, then from the <c>RUNNER_INTERNAL_TOKEN</c>
    /// environment variable — the same variable D5b's <c>main.go</c> reads, so one value configures
    /// both sides. It is never logged.
    /// </para>
    /// </summary>
    public string InternalToken { get; set; } = string.Empty;

    /// <summary>
    /// Base address of the Go <c>runner-gateway</c>'s internal API. Compose service name by default;
    /// the gateway is cluster-internal and is never published (docs/design/runner-protocol-adr.md §5:
    /// "cluster-internal API behind network policy plus a service token").
    /// </summary>
    public string GatewayBaseUrl { get; set; } = "http://runner-gateway:8090";

    /// <summary>
    /// How long a <c>GET /internal/v1/runners</c> answer is reused before the selector asks again.
    /// The plan's number: "runner online with the required caps (gateway query, cached 5 s)". A
    /// longer cache delays a newly paired runner becoming eligible; a shorter one puts a gateway call
    /// on the path of every step of every execution.
    /// </summary>
    public int RunnerQueryCacheSeconds { get; set; } = 5;

    /// <summary>
    /// When the selector would send a step to the <c>CloudPool</c> but no pool worker is online, run it
    /// <c>Local</c> instead. Default <c>true</c>.
    /// <para>
    /// A <c>CloudPool</c> job with no worker is never taken: the engine queues it, waits
    /// <c>SessionWaitSeconds</c>, and fails the step with "no runner was assigned" — and the idempotent
    /// job it left behind keeps its CloudPool target, so a desktop runner that connects afterwards still
    /// does not get it. On a deployment where the engine host already runs the sandbox (the media box),
    /// Local is the pool's own machine, so falling back to it is what "our infrastructure" means.
    /// Set <c>false</c> to restore the strict behaviour for a deployment whose pool is expected to
    /// have workers and should fail loudly without them.
    /// </para>
    /// </summary>
    public bool LocalWhenPoolEmpty { get; set; } = true;

    /// <summary>
    /// The on-demand pool waker's engine-side switch (<c>Compute:PoolWaker</c>). Off by default: with it
    /// off, or with the gateway reporting an unhealthy waker, the selector behaves exactly as it did
    /// before the waker existed.
    /// </summary>
    public PoolWakerOptions PoolWaker { get; set; } = new();

    /// <summary>
    /// The job types, comma separated wire names (<c>video.compile</c>, <c>video.analyze.extract</c>, <c>remotion.workspace-session</c>), that may be
    /// sent to the <c>CloudPool</c>. Empty (the default) means every job type, which is how the selector has
    /// always behaved. When it names some, a <c>CloudPool</c> decision for any other job type runs
    /// <c>Local</c> instead (<c>pool_job_type_not_enabled</c>). Use it to open the pool to the media jobs
    /// only and keep Remotion workspace sessions on the engine's own host.
    /// </summary>
    public string CloudPoolJobTypes { get; set; } = "";

    /// <summary><see cref="CloudPoolJobTypes"/> split on commas, trimmed, empty entries dropped.</summary>
    public string[] CloudPoolJobTypeList()
        => (CloudPoolJobTypes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Whether a paired desktop runner can take work in <c>Fabric</c> mode. Default <c>true</c>. With
    /// <c>false</c> a runner being online changes nothing: <c>Auto</c> and <c>PreferRunner</c> organizations
    /// go to the pool as if none were, and a <c>RunnerOnly</c> organization stays <c>Local</c>
    /// (<c>org_runners_disabled</c>) exactly as it does with <c>Compute:Mode=Local</c>. This is what makes
    /// <c>Fabric</c> a pool-only switch.
    /// </summary>
    public bool OrgRunnersEnabled { get; set; } = true;

    /// <summary>
    /// Send nothing to the <c>CloudPool</c> unless <see cref="PoolStorageEndpoint"/> is an https address a
    /// machine on the public internet can reach. A pool node fetches and publishes through presigned URLs
    /// signed against that address; a private one makes every pool job time out. Default <c>true</c>. The
    /// check only runs when <see cref="PoolStorageEndpoint"/> is known.
    /// </summary>
    public bool RequirePublicStorageForPool { get; set; } = true;

    /// <summary>The address presigned URLs are signed against (<c>MinIO:PublicEndpoint</c>, else <c>MinIO:Endpoint</c>). Filled in by the host; not a setting.</summary>
    public string? PoolStorageEndpoint { get; set; }

    /// <summary>Per-request timeout for the internal API. The long-poll's own wait is handled separately (see <see cref="JobWaitSeconds"/>).</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// The <c>wait</c> a job read asks for, in seconds. The specification's example is 30 s and the
    /// gateway caps it at 60 s, so a longer value buys nothing.
    /// </summary>
    public int JobWaitSeconds { get; set; } = 30;

    /// <summary>
    /// How long a caller waits for the per-execution session gate (<c>IExecutionSessionGate</c>)
    /// before giving up with <c>RUNNER_SESSION_BUSY</c>. <c>runner_sessions</c> permits ONE open
    /// session per execution, so a second session must queue behind the first; this is the bound on
    /// that queue.
    /// </summary>
    public int SessionWaitSeconds { get; set; } = 900;

    /// <summary>
    /// The token to authenticate with: the configured one when present, else
    /// <c>RUNNER_INTERNAL_TOKEN</c>, else null (which the client turns into a refusal, never an
    /// unauthenticated request).
    /// </summary>
    public string? ResolveInternalToken()
        => !string.IsNullOrWhiteSpace(InternalToken)
            ? InternalToken.Trim()
            : NullIfBlank(Environment.GetEnvironmentVariable("RUNNER_INTERNAL_TOKEN"));

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// <c>Compute:PoolWaker</c>: whether the engine may queue a CloudPool job when no pool worker is online
/// because a healthy pool waker will start one.
/// </summary>
public sealed class PoolWakerOptions
{
    /// <summary>Master switch. Default <c>false</c>: an empty pool runs the step Local (<c>pool_empty_local_fallback</c>).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The most a step waits for a runner to be ASSIGNED to a wake-and-wait job, in seconds, before the
    /// job is cancelled and the step runs Local. It never applies to a job a worker already holds.
    /// </summary>
    public int WaitSeconds { get; set; } = 240;
}
