using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The paid approval surfaces (the assistant's permission cards, the graphic and revision estimates) price
/// exactly what the execute path's preflight requires: the same steps, the same resolution (as the tool's
/// <c>RunStyleResolver</c> produces it) and the same organization, and the card never shows less than the
/// preflight's <c>RequiredMillis</c> — the figure is rounded UP at the shown precision.
/// </summary>
public sealed class ApprovalCardPreflightParityTests
{
    /// <summary>Yesterday: inside the estimator's history window, whatever day the suite runs on.</summary>
    private static readonly DateTime Past = DateTime.UtcNow.AddDays(-1);

    private static readonly Regex NeedsFigure = new(@"needs (\d+(?:\.\d+)?) ", RegexOptions.Compiled);
    private static readonly Regex NeedsUpToFigure = new(@"needs up to (\d+(?:\.\d+)?) ", RegexOptions.Compiled);

    // ------------------------------------------------------------------ the copy

    [Theory]
    [InlineData(4.94, "5")]
    [InlineData(12.4, "13")]
    [InlineData(0.3, "0.3")]
    [InlineData(2.21, "2.3")]
    [InlineData(9.96, "10")]
    [InlineData(10.0, "10")]
    [InlineData(160.0, "160")]
    public void CreditCostWording_Up_rounds_up_at_the_shown_precision(double credits, string expected) =>
        CreditCostWording.Up(credits).Should().Be(expected);

    [Fact]
    public void CreditCostWording_RunNeeds_says_the_hold_the_typical_cost_and_what_is_missing()
    {
        CreditCostWording.RunNeeds(250, 400, 788, 0, 286).Should().Be("needs up to 788 credits (usually about 286)");
        CreditCostWording.RunNeeds(250, 400, 788, 0, 286, balance: 500).Should().Be(
            "needs up to 788 credits (usually about 286); the workspace has 500, so 288 more are needed");
        CreditCostWording.RunNeeds(250, 400, 788, 0, 0, balance: 900).Should().Be("needs up to 788 credits (usually 250–400 credits)");
        CreditCostWording.RunNeeds(0, 0, 788, 0, 0).Should().Be("needs up to 788 credits");
        CreditCostWording.RunNeeds(0, 0, 0, 0, 0).Should().BeNull();
    }

    [Fact]
    public void CreditCostWording_RunNeeds_figure_is_never_below_required()
    {
        for (long millis = 1; millis <= 50_000; millis += 7)
        {
            double required = millis / 1000.0;
            string cost = CreditCostWording.RunNeeds(8, 12, required, 160, 10)!;
            decimal.Parse(NeedsUpToFigure.Match(cost).Groups[1].Value, CultureInfo.InvariantCulture)
                .Should().BeGreaterThanOrEqualTo(millis / 1000m, cost);
        }
    }

    [Theory]
    [InlineData(8.04, "8")]
    [InlineData(12.4, "12")]
    [InlineData(4.94, "4.9")]
    public void CreditCostWording_Near_rounds_to_nearest_for_ranges(double credits, string expected) =>
        CreditCostWording.Near(credits).Should().Be(expected);

    [Fact]
    public void CreditCostWording_range_with_required() =>
        CreditCostWording.RunCost(8, 12, 15.2, 0).Should().Be("usually 8–12 credits (needs 16 available to start; can cost more)");

    [Fact]
    public void CreditCostWording_single_figure_when_low_is_zero_or_rounds_like_high()
    {
        CreditCostWording.RunCost(0, 12, 12.4, 0).Should().Be("usually 12 credits (needs 13 available to start; can cost more)");
        CreditCostWording.RunCost(12.2, 12.4, 12.4, 0).Should().Be("usually 12 credits (needs 13 available to start; can cost more)");
    }

    [Fact]
    public void CreditCostWording_says_credit_for_a_figure_shown_as_one()
    {
        CreditCostWording.RunCost(0, 1, 31, 0).Should().Be("usually 1 credit (needs 31 available to start; can cost more)");
        CreditCostWording.RunCost(0, 0, 0, 1).Should().Be("up to 1 credit for generated video");
        CreditCostWording.RunCost(0, 0, 0.95, 0).Should().Be("needs 1 credit available to start; can cost more");
        CreditCostWording.RunCost(0, 0, 1.01, 0).Should().Be("needs 1.1 credits available to start; can cost more");
    }

    [Fact]
    public void CreditCostWording_range_without_required() =>
        CreditCostWording.RunCost(8, 12, 0, 0).Should().Be("usually 8–12 credits");

    [Fact]
    public void CreditCostWording_range_with_required_and_generated_video() =>
        CreditCostWording.RunCost(8, 12, 15.2, 160).Should().Be(
            "usually 8–12 credits (needs 16 available to start; can cost more), plus up to 160 for generated video");

    [Fact]
    public void CreditCostWording_generated_video_only()
    {
        CreditCostWording.RunCost(0, 0, 160.2, 160).Should().Be("up to 160 credits for generated video (needs 161 available to start)");
        CreditCostWording.RunCost(0, 0, 0, 160).Should().Be("up to 160 credits for generated video");
    }

    [Fact]
    public void CreditCostWording_required_only() =>
        CreditCostWording.RunCost(0, 0, 3.21, 0).Should().Be("needs 3.3 credits available to start; can cost more");

    [Fact]
    public void CreditCostWording_nothing_to_say() =>
        CreditCostWording.RunCost(0, 0, 0, 0).Should().BeNull();

    // QA (T-005): the edges of Up — exact precision boundaries, the 10-credit switch, binary noise, huge figures.
    [Theory]
    [InlineData(9.91, "10")]
    [InlineData(4.31, "4.4")]
    [InlineData(9.9, "9.9")]
    [InlineData(9.999, "10")]
    [InlineData(10.001, "11")]
    [InlineData(0.001, "0.1")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.0, "1")]
    [InlineData(99.999, "100")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(999_999_999_999_999.0, "999999999999999")]
    [InlineData(99_999_999_999_998.5, "99999999999999")]
    public void CreditCostWording_Up_edge_values(double credits, string expected) =>
        CreditCostWording.Up(credits).Should().Be(expected);

    [Fact]
    public void CreditCostWording_Up_ignores_binary_noise_on_an_exact_figure()
    {
        CreditCostWording.Up(0.1 + 0.2).Should().Be("0.3");
        CreditCostWording.Up(0.7 + 0.2 + 0.1).Should().Be("1");
        CreditCostWording.Up(3 * 1.1).Should().Be("3.3");
        CreditCostWording.Up(12_400 / 1000.0).Should().Be("13");
        CreditCostWording.Up(9_910 / 1000.0).Should().Be("10");
    }

    /// <summary>
    /// Property, over every millicredit amount up to 200 credits and a spread of large ones: the "needs"
    /// figure Up shows is never below the amount (exact decimal comparison, no float tolerance), and is at
    /// most one shown step above it (0.1 below 10 credits, 1 from 10).
    /// </summary>
    [Fact]
    public void CreditCostWording_Up_is_never_below_and_at_most_one_step_above_any_millicredit_amount()
    {
        IEnumerable<long> amounts = Enumerable.Range(1, 200_000).Select(m => (long)m)
            .Concat([999_999, 1_000_001, 123_456_789, 9_999_999_999, 4_503_599_627_370_495]);
        foreach (long millis in amounts)
        {
            decimal exact = millis / 1000m;
            string shown = CreditCostWording.Up(WorkflowsControllerToCredits(millis));
            decimal figure = decimal.Parse(shown, CultureInfo.InvariantCulture);
            decimal step = exact < 10 ? 0.1m : 1m;
            figure.Should().BeGreaterThanOrEqualTo(exact, $"{millis} millicredits may never be shown as {shown}");
            (figure - exact).Should().BeLessThan(step, $"{millis} millicredits shown as {shown} is more than one step up");
        }
    }

    /// <summary>Property: whichever branch RunCost takes, the figure after "needs " is never below the required amount.</summary>
    [Fact]
    public void CreditCostWording_RunCost_needs_figure_is_never_below_required_on_any_branch()
    {
        (double Low, double High, double FixedMax)[] shapes = [(8, 12, 0), (0, 12, 0), (8, 12, 160), (0, 0, 160), (0, 0, 0)];
        for (long millis = 1; millis <= 50_000; millis += 7)
        {
            double required = millis / 1000.0;
            foreach ((double low, double high, double fixedMax) in shapes)
            {
                string cost = CreditCostWording.RunCost(low, high, required, fixedMax)!;
                System.Text.RegularExpressions.Match needs = NeedsFigure.Match(cost);
                needs.Success.Should().BeTrue($"\"{cost}\" must say what the run needs");
                decimal.Parse(needs.Groups[1].Value, CultureInfo.InvariantCulture)
                    .Should().BeGreaterThanOrEqualTo(millis / 1000m, cost);
            }
        }
    }

    /// <summary>The cost line the card must show for a priced run: the same figures the preflight verdict carries.</summary>
    private static string? ExpectedRunNeeds(WorkflowPreflightResult priced)
    {
        WorkflowCostEstimate e = priced.Estimate;
        return CreditCostWording.RunNeeds(
            e.LowMillis / 1000.0, e.HighMillis / 1000.0, e.RequiredMillis / 1000.0, e.FixedMaxMillis / 1000.0,
            e.HasHistory ? e.TypicalMillis / 1000.0 : 0,
            priced.Refused ? priced.BalanceMillis / 1000.0 : null);
    }

    private static double WorkflowsControllerToCredits(long millis) =>
        ReelBolt.Inference.Api.Controllers.WorkflowsController.ToCredits(millis);

    // ------------------------------------------------------------------ the run card

    [Theory]
    [InlineData("cold start")]
    [InlineData("p90 above p75")]
    [InlineData("4k")]
    [InlineData("makes clips")]
    public async Task RunCard_ShowsPreflightRequired_RoundedUp(string scenario)
    {
        using RunFixture f = new();
        WorkflowDefinition workflow;
        string? resolution = null;
        int? shortSide = null;
        bool? regenerate = null;
        switch (scenario)
        {
            case "cold start":
                workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent), (StepType.Agent, AgentType.AuthorAgent));
                break;
            case "p90 above p75":
                workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent));
                SeedHistory(f.Db, f.Org, StepType.Agent, workflow.Steps.Single().AgentDefinitionId,
                    [1_000, 1_000, 1_000, 1_000, 1_000, 1_000, 1_000, 1_000, 30_000, 40_000]);
                break;
            case "4k":
                workflow = f.Workflow((StepType.Agent, AgentType.AuthorAgent), (StepType.VideoCompile, AgentType.DirectorAgent));
                resolution = "4k";
                shortSide = 2160;
                break;
            default:
                workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent), (StepType.VideoGenerate, AgentType.DirectorAgent));
                regenerate = true;
                break;
        }
        await f.Db.SaveChangesAsync();

        WorkflowPreflightResult priced = (await f.Price(workflow.Id, shortSide))!;
        double required = priced.Estimate.RequiredMillis / 1000.0;
        required.Should().BePositive("a run that holds nothing cannot show what it needs");

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool,
            Args(("workflowId", workflow.Id), ("outputResolution", resolution), ("regenerateVideoClips", regenerate)),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Cost.Should().Contain($"needs up to {CreditCostWording.Up(required)} ");
        double shown = double.Parse(NeedsUpToFigure.Match(card.Cost!).Groups[1].Value, CultureInfo.InvariantCulture);
        shown.Should().BeGreaterThanOrEqualTo(required, "the card may never ask for less than the preflight requires");
        card.Risk.Should().Be(AssistantActionRisk.Paid);
    }

    [Fact]
    public async Task RunCard_RequiredWithAFraction_RoundsUp()
    {
        using RunFixture f = new(new FixedEstimator(lowMillis: 8_000, highMillis: 12_000, requiredMillis: 12_400));
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id)),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Cost.Should().Be("needs up to 13 credits (usually 8–12 credits)");
    }

    [Fact]
    public async Task RunCard_ForAnUnaffordableRun_SaysHowManyCreditsAreMissing()
    {
        using RunFixture f = new();
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();
        WorkflowPreflightResult priced = (await f.Price(workflow.Id, null))!;
        priced.Refused.Should().BeTrue("precondition: the fixture's workspace cannot cover the run");

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id)),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        double required = priced.Estimate.RequiredMillis / 1000.0;
        double missing = CreditShortfallMessage.MissingCreditsFromCredits(required, priced.BalanceMillis / 1000.0);
        card.Cost.Should().StartWith($"needs up to {CreditCostWording.Up(required)} credits")
            .And.EndWith($"so {missing:0.#} more are needed");
    }

    [Fact]
    public async Task RunCard_PricesTheSameResolutionAsTheTool()
    {
        using RunFixture f = new();
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.AuthorAgent), (StepType.VideoCompile, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();
        AssistantApprovalCardBuilder builder = f.Builder();
        var context = new AssistantToolContext(f.User, f.Project.Id);

        WorkflowPreflightResult at4kPriced = (await f.Price(workflow.Id, 2160))!;
        WorkflowCostEstimate at4k = at4kPriced.Estimate;
        ApprovalCard card = (await builder.BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputResolution", "4k")),
            context, null, CancellationToken.None)).Card!;

        card.Cost.Should().Be(ExpectedRunNeeds(at4kPriced));
        card.Changes!.Select(c => c.Text).Should().Contain("Resolution: 4K (2160p)");

        ApprovalCard refused = (await builder.BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputResolution", "8k")),
            context, null, CancellationToken.None)).Card!;

        refused.Cost.Should().BeNull("the tool refuses this run before anything is spent");
        refused.Changes!.Select(c => c.Text).Should().Contain(t => t.StartsWith("This run will not start:", StringComparison.Ordinal));
        refused.Changes!.Select(c => c.Text).Should().NotContain(t => t.StartsWith("Resolution:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCard_WithABrief_PricesItsResolutionLikeTheLegacyArgumentAndListsEveryFieldSet()
    {
        using RunFixture f = new();
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.AuthorAgent), (StepType.VideoCompile, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();
        AssistantApprovalCardBuilder builder = f.Builder();
        var context = new AssistantToolContext(f.User, f.Project.Id);
        static JsonElement Brief(string json) => JsonDocument.Parse(json).RootElement.Clone();

        ApprovalCard legacy = (await builder.BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputResolution", "4k")),
            context, null, CancellationToken.None)).Card!;
        ApprovalCard viaBrief = (await builder.BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool,
            Args(("workflowId", workflow.Id), ("brief", Brief(
                """{"resolution":"2160p","language":"it","lengthSec":45,"tone":"Calm","stylePreset":"fast-social-reel","designSystem":"none","voice":{"mode":"narrated"},"notes":"a launch film"}"""))),
            context, null, CancellationToken.None)).Card!;

        viaBrief.Cost.Should().Be(legacy.Cost, "a brief's resolution is priced exactly like the legacy argument");
        string[] lines = viaBrief.Changes!.Select(c => c.Text).ToArray();
        lines.Should().Contain("Resolution: 4K (2160p)");
        lines.Should().Contain("Language: it").And.Contain("Target length: 45 seconds").And.Contain("Tone: Calm");
        lines.Should().Contain(t => t.StartsWith("Editing style:", StringComparison.Ordinal));
        lines.Should().Contain(t => t.StartsWith("Brand: runs without", StringComparison.Ordinal));
        lines.Should().Contain("Voice: narrated.");
        lines.Should().Contain(t => t.StartsWith("Your request:", StringComparison.Ordinal) && t.Contains("a launch film"));

        ApprovalCard bad = (await builder.BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool,
            Args(("workflowId", workflow.Id), ("brief", Brief("""{"resolution":"8k"}"""))), context, null, CancellationToken.None)).Card!;
        bad.Cost.Should().BeNull();
        bad.Changes!.Select(c => c.Text).Should().Contain(t => t.StartsWith("This run will not start:", StringComparison.Ordinal));

        ApprovalCard unknown = (await builder.BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool,
            Args(("workflowId", workflow.Id), ("brief", Brief("""{"langauge":"it"}"""))), context, null, CancellationToken.None)).Card!;
        unknown.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("not recognised"));
    }

    [Fact]
    public async Task RunCard_BadStyle_SaysItWillNotStart()
    {
        using RunFixture f = new();
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("stylePresetKey", "no-such-preset")),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Cost.Should().BeNull();
        card.Changes!.Select(c => c.Text).Should().Contain("This run will not start: Unknown style preset 'no-such-preset'.");
    }

    // QA (T-005): spellings the tool's resolver normalises price exactly what the tool's preflight prices,
    // with a real estimator and preflight, on a workflow whose price depends on the resolution.
    [Theory]
    [InlineData(" 4K ", 2160)]
    [InlineData("2160P", 2160)]
    [InlineData("2160", 2160)]
    [InlineData("720", 720)]
    [InlineData("1080p", 1080)]
    [InlineData("AUTO", null)]
    [InlineData("", null)]
    public async Task RunCard_ResolverNormalisedResolution_PricesWhatTheToolPrices(string asked, int? resolved)
    {
        using RunFixture f = new();
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.AuthorAgent), (StepType.VideoCompile, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();
        WorkflowPreflightResult expectedPriced = (await f.Price(workflow.Id, resolved))!;
        WorkflowCostEstimate expected = expectedPriced.Estimate;
        WorkflowCostEstimate auto = (await f.Price(workflow.Id, null))!.Estimate;
        if (resolved == 2160)
            expected.RequiredMillis.Should().BeGreaterThan(auto.RequiredMillis, "precondition: the resolution changes the price");

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputResolution", asked)),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Cost.Should().Be(ExpectedRunNeeds(expectedPriced));
        IEnumerable<string> lines = (card.Changes ?? []).Select(c => c.Text);
        lines.Should().NotContain(t => t.StartsWith("This run will not start:", StringComparison.Ordinal));
        if (resolved is { } shortSide)
            lines.Should().Contain($"Resolution: {ReelBolt.Shared.Styles.OutputResolution.DisplayName(shortSide)}");
        else
            lines.Should().NotContain(t => t.StartsWith("Resolution:", StringComparison.Ordinal));
    }

    // QA (T-005): a refused output format is a refusal too — no cost, the not-start line.
    [Fact]
    public async Task RunCard_BadFormat_SaysItWillNotStart()
    {
        using RunFixture f = new();
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputFormat", "Panoramic")),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Cost.Should().BeNull();
        card.Changes!.Select(c => c.Text).Should().Contain(t => t.StartsWith("This run will not start: Unknown output format", StringComparison.Ordinal));
    }

    // QA (T-005): the card reads the PREFLIGHT's estimate (what the execute path checks), not the display
    // estimate — an estimator whose two answers differ shows which one the card follows.
    [Fact]
    public async Task RunCard_FollowsThePreflight_NotTheDisplayEstimate()
    {
        using RunFixture f = new(new SplitEstimator(display: new(true, true, 8_000, 12_000, 12_000, []), preflight: new(true, true, 8_000, 12_000, 31_200, [])));
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputResolution", "4k")),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Cost.Should().Be("needs up to 32 credits (usually 8–12 credits)");
    }

    // ------------------------------------------------------------------ a preflight that could not be evaluated (SEC-EF-1)

    private const string RunCostUnknown = "the credits this run uses could not be worked out in advance";
    private const string CostUnknown = "the credits this uses could not be worked out in advance";

    [Fact]
    public async Task Preflight_EstimateThrows_AllowsTheRun_ButSaysItWasNotEvaluated()
    {
        WorkflowCostEstimator estimator = new(
            new ThrowingScopes(), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);

        // A null step makes the estimate itself throw (any failure out of the estimate takes this path).
        WorkflowPreflightResult result = await estimator.PreflightAsync(Guid.NewGuid(), [null!], CancellationToken.None);

        result.Outcome.Should().Be(WorkflowPreflightOutcome.Allowed, "the execute path still fails open");
        result.Evaluated.Should().BeFalse();
    }

    [Fact]
    public async Task Preflight_BalanceReadThrows_KeepsTheComputedEstimate()
    {
        using InferenceApiDbContext noBalance = UnreadableContext<ReelBolt.Shared.Data.Models.Billing.CreditAccount>();
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(noBalance), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Guid.NewGuid(), MaxIterations = 1 };

        WorkflowPreflightResult result = await estimator.PreflightAsync(Guid.NewGuid(), [step], CancellationToken.None);

        result.Outcome.Should().Be(WorkflowPreflightOutcome.Allowed, "the execute path still fails open");
        result.Evaluated.Should().BeFalse();
        result.Estimate.BillingEnabled.Should().BeTrue();
        result.Estimate.RequiredMillis.Should().BePositive("the estimate was computed; only the balance could not be read");
    }

    [Fact]
    public async Task Preflight_NoOrganization_IsEvaluated()
    {
        WorkflowCostEstimator estimator = new(
            new ThrowingScopes(), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);

        WorkflowPreflightResult result = await estimator.PreflightAsync(null, [], CancellationToken.None);

        result.Evaluated.Should().BeTrue("no organization is an answer (nothing is charged), not a failure");
    }

    [Fact]
    public async Task RunCard_PreflightNotEvaluated_StaysPaid_AndSaysTheCostIsUnknown()
    {
        using RunFixture f = new(new NotEvaluatedEstimator());
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id)),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Cost.Should().Be(RunCostUnknown);
    }

    [Fact]
    public async Task RunCard_BalanceReadThrows_StillShowsTheRealFigures()
    {
        using InferenceApiDbContext noBalance = UnreadableContext<ReelBolt.Shared.Data.Models.Billing.CreditAccount>();
        using RunFixture f = new(new WorkflowCostEstimator(
            new SingleContextScopes(noBalance), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance));
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent), (StepType.Agent, AgentType.AuthorAgent));
        await f.Db.SaveChangesAsync();
        WorkflowPreflightResult estimatePriced = (await f.Price(workflow.Id, null))!;
        WorkflowCostEstimate estimate = estimatePriced.Estimate;
        estimate.RequiredMillis.Should().BePositive("precondition: the estimate survives a failed balance read");

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id)),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Cost.Should().Be(ExpectedRunNeeds(estimatePriced));
        card.Cost.Should().Contain("needs up to ");
    }

    [Fact]
    public async Task RunCard_BillingOff_ShowsNoCost_AndIsNormal()
    {
        using RunFixture f = new(new WorkflowCostEstimator(
            new ThrowingScopes(), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = false }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance));
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id)),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Normal);
        card.Cost.Should().BeNull();
    }

    // ------------------------------------------------------------------ agent types unreadable (QA-EF-11)

    [Fact]
    public async Task Preflight_AgentTypeReadThrows_AllowsTheRun_ButSaysItWasNotEvaluated()
    {
        using InferenceApiDbContext unreadable = UnreadableAgentsContext();
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(unreadable), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);
        // An Author step known only by id: whether it renders the 4K output depends on the type read that fails.
        WorkflowStep author = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Guid.NewGuid(), MaxIterations = 1 };

        WorkflowPreflightResult result = await estimator.PreflightAsync(Guid.NewGuid(), [author], 2160, CancellationToken.None);

        result.Outcome.Should().Be(WorkflowPreflightOutcome.Allowed, "the execute path still fails open");
        result.Evaluated.Should().BeFalse("a figure that may miss a render step's scaling is not an answer");
        result.Estimate.Should().BeSameAs(WorkflowCostEstimate.Disabled);
        (await estimator.EstimateAsync(Guid.NewGuid(), [author], 2160, CancellationToken.None))
            .Should().BeSameAs(WorkflowCostEstimate.Disabled, "no surface may show a needs figure below the engine's hold");
    }

    [Fact]
    public async Task Preflight_HistoryReadThrows_StaysEvaluated()
    {
        using InferenceApiDbContext noHistory = UnreadableContext<UsageEvent>();
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(noHistory), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);
        WorkflowStep author = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Guid.NewGuid(), MaxIterations = 1 };

        WorkflowPreflightResult result = await estimator.PreflightAsync(Guid.NewGuid(), [author], 2160, CancellationToken.None);

        result.Evaluated.Should().BeTrue("only an unreadable agent type makes the estimate unknown; history falls back to cold start");
        result.Estimate.RequiredMillis.Should().BePositive();
    }

    [Fact]
    public async Task Preflight_AgentTypesReadable_KeepsTheScaledFigures()
    {
        using RunFixture f = new();
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.AuthorAgent));
        await f.Db.SaveChangesAsync();

        WorkflowPreflightResult at1080 = (await f.Price(workflow.Id, 1080))!;
        WorkflowPreflightResult at4k = (await f.Price(workflow.Id, 2160))!;

        at4k.Evaluated.Should().BeTrue();
        at4k.Estimate.RequiredMillis.Should().BeGreaterThan(at1080.Estimate.RequiredMillis, "the Author renders the output, so 4K holds more");
    }

    [Fact]
    public async Task RunCard_AgentTypeReadThrows_StaysPaid_AndSaysTheCostIsUnknown()
    {
        using InferenceApiDbContext unreadable = UnreadableAgentsContext();
        using RunFixture f = new(new WorkflowCostEstimator(
            new SingleContextScopes(unreadable), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance));
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.AuthorAgent));
        await f.Db.SaveChangesAsync();

        ApprovalCard card = (await f.Builder().BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputResolution", "4k")),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Cost.Should().Be(RunCostUnknown);
    }

    // ------------------------------------------------------------------ the plan's resolution gate (QA-EF-6)

    [Fact]
    public async Task RunCard_PlanRefusesTheResolution_SaysItWillNotStart()
    {
        using RunFixture f = new();
        WorkflowDefinition workflow = f.Workflow((StepType.Agent, AgentType.AuthorAgent), (StepType.VideoCompile, AgentType.DirectorAgent));
        await f.Db.SaveChangesAsync();
        Mock<IPlanGateService> plan = new();
        plan.Setup(p => p.CheckResolutionAsync(It.IsAny<Guid>(), 2160, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Billing.PlanGateViolation("PLAN_RESOLUTION", "resolution", "Your plan renders up to 1080p.", 403));
        AssistantApprovalCardBuilder builder = f.Builder(plan.Object);
        var context = new AssistantToolContext(f.User, f.Project.Id);

        ApprovalCard refused = (await builder.BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputResolution", "4k")),
            context, null, CancellationToken.None)).Card!;

        refused.Changes!.Select(c => c.Text).Should().Contain("This run will not start: Your plan renders up to 1080p.");
        refused.Changes!.Select(c => c.Text).Should().NotContain(t => t.StartsWith("Resolution:", StringComparison.Ordinal));
        refused.Cost.Should().BeNull("the tool refuses this run before anything is spent");
        plan.Verify(p => p.CheckResolutionAsync(f.Project.OrganizationId, 2160, It.IsAny<CancellationToken>()), Times.Once);

        ApprovalCard allowed = (await builder.BuildAsync(
            AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("outputResolution", "1080p")),
            context, null, CancellationToken.None)).Card!;

        allowed.Cost.Should().Contain("needs ");
        allowed.Changes!.Select(c => c.Text).Should().Contain($"Resolution: {ReelBolt.Shared.Styles.OutputResolution.DisplayName(1080)}");
    }

    // ------------------------------------------------------------------ designed graphics

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GraphicEstimate_PricesTheRealStep(bool hiddenWorkflowPresent)
    {
        using GraphicFixture f = new();
        // The designer's own runs are cheap; another agent's DesignGraphic runs are dear. A synthetic step
        // with no agent pools both, the real hidden step reads the designer's exact bucket.
        SeedHistory(f.Db, f.Org, StepType.DesignGraphic, f.DesignerId, [.. Enumerable.Repeat(1_000L, 10)]);
        SeedHistory(f.Db, f.Org, StepType.DesignGraphic, Guid.NewGuid(), [.. Enumerable.Repeat(60_000L, 10)]);
        await f.Db.SaveChangesAsync();

        if (hiddenWorkflowPresent)
            (await f.Request()).StatusCode.Should().Be(402);

        ImageGraphicEstimateDto estimate = (await f.Service.EstimateAsync(f.Org, f.Project.Id, CancellationToken.None))!;
        if (!hiddenWorkflowPresent)
        {
            (await f.Db.WorkflowDefinitions.AnyAsync(w => w.Kind == WorkflowDefinitionKind.GraphicDesign))
                .Should().BeFalse("an estimate never creates the hidden workflow");
        }

        GraphicDesignRequestResult refused = await f.Request();
        refused.StatusCode.Should().Be(402, "an empty workspace cannot afford it, so the preflight reports what it requires");
        double required = (double)refused.Extra!["required"]!;
        required.Should().BePositive();
        estimate.RequiredCredits.Should().Be(required);
    }

    [Fact]
    public async Task GraphicCard_ShowsRequired()
    {
        using GraphicFixture f = new();
        ImageGraphicEstimateDto estimate = (await f.Service.EstimateAsync(f.Org, f.Project.Id, CancellationToken.None))!;
        estimate.RequiredCredits.Should().BePositive();

        var user = new Caller(f.User);
        var builder = new AssistantApprovalCardBuilder(
            f.Db, TestTenancy.Access(f.Db, user), user, f.Estimator, graphics: f.Service);
        ApprovalCard card = (await builder.BuildAsync(
            "ApplyDesignGraphic", Args(("documentId", f.Document.Id), ("description", "A red SALE badge"), ("confirmationToken", "t")),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Cost.Should().Contain($"needs {CreditCostWording.Up(estimate.RequiredCredits)} available to start");
        card.Cost.Should().NotContain("run");
        card.CostCredits.Should().Be(estimate.RequiredCredits, "auto mode compares the graphic's required credits with the limit");
        card.CostUnestimated.Should().BeFalse();
    }

    // SEC-EF-4: an earlier proposal's figures may be stale (history moved since), so they are never shown.
    [Fact]
    public async Task GraphicCard_WithoutTheService_DoesNotShowTheProposalsFigures()
    {
        using GraphicFixture f = new();
        var user = new Caller(f.User);
        var builder = new AssistantApprovalCardBuilder(f.Db, TestTenancy.Access(f.Db, user), user);
        var proposal = new ProposalRecord("ProposeDesignGraphic", JsonDocument.Parse(
            """{"estimatedCost":{"credits":3.1,"lowCredits":1.2,"highCredits":3.1,"requiredCredits":4.42},"confirmationToken":"t"}""").RootElement.Clone());

        ApprovalCard card = (await builder.BuildAsync(
            "ApplyDesignGraphic", Args(("documentId", f.Document.Id), ("confirmationToken", "t")),
            new AssistantToolContext(f.User, f.Project.Id), proposal, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Cost.Should().Be(CostUnknown);
        card.CostCredits.Should().BeNull();
        card.CostUnestimated.Should().BeTrue("a graphic with no figure fails closed to a card in auto mode");
    }

    [Fact]
    public async Task GraphicCard_FreshEstimateUnavailable_SaysTheCostIsUnknown_NotTheProposalsFigures()
    {
        using GraphicFixture f = new();
        var user = new Caller(f.User);
        Mock<IGraphicDesignService> graphics = new();
        var builder = new AssistantApprovalCardBuilder(f.Db, TestTenancy.Access(f.Db, user), user, f.Estimator, graphics: graphics.Object);
        var proposal = new ProposalRecord("ProposeDesignGraphic", JsonDocument.Parse(
            """{"estimatedCost":{"credits":3.1,"lowCredits":1.2,"highCredits":3.1,"requiredCredits":4.42},"confirmationToken":"t"}""").RootElement.Clone());

        ApprovalCard card = (await builder.BuildAsync(
            "ApplyDesignGraphic", Args(("documentId", f.Document.Id), ("confirmationToken", "t")),
            new AssistantToolContext(f.User, f.Project.Id), proposal, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Cost.Should().Be(CostUnknown);
        card.CostCredits.Should().BeNull();
        card.CostUnestimated.Should().BeTrue("a graphic with no figure fails closed to a card in auto mode");
    }

    // SEC-EF-3: a project the caller cannot reach never reaches the estimate.
    [Fact]
    public async Task GraphicCard_UnreachableProject_IsPricedWithoutAProject()
    {
        using GraphicFixture f = new();
        var user = new Caller(f.User);
        Mock<IGraphicDesignService> graphics = new();
        var builder = new AssistantApprovalCardBuilder(f.Db, TestTenancy.Access(f.Db, user), user, f.Estimator, graphics: graphics.Object);

        await builder.BuildAsync(
            "ApplyDesignGraphic", Args(("documentId", f.Document.Id), ("confirmationToken", "t")),
            new AssistantToolContext(f.User, Guid.NewGuid()), null, CancellationToken.None);

        graphics.Verify(g => g.EstimateAsync(It.IsAny<Guid?>(), null, It.IsAny<CancellationToken>()), Times.Once);
        graphics.Verify(g => g.EstimateAsync(It.IsAny<Guid?>(), It.IsNotNull<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GraphicCard_BillingOff_IsNormal()
    {
        using GraphicFixture f = new();
        var user = new Caller(f.User);
        WorkflowCostEstimator off = new(
            new ThrowingScopes(), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = false }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);
        var builder = new AssistantApprovalCardBuilder(f.Db, TestTenancy.Access(f.Db, user), user, off, graphics: new Mock<IGraphicDesignService>().Object);

        ApprovalCard card = (await builder.BuildAsync(
            "ApplyDesignGraphic", Args(("documentId", f.Document.Id), ("confirmationToken", "t")),
            new AssistantToolContext(f.User, f.Project.Id), null, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Normal);
        card.Cost.Should().BeNull();
    }

    [Fact]
    public async Task GraphicEstimate_BalanceReadThrows_IsUnavailable()
    {
        using GraphicFixture f = new();
        WorkflowCostEstimator throwing = new(
            new ThrowingScopes(), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);
        GraphicDesignService service = new(
            f.Db,
            new WorkflowExecutionService(f.Db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            new FakeFileStorage(),
            new ConfigurationBuilder().Build(),
            NullLogger<GraphicDesignService>.Instance,
            throwing);

        // No balance to compare: the estimate must not claim "not affordable" against a balance of zero.
        (await service.EstimateAsync(f.Org, f.Project.Id, CancellationToken.None)).Should().BeNull();
    }

    // ------------------------------------------------------------------ picture tools

    [Theory]
    [InlineData(4.94, "about 5 credits per picture; larger pictures or several results can cost more")]
    [InlineData(12.4, "about 13 credits per picture; larger pictures or several results can cost more")]
    public async Task PictureToolCard_RoundsUp(double quote, string expected)
    {
        using GraphicFixture f = new();
        var user = new Caller(f.User);
        var builder = new AssistantApprovalCardBuilder(f.Db, TestTenancy.Access(f.Db, user), user);
        var proposal = new ProposalRecord("ProposeRunPictureTool", JsonDocument.Parse(
            $$"""{"estimatedCost":{"credits":{{quote.ToString(CultureInfo.InvariantCulture)}}},"confirmationToken":"t"}""").RootElement.Clone());

        ApprovalCard card = (await builder.BuildAsync(
            "ApplyRunPictureTool", Args(("tool", "upscale"), ("documentId", f.Document.Id), ("confirmationToken", "t")),
            new AssistantToolContext(f.User, f.Project.Id), proposal, CancellationToken.None)).Card!;

        card.Cost.Should().Be(expected);
        card.Risk.Should().Be(AssistantActionRisk.Paid);
    }

    // SEC-EF-2 / QA-EF-7: with billing on, an unknown quote is not "free".
    [Theory]
    [InlineData("""{"estimatedCost":{"credits":null,"note":"The price could not be worked out in advance."},"confirmationToken":"t"}""")]
    [InlineData("""{"confirmationToken":"t"}""")]
    [InlineData("")]
    public async Task PictureToolCard_UnknownQuote_StaysPaid_AndSaysTheCostIsUnknown(string proposalJson)
    {
        using GraphicFixture f = new();
        var user = new Caller(f.User);
        var builder = new AssistantApprovalCardBuilder(f.Db, TestTenancy.Access(f.Db, user), user, f.Estimator);
        ProposalRecord? proposal = proposalJson.Length == 0
            ? null
            : new ProposalRecord("ProposeRunPictureTool", JsonDocument.Parse(proposalJson).RootElement.Clone());

        ApprovalCard card = (await builder.BuildAsync(
            "ApplyRunPictureTool", Args(("tool", "upscale"), ("documentId", f.Document.Id), ("confirmationToken", "t")),
            new AssistantToolContext(f.User, f.Project.Id), proposal, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Cost.Should().Be(CostUnknown);
    }

    [Theory]
    [InlineData(true, """{"estimatedCost":{"credits":0,"note":"This workspace uses its own AI service for this tool, so it is billed by that service, not in credits."},"confirmationToken":"t"}""")]
    [InlineData(false, """{"estimatedCost":{"credits":null,"note":"Billing is off on this installation: no credits are charged."},"confirmationToken":"t"}""")]
    public async Task PictureToolCard_ExplicitlyNotCharged_IsNormal(bool billingOn, string proposalJson)
    {
        using GraphicFixture f = new();
        var user = new Caller(f.User);
        WorkflowCostEstimator estimator = billingOn
            ? f.Estimator
            : new(new ThrowingScopes(), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = false }),
                Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);
        var builder = new AssistantApprovalCardBuilder(f.Db, TestTenancy.Access(f.Db, user), user, estimator);
        var proposal = new ProposalRecord("ProposeRunPictureTool", JsonDocument.Parse(proposalJson).RootElement.Clone());

        ApprovalCard card = (await builder.BuildAsync(
            "ApplyRunPictureTool", Args(("tool", "upscale"), ("documentId", f.Document.Id), ("confirmationToken", "t")),
            new AssistantToolContext(f.User, f.Project.Id), proposal, CancellationToken.None)).Card!;

        card.Risk.Should().Be(AssistantActionRisk.Normal);
        card.Cost.Should().BeNull();
    }

    [Theory]
    [InlineData(4_940L, 4.94)]
    [InlineData(4_941L, 4.95)]
    [InlineData(12_400L, 12.4)]
    public void PictureToolQuote_RoundsUpToTheHundredth(long millis, double expected) =>
        AssistantPictureTools.QuoteCredits(millis).Should().Be(expected);

    // ------------------------------------------------------------------ revision lineage

    [Theory]
    [InlineData(null)]
    [InlineData(2160)]
    public async Task RevisionLineageEstimate_MatchesPostPreflight(int? resolution)
    {
        using RevisionFixture f = new();
        SeedHistory(f.Db, f.Org, StepType.Revise, f.ReviserId, [.. Enumerable.Repeat(1_000L, 10)]);
        SeedHistory(f.Db, f.Org, StepType.Revise, Guid.NewGuid(), [.. Enumerable.Repeat(60_000L, 10)]);
        WorkflowExecution run = f.FilmRun(resolution);
        f.Capture(run);
        await f.Db.SaveChangesAsync();

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, run.Id, f.Org, CancellationToken.None))!;
        history.Replay.Should().Match<RevisionReplayResponse?>(r => r == null || !r.Available, "this is the non-replay lineage estimate");
        RevisionCostEstimateResponse estimate = history.Estimate!;

        CompositionRevisionRequestResult refused = await f.Service.RequestAsync(
            f.Project, run.Id, f.User, f.Org, new CreateCompositionRevisionRequest("Bigger intro title", null, null), CancellationToken.None);
        refused.StatusCode.Should().Be(402, refused.Message);
        double required = (double)refused.Extra!["required"]!;
        required.Should().BePositive();
        estimate.RequiredCredits.Should().Be(required);
    }

    // ------------------------------------------------------------------ fixtures

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

    private static WorkflowCostEstimator RealEstimator(InferenceApiDbContext db) =>
        new(new SingleContextScopes(db), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);

    private static AgentDefinition AddAgent(InferenceApiDbContext db, AgentType type, bool builtIn = false)
    {
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = type.ToString(), AgentType = type, IsBuiltIn = builtIn };
        db.AgentDefinitions.Add(agent);
        return agent;
    }

    /// <summary>One Passed execution in <paramref name="org"/> with one charged step result per figure, under (<paramref name="type"/>, <paramref name="agent"/>).</summary>
    private static void SeedHistory(InferenceApiDbContext db, Guid org, StepType type, Guid agent, long[] credits)
    {
        Guid definitionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();

        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = definitionId, Name = "history", ProjectId = projectId });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            ProjectId = projectId,
            OrganizationId = org,
            Status = ExecutionStatus.Passed,
            QueuedAt = Past
        });
        db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = definitionId, StepOrder = 1, StepType = type, AgentDefinitionId = agent });

        foreach (long value in credits)
        {
            Guid resultId = Guid.NewGuid();
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = resultId,
                WorkflowExecutionId = executionId,
                WorkflowStepId = stepId,
                Status = StepStatus.Completed,
                ExecutedAt = Past
            });
            db.UsageEvents.Add(new UsageEvent
            {
                Id = Guid.NewGuid(),
                OrganizationId = org,
                ProjectId = projectId,
                ExecutionId = executionId,
                StepResultId = resultId,
                Source = UsageSource.Workflow,
                Kind = UsageKind.LlmInputTokens,
                Quantity = 1,
                ProviderKind = InferenceProviderKind.OpenAICompatible,
                ModelName = "test",
                CreditsMillis = value,
                IdempotencyKey = $"{resultId:N}:0",
                OccurredAt = Past
            });
        }
    }

    private sealed class Caller(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified => true;
    }

    private sealed class RunFixture : IDisposable
    {
        private readonly Caller _caller;

        public RunFixture(IWorkflowCostEstimator? estimator = null)
        {
            Db = AssistantChatControllerTests.CreateContext();
            _caller = new Caller(User);
            Project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = User, OrganizationId = Org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            Estimator = estimator ?? RealEstimator(Db);
        }

        public InferenceApiDbContext Db { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Guid Org => TestTenancy.OrgOf(User);
        public Project Project { get; }
        public IWorkflowCostEstimator Estimator { get; }

        public WorkflowDefinition Workflow(params (StepType Type, AgentType Agent)[] steps)
        {
            WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Product reel", ProjectId = Project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            int order = 0;
            foreach ((StepType type, AgentType agentType) in steps)
            {
                AgentDefinition agent = AddAgent(Db, agentType, builtIn: true);
                workflow.Steps.Add(new WorkflowStep
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = workflow.Id,
                    AgentDefinitionId = agent.Id,
                    StepOrder = ++order,
                    StepType = type,
                    MaxIterations = 1
                });
            }
            Db.WorkflowDefinitions.Add(workflow);
            return workflow;
        }

        public AssistantApprovalCardBuilder Builder(IPlanGateService? planGate = null) =>
            new(Db, TestTenancy.Access(Db, _caller), _caller, Estimator, planGate: planGate);

        public Task<WorkflowPreflightResult?> Price(Guid workflowId, int? resolution) =>
            new WorkflowExecutionPreflight(Db, Estimator).PriceAsync(Org, Project.Id, workflowId, resolution, CancellationToken.None);

        public void Dispose() => Db.Dispose();
    }

    private sealed class GraphicFixture : IDisposable
    {
        public GraphicFixture()
        {
            Db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase("card-graphics-" + Guid.NewGuid().ToString("N")).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = User, OrganizationId = Org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, OriginalFileName = "cover.png", MimeType = "image/png", StorageKey = "k", Category = "userFiles" };
            Document = new EditImageDocument { Id = Guid.NewGuid(), ProjectId = Project.Id, SourceProjectFileId = file.Id, CanvasWidth = 1280, CanvasHeight = 720 };
            Db.Projects.Add(Project);
            Db.ProjectFiles.Add(file);
            Db.EditImageDocuments.Add(Document);
            Db.EditImageLayers.Add(new EditImageLayer { Id = Guid.NewGuid(), DocumentId = Document.Id, ZOrder = 1, Kind = EditImageLayerKind.Source, Label = "cover.png", StorageKey = "k", TransformJson = "{}" });
            DesignerId = AddAgent(Db, AgentType.GraphicDesigner, builtIn: true).Id;
            Db.SaveChanges();
            Estimator = RealEstimator(Db);
            Service = new GraphicDesignService(
                Db,
                new WorkflowExecutionService(Db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
                new FakeFileStorage(),
                new ConfigurationBuilder().Build(),
                NullLogger<GraphicDesignService>.Instance,
                Estimator);
        }

        public InferenceApiDbContext Db { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Guid Org => TestTenancy.OrgOf(User);
        public Project Project { get; }
        public EditImageDocument Document { get; }
        public Guid DesignerId { get; }
        public WorkflowCostEstimator Estimator { get; }
        public GraphicDesignService Service { get; }

        public Task<GraphicDesignRequestResult> Request() =>
            Service.RequestAsync(Project, Document.Id, User, Org, new CreateImageGraphicRequest("A red SALE badge", "topRight", 400, 200), ExecutionInitiator.Human, CancellationToken.None);

        public void Dispose() => Db.Dispose();
    }

    private sealed class RevisionFixture : IDisposable
    {
        private const string Manifest =
            "{\"Version\":1,\"CompositionId\":\"FinalVideo\",\"Fps\":30,\"Scenes\":[" +
            "{\"Handle\":\"scene-1\",\"Name\":\"Intro\",\"FromFrame\":0,\"DurationInFrames\":90,\"Description\":\"Logo\",\"OnScreenText\":[]}]}";

        public RevisionFixture()
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase("card-revisions-" + Guid.NewGuid().ToString("N")).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = User, OrganizationId = Org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            ReviserId = AddAgent(Db, AgentType.CompositionReviser, builtIn: true).Id;
            Db.SaveChanges();
            Service = new CompositionRevisionService(
                Db,
                new WorkflowExecutionService(Db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
                new ConfigurationBuilder().Build(),
                NullLogger<CompositionRevisionService>.Instance,
                RealEstimator(Db));
        }

        public InferenceApiDbContext Db { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Guid Org => TestTenancy.OrgOf(User);
        public Project Project { get; }
        public Guid ReviserId { get; }
        public CompositionRevisionService Service { get; }

        /// <summary>A finished run whose Author step rendered a film, at <paramref name="resolution"/>.</summary>
        public WorkflowExecution FilmRun(int? resolution)
        {
            AgentDefinition author = AddAgent(Db, AgentType.AuthorAgent, builtIn: true);
            WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Promo", ProjectId = Project.Id };
            WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = author.Id, StepOrder = 1, StepType = StepType.Agent };
            WorkflowExecution run = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                ProjectId = Project.Id,
                OrganizationId = Org,
                Status = ExecutionStatus.Passed,
                OutputResolution = resolution,
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                CompletedAt = DateTime.UtcNow.AddMinutes(-1)
            };
            Db.WorkflowDefinitions.Add(workflow);
            Db.WorkflowSteps.Add(step);
            Db.WorkflowExecutions.Add(run);
            Db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = run.Id,
                WorkflowStepId = step.Id,
                Status = StepStatus.Completed,
                OutputStorageKey = $"projects/{Project.Id}/outputFiles/{run.Id}/promo.mp4"
            });
            Db.SaveChanges();
            return run;
        }

        public void Capture(WorkflowExecution run)
        {
            CompositionWorkspace workspace = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, OrganizationId = Org, OriginExecutionId = run.Id, LatestVersionNumber = 1 };
            Db.CompositionWorkspaces.Add(workspace);
            Db.CompositionWorkspaceVersions.Add(new CompositionWorkspaceVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                ProjectId = Project.Id,
                VersionNumber = 1,
                StorageKey = CompositionWorkspaceKeys.ArchiveKey(Project.Id, workspace.Id, Guid.NewGuid()),
                Sha256 = new string('a', 64),
                CreatedByExecutionId = run.Id,
                CompositionId = "FinalVideo",
                Fps = 30,
                SceneManifestJson = Manifest,
                RenderOutputStorageKey = $"projects/{Project.Id}/outputFiles/{run.Id}/promo.mp4"
            });
        }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>An estimator with fixed figures and a <c>RequiredMillis</c> of its own (above the displayed high).</summary>
    private sealed class FixedEstimator(long lowMillis, long highMillis, long requiredMillis) : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        private WorkflowCostEstimate Estimate => new(true, true, lowMillis, highMillis, requiredMillis, []);

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate);

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed, Estimate, 1_000_000));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            Task.FromResult(Estimate);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            PreflightAsync(organizationId, runSteps, ct);
    }

    /// <summary>An estimator whose display estimate and preflight disagree, to show which one a surface reads.</summary>
    private sealed class SplitEstimator(WorkflowCostEstimate display, WorkflowCostEstimate preflight) : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(display);

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed, preflight, 1_000_000));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            Task.FromResult(display);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            PreflightAsync(organizationId, runSteps, ct);
    }

    /// <summary>
    /// What the real estimator answers when it could not evaluate the preflight and had no estimate to keep
    /// (see <c>Preflight_EstimateThrows_AllowsTheRun_ButSaysItWasNotEvaluated</c>).
    /// </summary>
    private sealed class NotEvaluatedEstimator : IWorkflowCostEstimator
    {
        private static readonly WorkflowPreflightResult NotEvaluated =
            new(WorkflowPreflightOutcome.Allowed, WorkflowCostEstimate.Disabled, 0, Evaluated: false);

        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(WorkflowCostEstimate.Disabled);

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(NotEvaluated);

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            Task.FromResult(WorkflowCostEstimate.Disabled);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            Task.FromResult(NotEvaluated);
    }

    /// <summary>An empty context whose <typeparamref name="TUnreadable"/> table cannot be read; every other table reads normally.</summary>
    private static InferenceApiDbContext UnreadableContext<TUnreadable>() =>
        new UnreadableTableDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            typeof(TUnreadable));

    private static InferenceApiDbContext UnreadableAgentsContext() => UnreadableContext<AgentDefinition>();

    private sealed class UnreadableTableDbContext(DbContextOptions<InferenceApiDbContext> options, Type unreadable) : InferenceApiDbContext(options)
    {
        public override DbSet<TEntity> Set<TEntity>() =>
            typeof(TEntity) == unreadable
                ? throw new InvalidOperationException($"the {typeof(TEntity).Name} table is unreadable")
                : base.Set<TEntity>();
    }

    /// <summary>
    /// A scope factory that cannot open a scope: every database read the estimator makes through it fails.
    /// The estimate's own reads degrade to the cold-start figures; the balance read throws.
    /// </summary>
    private sealed class ThrowingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("the database is unreachable");
    }

    /// <summary>A scope factory handing every caller the one context the test seeded.</summary>
    private sealed class SingleContextScopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(db);

        private sealed class FixedScope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(db);

            public void Dispose()
            {
            }
        }

        private sealed class FixedProvider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }
}
