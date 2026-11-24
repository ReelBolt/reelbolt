namespace ReelBolt.Shared.Moderation;

/// <summary>
/// Reserved <see cref="ModerationPolicyVerdict.ReasonCode"/> values the gates treat specially.
/// A verdict's normal ReasonCode is the model's top category; these two are ours.
/// </summary>
public static class ModerationReasonCodes
{
    /// <summary>The response carried no scores and no flags: it could not be classified.</summary>
    public const string Unclassified = "unclassified";

    /// <summary>
    /// A Block/Review decision whose audit row could not be written after the retries. Publish must
    /// deny this regardless of <see cref="ContentModerationOptions.FailClosedAtPublish"/>: an
    /// unrecorded block is a security gap, not an outage to fail open on.
    /// </summary>
    public const string AuditUnavailable = "audit_unavailable";

    /// <summary>
    /// The file plays for more than the 12 hours that can be judged (a short video under a 13-hour audio track). Final, and the
    /// refusal says length is the reason.
    /// </summary>
    public const string TooLong = "too_long";
}
