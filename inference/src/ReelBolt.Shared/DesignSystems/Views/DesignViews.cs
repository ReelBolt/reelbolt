namespace ReelBolt.Shared.DesignSystems.Views;

/// <summary>
/// The design-document views an agent can be shown (P2-16). Members are append-only:
/// <see cref="GraphicsPalette"/> is Phase 3 and <see cref="GradeHint"/> is Phase 4.
/// </summary>
public enum DesignView
{
    Tokens,
    Motion,
    Voice,
    Identity,
    Conformance,
    GraphicsPalette,
    GradeHint,
}

/// <summary>The product-map views an agent can be shown (P2-16). Members are append-only.</summary>
public enum ProductView
{
    Structure,
    Overview,
}
