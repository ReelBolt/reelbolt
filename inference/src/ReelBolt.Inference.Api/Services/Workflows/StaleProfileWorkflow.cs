using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// QA T-102: a project copy of the "Build brand and product profile" template provisioned at an older version (v1)
/// keeps its saved steps, and in v1 a product-only fallback runs on into the style extractor, which it also charges.
/// The build route provisions the current version by its versioned name, so it never runs such a copy;
/// the generic execute paths refuse it with <see cref="Code"/> before any preflight or hold.
/// <para>
/// A copy is stale when both hold: its name is the template's versioned name at an older version (the identity the
/// provisioning service gives it), and its layout still has the v1 defect: the Compose step's design fallback is the
/// step right after its last product-fallback step, so the product branch falls through into it. A copy a user has
/// fixed by hand, or any workflow of their own, is not refused.
/// </para>
/// </summary>
public static class StaleProfileWorkflow
{
    /// <summary>The refusal code.</summary>
    public const string Code = "PROFILE_WORKFLOW_OUTDATED";

    /// <summary>The refusal, in plain words.</summary>
    public const string Message = "This profile workflow is out of date. Use Build profile to run the current version.";

    /// <summary>Whether a workflow named <paramref name="name"/> with <paramref name="steps"/> is a stale profile copy.</summary>
    public static bool IsStale(string? name, IEnumerable<WorkflowStep> steps)
    {
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey);
        if (template is null || string.IsNullOrWhiteSpace(name)) return false;

        string trimmed = name.Trim();
        bool olderVersion = Enumerable.Range(1, Math.Max(0, template.Version - 1))
            .Any(v => string.Equals(trimmed, $"{template.Name} v{v}", StringComparison.Ordinal));
        return olderVersion && ProductFallbackRunsIntoDesignFallback(steps);
    }

    /// <summary>
    /// Whether the workflow <paramref name="workflowId"/> of <paramref name="projectId"/> is a stale profile copy. False
    /// when there is no such workflow (the caller's own not-found answer applies).
    /// </summary>
    public static async Task<bool> IsStaleAsync(InferenceApiDbContext db, Guid projectId, Guid workflowId, CancellationToken ct)
    {
        WorkflowDefinition? workflow = await db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.ProjectId == projectId, ct);
        return workflow is not null && IsStale(workflow.Name, workflow.Steps);
    }

    private static bool ProductFallbackRunsIntoDesignFallback(IEnumerable<WorkflowStep> steps)
    {
        foreach (WorkflowStep step in steps)
        {
            if (step.StepType != StepType.DesignSystem || string.IsNullOrWhiteSpace(step.DesignSystemConfigJson)) continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(step.DesignSystemConfigJson);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("op", out JsonElement op) || op.GetString() != "Compose"
                    || !root.TryGetProperty("compose", out JsonElement compose))
                {
                    continue;
                }

                int[] product = Orders(compose, "productFallbackStepOrders");
                int[] design = Orders(compose, "designFallbackStepOrders");
                if (product.Length > 0 && design.Contains(product.Max() + 1)) return true;
            }
            catch (JsonException)
            {
                // Not a config this check can read: not the template's.
            }
        }

        return false;
    }

    private static int[] Orders(JsonElement compose, string property)
        => compose.TryGetProperty(property, out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32())]
            : [];
}
