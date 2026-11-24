namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The caller's own first-run onboarding state: where the product tour stands and which checklist items
/// are closed. One row per USER (never per organization). <see cref="MarksJson"/> is a JSON array of
/// closed checklist keys; an item is stored the first time it is observed done, so it stays done.
/// </summary>
public class UserOnboardingState
{
    public Guid UserId { get; set; }

    /// <summary><c>pending</c>, <c>completed</c> or <c>skipped</c>.</summary>
    public string TourStatus { get; set; } = "pending";

    public int? TourVersion { get; set; }

    /// <summary>When the tour status/version last changed; null until the tour is first set.</summary>
    public DateTime? TourUpdatedAt { get; set; }

    public bool ChecklistDismissed { get; set; }

    /// <summary>JSON array of closed checklist item keys that are done (sticky).</summary>
    public string MarksJson { get; set; } = "[]";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
