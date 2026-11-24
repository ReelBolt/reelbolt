namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The string vocabulary of the <c>data-assistantAction</c> chat-stream frame
/// (docs/design/assistant-approvals.md). Plain constants rather than enums so the wire values are the
/// literals the web app switches on, whatever JSON options the frame is written with.
/// </summary>
public static class AssistantActionState
{
    public const string Pending = "pending";
    public const string Allowed = "allowed";
    public const string Declined = "declined";
    public const string Expired = "expired";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>How an action is decided: by the user (<c>ask</c>) or by their auto mode (<c>auto</c>).</summary>
public static class AssistantActionMode
{
    public const string Ask = "ask";
    public const string Auto = "auto";
}

public static class AssistantActionRisk
{
    public const string Normal = "normal";
    public const string Destructive = "destructive";
    public const string Paid = "paid";
}

public static class AssistantActionKind
{
    public const string Workflow = "workflow";
    public const string Project = "project";
    public const string File = "file";
    public const string Style = "style";
    public const string Picture = "picture";
    public const string Run = "run";
    public const string Connection = "connection";
    public const string Agent = "agent";
}

public static class AssistantChangeKind
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Change = "change";
    public const string Info = "info";
}

/// <summary>One line of the "what will change" list on a permission card.</summary>
public sealed record AssistantActionChange(string Kind, string Text);

/// <summary>
/// What the app should reload after an action finished. These ids are for the app, never shown to a person.
/// </summary>
public sealed record AssistantActionRefresh(Guid? ProjectId, Guid? WorkflowId, Guid? DocumentId);

/// <summary>
/// One <c>data-assistantAction</c> frame. A later frame with the same <see cref="Id"/> replaces an earlier
/// one, so a card moves <c>pending → allowed → done</c> in place. <see cref="Title"/> and
/// <see cref="Summary"/> are written by the server from names it read from the database — they never carry
/// an id, and never take their words from the model alone.
/// </summary>
public sealed record AssistantActionFrame(
    Guid Id,
    string State,
    string Mode,
    string Kind,
    string Risk,
    string Title,
    string? Summary = null,
    IReadOnlyList<AssistantActionChange>? Changes = null,
    string? ProjectName = null,
    string? Cost = null,
    string? Outcome = null,
    DateTime? ExpiresAt = null,
    AssistantActionRefresh? Refresh = null,
    AssistantAutoApproveNote? AutoNote = null);

/// <summary>
/// Present only on a card shown in auto-approve mode because its cost passed the user's limit. The UI says
/// "Shown because this costs about X credits (your auto-approve limit is Y)", or that the cost could not be
/// estimated when <see cref="CostCredits"/> is null.
/// </summary>
public sealed record AssistantAutoApproveNote(double LimitCredits, double? CostCredits);
