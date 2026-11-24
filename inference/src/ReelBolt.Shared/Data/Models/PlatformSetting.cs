namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One operator-set platform switch (F11). Table <c>platform_settings</c>, owned (migrated) by the
/// Inference API; the WorkflowEngine maps it read-only and the Go runner gateway reads it by raw SQL,
/// so the schema may change only here.
///
/// <para>
/// The table is a tiny key/value store rather than a single settings row because a kill switch has to
/// be expressible at more than one scope: a platform-wide pause, and one tenant's own suspension. Every
/// row means the same thing — <see cref="Paused"/> is true, this scope is stopped — and the key decides
/// which scope that is:
/// </para>
/// <list type="bullet">
/// <item><c>compute</c>: no new execution is admitted and no new job is assigned. Work already queued
/// stays <c>Queued</c>, so a pause is a pause and never a loss.</item>
/// <item><c>inference</c>: no new execution is admitted that would spend the <i>platform's</i>
/// inference budget; an organisation that brings its own providers keeps running.</item>
/// <item><c>org:&lt;guid&gt;</c>: that organisation is disabled — it is admitted nothing, its queued
/// runner jobs are never assigned, and its desktop runners are refused at the gateway with the
/// protocol's <c>4403 organisation disabled</c>. <see cref="PoolSlotCap"/> optionally overrides that
/// organisation's CloudPool concurrency cap.</item>
/// </list>
///
/// <para>
/// <b>A missing row means ON, not OFF.</b> An empty or partially populated table is a platform with
/// nothing paused, which is what every deployment that has never touched a switch must observe. The
/// direction that must never be guessed is the other one: an unreadable store is handled as paused by
/// every reader (<c>PlatformSwitches</c> in the WorkflowEngine, <c>switches.go</c> in the runner
/// gateway), never as "no switch says otherwise, so carry on".
/// </para>
/// </summary>
public class PlatformSetting
{
    /// <summary>Key of the platform-wide compute pause.</summary>
    public const string ComputeKey = "compute";

    /// <summary>Key of the platform-wide pause on the platform's own inference spend.</summary>
    public const string InferenceKey = "inference";

    /// <summary>Prefix of a per-organisation row; the rest of the key is the organisation id.</summary>
    public const string OrganizationKeyPrefix = "org:";

    /// <summary>The settings key for <paramref name="organizationId"/>, in canonical lower-case form.</summary>
    public static string OrganizationKey(Guid organizationId)
        => OrganizationKeyPrefix + organizationId.ToString("D").ToLowerInvariant();

    /// <summary>
    /// Reads the organisation out of a per-organisation key. False for every other key, including the
    /// two global ones — a caller that must not act on a global row relies on this rather than on a
    /// string prefix test of its own.
    /// </summary>
    public static bool TryParseOrganizationKey(string? key, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        if (key is null || !key.StartsWith(OrganizationKeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Guid.TryParse(key[OrganizationKeyPrefix.Length..], out organizationId);
    }

    /// <summary>True for <see cref="ComputeKey"/>, <see cref="InferenceKey"/> and <c>org:&lt;guid&gt;</c>.</summary>
    public static bool IsKnownKey(string? key)
        => key == ComputeKey || key == InferenceKey || TryParseOrganizationKey(key, out _);

    /// <summary>The scope: <c>compute</c>, <c>inference</c> or <c>org:&lt;lower-case guid&gt;</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// True when this scope is stopped. There is no second flag: "paused" is the whole state, and a
    /// row that exists with <c>false</c> is an explicitly recorded resume (which is worth keeping,
    /// because it carries <see cref="UpdatedBy"/> and <see cref="Reason"/> for the audit trail).
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>Why, in the operator's own words. Shown to nobody automatically; it is for the record.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Per-organisation CloudPool concurrency cap override (null means "use the gateway's configured
    /// default"). Only meaningful on an <c>org:</c> row.
    /// </summary>
    public int? PoolSlotCap { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Identity of the platform admin who last wrote this row (an email; never a secret).</summary>
    public string? UpdatedBy { get; set; }
}
