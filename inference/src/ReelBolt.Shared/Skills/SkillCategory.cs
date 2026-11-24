namespace ReelBolt.Shared.Skills;

/// <summary>
/// Broad grouping for a <see cref="SkillDescriptor"/>. Not used for access control (that's
/// <see cref="SkillCatalog.DefaultsFor"/>/the per-agent resolved skill set) — purely informational,
/// e.g. for the <c>/skills/status</c> diagnostic endpoint.
/// </summary>
public enum SkillCategory
{
    /// <summary>A skill vendored from the upstream Remotion skills corpus (see inference/skills/).</summary>
    Remotion,

    /// <summary>A ReelBolt-authored skill (e.g. reelbolt-blocks).</summary>
    ReelBolt,

    /// <summary>A skill authored by a workflow owner for their own Custom agents.</summary>
    Custom,

    /// <summary>
    /// A design-taste skill vendored from Leonxlnx/taste-skill (MIT): typography, colour, shape,
    /// copy and motion discipline for the agents that decide or write what a video looks like.
    /// Appended AFTER <see cref="Custom"/> on purpose: the enum is append-only, because a member
    /// inserted earlier would shift the numeric value of every member after it (the API and the
    /// dashboard carry the NAME, but nothing should have to prove that).
    /// </summary>
    DesignTaste
}
