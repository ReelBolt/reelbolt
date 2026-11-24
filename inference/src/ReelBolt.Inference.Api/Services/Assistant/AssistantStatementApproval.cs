namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// A statement an approval card showed verbatim and asked the person to confirm by allowing the action: today the
/// OBJECT statement of an object avatar (docs/design/avatar-objects.md "Avatar tools for the assistant and MCP": the
/// card shows the object statement and approving it is the tick).
/// </summary>
/// <param name="Version">The statement's version id (for example <c>avatar-object-2026-11</c>).</param>
/// <param name="Locale">The language it was shown in.</param>
/// <param name="Text">The exact text shown.</param>
public sealed record ApprovalStatement(string Version, string Locale, string Text);

/// <summary>
/// Marks the one tool call a person approved on a card that showed a statement to confirm
/// (<see cref="ApprovalCard.Statement"/>). Set by <see cref="AssistantApprovalGate"/> around the tool it runs, only when
/// that card was shown and allowed (never in auto mode: such a card always asks), and read by the tool as the person's
/// tick. Ambient to the call, so nothing a model writes can set it; it ends with the call.
/// </summary>
public static class AssistantStatementApproval
{
    private static readonly AsyncLocal<ApprovalStatement?> Current = new();

    /// <summary>The statement the person confirmed for the call running now; null outside such a call.</summary>
    public static ApprovalStatement? Approved => Current.Value;

    /// <summary>Marks the current call as confirming <paramref name="statement"/> until the returned scope is disposed.</summary>
    public static IDisposable Enter(ApprovalStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ApprovalStatement? before = Current.Value;
        Current.Value = statement;
        return new Scope(before);
    }

    private sealed class Scope(ApprovalStatement? before) : IDisposable
    {
        public void Dispose() => Current.Value = before;
    }
}
