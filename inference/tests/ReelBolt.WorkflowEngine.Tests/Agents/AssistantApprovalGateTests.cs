using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The "ask permission" step in front of every assistant tool that writes
/// (docs/design/assistant-approvals.md). What is pinned here is the property the feature exists for:
/// <b>in ask mode nothing runs until a person says yes</b>, an unanswered or abandoned request is a no, and
/// the words on the card come from names the server read — never an id.
/// </summary>
public class AssistantApprovalGateTests
{
    private static readonly Regex AnyGuid = new(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private sealed class Harness : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid OrgId => TestTenancy.OrgOf(UserId);
        public Project Project { get; }
        public AssistantApprovalBroker Broker { get; } = new();
        public List<AssistantActionFrame> Frames { get; } = new();
        public List<string> Captions { get; } = new();
        public AssistantApprovalGate Gate { get; }
        public AssistantToolContext Context { get; }

        public Harness(
            bool auto = false, int timeoutSeconds = 30, string projectName = "Launch Teaser",
            IProjectAccessService? access = null, IWorkflowCostEstimator? estimator = null,
            IWorkflowExecutionPreflight? preflight = null, string? costLimit = null, bool chatApproved = false, bool fromWake = false)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            Db.ApplicationUsers.Add(new ApplicationUser
            {
                Id = UserId,
                Email = "test@example.com",
                DisplayName = "Test",
                AssistantAutoApprove = auto
            });
            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = projectName,
                OwnerId = UserId,
                OrganizationId = OrgId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(Project);
            Db.SaveChanges();

            var user = new FakeCurrentUser(UserId);
            Context = new AssistantToolContext(UserId, Project.Id, ChatApproved: chatApproved, FromWake: fromWake);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AssistantApprovalGate.TimeoutKey] = timeoutSeconds.ToString(),
                    [AssistantApprovalGate.AutoApproveCostCardKey] = costLimit
                })
                .Build();
            Gate = new AssistantApprovalGate(
                Context,
                OrgId,
                Db,
                Broker,
                new AssistantApprovalCardBuilder(Db, access ?? TestTenancy.Access(Db, user), user, estimator, preflight),
                configuration,
                emit: (frame, _) =>
                {
                    lock (Frames) Frames.Add(frame);
                    return Task.CompletedTask;
                },
                caption: (text, _) =>
                {
                    lock (Captions) Captions.Add(text);
                    return Task.CompletedTask;
                });
        }

        public async Task<AssistantActionFrame> WaitForFrameAsync(string state)
        {
            for (int i = 0; i < 500; i++)
            {
                lock (Frames)
                {
                    AssistantActionFrame? found = Frames.FirstOrDefault(f => f.State == state);
                    if (found is not null)
                        return found;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"No '{state}' frame arrived. Frames: {string.Join(", ", Frames.Select(f => f.State))}");
        }

        public ApprovalAnswerResult Answer(Guid id, bool allow, bool alwaysAllow = false, Guid? asUser = null) =>
            Broker.Answer(id, asUser ?? UserId, asUser is null ? OrgId : TestTenancy.OrgOf(asUser.Value), allow, alwaysAllow);

        public string[] States()
        {
            lock (Frames) return Frames.Select(f => f.State).ToArray();
        }

        public void Dispose()
        {
            Db.Dispose();
        }
    }

    private sealed class Spy
    {
        public int Runs { get; private set; }

        public Func<ValueTask<object?>> Run(object? result = null) => () =>
        {
            Runs++;
            return ValueTask.FromResult(result ?? (object)"{\"ok\":true}");
        };
    }

    private static AIFunctionArguments Args(params (string Key, object? Value)[] values)
    {
        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in values)
            args[key] = value;
        return args;
    }

    private static string AsText(object? result) => result switch
    {
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
        JsonElement e => e.GetRawText(),
        _ => result?.ToString() ?? string.Empty
    };

    // ---------------------------------------------------------------------
    // Ask mode: nothing runs until a person says yes
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Ask_mode_holds_the_tool_until_the_user_allows_it()
    {
        using var h = new Harness();
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);

        spy.Runs.Should().Be(0, "a pending card means nothing has run");
        pending.IsCompleted.Should().BeFalse();
        card.Mode.Should().Be(AssistantActionMode.Ask);
        card.Title.Should().Be("Delete the project “Launch Teaser”");
        card.Risk.Should().Be(AssistantActionRisk.Destructive);
        card.Kind.Should().Be(AssistantActionKind.Project);
        card.ExpiresAt.Should().NotBeNull();
        h.Captions.Should().Contain(AssistantApprovalGate.WaitingCaption);

        h.Answer(card.Id, allow: true).Should().Be(ApprovalAnswerResult.Recorded);
        await pending;

        spy.Runs.Should().Be(1);
        h.States().Should().Equal(AssistantActionState.Pending, AssistantActionState.Allowed, AssistantActionState.Done);
        h.Frames.Last().Outcome.Should().Be("Deleted.");
        h.Frames.Last().Refresh!.ProjectId.Should().Be(h.Project.Id);
        h.Broker.OpenCount.Should().Be(0, "a finished request is closed");
    }

    [Fact]
    public async Task Declining_never_runs_the_tool_and_tells_the_model_so()
    {
        using var h = new Harness();
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        h.Answer(card.Id, allow: false);

        string result = AsText(await pending);

        spy.Runs.Should().Be(0);
        h.States().Should().Equal(AssistantActionState.Pending, AssistantActionState.Declined);
        using JsonDocument doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("declined").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("expired").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("message").GetString().Should().Contain("nothing was changed");
    }

    [Fact]
    public async Task An_unanswered_request_expires_and_does_not_run_the_tool()
    {
        using var h = new Harness(timeoutSeconds: 1);
        var spy = new Spy();

        string result = AsText(await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None));

        spy.Runs.Should().Be(0);
        h.States().Should().Equal(AssistantActionState.Pending, AssistantActionState.Expired);
        JsonDocument.Parse(result).RootElement.GetProperty("expired").GetBoolean().Should().BeTrue();
        result.Should().NotContain("or left", "an expired card means nobody answered in time, never that the connection dropped");
        h.Broker.OpenCount.Should().Be(0);
    }

    /// <summary>
    /// A phone screen switching off drops the connection, but the card is not an answer. The gate
    /// has no connection token any more; the request stays open and a screen that comes back answers it.
    /// </summary>
    [Fact]
    public async Task A_card_stays_pending_while_nobody_is_looking_and_can_still_be_answered_afterwards()
    {
        using var h = new Harness(timeoutSeconds: 3000);
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);

        await Task.Delay(300); // the screen is off; time passes, no one answers
        pending.IsCompleted.Should().BeFalse("a dropped connection is not an answer");
        h.Broker.OpenCount.Should().Be(1);
        h.States().Should().Equal(AssistantActionState.Pending);

        // The screen comes back (re-attaches) and the person says yes.
        h.Broker.Answer(card.Id, h.UserId, h.OrgId, allow: true, alwaysAllow: false).Should().Be(ApprovalAnswerResult.Recorded);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        spy.Runs.Should().Be(1, "the explicit allow ran it");
        h.States().Should().Equal(AssistantActionState.Pending, AssistantActionState.Allowed, AssistantActionState.Done);
    }

    [Fact]
    public async Task Cancelling_the_turn_while_a_card_is_pending_abandons_it_and_closes_the_request()
    {
        using var h = new Harness(timeoutSeconds: 3000);
        var spy = new Spy();
        using var turn = new CancellationTokenSource();

        Task<object?> pending = h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), turn.Token);
        await h.WaitForFrameAsync(AssistantActionState.Pending);

        turn.Cancel();
        Func<Task> act = () => pending.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<OperationCanceledException>();
        spy.Runs.Should().Be(0);
        h.Broker.OpenCount.Should().Be(0);
        h.States().Should().Equal(
            new[] { AssistantActionState.Pending, AssistantActionState.Expired },
            "a screen that re-attaches later must not be shown a card nobody can answer as still pending");
    }

    [Fact]
    public async Task Only_the_owner_can_answer_a_card_and_a_stranger_cannot_even_tell_it_exists()
    {
        using var h = new Harness();
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);

        h.Answer(card.Id, allow: true, asUser: Guid.NewGuid()).Should().Be(ApprovalAnswerResult.NotFound);
        h.Answer(Guid.NewGuid(), allow: true).Should().Be(ApprovalAnswerResult.NotFound);
        pending.IsCompleted.Should().BeFalse("a stranger's yes must not release the tool");
        spy.Runs.Should().Be(0);

        h.Answer(card.Id, allow: false).Should().Be(ApprovalAnswerResult.Recorded);
        h.Answer(card.Id, allow: true).Should().Be(ApprovalAnswerResult.AlreadyDecided, "the first answer stands");
        await pending;
        spy.Runs.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // Auto mode
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Auto_mode_runs_the_tool_without_a_card_and_still_reports_what_was_done()
    {
        using var h = new Harness(auto: true);
        var spy = new Spy();

        await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);

        spy.Runs.Should().Be(1);
        h.States().Should().Equal(AssistantActionState.Done);
        h.Frames.Single().Mode.Should().Be(AssistantActionMode.Auto);
        h.Frames.Single().Title.Should().Be("Delete the project “Launch Teaser”");
        h.Broker.OpenCount.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // Auto mode still shows a card above the cost limit
    // ---------------------------------------------------------------------

    /// <summary>
    /// AUTHZ-IB-2 / API-IA-3: the admin writes and the link import keep their card in auto mode, so content read during
    /// a turn cannot make them happen with no human step. Unanswered (a wake turn has nobody there), nothing runs.
    /// </summary>
    [Fact]
    public async Task Auto_mode_still_asks_for_a_link_import_and_the_admin_writes()
    {
        foreach (string tool in new[]
                 {
                     "ApplyCreateInferenceProvider", "ApplyUpdateInferenceProvider", "ApplyDeleteInferenceProvider",
                     "ApplyAssignAgentSkills", "ApplyImportFileFromUrl"
                 })
        {
            AssistantApprovalCatalog.IsGated(tool).Should().BeTrue(tool);
            AssistantApprovalCatalog.AlwaysAsksUser(tool).Should().BeTrue(tool);
        }
        AssistantApprovalCatalog.AlwaysAsksUser("ApplyUploadFile").Should().BeFalse();
        AssistantApprovalCatalog.AlwaysAsksUser("ApplyDeleteProject").Should().BeFalse();

        using var h = new Harness(auto: true, timeoutSeconds: 1);
        var spy = new Spy();

        string result = AsText(await h.Gate.RunAsync(
            "ApplyImportFileFromUrl", Args(("url", "https://cdn.example.com/clip.mp4"), ("confirmationToken", "t")), spy.Run(), CancellationToken.None));

        spy.Runs.Should().Be(0);
        h.States().Should().Equal(AssistantActionState.Pending, AssistantActionState.Expired);
        h.Frames[0].Mode.Should().Be(AssistantActionMode.Ask);
        JsonDocument.Parse(result).RootElement.GetProperty("expired").GetBoolean().Should().BeTrue();
    }

    private static IWorkflowExecutionPreflight Priced(double requiredCredits, bool evaluated = true)
    {
        var preflight = new Mock<IWorkflowExecutionPreflight>();
        preflight.Setup(p => p.PriceAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowPreflightResult(
                WorkflowPreflightOutcome.Allowed,
                new WorkflowCostEstimate(true, true, 1000, 2000, (long)(requiredCredits * 1000), Array.Empty<WorkflowStepCostEstimate>()),
                0, evaluated));
        return preflight.Object;
    }

    private static async Task<Guid> AddCompileWorkflowAsync(Harness h)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Plain",
            ProjectId = h.Project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        h.Db.WorkflowDefinitions.Add(workflow);
        h.Db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoCompile
        });
        await h.Db.SaveChangesAsync();
        return workflow.Id;
    }

    [Theory]
    [InlineData(166, null, true)]
    [InlineData(25.001, null, true)]
    [InlineData(25, null, false)]
    [InlineData(10, null, false)]
    [InlineData(40, "50", false)]
    [InlineData(60, "50", true)]
    [InlineData(30, "0", true)]
    [InlineData(30, "-5", true)]
    [InlineData(30, "abc", true)]
    [InlineData(20, "abc", false)]
    public async Task Auto_mode_shows_a_card_only_when_the_run_costs_more_than_the_limit(double required, string? limit, bool card)
    {
        using var h = new Harness(auto: true, preflight: Priced(required), costLimit: limit);
        Guid workflowId = await AddCompileWorkflowAsync(h);
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflowId)), spy.Run(), CancellationToken.None);

        if (!card)
        {
            await pending;
            spy.Runs.Should().Be(1);
            h.Frames.Should().OnlyContain(f => f.AutoNote == null && f.Mode == AssistantActionMode.Auto);
            return;
        }

        AssistantActionFrame pendingCard = await h.WaitForFrameAsync(AssistantActionState.Pending);
        spy.Runs.Should().Be(0, "nothing runs until the user approves");
        pendingCard.Mode.Should().Be(AssistantActionMode.Ask);
        pendingCard.AutoNote.Should().NotBeNull();
        pendingCard.AutoNote!.CostCredits.Should().BeApproximately(required, 0.0001);
        pendingCard.AutoNote.LimitCredits.Should().Be(double.TryParse(limit, out double l) && l > 0 ? l : 25);
        pendingCard.ExpiresAt.Should().NotBeNull();
        h.Answer(pendingCard.Id, allow: true);
        await pending;
        spy.Runs.Should().Be(1);
    }

    [Fact]
    public async Task Auto_mode_card_above_the_limit_can_be_declined_and_then_nothing_runs()
    {
        using var h = new Harness(auto: true, preflight: Priced(166));
        Guid workflowId = await AddCompileWorkflowAsync(h);
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflowId)), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        h.Answer(card.Id, allow: false);
        await pending;

        spy.Runs.Should().Be(0);
        h.States().Should().Equal(AssistantActionState.Pending, AssistantActionState.Declined);
    }

    [Fact]
    public async Task Auto_mode_with_a_run_cost_that_could_not_be_estimated_shows_a_card_saying_so()
    {
        using var h = new Harness(auto: true, preflight: Priced(0, evaluated: false));
        Guid workflowId = await AddCompileWorkflowAsync(h);
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflowId)), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);

        card.AutoNote.Should().NotBeNull();
        card.AutoNote!.CostCredits.Should().BeNull();
        spy.Runs.Should().Be(0);
        h.Answer(card.Id, allow: false);
        await pending;
    }

    [Fact]
    public async Task Auto_mode_with_billing_off_shows_no_card_for_a_run()
    {
        var off = new Mock<IWorkflowExecutionPreflight>();
        off.Setup(p => p.PriceAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowPreflightResult?)null);
        using var h = new Harness(auto: true, preflight: off.Object);
        Guid workflowId = await AddCompileWorkflowAsync(h);
        var spy = new Spy();

        await h.Gate.RunAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflowId)), spy.Run(), CancellationToken.None);

        spy.Runs.Should().Be(1);
        h.Frames.Should().OnlyContain(f => f.AutoNote == null);
    }

    [Fact]
    public async Task Ask_mode_is_unchanged_by_the_limit_and_carries_no_note()
    {
        using var h = new Harness(auto: false, preflight: Priced(166));
        Guid workflowId = await AddCompileWorkflowAsync(h);
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflowId)), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        card.AutoNote.Should().BeNull();
        card.Mode.Should().Be(AssistantActionMode.Ask);
        h.Answer(card.Id, allow: true);
        await pending;
    }

    [Theory]
    [InlineData(null, 25)]
    [InlineData("100", 100)]
    [InlineData("0", 25)]
    [InlineData("-1", 25)]
    [InlineData("nope", 25)]
    public void The_limit_falls_back_to_25_when_unset_or_invalid(string? configured, double expected)
    {
        IConfiguration c = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AssistantApprovalGate.AutoApproveCostCardKey] = configured })
            .Build();
        AssistantApprovalGate.ResolveAutoApproveLimit(c).Should().Be(expected);
    }

    [Fact]
    public async Task A_tool_without_a_cost_figure_in_auto_mode_runs_without_a_card_as_before()
    {
        using var h = new Harness(auto: true, preflight: Priced(500));
        var spy = new Spy();
        await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        spy.Runs.Should().Be(1);
        h.States().Should().Equal(AssistantActionState.Done);
    }

    [Theory]
    [InlineData(40.0, true, false)]
    [InlineData(5.0, false, false)]
    [InlineData(null, true, true)]
    public async Task Auto_mode_cost_gates_the_paid_picture_tool(double? credits, bool card, bool unestimated)
    {
        using var h = new Harness(auto: true);
        h.Gate.NoteProposal("ProposeRunPictureTool", JsonSerializer.SerializeToElement(JsonSerializer.Serialize(
            credits is { } c
                ? new { confirmationToken = "pic", estimatedCost = new { credits = c } }
                : (object)new { confirmationToken = "pic" })));
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync(
            "ApplyRunPictureTool", Args(("confirmationToken", "pic"), ("tool", "upscale"), ("documentId", Guid.NewGuid())), spy.Run(), CancellationToken.None);

        if (!card)
        {
            await pending;
            spy.Runs.Should().Be(1);
            h.Frames.Should().OnlyContain(f => f.AutoNote == null);
            return;
        }

        AssistantActionFrame frame = await h.WaitForFrameAsync(AssistantActionState.Pending);
        spy.Runs.Should().Be(0);
        frame.AutoNote.Should().NotBeNull();
        (frame.AutoNote!.CostCredits is null).Should().Be(unestimated);
        h.Answer(frame.Id, allow: false);
        await pending;
    }

    [Fact]
    public async Task Auto_mode_picture_tool_with_billing_off_runs_without_a_card()
    {
        var off = new Mock<IWorkflowCostEstimator>();
        off.SetupGet(e => e.BillingEnabled).Returns(false);
        using var h = new Harness(auto: true, estimator: off.Object);
        var spy = new Spy();
        await h.Gate.RunAsync("ApplyRunPictureTool", Args(("confirmationToken", "x"), ("tool", "upscale"), ("documentId", Guid.NewGuid())), spy.Run(), CancellationToken.None);
        spy.Runs.Should().Be(1);
        h.Frames.Should().OnlyContain(f => f.AutoNote == null);
    }

    [Fact]
    public async Task Auto_mode_is_read_live_so_switching_it_on_mid_turn_applies_to_the_next_action()
    {
        using var h = new Harness(auto: false, timeoutSeconds: 1);
        var spy = new Spy();

        // Ask mode first: the request expires, so the tool does not run.
        await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        spy.Runs.Should().Be(0);

        ApplicationUser user = await h.Db.ApplicationUsers.SingleAsync();
        user.AssistantAutoApprove = true;
        await h.Db.SaveChangesAsync();

        await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        spy.Runs.Should().Be(1, "the preference is read on every call, never cached for the turn");
    }

    [Fact]
    public async Task Always_allow_switches_the_user_to_auto_mode_before_the_tool_runs()
    {
        using var h = new Harness();
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        h.Answer(card.Id, allow: true, alwaysAllow: true);
        await pending;

        (await h.Db.ApplicationUsers.AsNoTracking().SingleAsync()).AssistantAutoApprove.Should().BeTrue();

        int cardsBefore = h.Frames.Count(f => f.State == AssistantActionState.Pending);
        await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t2")), spy.Run(), CancellationToken.None);
        h.Frames.Count(f => f.State == AssistantActionState.Pending).Should().Be(cardsBefore, "no second card after Always allow");
        spy.Runs.Should().Be(2);
    }

    [Fact]
    public async Task Always_allow_is_ignored_when_the_answer_is_no()
    {
        using var h = new Harness();
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        h.Answer(card.Id, allow: false, alwaysAllow: true);
        await pending;

        (await h.Db.ApplicationUsers.AsNoTracking().SingleAsync()).AssistantAutoApprove.Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // What happens after the tool ran
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("{\"error\":\"stale confirmationToken, re-propose and try again\"}", "The proposal was out of date, so nothing was changed.")]
    [InlineData("{\"error\":\"project not found\"}", "It couldn't be found any more, so nothing was changed.")]
    [InlineData("{\"error\":\"something internal exploded in Foo.Bar\"}", "That didn't go through.")]
    [InlineData("{\"executed\":false,\"code\":\"INSUFFICIENT_CREDITS\",\"message\":\"needs 12 credits\"}", "There aren't enough credits for this.")]
    public async Task A_tool_that_refuses_is_reported_as_failed_in_plain_words(string toolResult, string expectedOutcome)
    {
        using var h = new Harness(auto: true);

        await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), new Spy().Run(toolResult), CancellationToken.None);

        AssistantActionFrame frame = h.Frames.Single();
        frame.State.Should().Be(AssistantActionState.Failed);
        frame.Outcome.Should().Be(expectedOutcome);
        frame.Outcome.Should().NotContain("Foo.Bar", "a technical error string never reaches the card");
    }

    [Fact]
    public async Task A_tool_that_throws_is_reported_as_failed_and_the_exception_still_propagates()
    {
        using var h = new Harness(auto: true);

        Func<Task> act = () => h.Gate.RunAsync(
            "ApplyDeleteProject", Args(("confirmationToken", "t")),
            () => throw new InvalidOperationException("boom"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        h.Frames.Single().State.Should().Be(AssistantActionState.Failed);
    }

    [Fact]
    public async Task A_gated_tool_that_throws_still_fails_its_card_when_the_failure_reporter_answers_the_exception()
    {
        using var h = new Harness(auto: true);
        AIFunction inner = AIFunctionFactory.Create(string () => throw new InvalidOperationException("boom"), name: "ApplyDeleteProject");
        var chain = new AssistantToolFailureReporter(
            new AssistantToolInterceptor(inner, h.Gate, onResult: null),
            new AssistantToolFailureReporting(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "chat", true),
            threadId: null);

        object? answer = await chain.InvokeAsync(new AIFunctionArguments());

        h.Frames.Single().State.Should().Be(AssistantActionState.Failed);
        JsonSerializer.SerializeToElement(answer).GetProperty("code").GetString().Should().Be("TOOL_FAILED");
    }

    [Fact]
    public async Task An_apply_that_names_nothing_the_caller_can_reach_is_refused_and_the_tool_never_runs()
    {
        using var h = new Harness();
        var spy = new Spy();
        Guid someoneElsesProject = Guid.NewGuid();
        Guid stranger = Guid.NewGuid();
        h.Db.Projects.Add(new Project
        {
            Id = someoneElsesProject,
            Name = "Not yours",
            OwnerId = stranger,
            OrganizationId = TestTenancy.OrgOf(stranger),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();

        string result = AsText(await h.Gate.RunAsync(
            "ApplyDeleteProject", Args(("projectId", someoneElsesProject), ("confirmationToken", "t")), spy.Run(), CancellationToken.None));

        spy.Runs.Should().Be(0, "'no card' must never mean 'no check': a call the card builder cannot place does not run");
        h.Frames.Should().BeEmpty("there is nothing to approve");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Be("project not found");
        result.Should().NotContain("Not yours", "a refusal never reveals what it could not reach");
    }

    // ---------------------------------------------------------------------
    // The words on the card
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Cards_name_things_the_way_the_user_sees_them_and_never_carry_an_id()
    {
        using var h = new Harness(auto: true, projectName: "Launch Teaser");
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Product reel",
            ProjectId = h.Project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            OriginalFileName = "script.md",
            OriginalPath = "docs/script.md",
            StorageKey = "k",
            StorageBucket = "b",
            MimeType = "text/markdown",
            SizeBytes = 3,
            UploadedAt = DateTime.UtcNow
        };
        h.Db.WorkflowDefinitions.Add(workflow);
        h.Db.ProjectFiles.Add(file);
        await h.Db.SaveChangesAsync();

        var spy = new Spy();
        (string Tool, AIFunctionArguments Args, string Title)[] cases =
        [
            ("ApplyUpdateProject", Args(("projectId", h.Project.Id), ("name", "Launch Reel"), ("confirmationToken", "t")),
                "Rename the project “Launch Teaser” to “Launch Reel”"),
            ("ApplyCreateProject", Args(("name", "Spring Promo"), ("confirmationToken", "t")), "Create the project “Spring Promo”"),
            ("ApplyDeleteWorkflow", Args(("projectId", h.Project.Id), ("workflowId", workflow.Id), ("confirmationToken", "t")),
                "Delete the workflow “Product reel”"),
            ("ApplyDeleteProjectFile", Args(("projectId", h.Project.Id), ("fileId", file.Id), ("confirmationToken", "t")),
                "Delete the file “docs/script.md”"),
            ("ApplyWriteProjectFile", Args(("directoryPath", "docs"), ("fileName", "script.md"), ("confirmationToken", "t")),
                "Replace the contents of the file “docs/script.md”"),
        ];

        foreach ((string tool, AIFunctionArguments args, string title) in cases)
            await h.Gate.RunAsync(tool, args, spy.Run(), CancellationToken.None);

        h.Frames.Select(f => f.Title).Should().Equal(cases.Select(c => c.Title));
        foreach (AssistantActionFrame frame in h.Frames)
        {
            string visible = string.Join(" ", new[] { frame.Title, frame.Summary, frame.Outcome, frame.ProjectName, frame.Cost }
                .Concat(frame.Changes?.Select(c => c.Text) ?? []));
            AnyGuid.IsMatch(visible).Should().BeFalse($"'{frame.Title}' must not show an id");
        }

        h.Frames[4].Risk.Should().Be(AssistantActionRisk.Destructive, "overwriting a file loses its old contents");
        h.Frames[0].ProjectName.Should().Be("Launch Teaser");
    }

    [Fact]
    public async Task A_workflow_edit_card_lists_the_steps_the_proposal_changes()
    {
        using var h = new Harness(auto: true);
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Product reel",
            ProjectId = h.Project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        h.Db.WorkflowDefinitions.Add(workflow);
        await h.Db.SaveChangesAsync();

        // What ProposeWorkflow hands back, as AIFunctionFactory serialises it.
        string proposalJson = JsonSerializer.Serialize(new
        {
            additions = new[] { new { stepOrder = 3, agentDefinitionId = Guid.NewGuid(), stepType = "ReviewLoop", summary = "Final review" } },
            removals = new[] { new { stepOrder = 2, agentDefinitionId = Guid.NewGuid(), stepType = "Agent", summary = (string?)null } },
            modifications = Array.Empty<object>(),
            confirmationToken = "tok-1"
        });
        h.Gate.NoteProposal("ProposeWorkflow", JsonSerializer.SerializeToElement(proposalJson));

        await h.Gate.RunAsync(
            "ApplyWorkflow",
            Args(("workflowId", workflow.Id), ("name", "Product reel"), ("steps", new object[0]), ("confirmationToken", "tok-1")),
            new Spy().Run(), CancellationToken.None);

        AssistantActionFrame frame = h.Frames.Single();
        frame.Title.Should().Be("Change the workflow “Product reel”");
        frame.Kind.Should().Be(AssistantActionKind.Workflow);
        frame.Changes.Should().BeEquivalentTo(new[]
        {
            new AssistantActionChange(AssistantChangeKind.Add, "Step 3: Final review (review loop)"),
            new AssistantActionChange(AssistantChangeKind.Remove, "Step 2: Agent")
        }, o => o.WithStrictOrdering());
        frame.Refresh!.WorkflowId.Should().Be(workflow.Id);
    }

    [Fact]
    public async Task Running_a_workflow_that_first_has_to_ask_about_paid_clips_is_answered_by_the_gate_and_never_reaches_the_tool()
    {
        using var h = new Harness();
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "With clips",
            ProjectId = h.Project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        h.Db.WorkflowDefinitions.Add(workflow);
        h.Db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = "{\"maxSpendCredits\": 5}"
        });
        await h.Db.SaveChangesAsync();

        var spy = new Spy();
        string result = AsText(await h.Gate.RunAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id)), spy.Run(), CancellationToken.None));

        spy.Runs.Should().Be(0, "the gate must not call the tool on the claim that it will only ask: the tool re-reads the workflow, " +
            "and an edit in between would make it run with no card");
        h.Frames.Should().BeEmpty("there is nothing to approve yet");
        JsonElement answer = JsonDocument.Parse(result).RootElement;
        answer.GetProperty("needsDecision").GetBoolean().Should().BeTrue();
        answer.GetProperty("executed").GetBoolean().Should().BeFalse();
        answer.GetProperty("workflowName").GetString().Should().Be("With clips");
        answer.GetProperty("estimatedMaxCostUsd").GetDecimal().Should().Be(5m, "the model still gets the cap to tell the user");
    }

    [Fact]
    public async Task The_run_card_names_the_chosen_resolution()
    {
        using var h = new Harness();
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Plain",
            ProjectId = h.Project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        h.Db.WorkflowDefinitions.Add(workflow);
        h.Db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoCompile
        });
        await h.Db.SaveChangesAsync();

        var spy = new Spy();
        Task<object?> pending = h.Gate.RunAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool,
            Args(("workflowId", workflow.Id), ("outputResolution", "4k")), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        h.Answer(card.Id, allow: false);
        await pending;

        card.Changes!.Select(c => c.Text).Should().Contain("Resolution: 4K (2160p)");
    }

    [Fact]
    public async Task A_file_write_that_replaces_a_file_added_since_the_proposal_is_not_called_a_create()
    {
        using var h = new Harness(auto: true);
        // The proposal was made when the path was free ("replacesExistingFile": false)...
        h.Gate.NoteProposal("ProposeWriteProjectFile", JsonSerializer.SerializeToElement(
            JsonSerializer.Serialize(new { confirmationToken = "tok-w", replacesExistingFile = false, sizeBytes = 5 })));
        // ...and someone has since put a file there. The token covers the content, not whether the path is free.
        h.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            OriginalFileName = "script.md",
            OriginalPath = "docs/script.md",
            StorageKey = "k",
            StorageBucket = "b",
            MimeType = "text/markdown",
            SizeBytes = 3,
            UploadedAt = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();

        // Un-normalised on purpose: a leading "./" and a trailing slash — the tool would store it at docs/script.md.
        await h.Gate.RunAsync(
            "ApplyWriteProjectFile",
            Args(("directoryPath", "./docs/"), ("fileName", "script.md"), ("confirmationToken", "tok-w")),
            new Spy().Run(), CancellationToken.None);

        AssistantActionFrame card = h.Frames.First();
        card.Title.Should().Be("Replace the contents of the file “docs/script.md”");
        card.Risk.Should().Be(AssistantActionRisk.Destructive);
    }

    [Fact]
    public async Task A_proposal_that_cannot_be_read_still_gets_a_card()
    {
        using var h = new Harness(auto: true);

        h.Gate.NoteProposal("ProposeDeleteProject", "not json at all {");
        h.Gate.NoteProposal("ProposeDeleteProject", null);

        await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "unknown")), new Spy().Run(), CancellationToken.None);

        h.Frames.Single().Title.Should().Be("Delete the project “Launch Teaser”");
    }

    // ---------------------------------------------------------------------
    // A card that cannot be built (SEC-EF-10): a charged action never asks as free
    // ---------------------------------------------------------------------

    /// <summary>Every project read fails: any card that needs the project throws while being built.</summary>
    private sealed class UnreachableProjects : IProjectAccessService
    {
        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct) =>
            throw new InvalidOperationException("the database is unreachable");

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) =>
            throw new InvalidOperationException("the database is unreachable");

        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) =>
            throw new InvalidOperationException("the database is unreachable");
    }

    private static IWorkflowCostEstimator Billing(bool enabled)
    {
        var estimator = new Mock<IWorkflowCostEstimator>();
        estimator.SetupGet(e => e.BillingEnabled).Returns(enabled);
        return estimator.Object;
    }

    [Theory]
    [InlineData(AssistantApprovalCatalog.ExecuteWorkflowTool, CreditCostWording.RunCostUnknown)]
    [InlineData("ApplyRunPictureTool", CreditCostWording.CostUnknown)]
    [InlineData("ApplyDesignGraphic", CreditCostWording.CostUnknown)]
    public async Task A_paid_card_that_cannot_be_built_still_asks_as_paid_with_an_unknown_cost(string tool, string expectedCost)
    {
        using var h = new Harness(auto: true, access: new UnreachableProjects(), estimator: Billing(enabled: true));

        await h.Gate.RunAsync(
            tool, Args(("workflowId", Guid.NewGuid().ToString()), ("documentId", Guid.NewGuid().ToString()), ("confirmationToken", "t")),
            new Spy().Run(), CancellationToken.None);

        AssistantActionFrame card = h.Frames.First();
        card.Risk.Should().Be(AssistantActionRisk.Paid, "the action is charged even though its card could not be built");
        card.Cost.Should().Be(expectedCost);
    }

    [Fact]
    public async Task A_paid_card_that_cannot_be_built_is_paid_when_no_estimator_says_billing_is_off()
    {
        using var h = new Harness(auto: true, access: new UnreachableProjects());

        await h.Gate.RunAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", Guid.NewGuid().ToString())),
            new Spy().Run(), CancellationToken.None);

        AssistantActionFrame card = h.Frames.First();
        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Cost.Should().Be(CreditCostWording.RunCostUnknown);
    }

    [Theory]
    [InlineData(AssistantApprovalCatalog.ExecuteWorkflowTool)]
    [InlineData("ApplyRunPictureTool")]
    [InlineData("ApplyDesignGraphic")]
    public async Task A_paid_card_that_cannot_be_built_with_billing_off_is_normal_with_no_cost(string tool)
    {
        using var h = new Harness(auto: true, access: new UnreachableProjects(), estimator: Billing(enabled: false));

        await h.Gate.RunAsync(
            tool, Args(("workflowId", Guid.NewGuid().ToString()), ("documentId", Guid.NewGuid().ToString()), ("confirmationToken", "t")),
            new Spy().Run(), CancellationToken.None);

        AssistantActionFrame card = h.Frames.First();
        card.Risk.Should().Be(AssistantActionRisk.Normal);
        card.Cost.Should().BeNull();
    }

    [Fact]
    public async Task An_unpaid_card_that_cannot_be_built_asks_with_the_catalogs_generic_card()
    {
        using var h = new Harness(auto: true, access: new UnreachableProjects(), estimator: Billing(enabled: true));

        await h.Gate.RunAsync("ApplyDeleteProject", Args(("confirmationToken", "t")), new Spy().Run(), CancellationToken.None);

        AssistantActionFrame card = h.Frames.First();
        ApprovalToolInfo info = AssistantApprovalCatalog.Describe("ApplyDeleteProject");
        card.Kind.Should().Be(info.Kind);
        card.Risk.Should().Be(AssistantActionRisk.Destructive);
        card.Title.Should().Be(info.FallbackTitle);
        card.Cost.Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // A run that allows sending media always asks, and the approval is the consent
    // ---------------------------------------------------------------------

    private const string SegmentOff =
        """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"the plate","method":"Segmentation"}],"allowSourceMediaEgress":false}""";

    private static async Task<Guid> AddSegmentingWorkflowAsync(Harness h, string config = SegmentOff)
    {
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Blur the plate", ProjectId = h.Project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        h.Db.WorkflowDefinitions.Add(workflow);
        h.Db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            Label = "Find the plate",
            VideoAnalyzeConfigJson = config
        });
        await h.Db.SaveChangesAsync();
        return workflow.Id;
    }

    private static AIFunctionArguments MediaRunArgs(Guid workflowId) => Args(
        ("workflowId", workflowId),
        ("brief", System.Text.Json.JsonDocument.Parse("""{"inputs":{"allowMediaEgress":true}}""").RootElement.Clone()));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_run_that_allows_sending_media_waits_for_the_persons_click_in_every_mode(bool auto, bool fromWake)
    {
        using var h = new Harness(auto: auto, preflight: Priced(1), chatApproved: true, fromWake: fromWake);
        Guid workflowId = await AddSegmentingWorkflowAsync(h);
        int runs = 0;
        bool approvedInside = false;

        Task<object?> pending = h.Gate.RunAsync(AssistantApprovalCatalog.ExecuteWorkflowTool, MediaRunArgs(workflowId), () =>
        {
            runs++;
            approvedInside = AssistantMediaEgressApproval.IsApproved;
            return ValueTask.FromResult<object?>("{\"ok\":true}");
        }, CancellationToken.None);

        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        runs.Should().Be(0, "nothing runs until the person approves, in auto mode and on a woken turn too");
        card.Mode.Should().Be(AssistantActionMode.Ask);
        card.Changes!.Select(c => c.Text).Should().Contain(
            "Allows sending your media to AI services for this run: Find the plate tracks an object by name.");

        h.Answer(card.Id, allow: true);
        await pending;

        runs.Should().Be(1);
        approvedInside.Should().BeTrue("approving the card is the consent the run tool reads");
        AssistantMediaEgressApproval.IsApproved.Should().BeFalse("the mark ends with the call");
    }

    [Fact]
    public async Task Declining_the_media_card_runs_nothing()
    {
        using var h = new Harness(auto: true, preflight: Priced(1), chatApproved: true);
        Guid workflowId = await AddSegmentingWorkflowAsync(h);
        var spy = new Spy();

        Task<object?> pending = h.Gate.RunAsync(AssistantApprovalCatalog.ExecuteWorkflowTool, MediaRunArgs(workflowId), spy.Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        h.Answer(card.Id, allow: false);
        await pending;

        spy.Runs.Should().Be(0);
        h.States().Should().Equal(AssistantActionState.Pending, AssistantActionState.Declined);
    }

    [Fact]
    public async Task A_run_that_does_not_ask_to_send_media_is_never_marked_as_approved_for_it()
    {
        // Auto mode, a workflow whose own switch is already on, no permission in the brief: the usual auto run.
        using var h = new Harness(auto: true, preflight: Priced(1), chatApproved: true);
        Guid workflowId = await AddSegmentingWorkflowAsync(h, SegmentOff.Replace("\"allowSourceMediaEgress\":false", "\"allowSourceMediaEgress\":true"));
        bool approvedInside = true;

        await h.Gate.RunAsync(AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflowId)), () =>
        {
            approvedInside = AssistantMediaEgressApproval.IsApproved;
            return ValueTask.FromResult<object?>("{\"ok\":true}");
        }, CancellationToken.None);

        approvedInside.Should().BeFalse();
        h.Frames.Should().OnlyContain(f => f.Mode == AssistantActionMode.Auto);
    }

    // ---------------------------------------------------------------------
    // A workflow edit that switches a step's footage-sharing on asks like a media-sending card
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(false, "tracksAnObjectByName", "Allows sending your media to AI services from now on, in this workflow: “Find the plate” tracks an object by name.")]
    [InlineData(true, "tracksAnObjectByName", "Allows sending your media to AI services from now on, in this workflow: “Find the plate” tracks an object by name.")]
    [InlineData(true, null, "Allows sending your media to AI services from now on, in this workflow: the permission switch on “Find the plate”.")]
    public async Task An_edit_that_grants_a_steps_media_sending_lists_it_always_asks_and_the_approval_marks_the_call(bool auto, string? reason, string line)
    {
        using var h = new Harness(auto: auto, chatApproved: true);
        Guid workflowId = await AddSegmentingWorkflowAsync(h);
        h.Gate.NoteProposal("ProposeWorkflow", JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new
        {
            additions = Array.Empty<object>(),
            removals = Array.Empty<object>(),
            modifications = Array.Empty<object>(),
            egressGrants = new[] { new { stepOrder = 1, label = "Find the plate", reason } },
            confirmationToken = "tok-egress"
        })));
        bool approvedInside = false;

        Task<object?> pending = h.Gate.RunAsync(
            "ApplyWorkflow",
            Args(("workflowId", workflowId), ("name", "Blur the plate"), ("steps", new object[0]), ("confirmationToken", "tok-egress")),
            () =>
            {
                approvedInside = AssistantMediaEgressApproval.IsApproved;
                return ValueTask.FromResult<object?>("{\"ok\":true}");
            }, CancellationToken.None);

        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);
        card.Mode.Should().Be(AssistantActionMode.Ask, "auto mode never grants it");
        card.Changes!.Select(c => c.Text).Should().Contain(line);
        h.Answer(card.Id, allow: true);
        await pending;
        approvedInside.Should().BeTrue();
    }

    [Fact]
    public async Task An_edit_with_no_grant_is_not_a_media_card_and_runs_in_auto_mode()
    {
        using var h = new Harness(auto: true, chatApproved: true);
        Guid workflowId = await AddSegmentingWorkflowAsync(h);
        h.Gate.NoteProposal("ProposeWorkflow", JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new
        {
            additions = Array.Empty<object>(),
            removals = Array.Empty<object>(),
            modifications = Array.Empty<object>(),
            confirmationToken = "tok-none"
        })));
        bool approvedInside = true;

        await h.Gate.RunAsync(
            "ApplyWorkflow",
            Args(("workflowId", workflowId), ("name", "Blur the plate"), ("steps", new object[0]), ("confirmationToken", "tok-none")),
            () =>
            {
                approvedInside = AssistantMediaEgressApproval.IsApproved;
                return ValueTask.FromResult<object?>("{\"ok\":true}");
            }, CancellationToken.None);

        approvedInside.Should().BeFalse();
        h.Frames.Should().OnlyContain(f => f.Mode == AssistantActionMode.Auto);
    }

    [Fact]
    public async Task A_step_name_cannot_hide_or_replace_the_server_sentence_on_the_grant_card()
    {
        using var h = new Harness(auto: true, chatApproved: true);
        Guid workflowId = await AddSegmentingWorkflowAsync(h);
        h.Gate.NoteProposal("ProposeWorkflow", JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new
        {
            additions = Array.Empty<object>(),
            removals = Array.Empty<object>(),
            modifications = Array.Empty<object>(),
            egressGrants = new[] { new { stepOrder = 1, label = "x\n\r(nothing is sent)\u0007", reason = "tracksAnObjectByName" } },
            confirmationToken = "tok-label"
        })));

        Task<object?> pending = h.Gate.RunAsync(
            "ApplyWorkflow",
            Args(("workflowId", workflowId), ("name", "Blur the plate"), ("steps", new object[0]), ("confirmationToken", "tok-label")),
            new Spy().Run(), CancellationToken.None);
        AssistantActionFrame card = await h.WaitForFrameAsync(AssistantActionState.Pending);

        string line = card.Changes!.Select(c => c.Text).First();
        line.Should().Be("Allows sending your media to AI services from now on, in this workflow: “x(nothing is sent)” tracks an object by name.");
        line.Should().NotContain("\n").And.NotContain("\u0007");
        h.Answer(card.Id, allow: false);
        await pending;
    }
}
