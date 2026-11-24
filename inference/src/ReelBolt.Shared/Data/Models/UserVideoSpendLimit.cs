namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One person's own daily video-generation spend limit (table <c>user_video_spend_limits</c>,
/// docs/design/video-spend-limit.md). One row per USER, never per organization; removed with the account.
/// The Inference API owns and writes it; the engine's budget gate reads it (read-only mapping).
/// No row, or a null <see cref="DailyLimitCredits"/>, means no daily limit beyond the credit balance and each
/// step's <c>MaxSpendCredits</c>.
/// </summary>
public class UserVideoSpendLimit
{
    public Guid UserId { get; set; }

    /// <summary>Most credits of <c>VideoGenerate</c> the user's runs may spend per UTC day; null = no limit.</summary>
    public decimal? DailyLimitCredits { get; set; }

    /// <summary>The day's spend at which the app starts showing an alert; null = no alert. Informational only.</summary>
    public decimal? AlertThresholdCredits { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
