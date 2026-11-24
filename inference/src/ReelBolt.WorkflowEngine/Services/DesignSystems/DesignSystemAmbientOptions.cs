namespace ReelBolt.WorkflowEngine.Services.DesignSystems;

/// <summary>
/// Options for the per-run brand and product-profile ambient blocks (docs/design-system.md §11).
/// Bound from the <c>DesignSystem:Ambient</c> configuration section
/// (<c>DesignSystem__Ambient__*</c> env vars in compose).
/// </summary>
public sealed class DesignSystemAmbientOptions
{
    public const string SectionName = "DesignSystem:Ambient";

    /// <summary>When false no pin is loaded and every prompt is byte-identical.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per-block cap for the design-system view, in characters.</summary>
    public int DesignMaxChars { get; set; } = 2400;

    /// <summary>Per-block cap for the product-profile view, in characters.</summary>
    public int ProductMaxChars { get; set; } = 3200;
}
