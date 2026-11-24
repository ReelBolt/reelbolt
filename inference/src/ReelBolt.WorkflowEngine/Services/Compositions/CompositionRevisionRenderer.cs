namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// The render a Render-mode revision falls back to when the reviser agent changed the project but did
/// not render it. Implemented by the agent's own render tool
/// (<c>ReactRemotionSandboxTools.RenderVideoAndUploadToStorage</c>), so the fallback is the same render
/// the agent would have made: the run's resolution and output policy, the render deadline policy, the
/// same upload, the same <c>PendingOutputStorageKey</c> record and the same workspace capture. Called
/// inside the revision's execution-context scope.
/// </summary>
public interface ICompositionRevisionRenderer
{
    Task<CompositionRevisionRenderResult> RenderAsync(string compositionId, string outputFileName, CancellationToken ct);
}

/// <summary>Whether the fallback render succeeded; on failure, the render tool's own error text.</summary>
public sealed record CompositionRevisionRenderResult(bool Succeeded, string? Error);
