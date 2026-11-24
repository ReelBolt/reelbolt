namespace ReelBolt.Inference.Api.Data;

/// <summary>
/// One in-product report a signed-in user submitted — a bug flag or a piece of feedback — together
/// with <b>exactly</b> the context they chose to attach. API-owned table <c>feedback_reports</c>; the
/// WorkflowEngine never reads or writes it.
/// </summary>
/// <remarks>
/// <para>
/// This is the alpha's primary signal channel and, by product decision, the
/// <b>only</b> place user prose is stored. Two consequences follow and are load-bearing:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="ContextJson"/> holds what the client sent and nothing more. The server never enriches a
/// report with facts the user did not tick (the caller's id, the active project, the assistant
/// thread): the minimisation promise is made in the UI and kept here.
/// </description></item>
/// <item><description>
/// The row is personal data (the message is the user's own words), so it is registered in the C0
/// data map (<c>docs/design/saas-accounts.md</c>): it is exported in the account archive and deleted
/// by the account purge.
/// </description></item>
/// </list>
/// <para>
/// Not tenant-filtered, like <see cref="RunRateEvent"/>: a row is only ever read through the caller's
/// own <see cref="UserId"/> (or by a platform admin), and is never addressed by an id a caller
/// supplies.
/// </para>
/// </remarks>
public class FeedbackReport
{
    /// <summary>The kind value of a bug report.</summary>
    public const string BugKind = "bug";

    /// <summary>The kind value of a general feedback message.</summary>
    public const string FeedbackKind = "feedback";

    public Guid Id { get; set; }

    /// <summary>The user who submitted the report; the key the export and the purge act on.</summary>
    public Guid UserId { get; set; }

    /// <summary>The organization active when the report was submitted.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary><see cref="BugKind"/> or <see cref="FeedbackKind"/>.</summary>
    public string Kind { get; set; } = FeedbackKind;

    /// <summary>The user's own words. The highest-risk column in the platform.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The context the user attached, serialised as submitted (JSON): an optional <c>page</c>,
    /// <c>projectId</c>, <c>executionId</c> and <c>assistantTurns</c>. Empty object when the user
    /// attached nothing.
    /// </summary>
    public string ContextJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
