using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Compositions.ReplayShapes;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// A change request on the script (docs/change-requests.md "Script (by scene)"): a fake chat agent, no network or paid call.
/// </summary>
public class ScriptChangeRequestTests
{
    private static readonly Guid RequestId = Guid.Parse("34343434-3434-3434-3434-343434343434");

    private const string Previous = """
        {"title":"Promo","durationInSeconds":30,"scenes":[
          {"sceneId":"sc1","startTime":0,"duration":10,"voiceover":"Meet the new way to make videos.","onScreenText":["Make videos","Fast"],"visualDescription":"A wide shot of a desk."},
          {"sceneId":"sc2","startTime":10,"duration":10,"voiceover":"It writes, speaks and edits for you.","onScreenText":["All in one"],"visualDescription":"Screen recording of the editor with a timeline."},
          {"sceneId":"sc3","startTime":20,"duration":10,"voiceover":"Try it today.","onScreenText":["Try it"],"visualDescription":"Logo on a clean background."}
        ],"narrative":"A short promo."}
        """;

    private static ChangeRequestPayload Payload(string? instruction = null, string[]? items = null, string? overridesJson = null) =>
        new(instruction, items, overridesJson is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(overridesJson)!
                .ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, JsonElement>)p.Value));

    private sealed class FixedContext(StepChangeRequestScope? scope) : IStepChangeRequestContext
    {
        public Task<StepChangeRequestScope?> FindAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct) =>
            Task.FromResult(scope);
    }

    private sealed class Harness
    {
        public Mock<IReelBoltAgent> Agent { get; } = new();
        public List<string> Prompts { get; } = [];

        public Harness(string? modelOutput = null)
        {
            Agent.Setup(a => a.Name).Returns("ScriptwriterAgent");
            Agent.Setup(a => a.AgentType).Returns(AgentType.ScriptwriterAgent);
            Agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
            Agent.Setup(a => a.OutputSchemaType).Returns(typeof(ReelBolt.Shared.Data.OutputSchemas.ScriptwriterOutput));
            Agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .Callback<string, Guid?, CancellationToken>((p, _, _) => Prompts.Add(p))
                .ReturnsAsync(() => new AgentRunResult { Output = modelOutput ?? "{}", Success = true, TokensUsed = 120, InputTokens = 80, OutputTokens = 40 });
        }

        public async Task<StepExecutionResult> RunAsync(ChangeRequestPayload payload, string? previous = Previous, bool withScope = true, bool malformed = false, ReelBolt.Shared.Moderation.IContentModerationGate? moderation = null)
        {
            Mock<IAgentRegistry> registry = new();
            registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(Agent.Object);
            Mock<IWorkflowExecutionContextAccessor> accessor = new();
            accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);
            AgentStepExecutor executor = new(
                registry.Object, accessor.Object, Mock.Of<IMotionGraphicsPlacementAnnotator>(),
                new ConfigurationBuilder().Build(), Mock.Of<IDecisionGate>(), NullLogger<AgentStepExecutor>.Instance,
                changeRequests: new FixedContext(withScope ? new StepChangeRequestScope(RequestId, payload, previous) { Malformed = malformed } : null),
                moderation: moderation);

            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                AgentDefinitionId = Guid.NewGuid(),
                StepOrder = 2,
                StepType = StepType.Agent,
                AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.ScriptwriterAgent, Name = "ScriptwriterAgent" }
            };
            return await executor.ExecuteAsync(new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                StepOutputHistory = [],
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            });
        }
    }

    private static JsonArray Scenes(string json) => (JsonArray)JsonNode.Parse(json)!["scenes"]!;

    // ------------------------------------------------------------------
    // Structured overrides: direct, free
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_blocked_structured_override_text_fails_the_step_and_stores_nothing()
    {
        Harness h = new();
        Mock<ReelBolt.Shared.Moderation.IContentModerationGate> gate = new();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ReelBolt.Shared.Moderation.ModerationInput>(), It.IsAny<ReelBolt.Shared.Moderation.ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Moderation.ModerationDecision(ReelBolt.Shared.Moderation.ModerationAction.Block, true, false, "x", "Not allowed.", "h"));

        StepExecutionResult result = await h.RunAsync(Payload(overridesJson: """{"sc2":{"visualDescription":"something forbidden"}}"""), moderation: gate.Object);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CONTENT_BLOCKED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.Output.Should().NotContain("forbidden");
        gate.Verify(g => g.ModerateAsync(It.Is<ReelBolt.Shared.Moderation.ModerationInput>(i => i.Text!.Contains("something forbidden")), It.IsAny<ReelBolt.Shared.Moderation.ModerationAttribution>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // QA pin (PR #619 verification, uncommitted): a MIXED request (a structured override on one scene plus an
    // instruction naming another) must screen the structured text too (docs/change-requests.md: "Texts a structured
    // script change writes ... go through the content moderation gate before any output is stored").
    [Fact]
    public async Task Qa_a_blocked_structured_override_in_a_mixed_request_is_screened_before_the_model_call()
    {
        Harness h = new(GreedyRewrite);
        Mock<ReelBolt.Shared.Moderation.IContentModerationGate> gate = new();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ReelBolt.Shared.Moderation.ModerationInput>(), It.IsAny<ReelBolt.Shared.Moderation.ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Moderation.ModerationDecision(ReelBolt.Shared.Moderation.ModerationAction.Block, true, false, "x", "Not allowed.", "h"));

        StepExecutionResult result = await h.RunAsync(
            Payload("make scene 2 calmer", ["sc2"], """{"sc1":{"visualDescription":"something forbidden"}}"""), moderation: gate.Object);

        result.Output.Should().NotContain("something forbidden", "the owner's structured text is never stored unscreened");
        result.FailureCode.Should().Be("CONTENT_BLOCKED");
    }

    [Fact]
    public async Task An_unavailable_moderation_gate_fails_open_for_a_structured_override()
    {
        Harness h = new();
        Mock<ReelBolt.Shared.Moderation.IContentModerationGate> gate = new();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ReelBolt.Shared.Moderation.ModerationInput>(), It.IsAny<ReelBolt.Shared.Moderation.ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Moderation.ModerationDecision(ReelBolt.Shared.Moderation.ModerationAction.Allow, false, false, null, string.Empty, "h"));

        StepExecutionResult result = await h.RunAsync(Payload(overridesJson: """{"sc2":{"voiceover":"A fine line."}}"""), moderation: gate.Object);

        result.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task A_failed_model_call_never_stores_the_raw_model_output()
    {
        Harness h = new("RAW MODEL TEXT");
        h.Agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = "RAW MODEL TEXT", Success = false, FailureReason = "boom" });

        StepExecutionResult result = await h.RunAsync(Payload(instruction: "Make scene 2 punchier", items: ["sc2"]));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("SCRIPT_REWRITE_INVALID");
        result.Output.Should().BeEmpty();
        result.ErrorDetails.Should().NotContain("RAW MODEL TEXT");
    }

    [Fact]
    public async Task A_structured_override_changes_exactly_that_scene_and_makes_no_model_call()
    {
        Harness h = new();

        StepExecutionResult result = await h.RunAsync(Payload(overridesJson: """{"sc2":{"voiceover":"It does the whole job for you.","onScreenText":"Everything\nIn one place"}}"""));

        result.Status.Should().Be(StepStatus.Completed);
        h.Agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        result.TokensUsed.Should().Be(0);
        JsonArray before = Scenes(Previous);
        JsonArray after = Scenes(result.Output);
        after.Count.Should().Be(3);
        after[0]!.ToJsonString().Should().Be(before[0]!.ToJsonString());
        after[2]!.ToJsonString().Should().Be(before[2]!.ToJsonString());
        after[1]!["voiceover"]!.GetValue<string>().Should().Be("It does the whole job for you.");
        after[1]!["onScreenText"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("Everything", "In one place");
        after[1]!["visualDescription"]!.GetValue<string>().Should().Be(before[1]!["visualDescription"]!.GetValue<string>(), "a field the request did not set stays");
        after[1]!["sceneId"]!.GetValue<string>().Should().Be("sc2");
        after[1]!["startTime"]!.GetValue<int>().Should().Be(10);

        JsonNode root = JsonNode.Parse(result.Output)!;
        root["title"]!.GetValue<string>().Should().Be("Promo");
        root["changeRequest"]!["requestId"]!.GetValue<string>().Should().Be(RequestId.ToString("D"));
        root["changeRequest"]!["redoneSceneIds"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("sc2");
        root["changeRequest"]!["reusedSceneCount"]!.GetValue<int>().Should().Be(2);
        ScriptChange.IsValidScript(result.Output, 3).Should().BeTrue("downstream (Voiceover ScriptScenes, the author) reads it unchanged");
    }

    [Fact]
    public async Task A_star_override_applies_to_every_scene()
    {
        StepExecutionResult result = await new Harness().RunAsync(Payload(overridesJson: """{"*":{"visualDescription":"Dark theme."}}"""));

        Scenes(result.Output).Select(s => s!["visualDescription"]!.GetValue<string>()).Should().OnlyContain(v => v == "Dark theme.");
        JsonNode.Parse(result.Output)!["changeRequest"]!["redoneSceneIds"]!.AsArray().Count.Should().Be(3);
    }

    // ------------------------------------------------------------------
    // Instruction path: one model call, enforced in code
    // ------------------------------------------------------------------

    private const string GreedyRewrite = """
        {"title":"A different title","durationInSeconds":45,"scenes":[
          {"sceneId":"s1","startTime":0,"duration":15,"voiceover":"Changed scene one too.","onScreenText":["x"],"visualDescription":"changed"},
          {"sceneId":"s2","startTime":15,"duration":15,"voiceover":"A calmer, slower line.","onScreenText":["Calm"],"visualDescription":"Slow pan across the editor."},
          {"sceneId":"s3","startTime":30,"duration":15,"voiceover":"Changed scene three too.","onScreenText":["y"],"visualDescription":"changed"}
        ],"narrative":"Rewritten."}
        """;

    [Fact]
    public async Task A_malformed_stored_request_is_refused_before_any_model_call()
    {
        Harness h = new(GreedyRewrite);

        StepExecutionResult result = await h.RunAsync(Payload("make scene 2 calmer"), malformed: true);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        h.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_instruction_calls_the_model_once_and_the_unnamed_scenes_come_back_verbatim_with_their_ids()
    {
        Harness h = new(GreedyRewrite);

        StepExecutionResult result = await h.RunAsync(Payload("make scene 2 calmer", ["sc2"]));

        result.Status.Should().Be(StepStatus.Completed);
        h.Agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Prompts.Single().Should().Contain("make scene 2 calmer").And.Contain("sc2").And.Contain("Meet the new way to make videos.");
        result.TokensUsed.Should().Be(120, "the metered usage is the agent's normal usage");
        result.InputTokens.Should().Be(80);

        JsonArray before = Scenes(Previous);
        JsonArray after = Scenes(result.Output);
        after[0]!.ToJsonString().Should().Be(before[0]!.ToJsonString(), "the model also changed scene 1: undone");
        after[2]!.ToJsonString().Should().Be(before[2]!.ToJsonString(), "the model also changed scene 3: undone");
        after[1]!["sceneId"]!.GetValue<string>().Should().Be("sc2", "the model renumbered its scenes; the ids stay");
        after[1]!["startTime"]!.GetValue<int>().Should().Be(10);
        after[1]!["duration"]!.GetValue<int>().Should().Be(10);
        after[1]!["voiceover"]!.GetValue<string>().Should().Be("A calmer, slower line.");
        JsonNode root = JsonNode.Parse(result.Output)!;
        root["title"]!.GetValue<string>().Should().Be("Promo");
        root["durationInSeconds"]!.GetValue<int>().Should().Be(30);
        root["changeRequest"]!["redoneSceneIds"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("sc2");
        ScriptChange.IsValidScript(result.Output, 3).Should().BeTrue();
    }

    [Fact]
    public async Task A_rewrite_that_changes_the_number_of_scenes_is_refused()
    {
        Harness h = new("""{"title":"T","durationInSeconds":30,"scenes":[{"sceneId":"sc1","startTime":0,"duration":10,"voiceover":"One.","onScreenText":[],"visualDescription":"v"}],"narrative":"n"}""");

        StepExecutionResult result = await h.RunAsync(Payload("shorten it", ["sc1"]));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("SCRIPT_REWRITE_INVALID");
        result.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        result.Retryable.Should().BeFalse("a model answer that is not a script is not retried; the spend is refunded");
        result.Output.Should().BeEmpty("the raw model answer is never stored on this failure");
        result.ArtifactStorageKey.Should().BeNull();
        result.OutputStorageKey.Should().BeNull();
        result.Reasoning.Should().BeEmpty();
        result.ToolCalls.Should().BeEmpty();
        result.ErrorDetails.Should().StartWith("SCRIPT_REWRITE_INVALID:").And.NotContain("One.");
        result.ErrorDetails.Should().Contain("number of scenes");
        result.TokensUsed.Should().Be(120, "the call was made and is metered");
        ReelBolt.WorkflowEngine.Services.Billing.FailureClassifier.ForCode("SCRIPT_REWRITE_INVALID").Should().Be(FailureClass.PlatformProviderError);
        ReelBolt.WorkflowEngine.Services.Billing.FailureClassifier.ForCode("CHANGE_NOT_APPLIED").Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task A_request_that_names_no_scene_of_the_script_is_not_applied_and_calls_no_model()
    {
        Harness h = new(GreedyRewrite);

        StepExecutionResult none = await h.RunAsync(Payload("make it calmer", ["sc9"]));
        StepExecutionResult override9 = await h.RunAsync(Payload(overridesJson: """{"sc9":{"voiceover":"x"}}"""));

        foreach (StepExecutionResult r in new[] { none, override9 })
        {
            r.Status.Should().Be(StepStatus.Failed);
            r.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
            r.FailureClass.Should().Be(FailureClass.UserInput);
        }

        h.Agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Without_a_change_request_the_step_runs_as_it_always_did()
    {
        Harness h = new(Previous);

        StepExecutionResult result = await h.RunAsync(Payload("x", ["sc1"]), withScope: false);

        result.Status.Should().Be(StepStatus.Completed);
        h.Agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Prompts.Single().Should().NotContain("revision of an existing video script");
    }

    // ------------------------------------------------------------------
    // Capability per agent type
    // ------------------------------------------------------------------

    [Fact]
    public void The_scriptwriter_is_supported_by_scene_and_a_plain_agent_step_is_not()
    {
        StepChangeCapability script = StepChangeCapabilities.For(StepType.Agent, AgentType.ScriptwriterAgent);

        script.Supported.Should().BeTrue();
        script.ItemKind.Should().Be(StepItemKind.Scene);
        script.Overrides.Select(o => o.Key).Should().Equal("voiceover", "onScreenText", "visualDescription");
        script.Overrides.Single(o => o.Key == "voiceover").MaxLength.Should().Be(1000);
        script.Overrides.Single(o => o.Key == "onScreenText").MaxLength.Should().Be(600);
        script.CostNote.Should().Be("script");
        StepChangeCapabilities.For(StepType.Agent).Supported.Should().BeFalse("without the agent type the step is still not supported");
        StepChangeCapabilities.For(StepType.Agent, AgentType.DirectorAgent).Supported.Should().BeFalse();
        StepChangeCapabilities.For(StepType.Voiceover, AgentType.ScriptwriterAgent).Should().BeSameAs(StepChangeCapabilities.For(StepType.Voiceover));
    }

    [Theory]
    [InlineData(AgentType.AuthorAgent)]
    [InlineData(AgentType.CompositionReviser)]
    [InlineData(AgentType.RemotionComponentTranslator)]
    [InlineData(AgentType.GraphicDesigner)]
    public void The_agents_that_make_the_film_route_to_a_composition_revision(AgentType agent)
    {
        StepChangeCapability cap = StepChangeCapabilities.ForAgent(agent);

        cap.Supported.Should().BeFalse();
        cap.Route.Should().Be("composition-revision");
        cap.NotSupportedReason.Should().NotBeNullOrWhiteSpace();
    }

    public static IEnumerable<object[]> AllAgentTypes() => Enum.GetValues<AgentType>().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllAgentTypes))]
    public void Every_agent_type_has_a_deliberate_entry(AgentType agent)
    {
        StepChangeCapability cap = StepChangeCapabilities.ForAgent(agent);

        cap.Supported.Should().Be(agent == AgentType.ScriptwriterAgent, "an agent type flips to Supported together with its executor and this expectation");
        if (cap.Supported)
            cap.Overrides.Should().NotBeEmpty();
        else
            cap.NotSupportedReason.Should().NotBeNullOrWhiteSpace("a NotSupported agent type states why");
    }

    [Fact]
    public void Every_supported_agent_type_is_applied_by_a_registered_executor()
    {
        HashSet<AgentType> applied = typeof(IChangeRequestAwareStepExecutor).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IChangeRequestAwareStepExecutor).IsAssignableFrom(t) && typeof(IStepExecutor).IsAssignableFrom(t))
            .Select(t => (IChangeRequestAwareStepExecutor)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t))
            .Where(e => e.StepType == StepType.Agent)
            .SelectMany(e => e.SupportedAgentTypes)
            .ToHashSet();

        foreach (AgentType agent in Enum.GetValues<AgentType>().Where(a => StepChangeCapabilities.ForAgent(a).Supported))
            applied.Should().Contain(agent, $"{agent} is Supported but no Agent executor applies it");
    }

    [Fact]
    public void The_scenes_are_listed_from_the_stored_output_by_their_own_ids()
    {
        IReadOnlyList<StepItem> items = StepChangeCapabilities.ListItems(StepType.Agent, AgentType.ScriptwriterAgent, Previous);

        items.Select(i => i.Id).Should().Equal("sc1", "sc2", "sc3");
        items[0].Text.Should().Be("Meet the new way to make videos.");
        StepChangeCapabilities.ListItems(StepType.Agent, AgentType.ScriptwriterAgent, "not json").Should().BeEmpty();
        StepChangeCapabilities.ListItems(StepType.Agent, AgentType.DirectorAgent, Previous).Should().BeEmpty();
        StepChangeCapabilities.ListItems(StepType.Agent, AgentType.ScriptwriterAgent, "Here you go:\n```json\n" + Previous + "\n```").Should().HaveCount(3, "prose around the JSON is tolerated");
        StepChangeCapabilities.ListItems(StepType.Agent, AgentType.ScriptwriterAgent, """{"scenes":[{"voiceover":"a"},{"voiceover":"b"}]}""")
            .Select(i => i.Id).Should().Equal("sc1", "sc2");
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    private static IReadOnlyList<ChangeRequestViolation> Validate(ChangeRequestPayload p) =>
        ChangeRequestValidator.Validate(StepType.Agent, AgentType.ScriptwriterAgent, p, Previous);

    [Fact]
    public void An_instruction_naming_a_scene_is_a_valid_request_and_an_instruction_alone_is_not()
    {
        Validate(Payload("make scene 2 calmer", ["sc2"])).Should().BeEmpty();
        Validate(Payload("make it calmer")).Should().ContainSingle(v => v.Path == "overrides" && v.Message.Contains("name the item"));
        Validate(Payload()).Should().NotBeEmpty();
        Validate(Payload("calmer", ["sc9"])).Should().ContainSingle(v => v.Path == "itemIds[0]");
    }

    [Fact]
    public void Scene_overrides_are_checked_against_the_closed_catalogue()
    {
        Validate(Payload(overridesJson: """{"sc2":{"voiceover":"New line.","onScreenText":["a","b"],"visualDescription":"v"}}""")).Should().BeEmpty();
        Validate(Payload(overridesJson: """{"*":{"onScreenText":"one\ntwo"}}""")).Should().BeEmpty("newline text is accepted");
        Validate(Payload(overridesJson: """{"sc2":{"voiceover":"" }}""")).Should().ContainSingle(v => v.Path == "overrides.sc2.voiceover");
        Validate(Payload(overridesJson: "{\"sc2\":{\"voiceover\":\"" + new string('a', 1001) + "\"}}")).Should().ContainSingle(v => v.Path == "overrides.sc2.voiceover");
        Validate(Payload(overridesJson: "{\"sc2\":{\"onScreenText\":\"" + new string('a', 601) + "\"}}")).Should().ContainSingle(v => v.Path == "overrides.sc2.onScreenText");
        Validate(Payload(overridesJson: """{"sc2":{"onScreenText":"1\n2\n3\n4\n5\n6\n7"}}""")).Should().ContainSingle(v => v.Path == "overrides.sc2.onScreenText" && v.Message.Contains("at most 6 lines"));
        Validate(Payload(overridesJson: """{"sc2":{"onScreenText":[1]}}""")).Should().ContainSingle(v => v.Path == "overrides.sc2.onScreenText");
        Validate(Payload(overridesJson: """{"sc2":{"voiceover":"a\nb"}}""")).Should().ContainSingle(v => v.Message.Contains("control"));
        Validate(Payload(overridesJson: """{"sc2":{"duration":3}}""")).Should().ContainSingle(v => v.Path == "overrides.sc2.duration");
        Validate(Payload(overridesJson: """{"sc9":{"voiceover":"x"}}""")).Should().ContainSingle(v => v.Path == "overrides.sc9");
    }

    [Fact]
    public void A_non_script_agent_step_is_refused_with_its_reason()
    {
        IReadOnlyList<ChangeRequestViolation> v = ChangeRequestValidator.Validate(StepType.Agent, AgentType.DirectorAgent, Payload("x", ["sc1"]), Previous);

        v.Should().ContainSingle(x => x.Path == "stepType");
    }

    // ------------------------------------------------------------------
    // Cost share
    // ------------------------------------------------------------------

    private const string Uneven = """
        {"scenes":[
          {"sceneId":"a","voiceover":"aaaaaaaaaa","visualDescription":"bbbbbbbbbb"},
          {"sceneId":"b","voiceover":"cccccccccccccccccccc","visualDescription":"dddddddddddddddddddd"}
        ]}
        """;

    [Fact]
    public void A_model_rewrite_holds_the_whole_script_and_a_structured_change_costs_about_nothing()
    {
        StepChangeScope.Compute(StepType.Agent, AgentType.ScriptwriterAgent, Payload("calmer", ["a"]), Uneven).Should().Be(1d);
        StepChangeScope.Compute(StepType.Agent, AgentType.ScriptwriterAgent, Payload("calmer", ["b"]), Uneven).Should().Be(1d);
        // mixed: structured a + instruction b -> a model call is made -> the full script
        StepChangeScope.Compute(StepType.Agent, AgentType.ScriptwriterAgent,
            Payload("calmer", ["b"], """{"a":{"voiceover":"x"}}"""), Uneven).Should().Be(1d);
        StepChangeScope.Compute(StepType.Agent, AgentType.ScriptwriterAgent, Payload("calmer", ["a", "b"]), Uneven).Should().Be(1d);
        StepChangeScope.Compute(StepType.Agent, AgentType.ScriptwriterAgent, Payload(overridesJson: """{"a":{"voiceover":"x"}}"""), Uneven)
            .Should().Be(ScriptChange.NoModelCallFraction).And.BeLessThan(1e-6);
        StepChangeScope.Compute(StepType.Agent, AgentType.ScriptwriterAgent, Payload(overridesJson: """{"*":{"voiceover":"x"}}"""), Uneven)
            .Should().Be(ScriptChange.NoModelCallFraction);
    }

    [Fact]
    public void An_unreadable_script_or_a_scene_without_text_holds_the_whole_step()
    {
        StepChangeScope.Compute(StepType.Agent, AgentType.ScriptwriterAgent, Payload("calmer", ["a"]), "not json").Should().Be(1d);
        StepChangeScope.Compute(StepType.Agent, AgentType.ScriptwriterAgent, Payload("calmer", ["a"]), """{"scenes":[{"sceneId":"a","voiceover":"x"},{"sceneId":"b"}]}""").Should().Be(1d);
        StepChangeScope.Compute(StepType.Agent, AgentType.DirectorAgent, Payload("calmer", ["a"]), Uneven).Should().Be(1d);
        StepChangeScope.Compute(StepType.Agent, Payload("calmer", ["a"]), Uneven).Should().Be(1d);
    }

    // ------------------------------------------------------------------
    // Replay on a promo-shaped workflow
    // ------------------------------------------------------------------

    [Fact]
    public void Changing_the_script_reuses_the_analysis_before_it_and_reruns_everything_after_it()
    {
        List<WorkflowStep> steps =
        [
            Step(1, AgentType.CodeStructureAnalyzer, mode: AgentInputContextMode.FullWorkflow),
            Step(2, AgentType.StyleAndThemeExtractor, mode: AgentInputContextMode.FullWorkflow),
            Step(3, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.FullWorkflow),
            Step(4, AgentType.DirectorAgent, mode: AgentInputContextMode.FullWorkflow),
            Step(5, AgentType.AuthorAgent, mode: AgentInputContextMode.FullWorkflow),
            Step(6, AgentType.ReviewAgent, mode: AgentInputContextMode.FullWorkflow)
        ];

        CompositionReplayPlan plan = CompositionReplayPlanner.Plan(steps, AllRan(steps), 3, CompositionCutPolicy.KeepCut, Guid.NewGuid(), Guid.NewGuid(),
            targetLabel: "the script", targetStepType: nameof(StepType.Agent));

        Dictionary<int, ReplayStepDisposition> d = plan.Steps.ToDictionary(s => s.StepOrder, s => s.Disposition);
        d[1].Should().Be(ReplayStepDisposition.Reuse);
        d[2].Should().Be(ReplayStepDisposition.Reuse);
        d[3].Should().Be(ReplayStepDisposition.Revise);
        d[4].Should().Be(ReplayStepDisposition.Run);
        d[5].Should().Be(ReplayStepDisposition.Run);
        d[6].Should().Be(ReplayStepDisposition.Run);
    }
}
