using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One shot's captioning request: the id the executor is asking about, the keyframe JPEG (as the
/// media workspace materializes it — a real path for a local session, the session-relative virtual
/// path for a remote one; see <see cref="KeyframeBytes"/>), and the sample time (for
/// logging/diagnostics only — never sent to the model, and never part of the response contract).
/// </summary>
/// <param name="KeyframeBytes">
/// The keyframe's JPEG bytes as the media workspace returned them, or null to read
/// <see cref="KeyframePath"/> off this host.
/// <para>
/// <b>Why the bytes and the path both exist.</b> A caption is one small image, so a remote session
/// (D11) can hand the engine its bytes through the session's own <c>read</c> op while the footage
/// itself never leaves the runner — but only if the captioner accepts bytes. An implementation
/// MUST prefer these bytes when they are present and treat <see cref="KeyframePath"/> as the
/// diagnostics label for them; falling back to the path is what keeps every direct-construction
/// caller (and every existing test) working unchanged.
/// </para>
/// </param>
public sealed record ShotCaptionRequest(
    string ShotId, string KeyframePath, double AtSec,
    /// <summary>
    /// Phase 4 — a short, already-formatted sentence of DETERMINISTIC measurements for this shot,
    /// or null when visual analysis was off/degraded. Never numbers the model may restate — only
    /// the words Phase 1 derived — so the no-timestamp/no-number discipline is unaffected.
    /// </summary>
    string? MeasuredContext = null,
    /// <summary>Phase 4 (<c>KeyframesPerShot</c> &gt; 1) — true when <see cref="KeyframePath"/> is a multi-frame contact sheet, not a single still.</summary>
    bool IsContactSheet = false,
    byte[]? KeyframeBytes = null);

/// <summary>
/// Produces a short structured scene description of one shot's representative keyframe via a
/// vision-capable chat-completions call (Phase 2 — see docs/video-editing.md "Vision captioning").
/// </summary>
/// <remarks>
/// <b>Critical safety property:</b> the shot-id &lt;-&gt; caption binding must never be
/// model-controlled. An implementation MAY populate <see cref="VideoShotCaption.ShotId"/> on the
/// value it returns however it likes (including leaving whatever the model emitted), because the
/// caller — <c>VideoAnalyzeStepExecutor</c> — always overwrites it with
/// <see cref="ShotCaptionRequest.ShotId"/> (the id it actually requested) before attaching the
/// caption to a shot. This mirrors the id-anchored discipline
/// <c>VideoEditDecisionOutput</c>/<c>VideoCompileStepExecutor</c> already use for the story-editor
/// agent's output: never trust an identifier the model echoes back for anything that matters.
/// </remarks>
public interface IShotCaptioner
{
    Task<VideoShotCaption> CaptionAsync(
        ResolvedInferenceProvider provider, ShotCaptionRequest request, int maxCaptionChars, CancellationToken ct);
}
