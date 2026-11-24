using System.Reflection;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Replay;

/// <summary>
/// Copies a <see cref="WorkflowStep"/> into another workflow definition — every scalar column (order,
/// type, label, context mode, every step-config blob, cache mode …) — with a new id and no navigation
/// properties set. Reflection over the entity rather than a hand-written list, so a config column added to
/// <see cref="WorkflowStep"/> later is carried into a composition revision's replay snapshot without
/// anyone remembering to add it here (a forgotten column would silently run the replayed step with its
/// default config). <c>WorkflowStepClonerTests</c> pins the property set.
/// </summary>
public static class WorkflowStepCloner
{
    private static readonly PropertyInfo[] Copied =
    [
        .. typeof(WorkflowStep).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && IsScalar(p.PropertyType))
            .Where(p => p.Name is not (nameof(WorkflowStep.Id) or nameof(WorkflowStep.WorkflowDefinitionId)))
    ];

    /// <summary>The names of the properties <see cref="Clone"/> copies (for the drift test).</summary>
    public static IReadOnlyList<string> CopiedPropertyNames { get; } = [.. Copied.Select(p => p.Name).Order(StringComparer.Ordinal)];

    public static WorkflowStep Clone(WorkflowStep source, Guid workflowDefinitionId)
    {
        WorkflowStep copy = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflowDefinitionId };
        foreach (PropertyInfo property in Copied)
            property.SetValue(copy, property.GetValue(source));
        return copy;
    }

    private static bool IsScalar(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(decimal);
    }
}
