using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The permission cards of the outputs, uploads and revision tools: built from the database and the exact
/// arguments, in the agreed words, and refusing a call they cannot place.
/// </summary>
public sealed class ApprovalCardsP0Tests : IDisposable
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

    public ApprovalCardsP0Tests()
    {
        _project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = _user, OrganizationId = TestTenancy.OrgOf(_user) };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Promo film", ProjectId = _project.Id };
        _run = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = _project.Id, WorkflowDefinitionId = workflow.Id, Status = ExecutionStatus.Passed };
        _db.Projects.Add(_project);
        _db.WorkflowDefinitions.Add(workflow);
        _db.WorkflowExecutions.Add(_run);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private AssistantApprovalCardBuilder Builder(
        IOutputDescriber? outputs = null, ICompositionRevisionService? revisions = null,
        IWorkflowCostEstimator? costEstimator = null, long? importMaxBytes = null)
    {
        var caller = new Caller(_user);
        return new AssistantApprovalCardBuilder(
            _db, TestTenancy.Access(_db, caller), caller, costEstimator: costEstimator, outputs: outputs, revisions: revisions,
            importOptions: importMaxBytes is { } max ? Options.Create(new UrlImportOptions { MaxBytes = max }) : null);
    }

    private static AIFunctionArguments Args(params (string Key, object? Value)[] values)
    {
        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in values)
        {
            if (value is not null)
                args[key] = value is Guid g ? g.ToString() : value;
        }

        return args;
    }

    private Task<ApprovalVerdict> BuildAsync(AssistantApprovalCardBuilder builder, string tool, params (string Key, object? Value)[] args) =>
        builder.BuildAsync(tool, Args(args), new AssistantToolContext(_user, _project.Id), null, CancellationToken.None);

    // ------------------------------------------------------------------ the catalog

    [Theory]
    [InlineData("ApplySaveOutputAsFile", "Save a render to your files", "Saved to your files.", AssistantActionRisk.Normal)]
    [InlineData("ApplyUploadFile", "Add an uploaded file", "File added.", AssistantActionRisk.Normal)]
    [InlineData("ApplyImportFileFromUrl", "Import a file from a link", "File imported.", AssistantActionRisk.Normal)]
    [InlineData("ApplyReviseComposition", "Change this video", "Started.", AssistantActionRisk.Paid)]
    [InlineData("ApplyStepChange", "Change steps of this run", "Started.", AssistantActionRisk.Paid)]
    public void The_catalog_presents_each_new_write_in_the_agreed_words(string tool, string title, string done, string risk)
    {
        AssistantApprovalCatalog.IsGated(tool).Should().BeTrue();
        ApprovalToolInfo info = AssistantApprovalCatalog.Describe(tool);

        info.FallbackTitle.Should().Be(title);
        info.DoneText.Should().Be(done);
        info.Risk.Should().Be(risk);
    }

    // ------------------------------------------------------------------ save a render

    private static OutputRender Render(long size = 5_242_880) =>
        new(Guid.NewGuid(), "projects/x/outputFiles/y/promo.mp4", "promo.mp4", "video/mp4", size);

    [Fact]
    public async Task SaveOutput_names_the_render_its_size_the_project_and_the_target_path()
    {
        OutputRender render = Render();
        var describer = new Mock<IOutputDescriber>();
        describer.Setup(d => d.FindRenderAsync(_project.Id, render.StepResultId, It.IsAny<CancellationToken>())).ReturnsAsync(render);

        ApprovalVerdict verdict = await BuildAsync(
            Builder(describer.Object), "ApplySaveOutputAsFile",
            ("outputId", render.StepResultId), ("fileName", "teaser-cut.mp4"), ("directoryPath", "music"), ("confirmationToken", "t"));

        ApprovalCard card = verdict.Card!;
        card.Title.Should().Be("Save a render to your files");
        card.Summary.Should().Be(
            "Copy “promo.mp4” (5 MB) into project “Launch Teaser” as “music/teaser-cut.mp4”. "
            + "The copy counts toward your workspace storage; the original render is not changed.");
        card.Risk.Should().Be(AssistantActionRisk.Normal);
        card.Summary.Should().NotContain(render.StepResultId.ToString());
    }

    [Fact]
    public async Task SaveOutput_for_a_render_the_project_does_not_have_is_refused_without_a_card()
    {
        var describer = new Mock<IOutputDescriber>();
        describer.Setup(d => d.FindRenderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((OutputRender?)null);

        ApprovalVerdict verdict = await BuildAsync(Builder(describer.Object), "ApplySaveOutputAsFile", ("outputId", Guid.NewGuid()));

        verdict.Card.Should().BeNull();
        verdict.Refusal.Should().NotBeNull();
    }

    // ------------------------------------------------------------------ upload

    private static string UploadToken(Guid projectId, string fileName = "voice.wav", string? relativePath = "audio/voice.wav", long size = 1_258_291) =>
        AssistantUploadTools.PackUploadToken(new ReelBolt.Inference.Api.Services.Uploads.DirectUploadTicket(
            "mac", DirectUploadMode.SinglePut, projectId, Guid.NewGuid(), "k", fileName, relativePath, "audio/wav", size,
            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), "https://s3.example/put", null, 0, 0,
            new Dictionary<string, string>(), null, null, null, null));

    [Fact]
    public async Task Upload_names_the_file_its_size_the_folder_and_the_project_from_the_ticket()
    {
        ApprovalVerdict verdict = await BuildAsync(Builder(), "ApplyUploadFile", ("uploadToken", UploadToken(_project.Id)));

        verdict.Card!.Title.Should().Be("Add an uploaded file");
        verdict.Card.Summary.Should().Be("Add voice.wav (1.2 MB) to audio in Launch Teaser. It counts toward your storage.");
    }

    [Fact]
    public async Task Upload_to_the_root_says_so()
    {
        ApprovalVerdict verdict = await BuildAsync(Builder(), "ApplyUploadFile", ("uploadToken", UploadToken(_project.Id, relativePath: null)));

        verdict.Card!.Summary.Should().Contain("to the project root in Launch Teaser");
    }

    [Fact]
    public async Task Upload_with_a_ticket_for_another_project_or_no_ticket_is_refused()
    {
        (await BuildAsync(Builder(), "ApplyUploadFile", ("uploadToken", UploadToken(Guid.NewGuid())))).Card.Should().BeNull();
        (await BuildAsync(Builder(), "ApplyUploadFile", ("uploadToken", "not-a-ticket"))).Refusal.Should().NotBeNull();
    }

    [Fact]
    public async Task Upload_resolves_the_tickets_project_not_the_pages()
    {
        var other = new Project { Id = Guid.NewGuid(), Name = "Second Film", OwnerId = _user, OrganizationId = TestTenancy.OrgOf(_user) };
        _db.Projects.Add(other);
        _db.SaveChanges();

        // The page is on "Launch Teaser"; the ticket was issued for "Second Film".
        ApprovalVerdict verdict = await BuildAsync(Builder(), "ApplyUploadFile", ("uploadToken", UploadToken(other.Id)));

        verdict.Card.Should().NotBeNull();
        verdict.Card!.ProjectId.Should().Be(other.Id);
        verdict.Card.ProjectName.Should().Be("Second Film");
        verdict.Card.Summary.Should().Contain("in Second Film").And.NotContain("Launch Teaser");
    }

    [Fact]
    public async Task Upload_for_a_project_the_caller_cannot_see_is_refused()
    {
        var stranger = Guid.NewGuid();
        var foreign = new Project { Id = Guid.NewGuid(), Name = "Not Yours", OwnerId = stranger, OrganizationId = TestTenancy.OrgOf(stranger) };
        _db.Projects.Add(foreign);
        _db.SaveChanges();

        (await BuildAsync(Builder(), "ApplyUploadFile", ("uploadToken", UploadToken(foreign.Id)))).Card.Should().BeNull();
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_shows_the_host_only_never_the_path_or_the_query()
    {
        ApprovalVerdict verdict = await BuildAsync(
            Builder(), "ApplyImportFileFromUrl",
            ("url", "https://cdn.example.com/private/clip.mp4?X-Amz-Signature=secret"), ("directoryPath", "clips"), ("confirmationToken", "t"));

        verdict.Card!.Title.Should().Be("Import a file from a link");
        verdict.Card.Summary.Should().Be(
            "Download a file from https://cdn.example.com into clips/clip.mp4 in Launch Teaser (up to 512 MB; counts toward your storage).");
        verdict.Card.Summary.Should().NotContain("secret").And.NotContain("private");
    }

    [Theory]
    [InlineData(100L * 1024 * 1024, "up to 100 MB")]
    [InlineData(2048L * 1024 * 1024, "up to 2048 MB")]
    public async Task Import_shows_the_configured_size_cap_not_a_fixed_one(long maxBytes, string expected)
    {
        ApprovalVerdict verdict = await BuildAsync(
            Builder(importMaxBytes: maxBytes), "ApplyImportFileFromUrl", ("url", "https://cdn.example.com/clip.mp4"));

        verdict.Card!.Summary.Should().Contain(expected).And.NotContain("512 MB");
    }

    [Fact]
    public async Task Import_of_an_unusable_link_is_refused_without_a_card()
    {
        ApprovalVerdict verdict = await BuildAsync(Builder(), "ApplyImportFileFromUrl", ("url", "http://example.com/a.mp4"));

        verdict.Card.Should().BeNull();
        verdict.Refusal.Should().NotBeNull();
    }

    // ------------------------------------------------------------------ revise

    private static CompositionRevisionHistoryResponse History(bool canRevise = true, RevisionCostEstimateResponse? estimate = null) =>
        new(Guid.NewGuid(), "Available", canRevise, canRevise ? null : "not finished", null, [], [], [], estimate);

    private Mock<ICompositionRevisionService> Revisions(CompositionRevisionHistoryResponse? history)
    {
        var revisions = new Mock<ICompositionRevisionService>();
        revisions.Setup(r => r.GetHistoryAsync(It.IsAny<Project>(), _run.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>())).ReturnsAsync(history);
        return revisions;
    }

    [Fact]
    public async Task Revise_prices_the_card_through_the_revision_service_and_says_which_scenes()
    {
        var estimate = new RevisionCostEstimateResponse(true, 4, 6, 8, 100, true);
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History(estimate: estimate)).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Bigger intro title"), ("scenes", new[] { "Intro" }), ("confirmationToken", "t"));

        ApprovalCard card = verdict.Card!;
        card.Title.Should().Be("Change this video");
        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Summary.Should().Be(
            "Apply “Bigger intro title” to the scene “Intro” of “Promo film” in “Launch Teaser”. "
            + "This starts a new run and counts as one of your paid runs this hour.");
        card.Cost.Should().Contain("usually 4–6 credits").And.Contain("needs up to 8 credits");
        card.CostCredits.Should().Be(8, "the figure compared with the auto-approve limit is the required credits");
        card.CostUnestimated.Should().BeFalse();
        card.Changes.Should().BeNull("an affordable revision carries no warning");
    }

    [Fact]
    public async Task Revise_of_the_whole_film_says_so_and_warns_when_the_balance_is_short()
    {
        var estimate = new RevisionCostEstimateResponse(true, 4, 6, 8, 3, false);
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History(estimate: estimate)).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Make it warmer"), ("confirmationToken", "t"));

        verdict.Card!.Summary.Should().Contain("to the whole film of “Promo film”");
        verdict.Card.Changes!.Single().Text.Should().Contain("don't have enough credits").And.Contain("needs up to 8 available").And.Contain("you have 3, so 5 more are needed");
    }

    [Fact]
    public async Task Revise_shortfall_reads_exactly_in_the_unit_the_figures_are_shown()
    {
        // Required 25, balance 9.95: the card shows "needs up to 25" and "you have 9.9", so the shortfall is
        // 15.1. Rounding it to the nearest whole credit would read "15", a figure that does not add up.
        var estimate = new RevisionCostEstimateResponse(true, 4, 6, 25, 9.95, false);
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History(estimate: estimate)).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Make it warmer"), ("confirmationToken", "t"));

        verdict.Card!.Changes!.Single().Text.Should()
            .Contain("needs up to 25 available").And.Contain("you have 9.9").And.Contain("15.1 more are needed");
    }

    [Fact]
    public async Task Revise_cost_line_prints_the_usual_range_when_the_estimate_carries_no_typical()
    {
        var estimate = new RevisionCostEstimateResponse(true, 4, 6, 8, 100, true);
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History(estimate: estimate)).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Make it warmer"), ("confirmationToken", "t"));

        verdict.Card!.Cost.Should().Be("needs up to 8 credits (usually 4–6 credits)");
    }

    [Fact]
    public async Task Revise_with_billing_off_has_no_cost_line_and_is_not_marked_paid()
    {
        var estimate = new RevisionCostEstimateResponse(false, 0, 0, 0, null, true);
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History(estimate: estimate)).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Make it warmer"), ("confirmationToken", "t"));

        verdict.Card!.Cost.Should().BeNull();
        verdict.Card.Risk.Should().Be(AssistantActionRisk.Normal);
    }

    private sealed class BillingOn : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;
        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Revise_with_billing_on_but_no_estimate_says_cost_unavailable_and_stays_paid()
    {
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History(estimate: null)).Object, costEstimator: new BillingOn()), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Make it warmer"), ("confirmationToken", "t"));

        verdict.Card!.Risk.Should().Be(AssistantActionRisk.Paid);
        verdict.Card.Cost.Should().Be("Cost unavailable — counted as a paid run");
        verdict.Card.CostUnestimated.Should().BeTrue("auto mode must fail closed to a card when a paid revision has no figure");
        verdict.Card.CostCredits.Should().BeNull();
    }

    [Fact]
    public async Task Revise_with_billing_off_and_no_estimate_stays_free()
    {
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History(estimate: null)).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Make it warmer"), ("confirmationToken", "t"));

        verdict.Card!.Risk.Should().Be(AssistantActionRisk.Normal);
        verdict.Card.Cost.Should().BeNull();
    }

    [Fact]
    public async Task Revise_card_marks_a_truncated_instruction_with_its_full_length()
    {
        string instruction = new string('a', 3999) + "Z";
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History()).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", instruction), ("confirmationToken", "t"));

        verdict.Card!.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("first 400 of 4000 characters") && t.Contains("all 4000"));
    }

    [Fact]
    public async Task Revise_card_does_not_claim_truncation_for_a_short_instruction()
    {
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History()).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Make it warmer"), ("confirmationToken", "t"));

        verdict.Card!.Changes.Should().BeNull();
    }

    [Fact]
    public async Task Revise_card_lists_frame_ranges_the_base_version_and_a_non_default_cut_policy()
    {
        Guid versionId = Guid.NewGuid();
        CompositionRevisionHistoryResponse history = History() with { Versions = [Version(versionId, 3)] };
        var ranges = System.Text.Json.JsonDocument.Parse("[{\"fromFrame\":0,\"toFrame\":30},{\"fromFrame\":120,\"toFrame\":180}]").RootElement;

        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(history).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Tighten"), ("frameRanges", ranges),
            ("fromVersionId", versionId), ("cutPolicy", "ReEdit"), ("confirmationToken", "t"));

        string[] lines = verdict.Card!.Changes!.Select(c => c.Text).ToArray();
        lines.Should().Contain(t => t.Contains("0–30, 120–180"));
        lines.Should().Contain(t => t.Contains("version 3"));
        lines.Should().Contain(t => t.Contains("re-edit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>S-16 (round 2): the card lists every range the tool binds — PascalCase names and numeric strings
    /// included — and all of them up to the tool's cap of 20, not the first 12 exact-number ones.</summary>
    [Fact]
    public async Task Revise_card_lists_every_frame_range_the_tool_binds_including_FromFrame_string_numbers_and_13_plus()
    {
        var items = new List<string>
        {
            "{\"FromFrame\":0,\"ToFrame\":30}",
            "{\"fromFrame\":\"40\",\"toFrame\":\"50\"}",
        };
        for (int i = 2; i < 16; i++)
            items.Add($"{{\"fromFrame\":{i * 100},\"toFrame\":{i * 100 + 10}}}");
        var ranges = System.Text.Json.JsonDocument.Parse("[" + string.Join(",", items) + "]").RootElement;

        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History()).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Tighten"), ("frameRanges", ranges), ("confirmationToken", "t"));

        string line = verdict.Card!.Changes!.Select(c => c.Text).Single(t => t.StartsWith("Only these frames", StringComparison.Ordinal));
        line.Should().Contain("0–30, 40–50, 200–210");
        line.Should().Contain("1500–1510", "the 16th range is applied, so it is listed");
        line.Should().NotContain("more");
    }

    [Fact]
    public async Task Revise_card_lists_the_first_20_frame_ranges_and_counts_the_rest()
    {
        var items = Enumerable.Range(0, 23).Select(i => $"{{\"fromFrame\":{i * 10},\"toFrame\":{i * 10 + 5}}}");
        var ranges = System.Text.Json.JsonDocument.Parse("[" + string.Join(",", items) + "]").RootElement;

        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History()).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Tighten"), ("frameRanges", ranges), ("confirmationToken", "t"));

        string line = verdict.Card!.Changes!.Select(c => c.Text).Single(t => t.StartsWith("Only these frames", StringComparison.Ordinal));
        line.Should().Contain("190–195").And.NotContain("200–205");
        line.Should().Contain("and 3 more");
    }

    [Fact]
    public async Task Revise_card_says_nothing_about_the_cut_policy_or_base_version_at_their_defaults()
    {
        ApprovalVerdict verdict = await BuildAsync(
            Builder(revisions: Revisions(History()).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "Tighten"), ("cutPolicy", "KeepCut"), ("confirmationToken", "t"));

        verdict.Card!.Changes.Should().BeNull();
    }

    private static CompositionVersionResponse Version(Guid id, int number) =>
        new(id, number, null, null, DateTime.UtcNow, null, "Revision", null, null, null, true, null, null, null, null, null, 0);

    [Fact]
    public async Task Revise_of_a_run_that_cannot_be_changed_or_is_not_in_the_project_is_refused()
    {
        (await BuildAsync(Builder(revisions: Revisions(History(canRevise: false)).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "x"))).Card.Should().BeNull();
        (await BuildAsync(Builder(revisions: Revisions(null).Object), "ApplyReviseComposition",
            ("executionId", _run.Id), ("instruction", "x"))).Refusal.Should().NotBeNull();
    }
}
