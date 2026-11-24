using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>The content one tracked plate was assigned, and how it is fitted (null = the config's own fit).</summary>
internal readonly record struct InsertContentChoice(InsertContentRef Ref, InsertContentFit? Fit);

/// <summary>
/// T-106: which content a tracked plate gets from the config — the most specific <c>InsertContents</c> entry
/// (region over source over neither, then list order), else <c>InsertContentRef</c>, else the legacy
/// <c>InsertContentProjectFileId</c>. The one scoring rule, shared by the legacy file-id path.
/// </summary>
internal static class InsertContentSelector
{
    public static InsertContentChoice? Pick(VideoCompileStepConfig config, VideoInsertRegionTrack region)
    {
        VideoInsertContentAssignment? best = null;
        int bestScore = -1;
        foreach (VideoInsertContentAssignment entry in config.InsertContents ?? [])
        {
            bool regionMatches = string.IsNullOrWhiteSpace(entry.RegionId) ||
                                 string.Equals(entry.RegionId.Trim(), region.Id, StringComparison.OrdinalIgnoreCase);
            bool sourceMatches = entry.SourceIndex is null || entry.SourceIndex == region.SourceIndex;
            if (!regionMatches || !sourceMatches)
                continue;

            int score = (string.IsNullOrWhiteSpace(entry.RegionId) ? 0 : 2) + (entry.SourceIndex is null ? 0 : 1);
            if (score > bestScore)
            {
                best = entry;
                bestScore = score;
            }
        }

        if (best is not null)
            return new InsertContentChoice(best.InsertContentRef ?? ProjectFile(best.ProjectFileId), best.Fit);
        if (config.InsertContentRef is not null)
            return new InsertContentChoice(config.InsertContentRef, null);
        return config.InsertContentProjectFileId is Guid fallback ? new InsertContentChoice(ProjectFile(fallback), null) : null;
    }

    private static InsertContentRef ProjectFile(Guid id) => new(InsertContentRef.ProjectFileKind, ProjectFileId: id);
}
