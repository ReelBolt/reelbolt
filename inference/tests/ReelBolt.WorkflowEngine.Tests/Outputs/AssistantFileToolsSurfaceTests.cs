#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>The assistant's file readers answer the neutral unavailable text for an output file that is not served, and read the rest.</summary>
public sealed class AssistantFileToolsSurfaceTests
{
    public enum Which { EarlierOutput, LaterOutput, Upload }

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public FakeFileStorage Storage { get; } = new();
        public Dictionary<Which, ProjectFile> Files { get; } = [];

        public Rig()
        {
            Add(Which.EarlierOutput, "outputFiles", "earlier.md", SurfaceWorld.Marker.AddHours(-2), true);
            Add(Which.LaterOutput, "outputFiles", "later.md", SurfaceWorld.Marker.AddDays(2), true);
            Add(Which.Upload, "userFiles", "mine.md", SurfaceWorld.Marker.AddHours(-5), false);
            W.Db.SaveChanges();
        }

        private void Add(Which which, string category, string name, DateTime at, bool made)
        {
            string key = $"projects/{W.Project.Id}/{category}/{name}";
            Storage.Objects[key] = Encoding.UTF8.GetBytes("# Title\nsecret body\n");
            ProjectFile f = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = W.Project.Id,
                OriginalFileName = name,
                OriginalPath = name,
                Category = category,
                StorageKey = key,
                StorageBucket = "bucket",
                MimeType = "text/markdown",
                SizeBytes = 20,
                UploadedAt = at,
                OutputProducedAt = made ? at : null,
            };
            Files[which] = f;
            W.Db.ProjectFiles.Add(f);
        }

        public async Task<string> Call(string tool, Which which, InferenceApiDbContext? db = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            AssistantFileTools tools = new(
                d, ConfirmationTokenService.CreateEphemeral(), Storage, Mock.Of<IPublishEndpoint>(),
                new RecordingSummarizationQueue(), TestTenancy.Access(d, W.User));
            AIFunction function = tools.GetTools(new AssistantToolContext(W.UserId, W.Project.Id)).Single(t => t.Name == tool);
            AIFunctionArguments args = new() { ["fileId"] = Files[which].Id };
            object? raw = await function.InvokeAsync(args);
            return raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw?.ToString() ?? string.Empty;
        }
    }

    /// <summary>What each reader's answer carries when it read the file. The outline of a heading-less result is not relied on: its heading scan has a time limit.</summary>
    private static string ReadMarker(string tool) => tool == "GetProjectFileOutline" ? "\"kind\":\"markdown\"" : "Title";

    public static IEnumerable<object[]> Tools() => [["ReadProjectFile"], ["ReadProjectFileLines"], ["GetProjectFileOutline"]];

    public static IEnumerable<object[]> ServedCases()
    {
        foreach (object[] tool in Tools())
        {
            yield return [tool[0], SurfaceState.Clear];
            yield return [tool[0], SurfaceState.ModerationOnly];
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task An_earlier_output_file_answers_the_neutral_text_and_no_content(string tool)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        string answer = await r.Call(tool, Which.EarlierOutput);
        answer.Should().Be(OutputGate.ToolJson());
        answer.Should().NotContain("secret body");
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task A_later_output_file_and_an_upload_are_read_while_earlier_outputs_are_kept(string tool)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Call(tool, Which.LaterOutput)).Should().Contain(ReadMarker(tool));
        (await r.Call(tool, Which.Upload)).Should().Contain(ReadMarker(tool));
    }

    [Theory]
    [MemberData(nameof(ServedCases))]
    public async Task The_earlier_output_file_is_read_when_the_project_keeps_nothing(string tool, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        (await r.Call(tool, Which.EarlierOutput)).Should().Contain(ReadMarker(tool));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task A_failed_read_answers_the_neutral_text(string tool)
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        (await r.Call(tool, Which.LaterOutput, failing)).Should().Be(OutputGate.ToolJson());
    }
}
