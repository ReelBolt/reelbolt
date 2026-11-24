using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Shared.Avatars;

/// <summary>
/// The one shared answer to "is this project file held by a control?", for both contexts that can remove file rows: the
/// API's hold-marker hook and the engine's superseded-render cleanup. A held row is quarantined by CSAM matching or by
/// moderation, or is a platform-made output at or before its project's content-quarantine marker. The engine never
/// hard-deletes a held row (it leaves it for the API's own cleanup, which stamps the library hold marker first).
/// </summary>
public static class HeldFileRule
{
    public const string Csam = "csam";
    public const string Moderation = "moderation";
    public const string ContentMarker = "content_marker";

    /// <summary>The reason the file is held (<c>csam</c>, <c>moderation</c>, <c>content_marker</c>), or null when it is not.</summary>
    public static string? Reason(
        CsamScanStatus csamStatus, string? csamQuarantineKey, FileModerationStatus moderationStatus,
        DateTime? outputProducedAt, DateTime? contentQuarantinedAt)
    {
        if (csamStatus == CsamScanStatus.Quarantined || csamQuarantineKey is not null) return Csam;
        if (moderationStatus == FileModerationStatus.Quarantined) return Moderation;
        if (outputProducedAt is { } produced && contentQuarantinedAt is { } marker && produced <= marker + ContentQuarantineRule.ClockSkewMargin)
            return ContentMarker;
        return null;
    }

    public static string? Reason(ProjectFile file, DateTime? contentQuarantinedAt) =>
        Reason(file.CsamStatus, file.CsamQuarantineKey, file.ModerationStatus, file.OutputProducedAt, contentQuarantinedAt);
}
