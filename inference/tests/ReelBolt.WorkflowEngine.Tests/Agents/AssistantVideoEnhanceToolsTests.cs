#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.EditTimelines;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// ProposeEnhanceVideo / ApplyEnhanceVideo: the token binds the exact arguments, a hosted operation never runs without the
/// project's recorded consent (and a refusal starts nothing), local operations need none, a project the caller cannot
/// edit is absent, and the approval card is built from the estimate. The service is a stand-in so each gate answer is
/// exact; the real service's gates are covered by VideoEnhanceRoutesTests.
/// </summary>
public class AssistantVideoEnhanceToolsTests
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

    private sealed class Rig : IDisposable
    {
        public ServerEditFixture F { get; private set; } = null!;
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();
        public Mock<IVideoEnhanceService> Service { get; } = new();
        public VideoEnhanceEstimate Estimate { get; set; } = LocalEstimate();
        public int Requests { get; private set; }
        public VideoEnhanceRequestBody? LastBody { get; private set; }

        public static async Task<Rig> CreateAsync()
        {
            Rig r = new() { F = await ServerEditFixture.CreateAsync(1) };
            r.Service.Setup(s => s.EstimateAsync(It.IsAny<VideoEnhanceCaller>(), It.IsAny<string>(), It.IsAny<VideoEnhanceRequestBody?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((VideoEnhanceCaller _, string _, VideoEnhanceRequestBody? body, CancellationToken _) =>
                {
                    r.LastBody = body;
                    return VideoEnhanceResult<VideoEnhanceEstimate>.Ok(r.Estimate);
                });
            r.Service.Setup(s => s.RequestAsync(It.IsAny<VideoEnhanceCaller>(), It.IsAny<string>(), It.IsAny<VideoEnhanceRequestBody?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    r.Requests++;
                    return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Ok(
                        new VideoEnhanceQueuedResponse(Guid.NewGuid(), "Upscale", "Local", false, "queued", 0), 202);
                });
            return r;
        }

        public static VideoEnhanceEstimate LocalEstimate() =>
            new(true, false, 0, 10, 0, true, null, null, false, 2160, 1920, 1080, 30, 1_000_000, 20, 600);

        public static VideoEnhanceEstimate HostedEstimate(double credits = 40) =>
            new(true, true, credits, 10, 1000, true, null, null, true, 2160, 1920, 1080, 60, 1_000_000, 60, 120);

        public AssistantVideoEnhanceTools Tools(Guid? user = null)
        {
            Caller caller = new(user ?? F.OwnerId);
            return new AssistantVideoEnhanceTools(F.Db, TestTenancy.Access(F.Db, caller), Service.Object, Tokens, caller);
        }

        public async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args, Guid? user = null)
        {
            AIFunction fn = Tools(user).GetTools(new AssistantToolContext(user ?? F.OwnerId, F.ProjectId)).Single(t => t.Name == tool);
            object? raw = await fn.InvokeAsync(new AIFunctionArguments(args));
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public Dictionary<string, object?> Args(string op = "Upscale", string engine = "Local", int factor = 2) =>
            new() { ["op"] = op, ["engine"] = engine, ["fileId"] = F.FileId, ["upscaleFactor"] = factor };

        public void Dispose() => F.Dispose();
    }

    [Fact]
    public async Task Propose_names_the_source_and_op_without_ids_and_runs_nothing()
    {
        using Rig r = await Rig.CreateAsync();
        JsonElement p = await r.CallAsync("ProposeEnhanceVideo", r.Args());

        p.GetProperty("confirmationToken").GetString().Should().NotBeNullOrEmpty();
        p.GetProperty("source").GetString().Should().Be("a.mp4");
        p.GetProperty("outputHeight").GetInt32().Should().Be(1080);
        p.GetProperty("credits").GetDouble().Should().Be(0);
        p.GetProperty("summary").GetString().Should().Contain("upscale the video 2×".Replace("upscale", "Upscale")).And.Contain("a.mp4").And.Contain("free");
        System.Text.RegularExpressions.Regex.IsMatch(p.GetRawText(), @"[0-9a-f]{8}-[0-9a-f]{4}-").Should().BeFalse("names, never ids");
        r.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Apply_with_the_proposed_token_starts_one_run_and_returns_without_waiting()
    {
        using Rig r = await Rig.CreateAsync();
        string token = (await r.CallAsync("ProposeEnhanceVideo", r.Args())).GetProperty("confirmationToken").GetString()!;
        Dictionary<string, object?> args = r.Args();
        args["confirmationToken"] = token;

        JsonElement applied = await r.CallAsync("ApplyEnhanceVideo", args);

        applied.TryGetProperty("executionId", out _).Should().BeTrue();
        applied.GetProperty("state").GetString().Should().Be("queued");
        r.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Apply_refuses_a_tampered_argument_or_a_fabricated_token_and_starts_nothing()
    {
        using Rig r = await Rig.CreateAsync();
        string token = (await r.CallAsync("ProposeEnhanceVideo", r.Args(factor: 2))).GetProperty("confirmationToken").GetString()!;

        Dictionary<string, object?> tampered = r.Args(factor: 4);
        tampered["confirmationToken"] = token;
        (await r.CallAsync("ApplyEnhanceVideo", tampered)).GetProperty("error").GetString().Should().Contain("stale confirmationToken");

        Dictionary<string, object?> fake = r.Args();
        fake["confirmationToken"] = "not-a-token";
        (await r.CallAsync("ApplyEnhanceVideo", fake)).GetProperty("error").GetString().Should().Contain("stale confirmationToken");

        Dictionary<string, object?> otherOp = r.Args(op: "Denoise");
        otherOp["confirmationToken"] = token;
        (await r.CallAsync("ApplyEnhanceVideo", otherOp)).TryGetProperty("error", out _).Should().BeTrue();
        r.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Hosted_without_recorded_consent_is_refused_and_mints_no_token_and_writes_nothing()
    {
        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate() with { UnavailableCode = VideoEnhanceService.ConsentRequiredCode, UnavailableReason = "consent", Consented = false };

        JsonElement p = await r.CallAsync("ProposeEnhanceVideo", r.Args(engine: "Hosted"));
        p.GetProperty("code").GetString().Should().Be("NEEDS_CUSTOMER_OK");
        p.GetProperty("error").GetString().Should().Contain("editor");
        p.TryGetProperty("confirmationToken", out _).Should().BeFalse();

        Dictionary<string, object?> args = r.Args(engine: "Hosted");
        args["confirmationToken"] = "x";
        (await r.CallAsync("ApplyEnhanceVideo", args)).GetProperty("code").GetString().Should().Be("NEEDS_CUSTOMER_OK");
        r.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Local_runs_without_consent_and_hosted_with_consent_runs()
    {
        using Rig r = await Rig.CreateAsync();
        // Local: no consent on the project, runs.
        Dictionary<string, object?> local = r.Args();
        local["confirmationToken"] = (await r.CallAsync("ProposeEnhanceVideo", r.Args())).GetProperty("confirmationToken").GetString();
        (await r.CallAsync("ApplyEnhanceVideo", local)).TryGetProperty("executionId", out _).Should().BeTrue();

        r.Estimate = Rig.HostedEstimate();
        Dictionary<string, object?> hosted = r.Args(engine: "Hosted");
        JsonElement p = await r.CallAsync("ProposeEnhanceVideo", hosted);
        p.GetProperty("credits").GetDouble().Should().Be(40);
        hosted["confirmationToken"] = p.GetProperty("confirmationToken").GetString();
        (await r.CallAsync("ApplyEnhanceVideo", hosted)).TryGetProperty("executionId", out _).Should().BeTrue();
        r.Requests.Should().Be(2);
    }

    [Fact]
    public async Task A_caller_who_cannot_edit_the_project_gets_nothing_and_no_estimate_is_read()
    {
        using Rig r = await Rig.CreateAsync();
        JsonElement p = await r.CallAsync("ProposeEnhanceVideo", r.Args(), user: Guid.NewGuid());

        p.TryGetProperty("error", out _).Should().BeTrue();
        p.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        r.Service.Verify(s => s.EstimateAsync(It.IsAny<VideoEnhanceCaller>(), It.IsAny<string>(), It.IsAny<VideoEnhanceRequestBody?>(), It.IsAny<CancellationToken>()), Times.Never);
        r.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Both_a_file_and_an_output_or_neither_is_an_invalid_request()
    {
        using Rig r = await Rig.CreateAsync();
        Dictionary<string, object?> both = r.Args();
        both["outputId"] = Guid.NewGuid();
        (await r.CallAsync("ProposeEnhanceVideo", both)).GetProperty("code").GetString().Should().Be("INVALID_REQUEST");
        Dictionary<string, object?> neither = r.Args();
        neither.Remove("fileId");
        (await r.CallAsync("ProposeEnhanceVideo", neither)).GetProperty("code").GetString().Should().Be("INVALID_REQUEST");
    }

    [Fact]
    public async Task The_card_shows_the_op_source_seconds_size_and_the_price_from_the_arguments()
    {
        using Rig r = await Rig.CreateAsync();
        Caller caller = new(r.F.OwnerId);
        AssistantApprovalCardBuilder builder = new(r.F.Db, TestTenancy.Access(r.F.Db, caller), caller, videoEnhance: r.Service.Object);
        AssistantToolContext ctx = new(r.F.OwnerId, r.F.ProjectId);

        Dictionary<string, object?> local = r.Args();
        ApprovalVerdict free = await builder.BuildAsync("ApplyEnhanceVideo", new AIFunctionArguments(local), ctx, null, CancellationToken.None);
        free.Card!.Cost.Should().Be("Free");
        free.Card.Summary.Should().Contain("Upscale the video 2×").And.Contain("a.mp4").And.Contain("10 s").And.Contain("1920×1080");
        string.Join("\n", free.Card.Changes!.Select(c => c.Text)).Should().Contain("1920×1080 at 30 fps");

        r.Estimate = Rig.HostedEstimate(credits: 500);
        ApprovalVerdict paid = await builder.BuildAsync("ApplyEnhanceVideo", new AIFunctionArguments(r.Args(engine: "Hosted")), ctx, null, CancellationToken.None);
        paid.Card!.Risk.Should().Be(AssistantActionRisk.Paid);
        paid.Card.Cost.Should().Contain("credits");
        // The auto-mode rule reads CostCredits: above the limit the card is still asked for.
        paid.Card.CostCredits.Should().BeGreaterThan(AssistantApprovalGate.ResolveAutoApproveLimit(null));
        free.Card.CostCredits.Should().BeNull();
    }

    [Fact]
    public async Task The_card_for_a_hosted_op_without_consent_answers_instead_of_asking()
    {
        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate() with { UnavailableCode = VideoEnhanceService.ConsentRequiredCode, UnavailableReason = "consent" };
        Caller caller = new(r.F.OwnerId);
        AssistantApprovalCardBuilder builder = new(r.F.Db, TestTenancy.Access(r.F.Db, caller), caller, videoEnhance: r.Service.Object);

        ApprovalVerdict v = await builder.BuildAsync("ApplyEnhanceVideo", new AIFunctionArguments(r.Args(engine: "Hosted")),
            new AssistantToolContext(r.F.OwnerId, r.F.ProjectId), null, CancellationToken.None);

        v.Card.Should().BeNull();
        v.Answer.Should().Contain("NEEDS_CUSTOMER_OK");
    }

    [Fact]
    public async Task Propose_refuses_an_unaffordable_hosted_run_with_the_routes_code_and_mints_no_token()
    {
        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate(credits: 40) with { Affordable = false, BalanceCredits = 5 };

        JsonElement p = await r.CallAsync("ProposeEnhanceVideo", r.Args(engine: "Hosted"));

        p.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        p.GetProperty("error").GetString().Should().Contain("credits");
        p.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        r.Requests.Should().Be(0);
    }

    private Dictionary<string, object?> ExtendArgs(Rig r, Action<Dictionary<string, object?>> more)
    {
        Dictionary<string, object?> args = new() { ["op"] = "Extend", ["engine"] = "Hosted", ["fileId"] = r.F.FileId };
        more(args);
        return args;
    }

    [Theory]
    [InlineData("9:16", "extend the picture to vertical 9:16")]
    [InlineData("1:1", "extend the picture to square 1:1")]
    [InlineData("16:9", "extend the picture to widescreen 16:9")]
    public async Task Extend_with_an_aspect_is_proposed_and_applied_and_the_summary_names_the_shape_in_plain_words(string aspect, string words)
    {
        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate();
        Dictionary<string, object?> args = ExtendArgs(r, a => a["targetAspect"] = aspect);

        JsonElement p = await r.CallAsync("ProposeEnhanceVideo", args);
        p.GetProperty("summary").GetString()!.ToLowerInvariant().Should().Contain(words);
        r.LastBody!.TargetAspect.Should().Be(aspect);
        args["confirmationToken"] = p.GetProperty("confirmationToken").GetString();

        (await r.CallAsync("ApplyEnhanceVideo", args)).TryGetProperty("executionId", out _).Should().BeTrue();
        r.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Extend_margins_are_described_by_side_and_bound_into_the_token()
    {
        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate();
        Dictionary<string, object?> args = ExtendArgs(r, a => { a["marginLeft"] = 0.2; a["marginRight"] = 0.2; });

        JsonElement p = await r.CallAsync("ProposeEnhanceVideo", args);
        p.GetProperty("summary").GetString().Should().Contain("wider on the left and right");
        AssistantVideoEnhanceTools.DescribeExtendShape(new VideoEnhanceRequestBody("Extend", "Hosted", MarginTop: 0.1)).Should().Be("taller on the top");
        AssistantVideoEnhanceTools.DescribeExtendShape(new VideoEnhanceRequestBody("Extend", "Hosted", MarginLeft: 0.1, MarginBottom: 0.1))
            .Should().Be("wider on the left and taller on the bottom");

        args["confirmationToken"] = p.GetProperty("confirmationToken").GetString();
        args["marginRight"] = 0.3;
        (await r.CallAsync("ApplyEnhanceVideo", args)).GetProperty("error").GetString().Should().Be(AssistantVideoEnhanceTools.StaleTokenMessage);
        r.Requests.Should().Be(0);
    }

    [Fact]
    public async Task ApplyEnhanceVideo_is_a_paid_catalog_entry_and_a_card_failure_falls_back_to_an_unestimated_paid_card()
    {
        AssistantApprovalCatalog.Describe("ApplyEnhanceVideo").Risk.Should().Be(AssistantActionRisk.Paid);

        using Rig r = await Rig.CreateAsync();
        Caller caller = new(r.F.OwnerId);
        AssistantApprovalCardBuilder builder = new(r.F.Db, TestTenancy.Access(r.F.Db, caller), caller, videoEnhance: r.Service.Object);

        // The gate asks with this card when BuildAsync threw; CostUnestimated makes auto mode ask instead of running.
        ApprovalCard card = builder.FallbackCard("ApplyEnhanceVideo");
        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.CostUnestimated.Should().BeTrue();
    }

    [Fact]
    public async Task The_hosted_card_says_the_video_leaves_the_platform_and_is_paid_risk_even_when_billing_is_off()
    {
        using Rig r = await Rig.CreateAsync();
        Caller caller = new(r.F.OwnerId);
        AssistantApprovalCardBuilder builder = new(r.F.Db, TestTenancy.Access(r.F.Db, caller), caller, videoEnhance: r.Service.Object);
        AssistantToolContext ctx = new(r.F.OwnerId, r.F.ProjectId);

        r.Estimate = Rig.HostedEstimate() with { BillingEnabled = false };
        ApprovalVerdict hosted = await builder.BuildAsync("ApplyEnhanceVideo", new AIFunctionArguments(r.Args(engine: "Hosted")), ctx, null, CancellationToken.None);
        hosted.Card!.Risk.Should().Be(AssistantActionRisk.Paid);
        string.Join("\n", hosted.Card.Changes!.Select(c => c.Text)).Should().Contain("sent to an outside video service");

        r.Estimate = Rig.LocalEstimate();
        ApprovalVerdict local = await builder.BuildAsync("ApplyEnhanceVideo", new AIFunctionArguments(r.Args()), ctx, null, CancellationToken.None);
        local.Card!.Risk.Should().Be(AssistantActionRisk.Normal);
        string.Join("\n", local.Card.Changes!.Select(c => c.Text)).Should().NotContain("outside video service");
    }

    [Fact]
    public async Task The_extend_prompt_is_sanitized_bounded_bound_into_the_token_and_never_shown_raw()
    {
        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate();
        Dictionary<string, object?> args = ExtendArgs(r, a => { a["targetAspect"] = "9:16"; a["prompt"] = "  a calm\u0007 sky\u0000  "; });

        JsonElement p = await r.CallAsync("ProposeEnhanceVideo", args);

        r.LastBody!.Prompt.Should().Be("a calm sky");
        p.GetRawText().Should().NotContain("calm").And.NotContain("\\u0007");
        args["confirmationToken"] = p.GetProperty("confirmationToken").GetString();
        args["prompt"] = "something else";
        (await r.CallAsync("ApplyEnhanceVideo", args)).GetProperty("error").GetString().Should().Be(AssistantVideoEnhanceTools.StaleTokenMessage);

        Dictionary<string, object?> tooLong = ExtendArgs(r, a => { a["targetAspect"] = "9:16"; a["prompt"] = new string('x', 501); });
        (await r.CallAsync("ProposeEnhanceVideo", tooLong)).GetProperty("code").GetString().Should().Be("INVALID_REQUEST");
    }

    [Fact]
    public async Task The_extend_card_shows_the_sanitized_truncated_prompt_so_the_customer_approves_what_is_sent()
    {
        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate();
        Caller caller = new(r.F.OwnerId);
        AssistantApprovalCardBuilder builder = new(r.F.Db, TestTenancy.Access(r.F.Db, caller), caller, videoEnhance: r.Service.Object);
        AssistantToolContext ctx = new(r.F.OwnerId, r.F.ProjectId);

        ApprovalVerdict v = await builder.BuildAsync("ApplyEnhanceVideo",
            new AIFunctionArguments(ExtendArgs(r, a => { a["targetAspect"] = "9:16"; a["prompt"] = "  a calm\u0007 sky  "; })), ctx, null, CancellationToken.None);
        string changes = string.Join("\n", v.Card!.Changes!.Select(c => c.Text));
        changes.Should().Contain("a calm sky").And.NotContain("\u0007");

        ApprovalVerdict longPrompt = await builder.BuildAsync("ApplyEnhanceVideo",
            new AIFunctionArguments(ExtendArgs(r, a => { a["targetAspect"] = "9:16"; a["prompt"] = new string('x', 400); })), ctx, null, CancellationToken.None);
        string longChange = longPrompt.Card!.Changes!.Single(c => c.Text.Contains("xxx")).Text;
        longChange.Length.Should().BeLessThan(200);
        longChange.Should().Contain("…");
    }

    [Fact]
    public async Task The_extend_card_prompt_has_no_bidi_controls_or_zero_width_characters()
    {
        string dirty = "a\u202Ecalm\u202C \u2066sky\u2069\u200Bx\u200C\u200D\u2060\uFEFF\u200E\u200F";
        AssistantVideoEnhanceTools.CardSafe(dirty).Should().Be("acalm skyx");

        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate();
        Caller caller = new(r.F.OwnerId);
        AssistantApprovalCardBuilder builder = new(r.F.Db, TestTenancy.Access(r.F.Db, caller), caller, videoEnhance: r.Service.Object);
        ApprovalVerdict v = await builder.BuildAsync("ApplyEnhanceVideo",
            new AIFunctionArguments(ExtendArgs(r, a => { a["targetAspect"] = "9:16"; a["prompt"] = dirty; })),
            new AssistantToolContext(r.F.OwnerId, r.F.ProjectId), null, CancellationToken.None);

        string changes = string.Join("\n", v.Card!.Changes!.Select(c => c.Text));
        changes.Should().Contain("acalm skyx");
        foreach (char bad in "\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069\u200E\u200F\u200B\u200C\u200D\u2060\uFEFF")
            changes.Should().NotContain(bad.ToString());
    }

    [Fact]
    public async Task The_card_for_an_extend_names_the_shape_and_the_tool_pair_is_unchanged()
    {
        using Rig r = await Rig.CreateAsync();
        r.Estimate = Rig.HostedEstimate();
        Caller caller = new(r.F.OwnerId);
        AssistantApprovalCardBuilder builder = new(r.F.Db, TestTenancy.Access(r.F.Db, caller), caller, videoEnhance: r.Service.Object);
        AssistantToolContext ctx = new(r.F.OwnerId, r.F.ProjectId);

        ApprovalVerdict v = await builder.BuildAsync("ApplyEnhanceVideo",
            new AIFunctionArguments(ExtendArgs(r, a => a["targetAspect"] = "9:16")), ctx, null, CancellationToken.None);

        v.Card!.Summary.Should().Contain("vertical 9:16");
        v.Card.Risk.Should().Be(AssistantActionRisk.Paid);
        r.Tools().GetTools(ctx).Select(t => t.Name).Should().Equal("ProposeEnhanceVideo", "ApplyEnhanceVideo");
    }
}
