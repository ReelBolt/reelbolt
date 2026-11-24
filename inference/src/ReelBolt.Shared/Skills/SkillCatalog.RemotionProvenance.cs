namespace ReelBolt.Shared.Skills;

/// <summary>
/// Where the five <see cref="SkillCategory.Remotion"/> skills came from. In this repository they are
/// written here (see <c>inference/skills/UPSTREAM.md</c>), so they carry no provenance link or commit.
/// </summary>
public static partial class SkillCatalog
{
    /// <summary>
    /// True when the Remotion skill directories are vendored from an upstream repository; false when
    /// they are written in this repository and carry no provenance link.
    /// </summary>
    public const bool RemotionSkillsAreVendored = false;

    /// <summary>Not used: the Remotion skills have no upstream repository.</summary>
    public const string RemotionUpstreamRepoUrl = "";

    /// <summary>Not used: the Remotion skills have no upstream commit.</summary>
    public const string RemotionUpstreamCommit = "";

    private const string? RemotionSourceCommit = null;

    private static string? RemotionSourceUrl(string relativePath) => null;
}
