using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>The scan utility works on an export file, and runs on the maintainers' own export when one is named.</summary>
public class WorkflowLintScanTests(ITestOutputHelper output)
{
    private const string Fixture = """
        [
          { "id": "a", "name": "Broken loop", "steps": [
              { "agentDefinitionId": "00000000-0000-0000-0000-000000000001", "stepOrder": 1, "stepType": "Agent", "agentType": "AuthorAgent" },
              { "agentDefinitionId": "00000000-0000-0000-0000-000000000002", "stepOrder": 2, "stepType": "ReviewLoop", "loopTargetStepOrder": 7, "agentType": "ReviewAgent" } ] },
          { "id": "b", "name": "Fine", "steps": [
              { "agentDefinitionId": "00000000-0000-0000-0000-000000000001", "stepOrder": 1, "stepType": "Agent", "agentType": "AuthorAgent" },
              { "agentDefinitionId": "00000000-0000-0000-0000-000000000002", "stepOrder": 2, "stepType": "ReviewLoop", "loopTargetStepOrder": 1, "agentType": "ReviewAgent" } ] }
        ]
        """;

    [Fact]
    public async Task The_scan_counts_rules_per_workflow_and_lists_the_errors()
    {
        using JsonDocument doc = JsonDocument.Parse(Fixture);
        using StringWriter writer = new();

        WorkflowLintScan.ScanResult result = await WorkflowLintScan.RunAsync(doc.RootElement, writer);

        result.Workflows.Should().Be(2);
        result.WorkflowsPerRule["WL003"].Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Workflow.Should().Be("Broken loop");
        writer.ToString().Should().Contain("Scanned 2 workflow(s).").And.Contain("WL003: fires on 1 workflow(s)");
    }

    [Fact]
    public async Task The_scan_reads_a_file_path_and_runs_on_the_operators_export_when_one_is_named()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lint-scan-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, Fixture);
        try
        {
            using StringWriter writer = new();
            (await WorkflowLintScan.RunAsync(path, writer)).Workflows.Should().Be(2);
        }
        finally
        {
            File.Delete(path);
        }

        string? export = Environment.GetEnvironmentVariable(WorkflowLintScan.ExportVariable);
        if (string.IsNullOrWhiteSpace(export))
            return; // no export named: nothing more to scan (this test never connects to anything)

        using StringWriter real = new();
        WorkflowLintScan.ScanResult result = await WorkflowLintScan.RunAsync(export, real);
        output.WriteLine(real.ToString());
        result.Workflows.Should().BeGreaterThan(0);
    }
}
