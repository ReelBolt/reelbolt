using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Marks one step-config property as settable from the app page (docs/design/app-inputs.md 7.1). The allowlist IS
/// these attributes: <see cref="AppBindableCatalog"/> reflects them into <c>StepCard.BindableFields</c>, the save
/// validator, the manifest validator and the binder, so nothing else can be bound. A text type makes the property a
/// placeholder target (<c>{{input.id}}</c> inside the string); any other type makes it a whole-value target (the
/// step's <c>appInputs</c> map). Consent, egress, providers, spend caps, ids, step references, timeouts and cache
/// settings must never carry it (the deny test in <c>AppBindableAllowlistTests</c> fails the build otherwise).
/// Optional numeric members use <see cref="double.NaN"/> for "not set" (attribute arguments cannot be nullable).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class AppBindableAttribute(params AppInputType[] types) : Attribute
{
    /// <summary>The input types that may bind this property; text types and other types never mix.</summary>
    public AppInputType[] Types { get; } = types;

    /// <summary>The plain label the builder offers when it makes this setting an app input.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Smallest value a number or duration may take (NaN = none).</summary>
    public double Min { get; init; } = double.NaN;

    /// <summary>Largest value a number or duration may take (NaN = none).</summary>
    public double Max { get; init; } = double.NaN;

    /// <summary>True when the value must be a whole number.</summary>
    public bool Integer { get; init; }

    /// <summary>The longest text the property takes (text types; 0 = the type's own cap).</summary>
    public int MaxLength { get; init; }

    /// <summary>The closed set of values a choice, shape or toggle-on value may take (null = any value of the property's type).</summary>
    public string[]? Choices { get; init; }

    /// <summary>For a toggle bound to a non-boolean property: the value written when the toggle is on (NaN = the property is boolean).</summary>
    public double ToggleOn { get; init; } = double.NaN;

    /// <summary>For a toggle bound to a non-boolean property: the value written when the toggle is off.</summary>
    public double ToggleOff { get; init; }

    /// <summary>True for a step spend cap in credits: the manifest validator then requires a plain, always-shown number input with a visible default and a maximum.</summary>
    public bool SpendCap { get; init; }

    /// <summary>The run-brief field that already governs this property; an app exposing that field may not bind it (4.5).</summary>
    public string? GovernedByBriefField { get; init; }
}
