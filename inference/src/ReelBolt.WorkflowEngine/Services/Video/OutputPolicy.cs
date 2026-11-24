namespace ReelBolt.WorkflowEngine.Services.Video;

using ReelBolt.Shared.Workflows;

/// <summary>
/// The trial output policy (see <c>docs/billing.md</c>) resolved for ONE compile: what the
/// run's plan entitles it to, translated into the two things
/// <see cref="OutputPolicyFilterBuilder"/> needs — a maximum output height and whether the ReelBolt
/// watermark must be composited.
///
/// <para>
/// Resolved by the EXECUTOR from <c>IOrganizationEntitlements</c> and passed down to the two
/// re-encode helpers as plain data, so neither helper touches a service provider, a configuration
/// source or the file system itself (the watermark asset path is only ever an <c>-i</c> argument
/// they append — see <see cref="WatermarkAssetPath"/>).
/// </para>
///
/// <para>
/// <see cref="None"/> is the unentitled policy: an unlimited cap (so the builder's own
/// <c>capIsNoOp</c> short-circuit fires and no filter is emitted at all) and no watermark. Every
/// pre-B8b call path is byte-identical to before under it.
/// </para>
/// </summary>
/// <param name="MaxHeight">Tallest permitted output height in pixels; <see cref="ReelBolt.Shared.Tenancy.OrganizationEntitlementSet.UnlimitedInt"/> means unlimited.</param>
/// <param name="Tier">Plan key the policy was read from, recorded as a provenance fact.</param>
/// <param name="WatermarkEntitled">Whether the plan entitles the org to a watermarked output — true even when the mark cannot be applied (see <paramref name="WatermarkSkippedReason"/>).</param>
/// <param name="ApplyWatermark">Whether the mark is actually composited into this encode.</param>
/// <param name="WatermarkAssetPath">Local raster to composite; non-null exactly when <paramref name="ApplyWatermark"/> is true.</param>
/// <param name="WatermarkSkippedReason">Why an entitled watermark is not applied; null when it is applied or was never entitled.</param>
public sealed record ResolvedOutputPolicy(
    int MaxHeight = ReelBolt.Shared.Tenancy.OrganizationEntitlementSet.UnlimitedInt,
    string? Tier = null,
    bool WatermarkEntitled = false,
    bool ApplyWatermark = false,
    string? WatermarkAssetPath = null,
    string? WatermarkSkippedReason = null)
{
    /// <summary>The unentitled policy — no cap, no watermark, no emitted filter. The default every pre-B8b call path gets.</summary>
    public static ResolvedOutputPolicy None { get; } = new();

    /// <summary>An entitled watermark that could not be applied because no asset is configured for this deployment.</summary>
    public const string AssetNotConfiguredReason = "watermark_asset_not_configured";

    /// <summary>An entitled watermark that could not be applied because the configured asset is not actually on disk.</summary>
    public const string AssetMissingReason = "watermark_asset_missing";

    /// <summary>An entitled watermark that no filter could carry, because this compile is a stream copy (no filtergraph at all).</summary>
    public const string StreamCopyReason = "stream_copy_has_no_filter_stage";

    /// <summary>
    /// Whether a frame of this size exceeds the cap, i.e. whether <see cref="OutputPolicyFilterBuilder"/>
    /// will actually emit a scale stage. The cap names the frame's SHORT side, so a 1080x1920 reel is
    /// within a 1080 cap; deciding on the height alone disagreed with the builder and left a bare
    /// <c>[vpol]</c> stage in the graph ("No such filter: ''", staging run 514ad632).
    /// </summary>
    public bool CapsAt(int widthPixels, int heightPixels) =>
        OutputPolicyFilterBuilder.CapApplies(widthPixels, heightPixels, MaxHeight);

    /// <summary>
    /// This policy as the recorded provenance fact, or null when nothing was resolved (no
    /// organization, no entitlement service) — which is what keeps the manifest byte-identical for
    /// every run that has no plan.
    /// </summary>
    /// <param name="sourceHeight">The height the encode's own picture pipeline produces BEFORE the policy.</param>
    /// <param name="heightCapApplied">
    /// Whether the encode that ran actually carried the cap. False on the stream-copy path, which has
    /// no filtergraph — and the recorded output height then follows THIS, never <see cref="CapsAt"/>,
    /// so a stream-copied 1080p file is never reported as 720p.
    /// </param>
    /// <param name="watermarkApplied">Whether the encode that ran actually carried the mark.</param>
    /// <param name="skippedReason">Override for the recorded skip reason (e.g. <see cref="StreamCopyReason"/>); null keeps this policy's own reason.</param>
    public TimelineOutputPolicy ToProvenance(int sourceWidth, int sourceHeight, bool heightCapApplied, bool watermarkApplied, string? skippedReason = null) =>
        new(
            Tier: Tier,
            // Unlimited is reported as "no cap", never as the int.MaxValue sentinel.
            MaxHeight: MaxHeight == ReelBolt.Shared.Tenancy.OrganizationEntitlementSet.UnlimitedInt ? null : MaxHeight,
            HeightCapApplied: heightCapApplied,
            OutputHeight: heightCapApplied ? OutputPolicyFilterBuilder.OutputHeight(sourceWidth, sourceHeight, MaxHeight) : sourceHeight,
            WatermarkEntitled: WatermarkEntitled,
            WatermarkApplied: watermarkApplied,
            WatermarkSkippedReason: watermarkApplied ? null : skippedReason ?? WatermarkSkippedReason);
}
