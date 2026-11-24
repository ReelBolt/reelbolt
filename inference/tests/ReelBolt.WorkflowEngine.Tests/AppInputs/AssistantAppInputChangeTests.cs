using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Tests.ChangeRequests;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Lane L9 (docs/design/app-inputs.md 25): GetChangeOptions lists an app run's fields, ProposeStepChange / ApplyStepChange
/// (MCP StepChange) take `inputs`, and the approval card names each changed field and value by label. With the switch
/// off, every schema, description and answer is as before.
/// </summary>
public sealed class AssistantAppInputChangeTests : IDisposable
{
    private sealed class Caller(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified => true;
    }

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase("app-input-change-tools-" + Guid.NewGuid().ToString("N")).Options);
    private readonly Guid _user = Guid.NewGuid();
    private readonly Project _project;
    private readonly Guid _execution = Guid.NewGuid();
    private readonly Mock<IStepChangeRequestService> _service = new();
    private readonly IConfirmationTokenService _tokens = ConfirmationTokenService.CreateEphemeral();
    private readonly AssistantExecutionGuard _guard;
    private CreateStepChangeRequest? _requested;

    public AssistantAppInputChangeTests()
    {
        _project = new Project { Id = Guid.NewGuid(), Name = "Caffè Lume", OwnerId = _user, OrganizationId = TestTenancy.OrgOf(_user), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Projects.Add(_project);
        _db.SaveChanges();
        _guard = new AssistantExecutionGuard(_db, new ConfigurationBuilder().Build());

        StepChangeAppInputsResponse app = new(Guid.NewGuid(),
        [
            new StepChangeAppInputFieldResponse("whatTheyDo", "What they do", "longText", J("\"Pours latte art\""), "Pours latte art",
                true, null, null, ["Make the spokesperson clip"], false, [], Input("whatTheyDo", "longText", "What they do")),
            new StepChangeAppInputFieldResponse("liteMode", "Lite mode (cheaper)", "toggle", J("false"), "no",
                false, "it picks another path through the app, so run it again instead", AppInputChangeCodes.ChangesBranch, [], true, [J("true")],
                Input("liteMode", "toggle", "Lite mode (cheaper)")),
        ]);
        _service.Setup(s => s.GetCapabilitiesAsync(It.IsAny<Project>(), _execution, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StepChangeCapabilitiesResponse(_execution, true, [], AppInputs: app));
        _service.Setup(s => s.EstimateAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepChangeRequestResult.Previewed(new StepChangeEstimatePreviewResponse(
                new StepChangeEstimateResponse(true, 1, 2, 3, 100, true),
                new StepChangeReplayResponse([
                    new StepChangeReplayStepResponse(1, "Lite mode?", "Reuse", "before"),
                    new StepChangeReplayStepResponse(6, "Make the spokesperson clip", "Revise", "uses the field")]),
                InputChanges: [new StepChangeInputChangeResponse("whatTheyDo", "What they do", "longText", "Pours latte art", "Waves hello", J("\"Waves hello\""))])));
        _service.Setup(s => s.RequestAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>(), It.IsAny<ExecutionInitiator>()))
            .Returns((Project _, Guid _, Guid _, Guid? _, CreateStepChangeRequest body, CancellationToken _, ExecutionInitiator _) =>
            {
                _requested = body;
                return Task.FromResult(StepChangeRequestResult.Ok(new StepChangeRequestResponse(
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), _execution, 6, null, "VideoGenerate", "Changed", [], "Queued", null, DateTime.UtcNow)));
            });
    }

    public void Dispose() => _db.Dispose();

    private static IConfiguration Config(bool on) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
    }).Build();

    private AssistantChangeRequestTools Tools(bool on)
    {
        Caller caller = new(_user);
        return new AssistantChangeRequestTools(TestTenancy.Access(_db, caller), _service.Object, _guard, _tokens, caller, trialCredits: null);
    }

    private AIFunction Tool(string name, bool on) => Tools(on).GetTools(new AssistantToolContext(_user, _project.Id)).Single(t => t.Name == name);

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args, bool on = true)
    {
        object? raw = await Tool(tool, on).InvokeAsync(new AIFunctionArguments(args));
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Dictionary<string, object?> InputArgs(string value, string? token = null)
    {
        Dictionary<string, object?> args = new()
        {
            ["executionId"] = _execution,
            ["inputs"] = new Dictionary<string, JsonElement> { ["whatTheyDo"] = J(JsonSerializer.Serialize(value)) },
        };
        if (token is not null)
            args["confirmationToken"] = token;
        return args;
    }

    [Fact]
    public void The_tools_take_the_app_fields()
    {
        Tool("ProposeStepChange", on: true).JsonSchema.GetRawText().Should().Contain("\"inputs\"");
        Tool("ApplyStepChange", on: true).Description.Should().EndWith(AssistantChangeRequestTools.InputsNote);
    }

    [Fact]
    public async Task GetChangeOptions_lists_the_app_fields_only_for_an_app_run()
    {
        JsonElement result = await CallAsync("GetChangeOptions", new() { ["executionId"] = _execution });

        JsonElement fields = result.GetProperty("appFields");
        fields[0].GetProperty("label").GetString().Should().Be("What they do");
        fields[0].GetProperty("redoes")[0].GetString().Should().Be("Make the spokesperson clip");
        fields[1].GetProperty("supported").GetBoolean().Should().BeFalse();
        fields[1].GetProperty("code").GetString().Should().Be(AppInputChangeCodes.ChangesBranch);

        _service.Setup(s => s.GetCapabilitiesAsync(It.IsAny<Project>(), _execution, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StepChangeCapabilitiesResponse(_execution, true, []));
        (await CallAsync("GetChangeOptions", new() { ["executionId"] = _execution })).TryGetProperty("appFields", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Propose_names_each_field_by_label_and_Apply_runs_exactly_the_proposed_values()
    {
        JsonElement proposal = await CallAsync("ProposeStepChange", InputArgs("Waves hello"));
        proposal.GetProperty("summary").GetString().Should().Contain("What they do: Pours latte art -> Waves hello")
            .And.Contain("Redone: Make the spokesperson clip");
        string token = proposal.GetProperty("confirmationToken").GetString()!;

        JsonElement stale = await CallAsync("ApplyStepChange", InputArgs("Something else", token));
        stale.GetProperty("error").GetString().Should().Be(AssistantChangeRequestTools.StaleTokenMessage);
        _requested.Should().BeNull();

        JsonElement applied = await CallAsync("ApplyStepChange", InputArgs("Waves hello", token));
        applied.GetProperty("status").GetString().Should().Be("Queued");
        _requested!.Inputs!["whatTheyDo"].GetString().Should().Be("Waves hello");
        _requested.StepOrder.Should().BeNull();
    }

    [Fact]
    public async Task Inputs_with_step_arguments_are_refused()
    {
        Dictionary<string, object?> args = InputArgs("Waves hello");
        args["stepOrder"] = 6;

        (await CallAsync("ProposeStepChange", args)).GetProperty("code").GetString().Should().Be("INVALID_REQUEST");
    }

    [Fact]
    public void F3_the_MCP_StepChange_description_mentions_the_app_fields()
    {
        McpToolDescriptor Projected(bool on) => AssistantMcpToolSurface.Project(
            Tools(on).GetTools(new AssistantToolContext(_user, _project.Id)), new HashSet<string>()).Single(t => t.Name == "StepChange");
        string pair = AssistantMcpToolSurface.Collapsed.Single(p => p.Name == "StepChange").Description;

        Projected(on: true).Description.Should().Be(pair + AssistantMcpToolSurface.AppInputNotes["StepChange"].Note)
            .And.Contain("inputs").And.Contain("brief");
        AssistantMcpToolSurface.AppInputNotes.Keys.Should().BeSubsetOf(AssistantMcpToolSurface.Collapsed.Select(p => p.Name));
    }

    [Fact]
    public async Task F3_fields_settings_and_steps_go_in_one_proposal_and_Apply_sends_them_together()
    {
        Dictionary<string, object?> args = InputArgs("Waves hello");
        args["brief"] = new ReelBolt.Shared.Briefs.RunBrief(Language: "it");
        args["changes"] = new[] { new StepChangeTargetRequest(6, null, "Make it brighter", null, null) };

        JsonElement proposal = await CallAsync("ProposeStepChange", args);
        string token = proposal.GetProperty("confirmationToken").GetString()!;

        // The token covers the brief and the changes: a different language is refused.
        Dictionary<string, object?> other = new(args) { ["brief"] = new ReelBolt.Shared.Briefs.RunBrief(Language: "fr"), ["confirmationToken"] = token };
        (await CallAsync("ApplyStepChange", other)).GetProperty("error").GetString().Should().Be(AssistantChangeRequestTools.StaleTokenMessage);

        args["confirmationToken"] = token;
        (await CallAsync("ApplyStepChange", args)).GetProperty("status").GetString().Should().Be("Queued");
        _requested!.Inputs!["whatTheyDo"].GetString().Should().Be("Waves hello");
        _requested.Brief!.Language.Should().Be("it");
        _requested.Targets!.Single().StepOrder.Should().Be(6);
        _requested.StepOrder.Should().BeNull();

        // Inputs alone keep the L9 token payload byte for byte.
        AssistantChangeRequestTools.PayloadInputs(_project.Id, _execution, new Dictionary<string, JsonElement> { ["a"] = J("1") })
            .Should().NotContain("brief").And.NotContain("changes");
        Tool("ProposeStepChange", on: true).JsonSchema.GetRawText().Should().Contain("\"brief\"");
    }

    [Fact]
    public async Task F3_the_card_lists_the_fields_and_the_step_changes_of_one_request()
    {
        _service.Setup(s => s.EstimateAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepChangeRequestResult.Previewed(new StepChangeEstimatePreviewResponse(
                new StepChangeEstimateResponse(true, 1, 2, 3, 100, true),
                new StepChangeReplayResponse([new StepChangeReplayStepResponse(6, "Make the spokesperson clip", "Revise", "uses the field")]),
                Targets:
                [
                    new StepChangeTargetResponse(2, "Write the script", "Agent", [], "Changed “Language”: (automatic) → it", "inputs"),
                    new StepChangeTargetResponse(6, "Make the spokesperson clip", "VideoGenerate", [], "Make it brighter", "step"),
                ],
                InputChanges: [new StepChangeInputChangeResponse("brief:language", "Language", "brief", "(automatic)", "it", null)])));
        Caller caller = new(_user);
        AssistantApprovalCardBuilder builder = new(_db, TestTenancy.Access(_db, caller), caller, stepChanges: _service.Object,
            appInputs: new AppInputValueValidator(_db));
        AIFunctionArguments args = new()
        {
            ["executionId"] = _execution.ToString(),
            ["brief"] = JsonDocument.Parse("""{"language":"it"}""").RootElement.Clone(),
            ["changes"] = JsonDocument.Parse("""[{"stepOrder":6,"instruction":"Make it brighter"}]""").RootElement.Clone(),
            ["confirmationToken"] = "t"
        };

        ApprovalVerdict verdict = await builder.BuildAsync("ApplyStepChange", args, new AssistantToolContext(_user, _project.Id), null, CancellationToken.None);

        ApprovalCard card = verdict.Card ?? throw new InvalidOperationException("no card: " + verdict.Refusal);
        card.Changes!.Select(c => c.Text).Should().Contain("“Language”: “(automatic)” → “it”")
            .And.Contain("Step “Make the spokesperson clip”: “Make it brighter”");
        card.Summary.Should().Contain("“Language”").And.Contain("the step “Make the spokesperson clip”");
    }

    [Fact]
    public async Task The_card_names_the_changed_fields_and_values_by_label()
    {
        Caller caller = new(_user);
        AssistantApprovalCardBuilder builder = new(_db, TestTenancy.Access(_db, caller), caller, stepChanges: _service.Object,
            appInputs: new AppInputValueValidator(_db));
        AIFunctionArguments args = new()
        {
            ["executionId"] = _execution.ToString(),
            ["inputs"] = JsonDocument.Parse("""{"whatTheyDo":"Waves hello"}""").RootElement.Clone(),
            ["confirmationToken"] = "t"
        };

        ApprovalVerdict verdict = await builder.BuildAsync("ApplyStepChange", args, new AssistantToolContext(_user, _project.Id), null, CancellationToken.None);

        ApprovalCard card = verdict.Card ?? throw new InvalidOperationException("no card: " + verdict.Refusal);
        card.Summary.Should().Contain("“What they do”").And.Contain("Caffè Lume");
        card.Changes!.Select(c => c.Text).Should().Contain("“What they do”: “Pours latte art” → “Waves hello”");
        card.Changes!.Should().Contain(c => c.Text.StartsWith("Redone: Make the spokesperson clip", StringComparison.Ordinal));
        card.Risk.Should().Be(AssistantActionRisk.Paid);
    }
}
