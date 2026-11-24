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
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>GetChangeOptions / ProposeStepChange / ApplyStepChange and the approval card, over a fake step-change service, the real run guard and the real token service.</summary>
public sealed class AssistantChangeRequestToolsTests : IDisposable
{
    private sealed class Caller(Guid userId, bool emailVerified = true, OrganizationRole role = OrganizationRole.Owner) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => role;
        public bool EmailVerified { get; } = emailVerified;
    }

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase("change-tools-" + Guid.NewGuid().ToString("N")).Options);
    private readonly Guid _user = Guid.NewGuid();
    private readonly Project _project;
    private readonly Guid _execution = Guid.NewGuid();
    private readonly Guid _stepResult = Guid.NewGuid();
    private readonly Mock<IStepChangeRequestService> _service = new();
    private readonly IConfirmationTokenService _tokens = ConfirmationTokenService.CreateEphemeral();
    private readonly AssistantExecutionGuard _guard;
    private int _requests;

    public AssistantChangeRequestToolsTests()
    {
        _project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = _user, OrganizationId = TestTenancy.OrgOf(_user), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Projects.Add(_project);
        _db.SaveChanges();
        _guard = new AssistantExecutionGuard(_db, new ConfigurationBuilder().Build());

        StepChangeStepCapabilityResponse voice = new(
            2, _stepResult, "Voiceover", "Narration", true, null, null, "Line",
            [new StepChangeItemResponse("line-1", "Completed", 3.2, "Welcome to Launch")],
            [new StepChangeOverrideResponse("speed", "Number", 0.5, 2.0, null, null)]);
        StepChangeStepCapabilityResponse film = new(
            5, Guid.NewGuid(), "Revise", "Final film", false, "the film is changed through a composition revision", "composition-revision", "None", [], []);
        StepChangeStepCapabilityResponse script = new(
            1, Guid.NewGuid(), "Agent", "Script", true, null, null, "Scene",
            [new StepChangeItemResponse("sc2", null, null, "We open on the harbour at dawn and the narrator greets the viewer warmly, explaining what the film is about in a long sentence.")],
            [new StepChangeOverrideResponse("voiceover", "Text", null, null, 1000, null)],
            "script", true);
        _service.Setup(s => s.GetCapabilitiesAsync(It.IsAny<Project>(), _execution, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StepChangeCapabilitiesResponse(_execution, true, [voice, film, script]));
        _service.Setup(s => s.EstimateAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepChangeRequestResult.Previewed(new StepChangeEstimatePreviewResponse(
                new StepChangeEstimateResponse(true, 1, 2, 3, 100, true),
                new StepChangeReplayResponse([
                    new StepChangeReplayStepResponse(2, "Narration", "Run", "changed"),
                    new StepChangeReplayStepResponse(1, "Script", "Reuse", "unchanged")]))));
        _service.Setup(s => s.RequestAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>(), It.IsAny<ExecutionInitiator>()))
            .Returns((Project _, Guid _, Guid _, Guid? _, CreateStepChangeRequest _, CancellationToken _, ExecutionInitiator _) =>
            {
                _requests++;
                Guid run = Guid.NewGuid();
                return Task.FromResult(StepChangeRequestResult.Ok(new StepChangeRequestResponse(
                    Guid.NewGuid(), run, Guid.NewGuid(), _execution, 2, _stepResult, "Voiceover", "Slower", ["line-1"], "Queued", null, DateTime.UtcNow)));
            });
    }

    public void Dispose() => _db.Dispose();

    private AssistantChangeRequestTools Tools(Guid? asUser = null, bool verified = true, OrganizationRole role = OrganizationRole.Owner)
    {
        var caller = new Caller(asUser ?? _user, verified, role);
        return new AssistantChangeRequestTools(TestTenancy.Access(_db, caller), _service.Object, _guard, _tokens, caller);
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args, AssistantChangeRequestTools? tools = null, ExecutionInitiator initiator = ExecutionInitiator.Assistant)
    {
        tools ??= Tools();
        AIFunction fn = tools.GetTools(new AssistantToolContext(_user, _project.Id, Initiator: initiator)).Single(t => t.Name == tool);
        object? raw = await fn.InvokeAsync(new AIFunctionArguments(args));
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Dictionary<string, object?> Args(string? token = null, string instruction = "Slower", string[]? items = null, Dictionary<string, Dictionary<string, JsonElement>>? overrides = null)
    {
        Dictionary<string, object?> args = new()
        {
            ["executionId"] = _execution,
            ["stepOrder"] = 2,
            ["instruction"] = instruction,
            ["itemIds"] = items ?? new[] { "line-1" },
        };
        if (overrides is not null) args["overrides"] = overrides;
        if (token is not null) args["confirmationToken"] = token;
        return args;
    }

    private async Task<string> TokenAsync(Dictionary<string, Dictionary<string, JsonElement>>? overrides = null, string[]? items = null) =>
        (await CallAsync("ProposeStepChange", Args(overrides: overrides, items: items))).GetProperty("confirmationToken").GetString()!;

    private static Dictionary<string, Dictionary<string, JsonElement>> Overrides(params (string Key, double Value)[] keys) =>
        new() { ["line-1"] = keys.ToDictionary(k => k.Key, k => JsonSerializer.SerializeToElement(k.Value)) };

    [Fact]
    public async Task GetChangeOptions_lists_steps_items_overrides_and_points_the_film_at_the_revision_tool()
    {
        JsonElement result = await CallAsync("GetChangeOptions", new() { ["executionId"] = _execution });

        JsonElement steps = result.GetProperty("steps");
        steps[0].GetProperty("label").GetString().Should().Be("Narration");
        steps[0].GetProperty("items")[0].GetProperty("text").GetString().Should().Be("Welcome to Launch");
        steps[0].GetProperty("overrides")[0].GetProperty("meaning").GetString().Should().Be("how fast it is said");
        steps[1].GetProperty("route").GetString().Should().Be("composition-revision");
        steps[1].GetProperty("useInstead").GetString().Should().Be("ProposeReviseComposition");
    }

    [Fact]
    public async Task GetChangeOptions_lists_the_choices_and_says_in_plain_words_what_each_new_key_means()
    {
        StepChangeStepCapabilityResponse thumb = new(
            4, Guid.NewGuid(), "ThumbnailGenerate", "Thumbnail", true, null, null, "None",
            [new StepChangeItemResponse("thumbnail", "Completed", null, "thumb.png")],
            [
                new StepChangeOverrideResponse("overlayPosition", "Enum", null, null, null, null, ["None", "LowerThird"]),
                new StepChangeOverrideResponse("atTimeSec", "Number", 0, 3600, null, null),
                new StepChangeOverrideResponse("mystery", "Text", null, null, 10, null),
            ]);
        StepChangeStepCapabilityResponse clips = new(
            3, Guid.NewGuid(), "VideoGenerate", "Clips", true, null, null, "Shot", [],
            [new StepChangeOverrideResponse("prompt", "Text", null, null, 2000, null), new StepChangeOverrideResponse("retake", "Flag", null, null, null, null)]);
        StepChangeStepCapabilityResponse music = new(
            2, Guid.NewGuid(), "AudioGenerate", "Music", true, null, null, "Take", [],
            [new StepChangeOverrideResponse("prompt", "Text", null, null, 4000, null)]);
        _service.Setup(s => s.GetCapabilitiesAsync(It.IsAny<Project>(), _execution, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StepChangeCapabilitiesResponse(_execution, true, [thumb, clips, music]));

        JsonElement steps = (await CallAsync("GetChangeOptions", new() { ["executionId"] = _execution })).GetProperty("steps");

        JsonElement[] thumbOverrides = steps[0].GetProperty("overrides").EnumerateArray().ToArray();
        thumbOverrides[0].GetProperty("values").EnumerateArray().Select(v => v.GetString()).Should().Equal("None", "LowerThird");
        thumbOverrides[0].GetProperty("meaning").GetString().Should().Contain("thumbnail");
        thumbOverrides[1].GetProperty("meaning").GetString().Should().Contain("moment");
        thumbOverrides[2].GetProperty("meaning").GetString().Should().Be("mystery", "an unknown key is passed through as written");
        steps[1].GetProperty("overrides")[0].GetProperty("meaning").GetString().Should().Contain("clip");
        steps[1].GetProperty("overrides")[1].GetProperty("meaning").GetString().Should().Contain("fresh take");
        steps[2].GetProperty("overrides")[0].GetProperty("meaning").GetString().Should().Contain("music");
    }

    [Fact]
    public void The_tool_descriptions_name_the_real_keys_of_every_step_in_plain_words()
    {
        var tools = Tools(_user).GetTools(new AssistantToolContext(_user, _project.Id));
        string propose = tools.Single(t => t.Name == "ProposeStepChange").Description;

        propose.Should().Contain("retake").And.Contain("durationSec").And.Contain("overlayText").And.Contain("atTimeSec")
            .And.Contain("overlayPosition").And.Contain("overlayFontColor").And.Contain("overlayBoxColor").And.Contain("pronunciation");
        tools.Single(t => t.Name == "GetChangeOptions").Description.Should().Contain("video clips").And.Contain("thumbnail");
        tools.Select(t => t.Name).Should().BeEquivalentTo("GetChangeOptions", "ProposeStepChange", "ApplyStepChange");
    }

    [Fact]
    public async Task GetChangeOptions_for_a_foreign_project_is_absent()
    {
        AssistantChangeRequestTools stranger = Tools(Guid.NewGuid());

        JsonElement result = await CallAsync("GetChangeOptions", new() { ["executionId"] = _execution }, stranger);

        result.TryGetProperty("error", out _).Should().BeTrue();
        _service.Verify(s => s.GetCapabilitiesAsync(It.IsAny<Project>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void WithExactPrice(double? exact)
    {
        _service.Setup(s => s.EstimateAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepChangeRequestResult.Previewed(new StepChangeEstimatePreviewResponse(
                new StepChangeEstimateResponse(true, 1, 2, 3, 100, true, exact, exact is null ? null : "Priced from the re-made lines at the current rate."),
                new StepChangeReplayResponse([new StepChangeReplayStepResponse(2, "Narration", "Run", "changed")]))));
    }

    [Fact]
    public async Task Propose_and_the_card_show_the_exact_price_of_the_redone_part_when_known()
    {
        WithExactPrice(1.5);

        JsonElement result = await CallAsync("ProposeStepChange", Args());
        result.GetProperty("targetExact").GetProperty("credits").GetDouble().Should().Be(1.5);
        result.GetProperty("targetExact").GetProperty("note").GetString().Should().NotBeNullOrWhiteSpace();

        ApprovalVerdict verdict = await CardAsync(Builder(), Args());
        verdict.Card!.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("This part costs exactly about 1.5 credits"));
        verdict.Card.CostCredits.Should().Be(3, "the card's cost stays the required figure");
    }

    [Fact]
    public async Task Without_an_exact_price_there_is_no_target_exact_and_no_line()
    {
        WithExactPrice(null);

        (await CallAsync("ProposeStepChange", Args())).TryGetProperty("targetExact", out JsonElement exact).Should().BeTrue();
        exact.ValueKind.Should().Be(JsonValueKind.Null);
        (await CardAsync(Builder(), Args())).Card!.Changes!.Select(c => c.Text).Should().NotContain(t => t.Contains("costs exactly"));
    }

    [Fact]
    public async Task Propose_prices_the_change_names_what_is_redone_and_reused_and_writes_nothing()
    {
        JsonElement result = await CallAsync("ProposeStepChange", Args());

        result.GetProperty("operation").GetString().Should().Be("StepChange");
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrEmpty();
        result.GetProperty("estimate").GetProperty("required").GetDouble().Should().Be(3);
        result.GetProperty("willRedo")[0].GetString().Should().Be("Narration");
        result.GetProperty("willReuse")[0].GetString().Should().Be("Script");
        result.GetProperty("items")[0].GetProperty("text").GetString().Should().Be("Welcome to Launch");
        _requests.Should().Be(0);
        _service.Verify(s => s.RequestAsync(It.IsAny<Project>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>(), It.IsAny<ExecutionInitiator>()), Times.Never);
        _db.RunRateEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Propose_passes_the_services_refusal_through_with_its_code()
    {
        _service.Setup(s => s.EstimateAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepChangeRequestResult.Refuse(400, "CHANGE_STEP_NOT_SUPPORTED", "This step cannot be changed."));

        JsonElement result = await CallAsync("ProposeStepChange", Args());

        result.GetProperty("code").GetString().Should().Be("CHANGE_STEP_NOT_SUPPORTED");
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_with_a_good_token_queues_the_change_as_a_paid_assistant_run_and_returns_the_new_run()
    {
        string token = await TokenAsync();

        JsonElement result = await CallAsync("ApplyStepChange", Args(token));

        result.GetProperty("executionId").GetGuid().Should().NotBeEmpty();
        result.GetProperty("changeRequestId").GetGuid().Should().NotBeEmpty();
        result.GetProperty("status").GetString().Should().Be("Queued");
        _requests.Should().Be(1);
        _db.RunRateEvents.Count(e => e.Source == RunRateEvent.RevisionSource && e.Bucket == RunRateEvent.PaidBucket).Should().Be(1);
        _service.Verify(s => s.RequestAsync(It.IsAny<Project>(), _execution, _user, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>(), ExecutionInitiator.Assistant), Times.Once);
    }

    [Fact]
    public async Task Apply_through_the_mcp_uses_the_mcp_initiator()
    {
        string token = await TokenAsync();

        await CallAsync("ApplyStepChange", Args(token), initiator: ExecutionInitiator.Mcp);

        _service.Verify(s => s.RequestAsync(It.IsAny<Project>(), _execution, _user, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>(), ExecutionInitiator.Mcp), Times.Once);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("")]
    public async Task Apply_refuses_a_bad_token_and_queues_nothing(string token)
    {
        JsonElement result = await CallAsync("ApplyStepChange", Args(token));

        result.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task Apply_refuses_a_stale_token_when_the_instruction_items_or_overrides_changed()
    {
        string token = await TokenAsync(Overrides(("speed", 1.1)));

        (await CallAsync("ApplyStepChange", Args(token, instruction: "Faster", overrides: Overrides(("speed", 1.1))))).GetProperty("error").GetString().Should().Contain("stale");
        (await CallAsync("ApplyStepChange", Args(token, overrides: Overrides(("speed", 1.2))))).GetProperty("error").GetString().Should().Contain("stale");
        (await CallAsync("ApplyStepChange", Args(token, items: new[] { "line-2" }, overrides: Overrides(("speed", 1.1))))).GetProperty("error").GetString().Should().Contain("stale");
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task The_token_is_the_same_for_reordered_override_keys_and_differs_for_a_changed_value()
    {
        string a = await TokenAsync(Overrides(("speed", 1.1), ("pitch", 2)));
        string b = await TokenAsync(Overrides(("pitch", 2), ("speed", 1.1)));
        string c = await TokenAsync(Overrides(("speed", 1.1), ("pitch", 3)));

        b.Should().Be(a);
        c.Should().NotBe(a);
        (await CallAsync("ApplyStepChange", Args(a, overrides: Overrides(("pitch", 2), ("speed", 1.1))))).TryGetProperty("changeRequestId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_refuses_a_caller_without_project_permission()
    {
        string token = await TokenAsync();

        JsonElement result = await CallAsync("ApplyStepChange", Args(token), Tools(role: OrganizationRole.Billing));

        result.TryGetProperty("error", out _).Should().BeTrue();
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task Apply_for_a_foreign_project_is_absent_and_queues_nothing()
    {
        string token = await TokenAsync();

        JsonElement result = await CallAsync("ApplyStepChange", Args(token), Tools(Guid.NewGuid()));

        result.TryGetProperty("error", out _).Should().BeTrue();
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task Apply_with_an_unverified_email_is_refused_before_anything_runs()
    {
        string token = await TokenAsync();

        JsonElement result = await CallAsync("ApplyStepChange", Args(token), Tools(verified: false));

        result.TryGetProperty("error", out _).Should().BeTrue();
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task Apply_with_a_full_paid_bucket_is_refused_with_RUN_RATE_LIMITED()
    {
        string token = await TokenAsync();
        for (int i = 0; i < 10; i++)
            _db.RunRateEvents.Add(new RunRateEvent { Id = Guid.NewGuid(), UserId = _user, ProjectId = _project.Id, Bucket = "paid", Source = "execute", CreatedAt = DateTime.UtcNow.AddMinutes(-5 - i) });
        _db.SaveChanges();

        JsonElement result = await CallAsync("ApplyStepChange", Args(token));

        result.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task The_services_refusal_on_apply_keeps_its_code_and_extra_fields()
    {
        string token = await TokenAsync();
        _service.Setup(s => s.RequestAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>(), It.IsAny<ExecutionInitiator>()))
            .ReturnsAsync(StepChangeRequestResult.Refuse(402, "INSUFFICIENT_CREDITS", "Not enough credits.", new Dictionary<string, object?> { ["required"] = 3.0 }));

        JsonElement result = await CallAsync("ApplyStepChange", Args(token));

        result.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        result.GetProperty("required").GetDouble().Should().Be(3);
    }

    // ------------------------------------------------------------------ the approval card

    private AssistantApprovalCardBuilder Builder()
    {
        var caller = new Caller(_user);
        return new AssistantApprovalCardBuilder(_db, TestTenancy.Access(_db, caller), caller, stepChanges: _service.Object);
    }

    private Task<ApprovalVerdict> CardAsync(AssistantApprovalCardBuilder builder, Dictionary<string, object?> args)
    {
        AIFunctionArguments fa = new();
        foreach ((string key, object? value) in args)
            fa[key] = value is Guid g ? g.ToString() : value;
        return builder.BuildAsync("ApplyStepChange", fa, new AssistantToolContext(_user, _project.Id), null, CancellationToken.None);
    }

    [Fact]
    public async Task The_card_names_the_step_the_item_the_instruction_the_price_and_what_is_redone_and_reused()
    {
        ApprovalVerdict verdict = await CardAsync(Builder(), Args());

        ApprovalCard card = verdict.Card!;
        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.Title.Should().Be("Change steps of this run");
        card.Summary.Should().Contain("Narration").And.Contain("Launch");
        card.Cost.Should().NotBeNullOrEmpty();
        card.CostCredits.Should().NotBeNull("auto-approve mode judges the price against the user's cost limit");
        string[] lines = card.Changes!.Select(c => c.Text).ToArray();
        lines.Should().Contain(l => l.Contains("Welcome to Launch"));
        lines.Should().Contain(l => l.Contains("Slower"));
        lines.Should().Contain(l => l.Contains("Redone: Narration") && l.Contains("Reused at no cost: Script"));
    }

    [Fact]
    public async Task The_card_for_a_script_scene_shows_the_instruction_the_scene_and_the_most_expensive_line()
    {
        Dictionary<string, object?> args = Args(instruction: "make scene 2 calmer", items: ["sc2"]);
        args["stepOrder"] = 1;

        ApprovalVerdict verdict = await CardAsync(Builder(), args);

        string[] lines = verdict.Card!.Changes!.Select(c => c.Text).ToArray();
        lines.Should().Contain(l => l.Contains("make scene 2 calmer"));
        lines.Should().Contain(l => l.StartsWith("Item:") && l.Contains("harbour at dawn") && !l.Contains("what the film is about in a long sentence."));
        lines.Should().Contain(l => l.Contains("most expensive option"));
    }

    [Fact]
    public void Scene_keys_are_described_in_plain_words()
    {
        AssistantChangeRequestTools.OverrideWords("Agent", "voiceover").Should().Contain("narration");
        AssistantChangeRequestTools.OverrideWords("Agent", "onScreenText").Should().Contain("screen");
        AssistantChangeRequestTools.OverrideWords("Agent", "visualDescription").Should().Contain("see");
        var overrides = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["sc2"] = new()
            {
                ["voiceover"] = JsonSerializer.SerializeToElement("Calm words."),
                ["onScreenText"] = JsonSerializer.SerializeToElement(new[] { "Dawn", "Harbour" }),
                ["visualDescription"] = JsonSerializer.SerializeToElement("Still water"),
            },
        };
        string line = AssistantChangeRequestTools.DescribeOverrides(overrides, id => id).Single();
        line.Should().Contain("new narration \"Calm words.\"").And.Contain("Dawn / Harbour").And.Contain("what we see: \"Still water\"");
    }

    [Fact]
    public void Long_texts_show_600_characters_and_say_how_much_was_left_out()
    {
        string longText = new('x', 1500);
        var overrides = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["a"] = new() { ["voiceover"] = JsonSerializer.SerializeToElement(longText), ["prompt"] = JsonSerializer.SerializeToElement(new string('y', 600)) },
        };
        string line = AssistantChangeRequestTools.DescribeOverrides(overrides, id => id).Single();
        line.Should().Contain(new string('x', 600)).And.NotContain(new string('x', 601))
            .And.Contain("(shortened: showing 600 of 1500 characters)");
        line.Should().NotContain("showing 600 of 600", "a text that fits is shown whole");
        line.Length.Should().BeLessThan(1500);
    }

    [Fact]
    public async Task The_card_shows_the_new_values_not_only_which_settings_change()
    {
        var overrides = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["line-1"] = new()
            {
                ["text"] = JsonSerializer.SerializeToElement("A calmer opening line."),
                ["speed"] = JsonSerializer.SerializeToElement(1.25),
                ["pronunciation"] = JsonSerializer.SerializeToElement(new[] { new { term = "Novara", spelling = "Novàra" } }),
            },
        };

        ApprovalVerdict verdict = await CardAsync(Builder(), Args(overrides: overrides));

        string[] lines = verdict.Card!.Changes!.Select(c => c.Text).ToArray();
        lines.Should().Contain(l => l.Contains("A calmer opening line.") && l.Contains("speed 1.25") && l.Contains("Novara said as Novàra"));
    }

    [Fact]
    public void The_card_describes_clip_music_and_thumbnail_values_in_words_and_ignores_an_unknown_key()
    {
        var overrides = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["v1"] = new()
            {
                ["prompt"] = JsonSerializer.SerializeToElement(new string('x', 900)),
                ["retake"] = JsonSerializer.SerializeToElement(true),
            },
            ["a1"] = new()
            {
                ["prompt"] = JsonSerializer.SerializeToElement("Calmer piano"),
                ["durationSec"] = JsonSerializer.SerializeToElement(45),
            },
            ["thumbnail"] = new()
            {
                ["overlayText"] = JsonSerializer.SerializeToElement("Launch day"),
                ["atTimeSec"] = JsonSerializer.SerializeToElement(12.5),
                ["overlayPosition"] = JsonSerializer.SerializeToElement("LowerThird"),
                ["overlayFontColor"] = JsonSerializer.SerializeToElement("yellow"),
                ["overlayBoxColor"] = JsonSerializer.SerializeToElement("black@0.45"),
                ["somethingNew"] = JsonSerializer.SerializeToElement("whatever"),
            },
            ["*"] = new() { ["retake"] = JsonSerializer.SerializeToElement(false) },
        };

        string[] lines = AssistantChangeRequestTools.DescribeOverrides(overrides, id => id).ToArray();

        lines.Should().Contain(l => l.StartsWith("v1: new description \"xxx") && l.Contains("(shortened: showing 600 of 900 characters)") && l.EndsWith("a fresh take"));
        lines.Single(l => l.StartsWith("v1:")).Length.Should().BeLessThan(700, "a long prompt is shortened on the card");
        lines.Should().Contain("a1: length 45 seconds; new description \"Calmer piano\"");
        lines.Should().Contain("thumbnail: taken at 12.5 seconds; box behind the text: black at 45% opacity; text colour yellow; text position lower third; thumbnail text \"Launch day\"");
        lines.Should().NotContain(l => l.Contains("somethingNew") || l.Contains("whatever"));
        lines.Should().NotContain(l => l.StartsWith("All items"), "retake false asks for nothing");
    }

    [Fact]
    public async Task A_long_new_wording_is_shown_up_to_600_characters_with_a_note()
    {
        string longText = new('a', 900);
        var overrides = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["line-1"] = new() { ["text"] = JsonSerializer.SerializeToElement(longText) },
        };

        ApprovalVerdict verdict = await CardAsync(Builder(), Args(overrides: overrides));

        verdict.Card!.Changes!.Select(c => c.Text).Should().Contain(l => l.Contains("shortened: showing 600 of 900 characters"));
    }

    [Fact]
    public async Task Many_items_never_push_the_new_values_and_the_redone_line_off_the_card()
    {
        string[] ids = Enumerable.Range(1, 14).Select(i => $"line-{i}").ToArray();
        var overrides = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["line-1"] = new() { ["speed"] = JsonSerializer.SerializeToElement(1.2) },
        };

        ApprovalVerdict verdict = await CardAsync(Builder(), Args(items: ids, overrides: overrides));

        string[] lines = verdict.Card!.Changes!.Select(c => c.Text).ToArray();
        lines.Should().Contain(l => l.Contains("speed 1.2"));
        lines.Should().Contain(l => l.StartsWith("Redone:"));
    }

    [Fact]
    public async Task The_card_refuses_a_step_it_cannot_place()
    {
        Dictionary<string, object?> args = Args();
        args["stepOrder"] = 9;

        ApprovalVerdict verdict = await CardAsync(Builder(), args);

        verdict.Card.Should().BeNull();
        verdict.Refusal.Should().NotBeNull();
    }

    [Fact]
    public async Task The_card_refuses_the_film_step_and_a_foreign_project()
    {
        Dictionary<string, object?> film = Args();
        film["stepOrder"] = 5;
        (await CardAsync(Builder(), film)).Card.Should().BeNull();

        var stranger = new Caller(Guid.NewGuid());
        var builder = new AssistantApprovalCardBuilder(_db, TestTenancy.Access(_db, stranger), stranger, stepChanges: _service.Object);
        (await CardAsync(builder, Args())).Card.Should().BeNull();
    }

    [Fact]
    public void Captions_cover_the_three_tools_in_plain_words()
    {
        AssistantChangeRequestTools.Captions.Keys.Should().BeEquivalentTo("GetChangeOptions", "ProposeStepChange", "ApplyStepChange");
        AssistantChangeRequestTools.Captions.Values.Should().OnlyContain(c => !c.Contains("Step") && c.Length > 5);
    }

    private static StepChangeTargetRequest[] TwoChanges(string second = "Make the title bolder") =>
    [
        new(2, null, "Slower", ["line-1"], null),
        new(1, null, second, ["sc2"], null),
    ];

    private Dictionary<string, object?> ManyArgs(StepChangeTargetRequest[] changes, string? token = null)
    {
        Dictionary<string, object?> args = new() { ["executionId"] = _execution, ["changes"] = changes };
        if (token is not null) args["confirmationToken"] = token;
        return args;
    }

    [Fact]
    public async Task Propose_with_changes_sends_targets_once_and_returns_one_estimate_and_every_change()
    {
        JsonElement result = await CallAsync("ProposeStepChange", ManyArgs(TwoChanges()));

        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("estimate").GetProperty("high").GetDouble().Should().Be(2);
        JsonElement[] shown = result.GetProperty("changes").EnumerateArray().ToArray();
        shown.Select(c => c.GetProperty("step").GetString()).Should().Equal("Narration", "Script");
        shown[0].GetProperty("instruction").GetString().Should().Be("Slower");
        _service.Verify(s => s.EstimateAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid?>(),
            It.Is<CreateStepChangeRequest>(b => b.Targets != null && b.Targets.Count == 2 && b.StepOrder == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Apply_with_changes_and_the_matching_token_starts_one_run_even_if_the_list_is_reordered()
    {
        string token = (await CallAsync("ProposeStepChange", ManyArgs(TwoChanges()))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await CallAsync("ApplyStepChange", ManyArgs(TwoChanges().Reverse().ToArray(), token), initiator: ExecutionInitiator.Mcp);

        result.TryGetProperty("executionId", out _).Should().BeTrue();
        _requests.Should().Be(1);
        _service.Verify(s => s.RequestAsync(It.IsAny<Project>(), _execution, _user, It.IsAny<Guid?>(),
            It.Is<CreateStepChangeRequest>(b => b.Targets != null && b.Targets.Count == 2), It.IsAny<CancellationToken>(), ExecutionInitiator.Mcp), Times.Once);
    }

    [Fact]
    public async Task Apply_with_a_changed_entry_is_refused_and_starts_nothing()
    {
        string token = (await CallAsync("ProposeStepChange", ManyArgs(TwoChanges()))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await CallAsync("ApplyStepChange", ManyArgs(TwoChanges("Something else"), token));

        result.TryGetProperty("error", out _).Should().BeTrue();
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task Both_shapes_at_once_are_refused_by_propose_and_apply()
    {
        Dictionary<string, object?> both = Args();
        both["changes"] = TwoChanges();

        (await CallAsync("ProposeStepChange", both)).TryGetProperty("error", out _).Should().BeTrue();
        both["confirmationToken"] = "x";
        (await CallAsync("ApplyStepChange", both)).TryGetProperty("error", out _).Should().BeTrue();
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task The_card_is_not_built_when_changes_and_a_single_step_argument_are_both_sent()
    {
        Dictionary<string, object?> both = ManyArgs(TwoChanges());
        both["stepOrder"] = 2;

        ApprovalVerdict verdict = await CardAsync(Builder(), both);

        verdict.Card.Should().BeNull("Apply refuses a mixed call, so no card may be shown for it");
        verdict.Refusal.Should().NotBeNull();
    }

    [Fact]
    public async Task A_null_entry_in_changes_is_refused_by_both_tools_before_anything_is_written()
    {
        StepChangeTargetRequest?[] withNull = [TwoChanges()[0], null];

        (await CallAsync("ProposeStepChange", ManyArgs(withNull!))).TryGetProperty("error", out _).Should().BeTrue();
        (await CallAsync("ApplyStepChange", ManyArgs(withNull!, "x"))).TryGetProperty("error", out _).Should().BeTrue();
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task A_film_entry_and_a_voiceover_entry_go_through_and_the_card_lists_both_with_one_total()
    {
        StepChangeStepCapabilityResponse voice = new(2, _stepResult, "Voiceover", "Narration", true, null, null, "Line",
            [new StepChangeItemResponse("line-1", "Completed", 3.2, "Welcome to Launch")], [], null, false, false);
        StepChangeStepCapabilityResponse film = new(5, Guid.NewGuid(), "Revise", "Final film", false, "film", "composition-revision", "None", [], [], null, false, true);
        _service.Setup(s => s.GetCapabilitiesAsync(It.IsAny<Project>(), _execution, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StepChangeCapabilitiesResponse(_execution, true, [voice, film]));
        StepChangeTargetRequest[] changes = [new(2, null, "Slower", ["line-1"], null), new(5, null, "Add a title card", null, null)];

        JsonElement proposal = await CallAsync("ProposeStepChange", ManyArgs(changes));
        ApprovalVerdict verdict = await CardAsync(Builder(), ManyArgs(changes));

        proposal.GetProperty("changes").GetArrayLength().Should().Be(2);
        string[] lines = verdict.Card!.Changes!.Select(c => c.Text).ToArray();
        lines.Should().Contain(t => t.Contains("Narration")).And.Contain(t => t.Contains("Final film")).And.Contain(t => t.Contains("Add a title card"));
        verdict.Card.CostCredits.Should().Be(3, "one combined estimate, not one per change");
        _service.Verify(s => s.EstimateAsync(It.IsAny<Project>(), _execution, It.IsAny<Guid?>(), It.IsAny<CreateStepChangeRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task The_card_refuses_a_change_it_cannot_place_and_a_foreign_project()
    {
        StepChangeTargetRequest[] unknown = [new(2, null, "Slower", ["line-1"], null), new(9, null, "x", null, null)];
        (await CardAsync(Builder(), ManyArgs(unknown))).Card.Should().BeNull();

        var stranger = new Caller(Guid.NewGuid());
        var builder = new AssistantApprovalCardBuilder(_db, TestTenancy.Access(_db, stranger), stranger, stepChanges: _service.Object);
        (await CardAsync(builder, ManyArgs(TwoChanges()))).Card.Should().BeNull();
        (await CallAsync("ProposeStepChange", ManyArgs(TwoChanges()), Tools(Guid.NewGuid()))).TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Authority_is_read_again_on_every_apply_with_changes()
    {
        string token = (await CallAsync("ProposeStepChange", ManyArgs(TwoChanges()))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await CallAsync("ApplyStepChange", ManyArgs(TwoChanges(), token), Tools(verified: false));

        result.TryGetProperty("error", out _).Should().BeTrue();
        _requests.Should().Be(0);
    }

    [Fact]
    public void Both_tools_expose_changes_and_say_when_to_use_it()
    {
        var tools = Tools().GetTools(new AssistantToolContext(_user, _project.Id));
        foreach (string name in new[] { "ProposeStepChange", "ApplyStepChange" })
        {
            AIFunction fn = tools.Single(t => t.Name == name);
            fn.JsonSchema.GetProperty("properties").TryGetProperty("changes", out _).Should().BeTrue();
            fn.Description.Should().Contain("several steps").And.Contain("`changes`");
        }
    }

    [Fact]
    public async Task A_card_for_six_changes_shows_every_headline_and_instruction_and_the_total()
    {
        StepChangeStepCapabilityResponse[] steps = Enumerable.Range(1, 6).Select(n => new StepChangeStepCapabilityResponse(
            n, Guid.NewGuid(), "VideoGenerate", $"Clip step {n}", true, null, null, "Shot",
            [new StepChangeItemResponse("v1", "Completed", 2, $"shot {n}")],
            [new StepChangeOverrideResponse("prompt", "Text", null, null, 2000, null)])).ToArray();
        _service.Setup(s => s.GetCapabilitiesAsync(It.IsAny<Project>(), _execution, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StepChangeCapabilitiesResponse(_execution, true, steps));
        StepChangeTargetRequest[] changes = steps.Select(st => new StepChangeTargetRequest(
            st.StepOrder, null, $"instruction number {st.StepOrder}", ["v1"],
            new() { ["v1"] = new() { ["prompt"] = JsonSerializer.SerializeToElement($"long prompt {st.StepOrder}") } })).ToArray();

        ApprovalVerdict verdict = await CardAsync(Builder(), ManyArgs(changes));

        string[] lines = verdict.Card!.Changes!.Select(c => c.Text).ToArray();
        for (int n = 1; n <= 6; n++)
            lines.Should().Contain(t => t.Contains($"Clip step {n}") && t.Contains($"shot {n}")).And.Contain(t => t.Contains($"instruction number {n}"));
        lines.Should().Contain(t => t.StartsWith("Redone:"));
        verdict.Card.CostCredits.Should().Be(3);
    }
}
