namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// What kind of runner a <c>runner_devices</c> row is (docs/runner-protocol.md "Connection
/// handshake and authentication"). Append-only: stored as the member name.
/// </summary>
public enum RunnerKind
{
    /// <summary>A user's own machine, paired through the desktop app and authenticated by an Ed25519 key.</summary>
    Desktop,

    /// <summary>One of our own worker VMs, authenticated by the pool credential; serves any organization.</summary>
    CloudPool
}

/// <summary>
/// The job types of runner protocol v1. Stored as the member name; the wire names
/// (<c>remotion.workspace-session</c>, <c>video.compile</c>, <c>video.analyze.extract</c>) are the
/// gateway's concern. Append-only.
/// </summary>
public enum RunnerJobType
{
    RemotionWorkspaceSession,
    VideoCompile,
    VideoAnalyzeExtract
}

/// <summary>
/// Lifecycle of a runner job (docs/runner-protocol.md "Job lifecycle, leases and fencing"):
/// Queued, Assigned, Running, then exactly one terminal state. Append-only.
/// </summary>
public enum RunnerJobStatus
{
    Queued,
    Assigned,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Expired
}

/// <summary>
/// Where a runner job is allowed to run (docs/runner-protocol.md "Routing and targets"): chosen by the
/// engine's selector, never by the gateway. Append-only.
/// </summary>
public enum RunnerJobTarget
{
    CloudPool,
    OrgRunner
}

/// <summary>
/// Lifecycle of one device pairing request (WP E4a) on <c>runner_pairing_requests</c>. Stored as the
/// member name. Append-only: <see cref="Pending"/> is the only non-terminal state, and a request
/// leaves it exactly once — approved, denied by the user, or swept to expired. The short user code is
/// unique among <see cref="Pending"/> rows only, so a code is reusable once its request settles.
/// </summary>
public enum RunnerPairingStatus
{
    /// <summary>Waiting for a signed-in user to approve or deny the short code.</summary>
    Pending,

    /// <summary>Approved: a <see cref="RunnerDevice"/> row was created and is linked from the request.</summary>
    Approved,

    /// <summary>The user explicitly refused the code (or its organization choice).</summary>
    Denied,

    /// <summary>The ten-minute window elapsed before anyone acted on it.</summary>
    Expired
}
