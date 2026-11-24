using System.Collections.Generic;
namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Marks the one tool call a person approved on a card that said "allows sending your media to AI services for this
/// run" (docs/design/app-inputs.md "Permission to send media as an app field"). Set by <see cref="AssistantApprovalGate"/>
/// around the tool it runs, only when that card was shown and allowed (never in auto mode, never for a generic card),
/// and read by the run tool as the consent. Ambient to the call, so nothing a model writes can set it.
/// </summary>
public static class AssistantMediaEgressApproval
{
    private static readonly AsyncLocal<bool> Approved = new();
    private static readonly AsyncLocal<IReadOnlyCollection<int>?> Listed = new();

    /// <summary>True while a tool runs under a card that asked for, and got, the permission.</summary>
    public static bool IsApproved => Approved.Value;

    /// <summary>
    /// The step orders the approved card listed as having their footage-sharing switch turned on (a workflow edit's card), or
    /// null when it listed none. A step the edit would grant that is not here was not on the card the person allowed.
    /// </summary>
    public static IReadOnlyCollection<int>? ListedSteps => Listed.Value;

    /// <summary>Marks the current call as approved until the returned scope is disposed; <paramref name="listedSteps"/> are the step orders its card listed.</summary>
    public static IDisposable Enter(IReadOnlyCollection<int>? listedSteps = null)
    {
        bool before = Approved.Value;
        IReadOnlyCollection<int>? listedBefore = Listed.Value;
        Approved.Value = true;
        Listed.Value = listedSteps;
        return new Scope(before, listedBefore);
    }

    private sealed class Scope(bool before, IReadOnlyCollection<int>? listedBefore) : IDisposable
    {
        public void Dispose()
        {
            Approved.Value = before;
            Listed.Value = listedBefore;
        }
    }
}
