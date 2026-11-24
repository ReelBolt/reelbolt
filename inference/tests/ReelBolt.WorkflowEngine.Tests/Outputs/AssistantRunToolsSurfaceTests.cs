#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>The assistant's run readers show a run's step results, but not the outputs of an earlier run the project keeps back.</summary>
public sealed class AssistantRunToolsSurfaceTests
{
    public enum Reader { ExecutionStatus, WorkflowExecution }

    private static async Task<string> Read(SurfaceWorld w, Reader reader, Guid executionId, InferenceApiDbContext? db = null)
    {
        InferenceApiDbContext d = db ?? w.Db;
        AssistantToolContext context = new(w.UserId, w.Project.Id);
        IEnumerable<AIFunction> tools = reader == Reader.ExecutionStatus
            ? new AssistantRunTools(d, TestTenancy.Access(d, w.User), w.User, Mock.Of<ICreditBalanceReader>()).GetTools(context)
            : new AssistantWorkflowTools(d, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(d, w.User), w.User).GetTools(context);
        AIFunction function = tools.Single(t => t.Name == (reader == Reader.ExecutionStatus ? "GetExecutionStatus" : "GetWorkflowExecution"));
        object? raw = await function.InvokeAsync(new AIFunctionArguments { ["executionId"] = executionId });
        return raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } s => s.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw?.ToString() ?? string.Empty,
        };
    }

    public static IEnumerable<object[]> Readers() => [[Reader.ExecutionStatus], [Reader.WorkflowExecution]];

    public static IEnumerable<object[]> ServedStates()
    {
        foreach (Reader r in Enum.GetValues<Reader>())
        {
            yield return [r, SurfaceState.Clear];
            yield return [r, SurfaceState.ModerationOnly];
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task The_outputs_of_an_earlier_run_are_not_offered(Reader reader)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        string json = await Read(w, reader, w.EarlierRun.Id);
        json.Should().NotContain("result text").And.NotContain(w.EarlierResult.Id.ToString());
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task The_outputs_of_a_later_run_are_offered(Reader reader)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        string json = await Read(w, reader, w.LaterRun.Id);
        json.Should().Contain(w.LaterResult.Id.ToString());
    }

    [Theory]
    [MemberData(nameof(ServedStates))]
    public async Task The_outputs_of_the_earlier_run_are_offered_when_the_project_keeps_nothing(Reader reader, SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        string json = await Read(w, reader, w.EarlierRun.Id);
        json.Should().Contain(w.EarlierResult.Id.ToString());
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task A_failed_read_offers_no_output(Reader reader)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        string json = await Read(w, reader, w.LaterRun.Id, failing);
        json.Should().NotContain(w.LaterResult.Id.ToString());
    }
}
