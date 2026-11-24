using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// The StepChange approval card says which film version a change with the film as a target builds on, by number and in
/// the revision card's "starts from version N" wording; never an id.
/// </summary>
public sealed class StepChangeFilmBaseCardTests : IDisposable
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

    private readonly Guid _user = Guid.NewGuid();
    private readonly InferenceApiDbContext _db =
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Project _project;
    private readonly WorkflowExecution _run;

    public StepChangeFilmBaseCardTests()
    {
        _project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = _user, OrganizationId = TestTenancy.OrgOf(_user) };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Promo film", ProjectId = _project.Id };
        _run = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = _project.Id, WorkflowDefinitionId = workflow.Id, Status = ExecutionStatus.Passed };
        _db.Projects.Add(_project);
        _db.WorkflowDefinitions.Add(workflow);
        _db.WorkflowExecutions.Add(_run);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private async Task<ApprovalCard> CardAsync(StepChangeFilmBaseResponse? filmBase)
    {
        StepChangeCapabilitiesResponse caps = new(_run.Id, true,
        [
            new StepChangeStepCapabilityResponse(3, Guid.NewGuid(), "Voiceover", "Narration", true, null, null, "line",
                [new StepChangeItemResponse("vo0", "ok", 2, "Hello there")], []),
            new StepChangeStepCapabilityResponse(5, Guid.NewGuid(), "Agent", "The film", false, "film", "composition-revision", "scene",
                [], [], Combinable: true)
        ]);
        StepChangeEstimatePreviewResponse preview = new(null,
            new StepChangeReplayResponse([new StepChangeReplayStepResponse(3, "Narration", "Revise", "target"), new StepChangeReplayStepResponse(5, "The film", "Revise", "target")]),
            null, filmBase);
        Mock<IStepChangeRequestService> service = new();
        service.Setup(s => s.GetCapabilitiesAsync(It.IsAny<Project>(), _run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(caps);
        service.Setup(s => s.EstimateAsync(It.IsAny<Project>(), _run.Id, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepChangeRequestResult.Previewed(preview));

        Caller caller = new(_user);
        AssistantApprovalCardBuilder builder = new(_db, TestTenancy.Access(_db, caller), caller, stepChanges: service.Object);
        AIFunctionArguments args = new()
        {
            ["executionId"] = _run.Id.ToString(),
            ["changes"] = JsonDocument.Parse("""[{"stepOrder":3,"itemIds":["vo0"],"instruction":"Slower"},{"stepOrder":5,"instruction":"Bigger title"}]""").RootElement.Clone(),
            ["confirmationToken"] = "t"
        };
        ApprovalVerdict verdict = await builder.BuildAsync("ApplyStepChange", args, new AssistantToolContext(_user, _project.Id), null, CancellationToken.None);
        return verdict.Card ?? throw new InvalidOperationException("no card: " + verdict.Refusal);
    }

    [Fact]
    public async Task A_film_change_on_a_newer_version_names_both_version_numbers()
    {
        ApprovalCard card = await CardAsync(new StepChangeFilmBaseResponse(2, 1, true, "x"));

        card.Changes!.Should().Contain(c => c.Text == "The video starts from version 2, the latest, not version 1 this run made");
    }

    [Fact]
    public async Task A_film_change_on_the_runs_own_version_says_so_and_no_film_base_adds_no_line()
    {
        (await CardAsync(new StepChangeFilmBaseResponse(1, 1, false, "x"))).Changes!
            .Should().Contain(c => c.Text == "The video starts from version 1, the version this run made");
        (await CardAsync(null)).Changes!.Should().NotContain(c => c.Text.Contains("starts from version"));
    }
}
