using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The broker that pairs a waiting assistant turn with the user's click, the endpoint that carries the click,
/// the approval-mode preference, and the catalog that says which tools are gated at all.
/// </summary>
public class AssistantApprovalsControllerTests
{
    private static InferenceApiDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AssistantApprovalsController Controller(InferenceApiDbContext db, Guid userId, AssistantApprovalBroker broker) =>
        new(db, new FakeCurrentUser(userId), broker);

    private static ApplicationUser SeedUser(InferenceApiDbContext db, Guid userId, bool auto = false)
    {
        var user = new ApplicationUser { Id = userId, Email = $"{userId}@example.com", DisplayName = "T", AssistantAutoApprove = auto };
        db.ApplicationUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    // ---------------------------------------------------------------------
    // Broker
    // ---------------------------------------------------------------------

    [Fact]
    public async Task The_owner_answers_a_request_and_the_waiting_side_hears_it()
    {
        var broker = new AssistantApprovalBroker();
        Guid user = Guid.NewGuid(), id = Guid.NewGuid();
        using ApprovalTicket ticket = broker.Open(id, user, TestTenancy.OrgOf(user));

        broker.Answer(id, user, TestTenancy.OrgOf(user), allow: true, alwaysAllow: true).Should().Be(ApprovalAnswerResult.Recorded);

        ApprovalAnswer answer = await ticket.Answer;
        answer.Should().Be(new ApprovalAnswer(Allow: true, AlwaysAllow: true));
    }

    [Fact]
    public void A_request_cannot_be_answered_by_another_user_or_from_another_workspace()
    {
        var broker = new AssistantApprovalBroker();
        Guid user = Guid.NewGuid(), id = Guid.NewGuid();
        using ApprovalTicket ticket = broker.Open(id, user, TestTenancy.OrgOf(user));

        broker.Answer(id, Guid.NewGuid(), TestTenancy.OrgOf(user), true, false).Should().Be(ApprovalAnswerResult.NotFound);
        broker.Answer(id, user, Guid.NewGuid(), true, false).Should().Be(ApprovalAnswerResult.NotFound);
        ticket.Answer.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_closed_request_is_gone_and_a_decided_one_stays_decided()
    {
        var broker = new AssistantApprovalBroker();
        Guid user = Guid.NewGuid(), id = Guid.NewGuid();
        ApprovalTicket ticket = broker.Open(id, user, null);

        broker.Answer(id, user, null, false, false).Should().Be(ApprovalAnswerResult.Recorded);
        broker.Answer(id, user, null, true, false).Should().Be(ApprovalAnswerResult.AlreadyDecided);
        ticket.Expire();
        (await ticket.Answer).Allow.Should().BeFalse("expiring after an answer changes nothing");

        ticket.Dispose();
        broker.Answer(id, user, null, true, false).Should().Be(ApprovalAnswerResult.NotFound);
        broker.OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task Expiring_a_request_is_a_no_that_says_nobody_answered()
    {
        var broker = new AssistantApprovalBroker();
        Guid user = Guid.NewGuid();
        using ApprovalTicket ticket = broker.Open(Guid.NewGuid(), user, null);

        ticket.Expire();

        (await ticket.Answer).Should().Be(new ApprovalAnswer(Allow: false, AlwaysAllow: false, Expired: true));
    }

    // ---------------------------------------------------------------------
    // POST /approvals/{id}
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("allow", true)]
    [InlineData("ALLOW ", true)]
    [InlineData("deny", false)]
    public async Task Answering_a_card_returns_204_and_releases_the_turn(string decision, bool expectedAllow)
    {
        using InferenceApiDbContext db = CreateDb();
        var broker = new AssistantApprovalBroker();
        Guid user = Guid.NewGuid(), id = Guid.NewGuid();
        using ApprovalTicket ticket = broker.Open(id, user, TestTenancy.OrgOf(user));

        IActionResult result = Controller(db, user, broker).Answer(id, new AssistantApprovalAnswerRequest(decision));

        result.Should().BeOfType<NoContentResult>();
        (await ticket.Answer).Allow.Should().Be(expectedAllow);
    }

    [Fact]
    public void Answering_an_unknown_or_foreign_request_is_404_never_403()
    {
        using InferenceApiDbContext db = CreateDb();
        var broker = new AssistantApprovalBroker();
        Guid owner = Guid.NewGuid(), id = Guid.NewGuid();
        using ApprovalTicket ticket = broker.Open(id, owner, TestTenancy.OrgOf(owner));

        Controller(db, Guid.NewGuid(), broker).Answer(id, new AssistantApprovalAnswerRequest("allow"))
            .Should().BeOfType<NotFoundResult>();
        Controller(db, owner, broker).Answer(Guid.NewGuid(), new AssistantApprovalAnswerRequest("allow"))
            .Should().BeOfType<NotFoundResult>();
        ticket.Answer.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Answering_twice_is_a_conflict_and_a_bad_decision_is_rejected_before_anything_is_decided()
    {
        using InferenceApiDbContext db = CreateDb();
        var broker = new AssistantApprovalBroker();
        Guid user = Guid.NewGuid(), id = Guid.NewGuid();
        using ApprovalTicket ticket = broker.Open(id, user, TestTenancy.OrgOf(user));
        AssistantApprovalsController controller = Controller(db, user, broker);

        controller.Answer(id, new AssistantApprovalAnswerRequest("maybe")).Should().BeOfType<BadRequestObjectResult>();
        controller.Answer(id, new AssistantApprovalAnswerRequest(null)).Should().BeOfType<BadRequestObjectResult>();
        ticket.Answer.IsCompleted.Should().BeFalse("a malformed answer decides nothing");

        controller.Answer(id, new AssistantApprovalAnswerRequest("deny")).Should().BeOfType<NoContentResult>();
        controller.Answer(id, new AssistantApprovalAnswerRequest("allow")).Should().BeOfType<ConflictObjectResult>();
        (await ticket.Answer).Allow.Should().BeFalse("the first answer stands");
    }

    // ---------------------------------------------------------------------
    // GET / PUT /preferences
    // ---------------------------------------------------------------------

    private static string ModeOf(ActionResult<AssistantPreferencesDto> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<AssistantPreferencesDto>().Subject.ApprovalMode!;

    [Fact]
    public async Task The_default_is_to_ask_and_the_mode_round_trips_through_the_row()
    {
        using InferenceApiDbContext db = CreateDb();
        Guid user = Guid.NewGuid();
        SeedUser(db, user);
        AssistantApprovalsController controller = Controller(db, user, new AssistantApprovalBroker());

        ModeOf(await controller.GetPreferences(default)).Should().Be("ask");

        ModeOf(await controller.PutPreferences(new AssistantPreferencesDto("auto"), default)).Should().Be("auto");
        (await db.ApplicationUsers.AsNoTracking().SingleAsync()).AssistantAutoApprove.Should().BeTrue();

        ModeOf(await controller.PutPreferences(new AssistantPreferencesDto(" ASK "), default)).Should().Be("ask");
        (await db.ApplicationUsers.AsNoTracking().SingleAsync()).AssistantAutoApprove.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 25)]
    [InlineData("80", 80)]
    [InlineData("0", 25)]
    public async Task Preferences_carry_the_read_only_auto_approve_cost_limit(string? configured, double expected)
    {
        using InferenceApiDbContext db = CreateDb();
        Guid user = Guid.NewGuid();
        SeedUser(db, user);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AssistantApprovalGate.AutoApproveCostCardKey] = configured })
            .Build();
        var controller = new AssistantApprovalsController(db, new FakeCurrentUser(user), new AssistantApprovalBroker(), config);

        AssistantPreferencesDto Dto(ActionResult<AssistantPreferencesDto> r) =>
            (AssistantPreferencesDto)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

        Dto(await controller.GetPreferences(default)).AutoApproveCostLimitCredits.Should().Be(expected);
        Dto(await controller.PutPreferences(new AssistantPreferencesDto("auto", 9999), default)).AutoApproveCostLimitCredits
            .Should().Be(expected, "the limit is config-only: a client cannot set it");
    }

    [Fact]
    public async Task One_users_mode_never_touches_anothers()
    {
        using InferenceApiDbContext db = CreateDb();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid();
        SeedUser(db, alice);
        SeedUser(db, bob);

        await Controller(db, alice, new AssistantApprovalBroker()).PutPreferences(new AssistantPreferencesDto("auto"), default);

        (await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == alice)).AssistantAutoApprove.Should().BeTrue();
        (await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == bob)).AssistantAutoApprove.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yolo")]
    public async Task An_unknown_mode_is_rejected_and_changes_nothing(string? mode)
    {
        using InferenceApiDbContext db = CreateDb();
        Guid user = Guid.NewGuid();
        SeedUser(db, user, auto: true);

        ActionResult<AssistantPreferencesDto> result =
            await Controller(db, user, new AssistantApprovalBroker()).PutPreferences(new AssistantPreferencesDto(mode), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ApplicationUsers.AsNoTracking().SingleAsync()).AssistantAutoApprove.Should().BeTrue();
    }

    [Fact]
    public async Task A_caller_with_no_account_row_gets_404_rather_than_a_silent_no_op()
    {
        using InferenceApiDbContext db = CreateDb();

        ActionResult<AssistantPreferencesDto> result =
            await Controller(db, Guid.NewGuid(), new AssistantApprovalBroker()).PutPreferences(new AssistantPreferencesDto("auto"), default);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // ---------------------------------------------------------------------
    // Which tools are gated
    // ---------------------------------------------------------------------

    [Fact]
    public void Every_tool_that_writes_has_a_deliberate_catalog_entry_and_nothing_stale_is_listed()
    {
        using InferenceApiDbContext db = CreateDb();
        Guid user = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(user));

        string[] registered = provider.GetTools(new AssistantToolContext(user)).Select(t => t.Name).ToArray();
        string[] gated = registered.Where(AssistantApprovalCatalog.IsGated).ToArray();

        gated.Should().Contain(AssistantApprovalCatalog.ExecuteWorkflowTool, "starting a run spends credits");
        gated.Where(n => n.StartsWith("Apply", StringComparison.Ordinal)).Should().NotBeEmpty();
        gated.Should().BeEquivalentTo(AssistantApprovalCatalog.KnownTools,
            "a new Apply* tool needs a catalog entry so its card is written on purpose, and an entry for a tool that no longer exists must go");

        foreach (string apply in gated.Where(n => n.StartsWith("Apply", StringComparison.Ordinal)))
            registered.Should().Contain("Propose" + apply["Apply".Length..], "every gated write is the second half of a proposal");

        registered.Where(AssistantApprovalCatalog.IsProposal).Should().OnlyContain(n => !AssistantApprovalCatalog.IsGated(n),
            "a proposal writes nothing, so it is never asked about");
    }

    [Fact]
    public async Task Every_gated_tool_resolves_to_a_card_that_says_more_than_the_generic_fallback()
    {
        using InferenceApiDbContext db = CreateDb();
        Guid user = Guid.NewGuid();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Alpha",
            OwnerId = user,
            OrganizationId = TestTenancy.OrgOf(user),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        var current = new FakeCurrentUser(user);
        var builder = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, current), current);
        var context = new AssistantToolContext(user, project.Id);

        var generic = new List<string>();
        foreach (string tool in AssistantApprovalCatalog.KnownTools.Where(n => n.StartsWith("Apply", StringComparison.Ordinal)))
        {
            ApprovalToolInfo info = AssistantApprovalCatalog.Describe(tool);
            ApprovalVerdict verdict = await builder.BuildAsync(
                tool, new Microsoft.Extensions.AI.AIFunctionArguments { ["projectId"] = project.Id.ToString(), ["name"] = "Probe" }, context, null, default);

            // A tool-specific case either writes its own card or refuses the call; the catch-all is the one that
            // answers with the catalog's generic title and nothing else.
            if (verdict.Card is { } card && card.Title == info.FallbackTitle && card.Summary is null && card.Changes is null)
                generic.Add(tool);
        }

        generic.Should().BeEmpty("a tool whose card is only its generic title lets a person approve blind");
    }

    [Fact]
    public void Only_the_chat_transport_asks_the_bridge_provider_has_no_gate_and_exposes_the_same_names()
    {
        using InferenceApiDbContext db = CreateDb();
        Guid user = Guid.NewGuid();
        AssistantToolProvider plain = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(user));
        AssistantToolProvider gated = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(user));
        gated.ApprovalGate = new RecordingGate();

        gated.GetTools(new AssistantToolContext(user)).Select(t => t.Name)
            .Should().Equal(plain.GetTools(new AssistantToolContext(user)).Select(t => t.Name));
        gated.GetTools(new AssistantToolContext(user)).Single(t => t.Name == "ApplyDeleteProject").Description
            .Should().Be(plain.GetTools(new AssistantToolContext(user)).Single(t => t.Name == "ApplyDeleteProject").Description,
                "wrapping never changes what the model is shown");
    }

    private sealed class RecordingGate : IAssistantApprovalGate
    {
        public void NoteProposal(string toolName, object? result) { }

        public Task<object?> RunAsync(string toolName, Microsoft.Extensions.AI.AIFunctionArguments args, Func<ValueTask<object?>> run, System.Threading.CancellationToken ct) =>
            Task.FromResult<object?>("{\"declined\":true}");
    }
}
