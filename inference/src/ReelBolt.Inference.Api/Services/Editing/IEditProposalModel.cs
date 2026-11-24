using ReelBolt.Shared.Editing.Proposals;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// P8: the model half of an AI-assist proposal — takes the compiler's id-anchored view and brief,
/// returns the model's actions. The controller owns the view, the compiler owns the ops; this is the
/// only place a provider is called, and a test double replaces it wholesale.
/// </summary>
public interface IEditProposalModel
{
    /// <summary>
    /// Runs the agent's chat provider over the view. <c>null</c> means no chat provider is configured
    /// for that agent (the controller answers 503); a malformed answer is an empty output, never a throw.
    /// The one throw is <c>ModelUnpricedException</c>: the agent's model is an unpriced Platform model (the
    /// controller answers 409 MODEL_UNPRICED). <paramref name="provider"/> is the chat provider the controller resolved
    /// once and sized its credit hold on (T-026): the call runs on exactly that row; null means it could not be resolved
    /// up front, and the agent's provider is resolved here.
    /// </summary>
    Task<EditProposalOutput?> ProposeAsync(string agent, string view, string? brief, ResolvedInferenceProvider? provider, CancellationToken ct);
}
