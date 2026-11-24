namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The pure geometry decisions behind compositing one tracked screen insert: how far its warp
/// target is expanded past the tracked plate (overscan), and where that expanded target's corners
/// go. Kept out of <c>VideoCompileStepExecutor</c> so the encode and the real-footage measurement
/// harness (<c>InsertTrackingQualityHarness</c>) run the exact same arithmetic.
/// See docs/video-editing.md "Silhouette matting" and "Tracking quality on the customer clips".
/// </summary>
public static class InsertCompositeGeometry
{
    /// <summary>
    /// Overscan applied to a MATTED insert, overriding a smaller configured value. Large on
    /// purpose: the matte clips the content back to the plate's silhouette, so the only thing
    /// expansion can still do is guarantee the content reaches the plate's edge instead of
    /// stopping a tracking-grid pixel short of it.
    /// </summary>
    public const double MattedInsertOverscan = 0.10;

    /// <summary>The overscan an insert is actually composited with — never reduces a larger configured value.</summary>
    public static double EffectiveOverscan(double configured, bool matted) =>
        matted ? Math.Max(configured, MattedInsertOverscan) : configured;
}
