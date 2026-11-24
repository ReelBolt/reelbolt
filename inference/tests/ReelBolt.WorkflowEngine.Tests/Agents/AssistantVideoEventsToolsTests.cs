#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Tests.Outputs;
using ReelBolt.WorkflowEngine.Tests.Video.Events;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// <c>ListVideoEvents</c> and <c>DescribeVideoWindow</c> (progressive context): free and read-only, the project is
/// authorized on every call (another tenant's project is absent), at most six reads per turn, the answer has ids and
/// words but no time, and the MCP projection keeps both as plain read-only tools.
/// </summary>
public class AssistantVideoEventsToolsTests
{
    private static (OutputsWorld World, AssistantVideoEventsTools Tools) Rig(bool withAnalysis = true)
    {
        OutputsWorld w = new();
        if (withAnalysis)
        {
            WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = w.Step.WorkflowDefinitionId, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze };
            string key = $"projects/{w.Project.Id}/agentFiles/video-analysis/{w.Execution.Id}/step-1-analysis.json";
            w.Db.WorkflowSteps.Add(step);
            w.Db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = w.Execution.Id,
                WorkflowStepId = step.Id,
                Status = StepStatus.Completed,
                ArtifactStorageKey = key,
                CompletedAt = OutputsWorld.Now.UtcDateTime,
            });
            w.Db.SaveChanges();
            w.Storage.Objects[key] = Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(VideoEventOutlineTests.Artifact(300), VideoEventReader.ArtifactJsonOptions));
        }

        var access = TestTenancy.Access(w.Db, new OutputsUser(w.UserId, w.OrgId));
        return (w, new AssistantVideoEventsTools(w.Db, access, w.Storage, NullLogger<AssistantVideoEventsTools>.Instance));
    }

    private static async Task<(JsonElement Json, string Raw)> Call(IReadOnlyList<AIFunction> tools, string name, params (string Key, object? Value)[] args)
    {
        AIFunctionArguments arguments = new();
        foreach ((string k, object? v) in args) arguments[k] = v;
        object? raw = await tools.Single(t => t.Name == name).InvokeAsync(arguments);
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
        return (JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    [Fact]
    public async Task It_lists_the_strongest_events_as_words_and_ids_with_no_time()
    {
        (OutputsWorld w, AssistantVideoEventsTools tools) = Rig();
        using (w)
        {
            (JsonElement json, string raw) = await Call(tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)), "ListVideoEvents", ("limit", 10), ("types", "hit"));

            json.GetProperty("shown").GetInt32().Should().BeInRange(1, 10);
            json.GetProperty("events").EnumerateArray().Should().OnlyContain(e => e.GetProperty("type").GetString() == "hit");
            raw.Should().NotContainAny("\"frame", "\"sec", "\"time", "storage", "projects/", w.Execution.Id.ToString());
        }
    }

    [Fact]
    public async Task It_describes_a_window_between_two_events()
    {
        (OutputsWorld w, AssistantVideoEventsTools tools) = Rig();
        using (w)
        {
            (JsonElement json, _) = await Call(tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)), "DescribeVideoWindow", ("fromEventId", "e2"), ("toEventId", "e12"));

            json.GetProperty("eventCount").GetInt32().Should().Be(11);
            json.GetProperty("motion").GetArrayLength().Should().Be(8);
        }
    }

    [Fact]
    public async Task Another_tenants_project_is_absent_not_read()
    {
        (OutputsWorld w, AssistantVideoEventsTools tools) = Rig();
        using (w)
        {
            (Project theirs, _) = w.AddForeignProject();
            (JsonElement json, string raw) = await Call(
                tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)), "ListVideoEvents", ("projectId", theirs.Id));

            json.TryGetProperty("events", out _).Should().BeFalse();
            json.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
            raw.Should().NotContain("Someone else's");
        }
    }

    [Fact]
    public async Task A_project_with_no_analysis_with_events_says_so_plainly()
    {
        (OutputsWorld w, AssistantVideoEventsTools tools) = Rig(withAnalysis: false);
        using (w)
        {
            (JsonElement json, _) = await Call(tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)), "ListVideoEvents");
            json.GetProperty("error").GetString().Should().Be(AssistantVideoEventsTools.NoEventsMessage);
        }
    }

    [Fact]
    public async Task The_seventh_read_in_one_turn_is_refused_and_a_new_turn_starts_fresh()
    {
        (OutputsWorld w, AssistantVideoEventsTools tools) = Rig();
        using (w)
        {
            IReadOnlyList<AIFunction> turn = tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id));
            for (int i = 0; i < 6; i++)
                (await Call(turn, "ListVideoEvents")).Json.TryGetProperty("events", out _).Should().BeTrue();

            (await Call(turn, "ListVideoEvents")).Json.GetProperty("error").GetString().Should().Be("read limit reached");
            (await Call(turn, "DescribeVideoWindow", ("fromEventId", "e0"), ("toEventId", "e3"))).Json.GetProperty("error").GetString().Should().Be("read limit reached");

            (await Call(tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)), "ListVideoEvents")).Json.TryGetProperty("events", out _).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("ListVideoEvents", "List the moments found in a video")]
    [InlineData("DescribeVideoWindow", "Describe the stretch between two moments")]
    public void Both_tools_are_free_read_only_closed_world_and_not_gated(string name, string title)
    {
        AssistantToolAnnotation annotation = AssistantToolAnnotations.For(name);

        annotation.ReadOnlyHint.Should().BeTrue();
        annotation.DestructiveHint.Should().BeFalse();
        annotation.OpenWorldHint.Should().BeFalse();
        annotation.Title.Should().Be(title);
        AssistantApprovalCatalog.IsGated(name).Should().BeFalse();
        AssistantPlatformTools.Classify(name)!.Kind.Should().Be(AssistantToolCostKind.Free);
        AssistantToolProgress.CaptionFor(name).Should().Be(AssistantVideoEventsTools.Captions[name]);
    }

    [Fact]
    public void The_mcp_catalog_publishes_both_under_their_own_names_as_read_only_tools()
    {
        (OutputsWorld w, AssistantVideoEventsTools tools) = Rig(withAnalysis: false);
        using (w)
        {
            IReadOnlyList<McpToolDescriptor> catalog = AssistantMcpToolSurface.Project(
                tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)), new HashSet<string>(StringComparer.Ordinal));

            catalog.Select(t => t.Name).Should().Equal("ListVideoEvents", "DescribeVideoWindow");
            catalog.Should().OnlyContain(t => t.Annotation.ReadOnlyHint && !t.Annotation.DestructiveHint && t.Annotation.Title.Length > 0);
        }
    }
}
