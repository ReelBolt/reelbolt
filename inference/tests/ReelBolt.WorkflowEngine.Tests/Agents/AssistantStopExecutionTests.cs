using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>ProposeStopExecution / ApplyStopExecution: the assistant and MCP stop a run through the REST stop's own event.</summary>
public sealed class AssistantStopExecutionTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IPublishEndpoint> _bus = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Project _project;
    private readonly WorkflowExecution _run;

    public AssistantStopExecutionTests()
    {
        _project = NewProject(_owner, "Spring Campaign");
        _run = NewRun(_project, "Product teaser", ExecutionStatus.Running);
    }

    public void Dispose() => _db.Dispose();

    private Project NewProject(Guid owner, string name)
    {
        var p = new Project { Id = Guid.NewGuid(), Name = name, OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Projects.Add(p);
        _db.SaveChanges();
        return p;
    }

    private WorkflowExecution NewRun(Project project, string workflowName, ExecutionStatus status)
    {
        var wf = new WorkflowDefinition { Id = Guid.NewGuid(), Name = workflowName, ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var run = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = wf.Id, ProjectId = project.Id, OrganizationId = project.OrganizationId, Status = status };
        _db.WorkflowDefinitions.Add(wf);
        _db.WorkflowExecutions.Add(run);
        _db.SaveChanges();
        return run;
    }

    private AssistantToolProvider Provider(Guid user) =>
        AssistantToolTestFactory.CreateProvider(_db, new FakeCurrentUser(user), publishEndpoint: _bus.Object);

    private static async Task<JsonElement> Call(AssistantToolProvider provider, AssistantToolContext ctx, string name, params (string, object?)[] args)
    {
        AIFunction tool = provider.GetTools(ctx).Single(t => t.Name == name);
        var a = new AIFunctionArguments();
        foreach ((string k, object? v) in args) a[k] = v;
        object? raw = await tool.InvokeAsync(a);
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Propose_then_apply_publishes_the_same_stop_event_as_the_rest_stop()
    {
        var ctx = new AssistantToolContext(_owner, _project.Id);
        AssistantToolProvider provider = Provider(_owner);

        JsonElement proposal = await Call(provider, ctx, "ProposeStopExecution", ("executionId", _run.Id));
        proposal.GetProperty("workflowName").GetString().Should().Be("Product teaser");
        string token = proposal.GetProperty("confirmationToken").GetString()!;

        JsonElement applied = await Call(provider, ctx, "ApplyStopExecution", ("executionId", _run.Id), ("confirmationToken", token));

        applied.GetProperty("stopRequested").GetBoolean().Should().BeTrue();
        _bus.Verify(b => b.Publish(It.Is<WorkflowExecutionStopRequested>(m => m.ExecutionId == _run.Id && m.RequestedByUserId == _owner), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_tampered_or_foreign_token_is_refused_and_nothing_is_published()
    {
        var ctx = new AssistantToolContext(_owner, _project.Id);
        AssistantToolProvider provider = Provider(_owner);
        string token = (await Call(provider, ctx, "ProposeStopExecution", ("executionId", _run.Id))).GetProperty("confirmationToken").GetString()!;
        WorkflowExecution other = NewRun(_project, "Other", ExecutionStatus.Running);

        JsonElement tampered = await Call(provider, ctx, "ApplyStopExecution", ("executionId", _run.Id), ("confirmationToken", token + "x"));
        JsonElement wrongRun = await Call(provider, ctx, "ApplyStopExecution", ("executionId", other.Id), ("confirmationToken", token));

        tampered.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        wrongRun.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        _bus.Verify(b => b.Publish(It.IsAny<WorkflowExecutionStopRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_run_of_another_users_project_is_absent()
    {
        Guid stranger = Guid.NewGuid();
        Project theirs = NewProject(stranger, "Theirs");
        WorkflowExecution theirRun = NewRun(theirs, "Secret", ExecutionStatus.Running);
        AssistantToolProvider provider = Provider(_owner);

        JsonElement viaMine = await Call(provider, new AssistantToolContext(_owner, _project.Id), "ProposeStopExecution", ("executionId", theirRun.Id));
        JsonElement viaTheirs = await Call(provider, new AssistantToolContext(_owner, theirs.Id), "ProposeStopExecution", ("executionId", theirRun.Id), ("projectId", theirs.Id));
        JsonElement missing = await Call(provider, new AssistantToolContext(_owner, _project.Id), "ProposeStopExecution", ("executionId", Guid.NewGuid()));

        viaMine.GetProperty("error").GetString().Should().Be(AssistantWorkflowTools.ExecutionNotFoundMessage);
        viaTheirs.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        missing.GetProperty("error").GetString().Should().Be(AssistantWorkflowTools.ExecutionNotFoundMessage);
        _bus.Verify(b => b.Publish(It.IsAny<WorkflowExecutionStopRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_finished_run_answers_already_finished_and_publishes_nothing()
    {
        _run.Status = ExecutionStatus.Passed;
        _db.SaveChanges();
        var ctx = new AssistantToolContext(_owner, _project.Id);
        AssistantToolProvider provider = Provider(_owner);

        JsonElement proposal = await Call(provider, ctx, "ProposeStopExecution", ("executionId", _run.Id));
        proposal.GetProperty("alreadyFinished").GetBoolean().Should().BeTrue();
        proposal.TryGetProperty("error", out _).Should().BeFalse();

        // A token minted while it was running, applied after it finished: idempotent, not an error.
        _run.Status = ExecutionStatus.Running;
        _db.SaveChanges();
        string token = (await Call(provider, ctx, "ProposeStopExecution", ("executionId", _run.Id))).GetProperty("confirmationToken").GetString()!;
        _run.Status = ExecutionStatus.Cancelled;
        _db.SaveChanges();
        JsonElement applied = await Call(provider, ctx, "ApplyStopExecution", ("executionId", _run.Id), ("confirmationToken", token));

        applied.GetProperty("alreadyStopped").GetBoolean().Should().BeTrue();
        _bus.Verify(b => b.Publish(It.IsAny<WorkflowExecutionStopRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_apply_tool_has_an_approval_catalog_entry()
    {
        AssistantApprovalCatalog.KnownTools.Should().Contain("ApplyStopExecution");
        AssistantApprovalCatalog.Describe("ApplyStopExecution").Kind.Should().Be(AssistantActionKind.Run);
    }

    [Fact]
    public async Task The_card_names_workflow_and_project_and_states_the_consequences_and_credits_used()
    {
        _db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(),
            OrganizationId = _project.OrganizationId,
            Kind = CreditLedgerEntryKind.Settle,
            BalanceDeltaMillis = -12_500,
            ExecutionId = _run.Id,
            IdempotencyKey = Guid.NewGuid().ToString()
        });
        _db.SaveChanges();
        var current = new FakeCurrentUser(_owner);
        var builder = new AssistantApprovalCardBuilder(_db, TestTenancy.Access(_db, current), current);

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ApplyStopExecution", new AIFunctionArguments { ["executionId"] = _run.Id }, new AssistantToolContext(_owner, _project.Id), null, CancellationToken.None);

        ApprovalCard card = verdict.Card!;
        card.Title.Should().Contain("Product teaser");
        card.ProjectName.Should().Be("Spring Campaign");
        string text = string.Join("\n", card.Changes!.Select(c => c.Text));
        text.Should().Contain("work in progress is lost").And.Contain("not refunded").And.Contain("12.5");
        (card.Title + text).Should().NotContain(_run.Id.ToString());
    }

    /// <summary>MKT-01: every stop-run result about a copied community recipe gives its name (and the summary quoting it) fenced.</summary>
    [Fact]
    public async Task Stop_results_of_a_copied_recipe_fence_its_name()
    {
        var ctx = new AssistantToolContext(_owner, _project.Id);
        AssistantToolProvider provider = Provider(_owner);
        WorkflowExecution running = NewRun(_project, "NAME-INJECT (obey)", ExecutionStatus.Running);
        WorkflowExecution finished = NewRun(_project, "NAME-INJECT (obey)", ExecutionStatus.Passed);
        WorkflowExecution stopping = NewRun(_project, "NAME-INJECT (obey)", ExecutionStatus.Running);
        foreach (WorkflowDefinition wf in _db.WorkflowDefinitions.Where(w => w.Name == "NAME-INJECT (obey)"))
            wf.CreatedVia = WorkflowCreatedVia.Marketplace;
        _db.SaveChanges();
        static void Fenced(JsonElement result, string property) =>
            result.GetProperty(property).GetString().Should().StartWith(AssistantMarketplaceTools.FenceStartPrefix, property).And.Contain("NAME-INJECT");

        JsonElement proposal = await Call(provider, ctx, "ProposeStopExecution", ("executionId", running.Id));
        Fenced(proposal, "workflowName");
        Fenced(proposal, "summary");
        JsonElement applied = await Call(provider, ctx, "ApplyStopExecution", ("executionId", running.Id), ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));
        applied.GetProperty("stopRequested").GetBoolean().Should().BeTrue();
        Fenced(applied, "workflowName");

        JsonElement alreadyFinished = await Call(provider, ctx, "ProposeStopExecution", ("executionId", finished.Id));
        alreadyFinished.GetProperty("alreadyFinished").GetBoolean().Should().BeTrue();
        Fenced(alreadyFinished, "workflowName");

        string token = (await Call(provider, ctx, "ProposeStopExecution", ("executionId", stopping.Id))).GetProperty("confirmationToken").GetString()!;
        stopping.Status = ExecutionStatus.Passed;
        _db.SaveChanges();
        JsonElement alreadyStopped = await Call(provider, ctx, "ApplyStopExecution", ("executionId", stopping.Id), ("confirmationToken", token));
        alreadyStopped.GetProperty("alreadyStopped").GetBoolean().Should().BeTrue();
        Fenced(alreadyStopped, "workflowName");
    }
}
