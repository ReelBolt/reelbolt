using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.DesignSystems.Views;

/// <summary>
/// The design and product views each agent sees (SPEC 11). Shared so the preview route and the
/// engine (P2-17) answer from one table; every other agent gets no ambient view at all.
/// </summary>
public static class DesignSystemRoleViews
{
    /// <summary>
    /// The views for <paramref name="agentType"/>, or <c>(null, null)</c> when the agent is not
    /// shown an ambient design block.
    /// </summary>
    public static (DesignView? Design, ProductView? Product) ViewsFor(AgentType agentType) => agentType switch
    {
        AgentType.RemotionComponentTranslator => (DesignView.Tokens, ProductView.Structure),
        AgentType.AnimationStrategyAgent => (DesignView.Motion, ProductView.Overview),
        AgentType.ScriptwriterAgent => (DesignView.Voice, ProductView.Overview),
        AgentType.DirectorAgent => (DesignView.Identity, ProductView.Overview),
        AgentType.AuthorAgent => (DesignView.Tokens, ProductView.Structure),
        AgentType.ReviewAgent => (DesignView.Conformance, ProductView.Overview),
        AgentType.CompositionReviser => (DesignView.Tokens, ProductView.Structure),
        AgentType.MotionGraphicsPlanner => (DesignView.GraphicsPalette, null),
        AgentType.MotionGraphicsDirector => (DesignView.GraphicsPalette, null),
        AgentType.VideoReviewAgent => (DesignView.Conformance, null),
        AgentType.Colorist => (DesignView.GradeHint, null),
        AgentType.ColorGradeDirector => (DesignView.GradeHint, null),
        _ => (null, null),
    };
}
