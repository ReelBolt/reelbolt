#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>The assistant's output tools (look, describe, save) answer the neutral unavailable text for an output the project keeps back, and not the not-found text.</summary>
public sealed class AssistantOutputToolsSurfaceTests
{
    public enum Which { EarlierRender, LaterRender, EarlierFile, LaterFile, Upload }

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public InMemoryFileStorage Storage { get; } = new();

        public Rig()
        {
            foreach (string key in new[] { W.EarlierResult.OutputStorageKey!, W.LaterResult.OutputStorageKey!, W.EarlierFile.StorageKey, W.LaterFile.StorageKey, W.Upload.StorageKey })
                Storage.Objects[key] = [1, 2, 3, 4];
        }

        public Guid IdOf(Which w) => w switch
        {
            Which.EarlierRender => W.EarlierResult.Id,
            Which.LaterRender => W.LaterResult.Id,
            Which.EarlierFile => W.EarlierFile.Id,
            Which.LaterFile => W.LaterFile.Id,
            _ => W.Upload.Id,
        };

        public async Task<string> Call(string tool, Which which, InferenceApiDbContext? db = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            DateTimeOffset now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
            IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
            OutputLinkService links = new(tokens, new ConfigurationBuilder().Build(), new FixedClock(now));
            IPublishEndpoint publish = Mock.Of<IPublishEndpoint>();
            AssistantOutputTools tools = new(
                d, tokens, TestTenancy.Access(d, W.User), new OutputDescriber(d, Storage, links, publish), Storage,
                Mock.Of<IStorageObjectCopier>(), publish, new RecordingSummarizationQueue(), Mock.Of<ReelBolt.Inference.Api.Services.Billing.IPlanGateService>());
            AssistantToolContext context = new(W.UserId, W.Project.Id);
            AIFunction function = tool == "LookAtOutput"
                ? tools.GetLookAtOutputTool(context)
                : tools.GetTools(context).Single(t => t.Name == tool);
            AIFunctionArguments args = new() { ["outputId"] = IdOf(which) };
            object? raw = await function.InvokeAsync(args);
            return raw switch
            {
                JsonElement { ValueKind: JsonValueKind.String } s => s.GetString()!,
                JsonElement e => e.GetRawText(),
                _ => raw?.ToString() ?? string.Empty,
            };
        }
    }

    public static IEnumerable<object[]> Tools() => [["GetOutput"], ["LookAtOutput"], ["ProposeSaveOutputAsFile"]];

    public static IEnumerable<object[]> WithheldCases()
    {
        foreach (object[] tool in Tools())
            foreach (Which w in new[] { Which.EarlierRender, Which.EarlierFile })
                yield return [tool[0], w];
    }

    [Theory]
    [MemberData(nameof(WithheldCases))]
    public async Task An_earlier_output_answers_the_neutral_unavailable_text(string tool, Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Call(tool, which)).Should().Be(OutputGate.ToolJson());
    }

    [Theory]
    [InlineData("GetOutput", Which.LaterRender)]
    [InlineData("GetOutput", Which.LaterFile)]
    [InlineData("GetOutput", Which.Upload)]
    [InlineData("LookAtOutput", Which.LaterRender)]
    [InlineData("LookAtOutput", Which.Upload)]
    public async Task A_later_output_and_an_upload_are_not_refused_while_earlier_outputs_are_kept(string tool, Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Call(tool, which)).Should().NotContain("OUTPUT_UNAVAILABLE");
    }

    [Theory]
    [InlineData("GetOutput", Which.EarlierRender, SurfaceState.Clear)]
    [InlineData("GetOutput", Which.EarlierRender, SurfaceState.ModerationOnly)]
    [InlineData("GetOutput", Which.EarlierFile, SurfaceState.Clear)]
    [InlineData("GetOutput", Which.EarlierFile, SurfaceState.ModerationOnly)]
    [InlineData("LookAtOutput", Which.EarlierRender, SurfaceState.ModerationOnly)]
    public async Task The_earlier_output_is_not_refused_when_the_project_keeps_nothing(string tool, Which which, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        (await r.Call(tool, which)).Should().NotContain("OUTPUT_UNAVAILABLE");
    }

    [Theory]
    [InlineData("GetOutput")]
    [InlineData("LookAtOutput")]
    public async Task A_failed_read_answers_the_neutral_unavailable_text(string tool)
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        (await r.Call(tool, Which.LaterRender, failing)).Should().Be(OutputGate.ToolJson());
    }
}
