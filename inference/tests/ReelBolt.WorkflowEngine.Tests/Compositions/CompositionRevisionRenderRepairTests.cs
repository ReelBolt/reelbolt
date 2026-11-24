using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// Production run 22f79d02: the platform's render of a revision was refused BEFORE it ran (HTML5 &lt;Video&gt;
/// left in the restored film) and the refusal reached nobody who could fix it. A pre-render refusal now gets the
/// reviser ONE repair turn in the same scope and sandbox with the refusal quoted; a real render failure gets none.
/// </summary>
public class CompositionRevisionRenderRepairTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    private const string Html5Refusal =
        "HTML5_VIDEO_IN_RENDER: the composition plays video through remotion's HTML5 <Video>/<Html5Video>, which shows stale frames " +
        "in a render, so nothing was rendered: <Video> in src/Clip.tsx:12. Replace each one with <OffthreadVideo> imported from 'remotion'.";

    private sealed class Setup : IDisposable
    {
        public CompositionHarness H { get; } = new();
        public DictionarySessionFactory Sessions { get; } = new();
        public WorkflowExecutionContextAccessor Accessor { get; } = new();
        public ScriptedReviser? Agent { get; private set; }
        public ScriptedRevisionRenderer Renderer { get; }
        public FakeWorkspaceSession Sandbox { get; }
        public Guid Run { get; } = Guid.NewGuid();

        public Setup()
        {
            Renderer = new ScriptedRevisionRenderer(Accessor);
            Sandbox = new FakeWorkspaceSession(Run);
            Sessions.Sessions[Run] = Sandbox;
        }

        /// <summary>A runner whose agent runs <paramref name="first"/> on its first turn and <paramref name="repair"/> on any later one.</summary>
        public CompositionRevisionRunner Runner(Action<WorkflowExecutionContext> first, Action<WorkflowExecutionContext>? repair = null)
        {
            int turns = 0;
            Agent = new ScriptedReviser(Accessor, (ctx, _) =>
            {
                if (turns++ == 0)
                    first(ctx);
                else
                    repair?.Invoke(ctx);
                return Task.CompletedTask;
            });
            return new CompositionRevisionRunner(
                H.Store, H.Capture, H.Restorer, Sessions, new SingleAgentRegistry(Agent), Accessor,
                H.S3, H.Configuration, NullLogger<CompositionRevisionRunner>.Instance, Renderer);
        }

        public void Edit(string text) => Sandbox.Files["src/Intro.tsx"] = Encoding.UTF8.GetBytes(text);

        public void Dispose() => H.Dispose();
    }

    private static async Task<CompositionWorkspaceVersion> OriginAsync(Setup s)
    {
        Guid origin = Guid.NewGuid();
        await s.H.SeedExecutionAsync(origin, Project);
        await s.H.SeedExecutionAsync(s.Run, Project);
        FakeWorkspaceSession film = new(origin);
        film.Files["package.json"] = Encoding.UTF8.GetBytes("{}");
        film.Files["src/root.tsx"] = Encoding.UTF8.GetBytes("root");
        film.Files["src/Intro.tsx"] = Encoding.UTF8.GetBytes("<Video src={clip} />");
        return (await s.H.Capture.CaptureAsync(film, new CompositionCaptureRequest
        {
            ProjectId = Project,
            OrganizationId = Org,
            ExecutionId = origin,
            CompositionId = "FinalVideo",
            Fps = 30,
            Width = 1920,
            Height = 1080,
            DurationInFrames = 240,
            RenderOutputStorageKey = $"projects/{Project}/outputFiles/{origin}/promo.mp4"
        }, CancellationToken.None)).Version!;
    }

    private static CompositionRevisionRequest Request(Setup s, CompositionWorkspaceVersion v1) => new()
    {
        ProjectId = Project,
        OrganizationId = Org,
        ExecutionId = s.Run,
        CorrelationId = "c",
        WorkspaceId = v1.WorkspaceId,
        BaseVersionId = v1.Id,
        Instruction = "Re-record the voiceover and keep the clips",
        RequestedByUserId = Guid.NewGuid()
    };

    private static void RenderNow(WorkflowExecutionContext ctx) =>
        ctx.PendingOutputStorageKey = $"projects/{ctx.ProjectId}/outputFiles/{ctx.ExecutionId}/{CompositionRevisionBrief.OutputFileName}";

    [Fact]
    public async Task A_refused_platform_render_gets_one_repair_turn_and_the_agents_own_render_is_used()
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.FailNext.Enqueue(Html5Refusal);

        CompositionRevisionOutcome outcome = await s.Runner(
                first: _ => s.Edit("<Video src={clip} /> louder voice"),
                repair: ctx =>
                {
                    s.Edit("<OffthreadVideo src={clip} /> louder voice");
                    RenderNow(ctx);
                })
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        s.Agent!.Prompts.Should().HaveCount(2);
        string repairPrompt = s.Agent.Prompts[1];
        repairPrompt.Should().Contain("Your changes could not be rendered: HTML5_VIDEO_IN_RENDER")
            .And.Contain("src/Clip.tsx:12")
            .And.Contain("Re-record the voiceover and keep the clips")
            .And.Contain("`FinalVideo`").And.Contain(CompositionRevisionBrief.OutputFileName);
        s.Renderer.Calls.Should().HaveCount(1, "the agent rendered in its repair turn, so the platform adds no second render");
        outcome.RenderStorageKey.Should().Be($"projects/{Project}/outputFiles/{s.Run}/revision.mp4");
        outcome.ResultVersion!.ParentVersionId.Should().Be(v1.Id);
        outcome.TokensUsed.Should().Be(2 * 1234, "the repair turn is metered and reported like the first");
    }

    [Fact]
    public async Task A_repair_turn_that_changes_code_without_rendering_is_rendered_once_more_by_the_platform()
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.FailNext.Enqueue(Html5Refusal);

        CompositionRevisionOutcome outcome = await s.Runner(
                first: _ => s.Edit("<Video src={clip} /> louder voice"),
                repair: _ => s.Edit("<OffthreadVideo src={clip} /> louder voice"))
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        s.Agent!.Prompts.Should().HaveCount(2);
        s.Renderer.Calls.Should().HaveCount(2);
        outcome.RenderStorageKey.Should().NotBeNull();
    }

    [Fact]
    public async Task A_repair_turn_that_changes_nothing_fails_REVISION_RENDER_FAILED_with_the_refusal_text()
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.FailNext.Enqueue(Html5Refusal);

        CompositionRevisionOutcome outcome = await s.Runner(first: _ => s.Edit("<Video src={clip} /> louder voice"))
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureCode.Should().Be(CompositionRevisionRunner.RenderFailed);
        outcome.FailureMessage.Should().Contain("HTML5_VIDEO_IN_RENDER").And.Contain("src/Clip.tsx:12");
        s.Agent!.Prompts.Should().HaveCount(2);
        s.Renderer.Calls.Should().HaveCount(1, "nothing changed in the repair turn, so the platform does not render the same refused code again");
        outcome.RenderStorageKey.Should().BeNull();
        FailureClassifier.ForCode(outcome.FailureCode).Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public async Task There_is_at_most_one_repair_turn_even_when_the_second_render_is_refused_again()
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.FailNext.Enqueue(Html5Refusal);
        s.Renderer.FailNext.Enqueue(StaticFilePreflight.ErrorCode + ": the composition uses files that are not in the sandbox, so nothing was rendered: public/a.mp4.");

        CompositionRevisionOutcome outcome = await s.Runner(
                first: _ => s.Edit("<Video src={clip} /> louder voice"),
                repair: _ => s.Edit("<OffthreadVideo src={staticFile('a.mp4')} />"))
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.FailureCode.Should().Be(CompositionRevisionRunner.RenderFailed);
        outcome.FailureMessage.Should().Contain("MISSING_STATIC_FILE");
        s.Agent!.Prompts.Should().HaveCount(2, "one repair turn per revision step, never a loop");
        s.Renderer.Calls.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("Render timed out after 900s")]
    [InlineData("The video renderer exited with code 1. Remotion could not read a frame of public/a.mp4.")]
    public async Task A_real_render_failure_gets_no_repair_turn(string error)
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.FailsWith = error;

        CompositionRevisionOutcome outcome = await s.Runner(first: _ => s.Edit("<Video src={clip} /> louder voice"))
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.FailureCode.Should().Be(CompositionRevisionRunner.RenderFailed);
        outcome.FailureMessage.Should().Contain(error.TrimEnd('.'));
        s.Agent!.Prompts.Should().HaveCount(1);
        s.Renderer.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_render_that_throws_gets_no_repair_turn()
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.Throws = new InvalidOperationException("remotion exited 1: crashed at frame 42");

        CompositionRevisionOutcome outcome = await s.Runner(first: _ => s.Edit("<Video src={clip} /> louder voice"))
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.FailureCode.Should().Be(CompositionRevisionRunner.RenderFailed);
        s.Agent!.Prompts.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("HTML5_VIDEO_IN_RENDER: x", true)]
    [InlineData("  MISSING_STATIC_FILE: x", true)]
    [InlineData("NETWORK_FONT: src/a.tsx:1 loads a font", true)]
    [InlineData("Render timed out after 900s", false)]
    [InlineData("The render failed: HTML5_VIDEO_IN_RENDER: x", false)]
    [InlineData("RENDER_FAILED: this composition has used up its render attempts", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Only_the_pre_render_refusals_are_fixable(string? error, bool fixable) =>
        CompositionRevisionRunner.IsFixablePreRenderRefusal(error).Should().Be(fixable);

    [Fact]
    public void The_repair_brief_quotes_the_request_and_refusal_as_bounded_data()
    {
        string brief = CompositionRevisionBrief.BuildRenderRepair(
            "make it ```pop```\u0007", "FinalVideo", "HTML5_VIDEO_IN_RENDER: " + new string('x', 5_000) + "```");

        brief.Should().NotContain("```pop```").And.Contain("'''pop'''");
        brief.Should().NotContain("\u0007");
        brief.Should().Contain("...[truncated]");
        brief.Split("```text").Should().HaveCount(3, "exactly two fenced quotes: the request and the refusal");
    }

    // ------------------------------------------------------------------ crashes on the composition's own code (run a2be36db)

    private const string ReferenceErrorCrash = "execution failed: exit status 1. Remotion said: ReferenceError Label is not defined";

    [Fact]
    public async Task A_platform_render_that_crashes_on_a_code_error_gets_one_repair_turn_told_to_check_stills()
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.FailNext.Enqueue(ReferenceErrorCrash);

        CompositionRevisionOutcome outcome = await s.Runner(
                first: _ => s.Edit("<Label>Hi</Label>"),
                repair: ctx =>
                {
                    s.Edit("import { Label } from './Label'; <Label>Hi</Label>");
                    RenderNow(ctx);
                })
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        s.Agent!.Prompts.Should().HaveCount(2);
        s.Agent.Prompts[1].Should().Contain("crashed on an error in the composition's code")
            .And.Contain("Remotion said: ReferenceError Label is not defined")
            .And.Contain("RenderPreviewStills");
        s.Renderer.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_code_error_repair_that_still_crashes_ends_REVISION_RENDER_FAILED_after_two_platform_renders()
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.FailNext.Enqueue(ReferenceErrorCrash);
        s.Renderer.FailNext.Enqueue("execution failed: exit status 1. Remotion said: TypeError: Cannot read properties of undefined (reading 'map')");

        CompositionRevisionOutcome outcome = await s.Runner(
                first: _ => s.Edit("<Label>Hi</Label>"),
                repair: _ => s.Edit("<Label items={undefined}>Hi</Label>"))
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.FailureCode.Should().Be(CompositionRevisionRunner.RenderFailed);
        outcome.FailureMessage.Should().Contain("Cannot read properties of undefined");
        s.Agent!.Prompts.Should().HaveCount(2, "one repair turn per revision step");
        s.Renderer.Calls.Should().HaveCount(2, "at most two platform renders");
    }

    [Theory]
    [InlineData("execution failed: exit status 137 (SIGKILL). The video renderer was killed (likely out of memory).")]
    [InlineData("execution failed: exit status 1. Remotion said: Error: FATAL ERROR: Reached heap limit Allocation failed - JavaScript heap out of memory")]
    [InlineData("execution failed: exit status 1. Remotion could not read a frame of public/a.mp4 (3840x2160).")]
    [InlineData("execution failed: exit status 1. Remotion said: Error: Could not extract frame from compositor")]
    [InlineData("context deadline exceeded: the render timed out after 1800s")]
    [InlineData("execution failed: exit status 1. Remotion said: Error: ENOSPC: no space left on device, write")]
    [InlineData("execution failed: exit status 1. Remotion said: TypeError: Failed to launch the browser process")]
    public async Task A_platform_failure_gets_no_repair_turn_even_when_it_names_an_error(string error)
    {
        using Setup s = new();
        CompositionWorkspaceVersion v1 = await OriginAsync(s);
        s.Renderer.FailsWith = error;

        CompositionRevisionOutcome outcome = await s.Runner(first: _ => s.Edit("<Label>Hi</Label>"))
            .RunAsync(Request(s, v1), null, CancellationToken.None);

        outcome.FailureCode.Should().Be(CompositionRevisionRunner.RenderFailed);
        s.Agent!.Prompts.Should().HaveCount(1);
        s.Renderer.Calls.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(ReferenceErrorCrash, AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: TypeError: interpolate is not a function", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: SyntaxError: Unexpected token '<'", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: Error: Build failed with 1 error: src/Intro.tsx:3:20: ERROR: Could not resolve \"./Lable\"", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: Module not found: Error: Can't resolve './Title'", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: Error: Could not find composition with ID FinalVideo.", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: Error: inputRange must be strictly monotonically increasing but got [0,30,10]", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: Error: The \"durationInFrames\" prop of the <Sequence /> component must be a positive integer", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: Error: Objects are not valid as a React child (found: object with keys {a})", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("execution failed: exit status 1. Remotion said: ReferenceError: setTimeout is not defined", AgentFixableRenderFailureKind.CodeError)]
    [InlineData("HTML5_VIDEO_IN_RENDER: x", AgentFixableRenderFailureKind.PreRenderRefusal)]
    [InlineData("execution failed: exit status 1", AgentFixableRenderFailureKind.None)]
    [InlineData("Render timed out after 900s", AgentFixableRenderFailureKind.None)]
    [InlineData("execution failed: exit status 1. Remotion said: Error: A delayRender() \"Loading <OffthreadVideo>\" was called but not cleared after 28000ms", AgentFixableRenderFailureKind.None)]
    [InlineData("execution failed: exit status 1. Remotion said: ReferenceError x is not defined (killed)", AgentFixableRenderFailureKind.None)]
    [InlineData("Sandbox request failed: sandbox not found (404)", AgentFixableRenderFailureKind.None)]
    [InlineData("The video renderer exited with code 1. This composition has used up its render attempts, so it will not render again.", AgentFixableRenderFailureKind.None)]
    [InlineData(null, AgentFixableRenderFailureKind.None)]
    public void Code_errors_are_fixable_and_platform_failures_never_are(string? error, AgentFixableRenderFailureKind kind) =>
        AgentFixableRenderFailure.Classify(error).Should().Be(kind);
}

/// <summary>The Author's one final-render reminder quotes a pre-render refusal its own render got.</summary>
public class AuthorFinalRenderReminderRefusalTests
{
    private static AgentToolCallTrace Render(string result) => new() { ToolName = "RenderVideoAndUploadToStorage", Result = result };

    [Fact]
    public void The_last_pre_render_refusal_is_quoted_in_the_reminder()
    {
        string refusal = AgentStepExecutor.LastFixableRenderRefusal([
            Render("{\"ok\":false,\"error\":\"NETWORK_FONT: old\"}"),
            new AgentToolCallTrace { ToolName = "WriteSandboxFile", Result = "{\"ok\":true}" },
            Render("{\"ok\":false,\"error\":\"HTML5_VIDEO_IN_RENDER: <Video> in src/Clip.tsx:12.\",\"uses\":[]}")
        ])!;

        refusal.Should().StartWith("HTML5_VIDEO_IN_RENDER");
        AgentStepExecutor.BuildFinalRenderReminderPrompt("STEP INPUT", "done", refusal)
            .Should().Contain("refused before it rendered anything: HTML5_VIDEO_IN_RENDER: <Video> in src/Clip.tsx:12.");
    }

    [Theory]
    [InlineData("{\"ok\":false,\"error\":\"Render timed out\"}")]
    [InlineData("{\"ok\":true,\"storageKey\":\"k\"}")]
    [InlineData("not json HTML5_VIDEO_IN_RENDER: x")]
    public void Anything_else_quotes_no_refusal(string result)
    {
        AgentStepExecutor.LastFixableRenderRefusal([Render(result)]).Should().BeNull();
        AgentStepExecutor.BuildFinalRenderReminderPrompt("STEP INPUT", "done", null)
            .Should().NotContain("refused before it rendered anything").And.Contain("HTML5_VIDEO_IN_RENDER");
    }

    [Fact]
    public void A_final_render_that_crashed_on_a_code_error_is_quoted_with_Remotions_line_and_the_stills_check()
    {
        string error = AgentStepExecutor.LastFixableRenderRefusal([
            Render("{\"ok\":false,\"error\":\"execution failed: exit status 1\",\"outputTail\":\"Bundling...\\nReferenceError: Label is not defined\\n    at Intro (src/Intro.tsx:4)\"}")
        ])!;

        error.Should().Contain("Remotion said: ReferenceError: Label is not defined");
        string prompt = AgentStepExecutor.BuildFinalRenderReminderPrompt("STEP INPUT", "done", error);
        prompt.Should().Contain("crashed on an error in your composition's code: execution failed: exit status 1. Remotion said: ReferenceError: Label is not defined")
            .And.Contain("RenderPreviewStills")
            .And.NotContain("refused before it rendered anything");
    }

    [Theory]
    [InlineData("{\"ok\":false,\"error\":\"execution failed: exit status 137\",\"outputTail\":\"SIGKILL: renderer killed\"}")]
    [InlineData("{\"ok\":false,\"error\":\"execution failed: exit status 1\",\"outputTail\":\"Error: Could not extract frame from compositor\",\"videoHint\":\"x\"}")]
    [InlineData("{\"ok\":false,\"error\":\"render timed out after 1800s\"}")]
    public void A_final_render_that_failed_on_the_platform_quotes_nothing(string result) =>
        AgentStepExecutor.LastFixableRenderRefusal([Render(result)]).Should().BeNull();
}
