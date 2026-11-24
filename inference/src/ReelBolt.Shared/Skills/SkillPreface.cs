namespace ReelBolt.Shared.Skills;

/// <summary>
/// A vendored skill's body is published upstream and kept byte-identical to it
/// (<c>inference/skills/UPSTREAM.md</c>). When that text was written for another medium (the
/// taste skills target web pages, ReelBolt renders video), the only place we may say so is a
/// separate ReelBolt-authored file next to it: <see cref="FileName"/>. This is the single place
/// that decides how the two are joined, so the agent (via <c>UseSkill</c>) and the admin view of
/// the skill show exactly the same thing.
/// </summary>
public static class SkillPreface
{
    /// <summary>
    /// Name of the optional preface file inside a skill's directory. It is plain markdown with no
    /// frontmatter, and it is ours: it is never part of what is pinned to an upstream commit.
    /// </summary>
    public const string FileName = "REELBOLT-PREFACE.md";

    private const string Divider = "\n\n---\n\n*The upstream skill follows, unmodified.*\n\n";

    /// <summary>
    /// Returns <paramref name="upstreamContent"/> unchanged when there is no preface; otherwise the
    /// preface, a divider that says the rest is upstream's, and then the upstream content.
    /// </summary>
    public static string Compose(string? preface, string upstreamContent)
    {
        string trimmed = (preface ?? string.Empty).Replace("\r\n", "\n").Trim();
        return trimmed.Length == 0 ? upstreamContent : trimmed + Divider + upstreamContent;
    }
}
