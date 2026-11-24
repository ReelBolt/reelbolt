namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// The closed set of design-system kinds. Values are persisted verbatim, so they are
/// lowercase snake_case constants and this set is append-only.
/// </summary>
public static class DesignSystemKinds
{
    /// <summary>A design system extracted from a design.</summary>
    public const string Design = "design";

    /// <summary>A design system that maps design tokens to product surfaces.</summary>
    public const string ProductMap = "product_map";

    /// <summary>True when <paramref name="kind"/> is exactly one of the known kinds.</summary>
    public static bool IsValid(string? kind) =>
        string.Equals(kind, Design, StringComparison.Ordinal)
        || string.Equals(kind, ProductMap, StringComparison.Ordinal);
}
