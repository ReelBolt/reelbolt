using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// Gives the videos of a restored workspace the treatment a fresh staging gives them (docs/sandbox-service.md
/// "Oversized staged videos"): a workspace captured before staged videos were copied holds the 4K originals in
/// <c>public/</c> and no <c>.reelbolt/staged-videos.json</c>, and the reviser rarely stages every clip again, so
/// its render read the originals and died (run 9e22258a, frame 93 of a 2160x3840 clip). Implemented by the
/// sandbox tools (<c>ReactRemotionSandboxTools.RestoredVideos.cs</c>); called by
/// <see cref="CompositionWorkspaceRestorer"/> after every import, so every path that restores a captured
/// workspace (revisions and their replays, continue-composition, the graphic designer, a recovered sandbox)
/// gets it before anything can render.
/// </summary>
public interface IRestoredWorkspaceVideos
{
    /// <summary>
    /// Replaces each oversized, unrecorded video under <c>public/</c> with the engine-made same-fps copy under
    /// the same name and records every video it looked at. Bounded and fail-open: it never throws (except for
    /// <paramref name="ct"/>), and a video it could not handle stays exactly as restored.
    /// </summary>
    Task<RestoredVideosReport> PrepareRestoredVideosAsync(ISandboxSession session, Guid projectId, CancellationToken ct);
}

/// <summary>
/// What a pass did: videos looked at, replaced by a copy, kept as they are (and recorded), not handled because
/// of an error, and left alone because a bound (count, bytes, time) was reached.
/// </summary>
public sealed record RestoredVideosReport(int Examined, int Copied, int Kept, int Failed, int Skipped)
{
    public static readonly RestoredVideosReport None = new(0, 0, 0, 0, 0);
}
