using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>
/// The false-positive scan as a test-time utility (docs/workflow-linter.md "Reviewing false positives"): lints a
/// JSON export of real workflows with the graph rules and prints, per rule, how many workflows it fires on, plus
/// the workflows that carry an error. It reads a FILE and connects to nothing: the export is made by someone with
/// the right access (a read-only query or the workflows API), then the scan is run on it:
/// <code>WORKFLOW_LINT_SCAN_EXPORT=/path/export.json dotnet test --filter FullyQualifiedName~WorkflowLintScan</code>
/// The export is an array of <c>{ "id", "name", "steps": [ ... ] }</c>; a step has the members of
/// <c>CreateWorkflowStepRequest</c> (camelCase: stepOrder, stepType, label, loopTargetStepOrder, the
/// <c>*ConfigJson</c> strings) plus an optional <c>agentType</c> (the agent's AgentType name) so the agent rules
/// can judge it; a step without it is an unknown agent, which no rule judges. Database facts are unknown in a scan
/// (no provider, plan, file or voice rule can fire), so what it measures is the graph rules WL001 to WL005, WL013,
/// WL014, WL015 and WL019, which are the ones that can raise an error from the steps alone.
/// </summary>
public static class WorkflowLintScan
{
    /// <summary>The export path environment variable the scan test reads.</summary>
    public const string ExportVariable = "WORKFLOW_LINT_SCAN_EXPORT";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The scan's result: per-rule counts and the workflows with errors.</summary>
    public sealed record ScanResult(
        int Workflows,
        IReadOnlyDictionary<string, int> WorkflowsPerRule,
        IReadOnlyList<(string Workflow, LintFinding Finding)> Errors);

    /// <summary>Lints the export at <paramref name="exportPath"/> and writes a short report to <paramref name="output"/>.</summary>
    public static async Task<ScanResult> RunAsync(string exportPath, TextWriter output, CancellationToken ct = default)
    {
        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(exportPath, ct));
        return await RunAsync(doc.RootElement, output, ct);
    }

    /// <summary>Lints an export that is already parsed.</summary>
    public static async Task<ScanResult> RunAsync(JsonElement export, TextWriter output, CancellationToken ct = default)
    {
        WorkflowLinter linter = new(new LintKit.FixedFactsSource(UnknownLintFacts.Instance));
        Dictionary<string, int> perRule = [];
        List<(string, LintFinding)> errors = [];
        int count = 0;

        foreach (JsonElement workflow in export.EnumerateArray())
        {
            count++;
            string label = workflow.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()!
                : workflow.TryGetProperty("id", out JsonElement id) ? id.ToString() : $"#{count}";
            List<WorkflowStep> steps = [];
            foreach (JsonElement s in workflow.GetProperty("steps").EnumerateArray())
            {
                CreateWorkflowStepRequest? request = s.Deserialize<CreateWorkflowStepRequest>(Options);
                if (request is null)
                    continue;
                Dictionary<Guid, AgentDefinition> agents = [];
                if (s.TryGetProperty("agentType", out JsonElement at) && Enum.TryParse(at.GetString(), out AgentType type))
                    agents[request.AgentDefinitionId] = new AgentDefinition { Id = request.AgentDefinitionId, AgentType = type };
                steps.AddRange(WorkflowLinter.FromRequests([request], agents));
            }

            WorkflowLintReport report = await linter.LintAsync(
                new WorkflowLintRequest(Guid.NewGuid(), Guid.NewGuid(), steps, null, null, UnknownLintFacts.Instance), ct);
            foreach (string rule in report.Findings.Select(f => f.RuleId).Distinct())
                perRule[rule] = perRule.GetValueOrDefault(rule) + 1;
            foreach (LintFinding error in report.Errors)
                errors.Add((label, error));
        }

        output.WriteLine($"Scanned {count} workflow(s).");
        foreach ((string rule, int workflows) in perRule.OrderBy(p => p.Key, StringComparer.Ordinal))
            output.WriteLine($"  {rule}: fires on {workflows} workflow(s)");
        foreach ((string workflow, LintFinding error) in errors)
            output.WriteLine($"  ERROR {error.RuleId} in \"{workflow}\" step {error.StepOrder}: {error.Message}");
        return new ScanResult(count, perRule, errors);
    }
}
