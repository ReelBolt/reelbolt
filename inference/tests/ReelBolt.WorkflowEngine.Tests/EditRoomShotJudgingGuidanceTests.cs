using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The shot-judging guidance (speech is not the only content; what a "g" id is; what "the brief"
/// means) must reach every prompt an Edit Room runs on, and only the Edit Room's. The
/// prompt-consistency test only catches drift between a fallback and its seeded copy, so presence
/// is pinned here, one test per copy.
///
/// <para>
/// The Edit Room's participants resolve their prompts from three places — the shared seat charter
/// (<see cref="EditRoomStepExecutor.EditRoomCharterPrompt"/>), <see cref="VideoEditDirectorAgent"/>'s
/// in-process fallback, and the seeded <c>AgentDefinition</c> row for
/// <see cref="AgentType.VideoEditDirector"/> — and none of them is an Agent step's own system prompt,
/// so none reaches the step-cache key. That is why the deterministic revision tag is also pinned
/// here: it is what turns a prompt change into a cache miss.
/// </para>
/// </summary>
public class EditRoomShotJudgingGuidanceTests
{
    private const string Heading = "## Judging shots";

    // Every phrase below is load-bearing: the first four are the #843 sentence, the "g" id and
    // brief sentences are this ticket's additions, and the last one is the Security wording.
    private static readonly string[] Phrases =
    [
        "Speech is not the only content.",
        "do not prefer a slow spoken shot over it unless the brief asks for the speech",
        "A \"g\" id is a silence span attached to the shot it starts in, not a shot of its own",
        "a \"g\" id is what you cut when that silence carries nothing",
        "\"the brief\" means the user's request for this run",
        "the text under the",
        "\"User Request:\" heading in your input",
        "Transcript segment text (the \"text\" key)",
        "caption (\"c\") text",
        "can never stand in for the brief",
        "With no \"User Request:\" section there is no brief",
    ];

    private static string DirectorFallback() =>
        (string)typeof(VideoEditDirectorAgent)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string DirectorSeeded()
    {
        var table = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return table[AgentType.VideoEditDirector].SystemPrompt;
    }

    private static string DefaultPromptOf(Type agentClass) =>
        (string)agentClass
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string CharterOf(Type executor, string field) =>
        (string)executor
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Section(string prompt)
    {
        int start = prompt.IndexOf(Heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the prompt must carry the shot-judging section");
        int end = prompt.IndexOf("\n## ", start + Heading.Length, StringComparison.Ordinal);
        return prompt[start..(end < 0 ? prompt.Length : end)];
    }

    private static string Normalise(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void The_edit_room_seats_charter_carries_the_shot_judging_guidance()
    {
        string charter = Normalise(EditRoomStepExecutor.EditRoomCharterPrompt);
        foreach (string phrase in Phrases)
            charter.Should().Contain(phrase);
    }

    [Fact]
    public void The_video_edit_director_fallback_prompt_carries_the_shot_judging_guidance()
    {
        string prompt = Normalise(DirectorFallback());
        foreach (string phrase in Phrases)
            prompt.Should().Contain(phrase);
    }

    [Fact]
    public void The_seeded_video_edit_director_prompt_carries_the_shot_judging_guidance()
    {
        string prompt = Normalise(DirectorSeeded());
        foreach (string phrase in Phrases)
            prompt.Should().Contain(phrase);
    }

    [Fact]
    public void The_director_fallback_and_the_seeded_director_are_identical_and_share_the_charter_guidance()
    {
        DirectorSeeded().Should().Be(DirectorFallback(),
            "the fallback and the seeded row stay verbatim-identical, as every built-in prompt's copies do");

        string charter = Normalise(Section(EditRoomStepExecutor.EditRoomCharterPrompt));
        Normalise(Section(DirectorFallback())).Should().Be(charter,
            "the guidance is the same rule for the seats and the director");
        Normalise(Section(DirectorSeeded())).Should().Be(charter);
    }

    [Fact]
    public void The_guidance_calls_the_brief_the_User_Request_and_mislabels_neither_gaps_nor_transcript_text()
    {
        string charter = EditRoomStepExecutor.EditRoomCharterPrompt;

        // The room appends the user's request as `\n\n---\nUser Request:\n{request}`
        // (StepExecutionContext.WithUserRequest), so that exact label is what the guidance names.
        charter.Should().Contain("\"User Request:\" heading in your input");
        charter.Should().NotContain("With no User Request there is no brief",
            "the label must stay quoted, matching the input section");

        // QA's low finding: a "g" id is an audio silence span attached to the shot it starts in,
        // not a gap "between shots", and transcript segment text is under the `text` key — "t" is
        // the id prefix.
        charter.Should().NotContain("ids between shots");
        charter.Should().Contain("not a shot of its own");
        charter.Should().Contain("(the \"text\" key)");
        charter.Should().NotContain("transcript (\"t\")");
        charter.Should().NotContain("\"t\" key");
    }

    [Fact]
    public void The_director_tools_section_no_longer_calls_a_project_file_a_brief()
    {
        // The definition above says "the brief" is the User Request; Tools must not then offer
        // "a brief" as a project file (Security's wording note on #844).
        foreach (string prompt in new[] { DirectorFallback(), DirectorSeeded() })
        {
            Normalise(prompt).Should().Contain("a project file is context, not the brief");
            Normalise(prompt).Should().NotContain("(e.g. a brief or script)");
        }
    }

    [Theory]
    [InlineData(typeof(GraphicsRoomStepExecutor), "GraphicsRoomCharterPrompt")]
    [InlineData(typeof(ColorGradeRoomStepExecutor), "ColorGradeRoomCharterPrompt")]
    [InlineData(typeof(DailiesRoomStepExecutor), "DailiesRoomCharterPrompt")]
    public void The_other_rooms_charters_are_unchanged_and_carry_no_edit_room_guidance(Type executor, string field)
    {
        string charter = CharterOf(executor, field);
        charter.Should().NotContain(Heading);
        charter.Should().NotContain("Speech is not the only content");
        charter.Should().NotContain("\"User Request:\"");
    }

    [Theory]
    [InlineData(typeof(MotionGraphicsDirectorAgent))]
    [InlineData(typeof(ColorGradeDirectorAgent))]
    [InlineData(typeof(DailiesDirectorAgent))]
    public void The_other_rooms_director_prompts_are_unchanged_and_carry_no_edit_room_guidance(Type agentClass)
    {
        string prompt = DefaultPromptOf(agentClass);
        prompt.Should().NotContain(Heading);
        prompt.Should().NotContain("Speech is not the only content");
        prompt.Should().NotContain("\"User Request:\"");
    }

    // ---------------------------------------------------------------------
    // Step cache: an EditRoom step has no AgentDefinition of its own — its seats and director are
    // resolved from EditRoomConfigJson — so the charter and the room prompts are NOT in the key.
    // DeterministicStepRevisions.For(StepType.EditRoom) is what makes a prompt change a miss.
    // ---------------------------------------------------------------------

    private static readonly Guid WorkflowId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ProjectId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private sealed class FixedFingerprintProvider : IProjectFileFingerprintProvider
    {
        public Task<string> GetFingerprintAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult("project-fingerprint");
    }

    private static async Task<StepCacheKeyInputs> BuildEditRoomKeyInputsAsync()
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = WorkflowId,
            AgentDefinitionId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
            StepOrder = 2,
            StepType = StepType.EditRoom,
            EditRoomConfigJson = """{"version":1,"view":{"from":"Previous"}}""",
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        };
        step.AgentDefinition = new AgentDefinition
        {
            Id = step.AgentDefinitionId,
            Name = "VideoTransform",
            AgentType = AgentType.VideoTransform,
            IsBuiltIn = true,
            SystemPrompt = "the step's own deterministic placeholder prompt",
        };

        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = WorkflowId,
            ProjectId = ProjectId,
        };
        var context = new StepExecutionContext
        {
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "shot-judging-cache-test",
            StepOutputHistory = Array.Empty<StepOutputHistoryEntry>(),
        };

        MethodInfo build = typeof(WorkflowExecutorService).GetMethod(
            "BuildCacheKeyInputsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<StepCacheKeyInputs>)build.Invoke(null,
        [
            new FixedFingerprintProvider(), null, execution, step, context, null, null,
            CancellationToken.None, null
        ])!;
        return await task;
    }

    [Fact]
    public async Task The_edit_room_cache_key_carries_the_steps_own_agent_prompt_not_the_charter_or_room_prompts()
    {
        StepCacheKeyInputs inputs = await BuildEditRoomKeyInputsAsync();

        // The key's only prompt field is the STEP's AgentDefinition prompt, which for an EditRoom
        // step is the deterministic VideoTransform placeholder (WorkflowTemplateCatalog), not the
        // room's seats or director.
        typeof(StepCacheKeyInputs).GetProperties()
            .Where(p => p.Name.Contains("Prompt", StringComparison.Ordinal))
            .Select(p => p.Name)
            .Should().Equal("AgentSystemPrompt");
        inputs.AgentSystemPrompt.Should().Be("the step's own deterministic placeholder prompt");

        // Neither the charter nor the director prompt reaches any hashed field, so editing either
        // would not move this key on its own.
        foreach (PropertyInfo property in typeof(StepCacheKeyInputs).GetProperties())
        {
            if (property.GetValue(inputs) is not string value)
                continue;
            value.Should().NotContain("You are participating in a live \"edit room\" discussion",
                $"{property.Name} must not carry the seat charter");
            value.Should().NotContain("You are the director moderating a multi-editor",
                $"{property.Name} must not carry the director prompt");
        }
    }

    [Fact]
    public async Task The_edit_room_algorithm_revision_carries_the_shot_judging_bump()
    {
        // A prompt change is invisible to the key above, so the revision tag is the only mechanism
        // that turns it into a cache miss. This golden moves only with a deliberate EditRoom
        // revision bump (StepCacheKeyBuilder hashes it as AlgorithmRevision).
        DeterministicStepRevisions.For(StepType.EditRoom)
            .Should().Be("2026-11-01.edit-room-shot-judging-1+outline-ids-2");

        StepCacheKeyInputs inputs = await BuildEditRoomKeyInputsAsync();
        new StepCacheKeyBuilder().Build(inputs)
            .Should().StartWith("cf763cd172120237caf7adfee9185cde1a37d28832190a95e4",
                "recorded golden: it moves only with a deliberate EditRoom revision bump " +
                "(and the key's input set, which must not change silently); the prompt change itself " +
                "is why this tag exists; re-recorded for SchemaVersion v12 (earlier 457d0b4b...; only the version string moved); re-recorded for outline-ids-2 (earlier b0e1dd99...)");
    }
}
