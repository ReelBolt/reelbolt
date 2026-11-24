using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Ties the MCP package's recorded tool list to the C# MCP surface.
/// <c>mcp/test-fixtures/assistant-tool-names.json</c> holds the names the MCP catalog publishes
/// (the collapsed Propose/Apply surface, not the 115-tool chat registry); this test fails when the
/// two differ, and <c>mcp/src/bridge.test.ts</c> compares its own hard-coded catalog to the same
/// file, so adding or removing a tool breaks one side or the other until both are updated.
/// Regenerate the file with <c>UPDATE_MCP_TOOL_FIXTURE=1 dotnet test --filter AssistantMcpToolFixtureTests</c>.
/// </summary>
public class AssistantMcpToolFixtureTests
{
    private static string FixturePath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "mcp", "package.json")))
                return Path.Combine(dir.FullName, "mcp", "test-fixtures", "assistant-tool-names.json");
        }

        throw new InvalidOperationException("The repository root (the folder holding mcp/package.json) was not found above the test binaries.");
    }

    [Fact]
    public void The_mcp_fixture_lists_exactly_the_collapsed_mcp_catalog()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid user = Guid.NewGuid();
        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> registered = AssistantToolTestFactory
            .CreateProvider(db, new FakeCurrentUser(user))
            .GetTools(new AssistantToolContext(user));
        string[] catalog = AssistantMcpToolSurface
            .Project(registered, new HashSet<string>(StringComparer.Ordinal))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string path = FixturePath();

        if (Environment.GetEnvironmentVariable("UPDATE_MCP_TOOL_FIXTURE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        File.Exists(path).Should().BeTrue("run with UPDATE_MCP_TOOL_FIXTURE=1 to create {0}", path);
        string[] recorded = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))!;
        recorded.Should().Equal(catalog,
            "a tool was added or removed: regenerate with UPDATE_MCP_TOOL_FIXTURE=1, then update the catalog in mcp/src/bridge.test.ts");
    }
}
