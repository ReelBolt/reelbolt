namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Thin forwarding shim over <see cref="ReelBolt.Shared.Editing.OverlayTextSanitizer"/>, which
/// now holds the single implementation of the overlay-text sanitizer.
/// </summary>
/// <remarks>
/// <b>Why a shim rather than a clean move.</b> The implementation had to live in
/// <c>ReelBolt.Shared</c> because <c>EditTimelineValidator</c> (in
/// <c>ReelBolt.Shared.Editing</c>) applies the identical rule to a timeline's
/// <c>TitleClip</c>/<c>Caption</c> text, and <c>ReelBolt.Shared</c> cannot reference
/// <c>ReelBolt.WorkflowEngine</c>. Deleting this type outright would have forced a
/// <c>using</c> edit into every WorkflowEngine call site (<c>VideoCompileStepExecutor</c>,
/// <c>VisionShotCaptioner</c>) plus the existing <c>OverlayTextSanitizerTests</c>, while sibling
/// initiatives were still landing on those very files; keeping the class name and namespace
/// stable is what let all four need no edit.
/// <para>
/// Behaviour is byte-identical to the pre-move type — every member delegates straight through.
/// New callers should use <see cref="ReelBolt.Shared.Editing.OverlayTextSanitizer"/> directly;
/// once the remaining engine call sites are migrated, this file can be deleted in one commit.
/// </para>
/// </remarks>
public static class OverlayTextSanitizer
{
    /// <summary>
    /// See <see cref="ReelBolt.Shared.Editing.OverlayTextSanitizer.Sanitize"/>.
    /// </summary>
    public static string Sanitize(string? raw, int maxChars) =>
        ReelBolt.Shared.Editing.OverlayTextSanitizer.Sanitize(raw, maxChars);

    /// <summary>
    /// See <see cref="ReelBolt.Shared.Editing.OverlayTextSanitizer.TruncateByTextElements"/>.
    /// </summary>
    public static string TruncateByTextElements(string value, int maxChars) =>
        ReelBolt.Shared.Editing.OverlayTextSanitizer.TruncateByTextElements(value, maxChars);
}
