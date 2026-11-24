#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.PublicApi.Runs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Who may give the permission to send media for a run (docs/design/app-inputs.md "Permission to send media as an app
/// field", decision 6): the app session, a key with <c>media.egress</c>, MCP when the user allowed it, and a chat card the
/// person approved. Every other caller is refused, nothing is started, and no caller ever writes the saved step.
/// </summary>
public class MediaEgressRunStartTests
{
    private const string SegmentOff =
        """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"the plate","method":"Segmentation"}],"allowSourceMediaEgress":false}""";

    private static InferenceApiDbContext NewDb() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RunBrief Granting() => new(Inputs: Values((MediaEgressPermission.InputId, "true")));

    private static ClaimsPrincipal Session(Guid user, params Claim[] extra) =>
        new(new ClaimsIdentity([new Claim("sub", user.ToString()), .. extra], "test"));

    private static async Task<(Project Project, WorkflowDefinition Workflow, Guid Owner)> SeedAsync(InferenceApiDbContext db)
    {
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "VideoTransform", Description = "d", SystemPrompt = "p", AgentType = AgentType.VideoTransform, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Blur the plate",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps = [new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, AgentDefinitionId = agent.Id, Label = "Find the plate", VideoAnalyzeConfigJson = SegmentOff }]
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        db.ApplicationUsers.Add(new ApplicationUser { Id = owner, Email = "owner@example.com", DisplayName = "Owner" });
        await db.SaveChangesAsync();
        return (project, workflow, owner);
    }

    private static WorkflowsController Dashboard(InferenceApiDbContext db, Guid owner, ClaimsPrincipal? principal)
    {
        FakeCurrentUser user = new(owner);
        WorkflowsController controller = new(
            db, user, TestTenancy.Access(db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
        if (principal is not null)
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private static IConfiguration RunLimit() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxExecutionsPerHour"] = "10" }).Build();

    private static JsonElement BodyOf(ActionResult<WorkflowExecutionResponse> result, int status)
    {
        ObjectResult answer = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        answer.StatusCode.Should().Be(status);
        return JsonDocument.Parse(JsonSerializer.Serialize(answer.Value)).RootElement.Clone();
    }

    private static async Task AssertNothingStartedAndNothingSavedAsync(InferenceApiDbContext db, Guid workflowId)
    {
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        await AssertSavedStepUntouchedAsync(db, workflowId);
    }

    private static async Task AssertSavedStepUntouchedAsync(InferenceApiDbContext db, Guid workflowId) =>
        (await db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == workflowId)).VideoAnalyzeConfigJson
            .Should().Be(SegmentOff, "a run never writes the workflow's saved step");

    private static async Task<StoredRunBrief> StartedBriefAsync(InferenceApiDbContext db)
    {
        WorkflowExecution run = await db.WorkflowExecutions.AsNoTracking().SingleAsync();
        return RunBriefJson.TryParse(run.RunBriefJson)!;
    }

    // ---------------------------------------------------------------- a) the app session

    [Fact]
    public async Task The_app_session_gives_it_for_the_run_and_the_run_records_who_did()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);

        ActionResult<WorkflowExecutionResponse> result = await Dashboard(db, owner, Session(owner)).Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(Brief: Granting()), CancellationToken.None);

        BodyOf(result, 201);
        StoredRunBrief stored = await StartedBriefAsync(db);
        MediaEgressPermission.IsRequested(stored.Inputs).Should().BeTrue();
        stored.AllowMediaEgressBy.Should().Be(MediaEgressPermission.ByApp);
        await AssertSavedStepUntouchedAsync(db, workflow.Id);
    }

    [Fact]
    public async Task A_run_that_needs_it_is_refused_with_the_field_named_before_anything_is_written()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);

        JsonElement body = BodyOf(await Dashboard(db, owner, Session(owner)).Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(), CancellationToken.None), 400);

        body.GetProperty("code").GetString().Should().Be("APP_INPUT_MISSING");
        body.GetProperty("inputId").GetString().Should().Be("allowMediaEgress");
        body.GetProperty("message").GetString().Should().Be("Allow sending your media to AI services to run this app.");
        await AssertNothingStartedAndNothingSavedAsync(db, workflow.Id);
    }

    public static TheoryData<string> NotTheApp => new() { "none", "apiKey", "scoped", "engine" };

    [Theory]
    [MemberData(nameof(NotTheApp))]
    public async Task Any_other_caller_of_the_dashboard_route_is_refused_fail_closed(string kind)
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);
        ClaimsPrincipal? principal = kind switch
        {
            "apiKey" => Session(owner, new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString())),
            "scoped" => Session(owner, new Claim(AuthClaims.Scope, "assistant.tools")),
            "engine" => Session(owner, new Claim(OnBehalfOfTokenIssuer.ActorClaim, "workflow-engine")),
            _ => null
        };

        JsonElement body = BodyOf(await Dashboard(db, owner, principal).Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(Brief: Granting()), CancellationToken.None), 400);

        body.GetProperty("code").GetString().Should().Be("APP_INPUT_INVALID");
        body.GetProperty("inputId").GetString().Should().Be("allowMediaEgress");
        body.GetProperty("message").GetString().Should().StartWith("This permission can only be given in the app.");
        await AssertNothingStartedAndNothingSavedAsync(db, workflow.Id);
    }

    [Fact]
    public async Task The_workflow_assistant_route_has_no_card_so_it_cannot_give_it()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);
        FakeCurrentUser user = new(owner);
        WorkflowAssistantController controller = new(
            db, user, TestTenancy.Access(db, user), new WorkflowEditDiffService(db), new AssistantExecutionGuard(db, RunLimit()),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Session(owner) } };

        JsonElement body = BodyOf(await controller.Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(Brief: Granting()), CancellationToken.None), 400);

        body.GetProperty("code").GetString().Should().Be("APP_INPUT_INVALID");
        await AssertNothingStartedAndNothingSavedAsync(db, workflow.Id);
    }

    // ---------------------------------------------------------------- c) MCP and d) chat: the run tool

    private static async Task<JsonElement> ExecuteToolAsync(InferenceApiDbContext db, Guid owner, AssistantToolContext context, Guid workflowId, bool grant = true)
    {
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(
            db, new FakeCurrentUser(owner),
            executionGuard: new AssistantExecutionGuard(db, RunLimit()),
            executionService: new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            appInputs: new AppInputValueValidator(db));
        AIFunctionArguments args = new() { ["workflowId"] = workflowId, ["regenerateVideoClips"] = false };
        if (grant)
            args["brief"] = J("""{"inputs":{"allowMediaEgress":true}}""");
        object? result = await provider.GetTools(context).Single(t => t.Name == "ExecuteWorkflow").InvokeAsync(args);
        string text = result switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => JsonSerializer.Serialize(result)
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task SetMcpSettingAsync(InferenceApiDbContext db, Guid owner, bool enabled)
    {
        ApplicationUser user = await db.ApplicationUsers.SingleAsync(u => u.Id == owner);
        user.McpMediaEgressEnabled = enabled;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Over_mcp_it_is_taken_only_while_the_user_has_allowed_connected_tools_read_on_every_call()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);
        AssistantToolContext mcp = new(owner, project.Id, Initiator: ExecutionInitiator.Mcp);

        // Off by default: the plain refusal, and nothing starts.
        JsonElement refused = await ExecuteToolAsync(db, owner, mcp, workflow.Id);
        refused.GetProperty("error").GetString().Should().Be("media_egress_not_allowed");
        refused.GetProperty("inputId").GetString().Should().Be("allowMediaEgress");
        refused.GetProperty("message").GetString().Should().Be(
            "Allow connected tools to send your media in Settings > Assistant, or run this from the app page.");
        await AssertNothingStartedAndNothingSavedAsync(db, workflow.Id);

        // Switched on in the app: the same call starts the run, records who allowed it and says what may be sent.
        await SetMcpSettingAsync(db, owner, true);
        JsonElement started = await ExecuteToolAsync(db, owner, mcp, workflow.Id);
        started.TryGetProperty("error", out _).Should().BeFalse(started.GetRawText());
        JsonElement sent = started.GetProperty("mediaEgress");
        sent.GetProperty("allowed").GetBoolean().Should().BeTrue();
        sent.GetProperty("steps")[0].GetProperty("stepName").GetString().Should().Be("Find the plate");
        sent.GetProperty("steps")[0].GetProperty("sends").GetString().Should().Be("tracks an object by name");
        (await StartedBriefAsync(db)).AllowMediaEgressBy.Should().Be(MediaEgressPermission.ByMcp);
        await AssertSavedStepUntouchedAsync(db, workflow.Id);

        // Switched off again: the very next call is refused (the setting is read live, never remembered).
        await SetMcpSettingAsync(db, owner, false);
        (await ExecuteToolAsync(db, owner, mcp, workflow.Id)).GetProperty("error").GetString().Should().Be("media_egress_not_allowed");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Over_mcp_the_setting_alone_never_gives_it_the_run_must_ask()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);
        await SetMcpSettingAsync(db, owner, true);

        // The setting is on but this run does not set the value: it still needs the permission, so it is refused.
        JsonElement result = await ExecuteToolAsync(db, owner, new AssistantToolContext(owner, project.Id, Initiator: ExecutionInitiator.Mcp), workflow.Id, grant: false);

        result.GetProperty("code").GetString().Should().Be("APP_INPUT_MISSING");
        await AssertNothingStartedAndNothingSavedAsync(db, workflow.Id);
    }

    [Fact]
    public async Task In_chat_it_is_taken_only_under_a_card_the_person_approved_for_that_call()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);
        AssistantToolContext chat = new(owner, project.Id, ChatApproved: true);

        // No approved card around the call (auto mode, a generic card, any other path): refused, nothing starts.
        JsonElement refused = await ExecuteToolAsync(db, owner, chat, workflow.Id);
        refused.GetProperty("error").GetString().Should().Be("media_egress_not_allowed");
        await AssertNothingStartedAndNothingSavedAsync(db, workflow.Id);

        // The MCP setting does not help a chat call, and a card does not help a call that is not the chat's.
        await SetMcpSettingAsync(db, owner, true);
        (await ExecuteToolAsync(db, owner, chat, workflow.Id)).GetProperty("error").GetString().Should().Be("media_egress_not_allowed");
        using (AssistantMediaEgressApproval.Enter())
        {
            (await ExecuteToolAsync(db, owner, new AssistantToolContext(owner, project.Id), workflow.Id))
                .GetProperty("error").GetString().Should().Be("media_egress_not_allowed");
        }

        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);

        // Under the approved card: the run starts and records the card as the consent.
        JsonElement started;
        using (AssistantMediaEgressApproval.Enter())
            started = await ExecuteToolAsync(db, owner, chat, workflow.Id);
        started.TryGetProperty("error", out _).Should().BeFalse(started.GetRawText());
        started.GetProperty("mediaEgress").GetProperty("allowed").GetBoolean().Should().BeTrue();
        (await StartedBriefAsync(db)).AllowMediaEgressBy.Should().Be(MediaEgressPermission.ByAssistantCard);
        AssistantMediaEgressApproval.IsApproved.Should().BeFalse("the mark ends with the call it was given to");
        await AssertSavedStepUntouchedAsync(db, workflow.Id);
    }

    // ---------------------------------------------------------------- b) the public API key

    private static PublicRunStartService PublicStart(InferenceApiDbContext db, Guid owner)
    {
        FakeCurrentUser user = new(owner);
        return new PublicRunStartService(
            db, user, TestTenancy.Access(db, user),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            new AssistantExecutionGuard(db, RunLimit()));
    }

    private static async Task<Guid> AddKeyAsync(InferenceApiDbContext db, Guid owner, params string[] scopes)
    {
        ApiKey key = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedByUserId = owner,
            ServiceUserId = owner,
            Name = "Nightly builds",
            KeyId = Guid.NewGuid().ToString("N")[..12],
            SecretHash = "h",
            LastFour = "abcd",
            Scopes = scopes
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();
        return key.Id;
    }

    [Fact]
    public async Task A_key_without_the_media_egress_scope_gets_the_scope_refusal_and_starts_nothing()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);
        Guid keyId = await AddKeyAsync(db, owner, ApiScopes.RunsExecute);

        RunStartOutcome outcome = await PublicStart(db, owner).StartAsync(
            project.Id, workflow.Id, new PublicStartRunRequest(Brief: Granting()), keyId, null, null, CancellationToken.None);

        ObjectResult refusal = outcome.Refusal.Should().BeAssignableTo<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(403);
        JsonElement error = JsonDocument.Parse(JsonSerializer.Serialize(refusal.Value)).RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("insufficient_scope");
        error.GetProperty("message").GetString().Should().Be(
            "This key may not allow sending media. Add the media.egress permission to the key in the app.");
        error.GetProperty("requiredScopes")[0].GetString().Should().Be("media.egress");
        await AssertNothingStartedAndNothingSavedAsync(db, workflow.Id);

        // A key that does not exist holds no scope either (fail closed).
        (await PublicStart(db, owner).StartAsync(project.Id, workflow.Id, new PublicStartRunRequest(Brief: Granting()), Guid.NewGuid(), null, null, CancellationToken.None))
            .Refusal.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task A_key_with_the_scope_allows_it_per_run_and_only_when_the_run_asks()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db);
        Guid keyId = await AddKeyAsync(db, owner, ApiScopes.RunsExecute, ApiScopes.MediaEgress);

        // The scope is the ability, not the consent: a run that does not set the value is still refused.
        PublicRunStartService silent = PublicStart(db, owner);
        RunStartOutcome missing = await silent.StartAsync(project.Id, workflow.Id, new PublicStartRunRequest(), keyId, null, null, CancellationToken.None);
        missing.Refusal.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
        silent.MediaEgressSent.Should().BeNull();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);

        PublicRunStartService service = PublicStart(db, owner);
        RunStartOutcome outcome = await service.StartAsync(
            project.Id, workflow.Id, new PublicStartRunRequest(Brief: Granting()), keyId, null, null, CancellationToken.None);

        outcome.Refusal.Should().BeNull();
        outcome.Started.Should().NotBeNull();
        MediaEgressPermission.Sent(service.MediaEgressSent!).Should().ContainSingle().Which.StepName.Should().Be("Find the plate");
        (await StartedBriefAsync(db)).AllowMediaEgressBy.Should().Be(MediaEgressPermission.ByApiKey);
        await AssertSavedStepUntouchedAsync(db, workflow.Id);
    }
}
