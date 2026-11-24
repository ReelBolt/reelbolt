using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// The plain-language warnings a run page shows about the design the run was pinned to: the pinned
/// design has no tokens (the brand was not applied), or a newer Draft was waiting for review when
/// the run started. A run is never blocked by either: the customer may have meant to run without a
/// brand, so the page says what happened and what to do. Reads only; any failure answers no warning.
/// </summary>
public static class DesignSystemRunWarnings
{
    /// <summary>The warnings for one run. Empty when the run's design is fine or cannot be read.</summary>
    public static async Task<IReadOnlyList<string>> ForExecutionAsync(
        InferenceApiDbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        try
        {
            if (RunBriefJson.TryParse(execution.RunBriefJson)?.DesignSystem == RunBrief.DesignSystemNone)
                return [];

            DesignSystemPin? pin = DesignSystemPinJson.TryParse(execution.DesignSystemPinJson);
            var warnings = new List<string>();

            int? pinnedNumber = pin?.Design?.VersionNumber;
            if (pin?.Design is { VersionId: { } versionId, Source: null or "project" })
            {
                string? documentJson = await db.DesignSystemVersions.AsNoTracking()
                    .Where(v => v.Id == versionId)
                    .Select(v => v.DocumentJson)
                    .FirstOrDefaultAsync(ct);
                if (documentJson is not null
                    && JsonNode.Parse(documentJson) is JsonObject document
                    && DesignImportGuard.CountTokens(document) == 0)
                {
                    warnings.Add(
                        $"This run used design version {pinnedNumber!.Value.ToString(CultureInfo.InvariantCulture)}, which has no colours, fonts or other design tokens, so your brand was not applied. "
                        + "Import or build your design, approve it, and run again.");
                }
            }

            // A Draft that was already waiting when the run started and is still waiting: the run did not use it.
            int? draftNumber = await db.DesignSystemVersions.AsNoTracking()
                .Where(v => v.State == DesignVersionState.Draft
                    && v.CreatedAt <= (execution.QueuedAt ?? execution.StartedAt ?? DateTime.UtcNow)
                    && db.DesignSystems.Any(s => s.Id == v.DesignSystemId
                        && s.ProjectId == execution.ProjectId
                        && s.Kind == DesignSystemKinds.Design)
                    && (pinnedNumber == null || v.VersionNumber > pinnedNumber))
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => (int?)v.VersionNumber)
                .FirstOrDefaultAsync(ct);
            if (draftNumber is { } draft)
            {
                string draftText = draft.ToString(CultureInfo.InvariantCulture);
                warnings.Add(pinnedNumber is { } used
                    ? $"A newer design draft (version {draftText}) was waiting for review when this run started, so the run used version {used.ToString(CultureInfo.InvariantCulture)}. Approve the draft and run again to use it."
                    : $"This run had no approved design to use, and a design draft (version {draftText}) was waiting for review. Approve the draft and run again to apply your brand.");
            }

            return warnings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A warning is advice, never a reason to fail reading a run.
            return [];
        }
    }
}
