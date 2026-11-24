namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The hand-written part of a step's catalog card (docs/design/workflow-apps.md section 7.1): what code cannot
/// infer. Sits on the step's config record, or on the <see cref="ReelBolt.Shared.Data.Models.StepType"/> member for
/// a step type that has no config record. Everything else on a card is reflected from code by
/// <see cref="StepCatalog"/>: the cost class is derived (<c>AppCostClass</c>) and this value must agree with it
/// (a test pins that), the brief fields come from <c>BriefFieldConsumers</c>, the mistakes and lint rules from
/// <c>StepCompatibilityRules</c>. Never put a price in any of these strings: cost class only.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class StepCardAttribute(string purpose, string[] needs, string produces, string costClass) : Attribute
{
    /// <summary>One plain sentence: what the step does.</summary>
    public string Purpose { get; } = purpose;

    /// <summary>What the step needs, in plain words, beyond the needs the linter rules state.</summary>
    public string[] Needs { get; } = needs;

    /// <summary>The shape of what the step writes.</summary>
    public string Produces { get; } = produces;

    /// <summary>One of the <c>AppCostClass</c> constants; must equal the derived class.</summary>
    public string CostClass { get; } = costClass;

    /// <summary>Who may read the output (plain words, one entry each).</summary>
    public string[] ReadableBy { get; init; } = [];

    /// <summary>Steps that usually follow, each as <c>StepTypeName: via</c> (how the next step reads this one).</summary>
    public string[] PairsWith { get; init; } = [];

    /// <summary>Name of the output contract class in <c>OutputSchemas</c>, when the step writes a named schema.</summary>
    public string? OutputContract { get; init; }
}
