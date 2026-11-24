using System;
using System.Collections.Generic;
using System.Threading;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// <see cref="VisualCheckSourceResolver"/>: the output the loop reviews is the latest playable output in its
/// window — an Agent's Remotion render or a VideoCompile's compile, whichever ran last.
/// </summary>
public class VisualCheckSourceResolverTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static string Key(string name) => $"projects/{ProjectId:D}/outputFiles/{name}";

    [Fact]
    public void A_remotion_render_by_the_author_step_is_the_source()
    {
        StepExecutionContext context = Context(loopTarget: 1, reviewOrder: 3,
            new StepOutputHistoryEntry(1, "Author", "{\"composition\":\"Main\"}", Key("render-1.mp4")),
            new StepOutputHistoryEntry(2, "Lint", "{}"));

        VisualCheckSource? source = VisualCheckSourceResolver.Resolve(context);

        source.Should().NotBeNull();
        source!.StepOrder.Should().Be(1);
        source.StorageKey.Should().Be(Key("render-1.mp4"));
    }

    [Fact]
    public void A_compile_output_after_the_planner_is_the_source()
    {
        StepExecutionContext context = Context(loopTarget: 2, reviewOrder: 4,
            new StepOutputHistoryEntry(1, "Analyze", "{}", null, $"projects/{ProjectId:D}/video-analysis/a.json"),
            new StepOutputHistoryEntry(2, "Planner", "{\"plan\":[]}"),
            new StepOutputHistoryEntry(3, "VideoCompile", "{\"status\":\"completed\"}", Key("compile.mp4")));

        VisualCheckSource? source = VisualCheckSourceResolver.Resolve(context);

        source!.StepOrder.Should().Be(3);
        source.StorageKey.Should().Be(Key("compile.mp4"));
    }

    [Fact]
    public void After_a_loop_back_the_fresh_render_wins_over_the_stale_one()
    {
        StepExecutionContext context = Context(loopTarget: 1, reviewOrder: 2,
            new StepOutputHistoryEntry(1, "Author", "{}", Key("render-1.mp4")),
            new StepOutputHistoryEntry(1, "Author", "{}", Key("render-2.mp4")));

        VisualCheckSourceResolver.Resolve(context)!.StorageKey.Should().Be(Key("render-2.mp4"));
    }

    [Fact]
    public void Output_outside_the_loop_window_is_not_what_the_loop_reviews()
    {
        StepExecutionContext context = Context(loopTarget: 2, reviewOrder: 3,
            new StepOutputHistoryEntry(1, "Earlier render", "{}", Key("earlier.mp4")),
            new StepOutputHistoryEntry(2, "Author", "{}"));

        VisualCheckSourceResolver.Resolve(context).Should().BeNull();
    }

    [Theory]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/outputFiles/thumb.jpg")]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/outputFiles/manifest.json")]
    [InlineData("projects/99999999-2222-3333-4444-555555555555/outputFiles/other-project.mp4")]
    [InlineData("https://example.com/render.mp4")]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/../x.mp4")]
    public void Only_a_video_in_this_project_is_playable(string key)
    {
        StepExecutionContext context = Context(loopTarget: 1, reviewOrder: 2,
            new StepOutputHistoryEntry(1, "Author", "{}", key));

        VisualCheckSourceResolver.Resolve(context).Should().BeNull();
    }

    private static StepExecutionContext Context(int? loopTarget, int reviewOrder, params StepOutputHistoryEntry[] history)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = reviewOrder,
            StepType = StepType.ReviewLoop,
            LoopTargetStepOrder = loopTarget,
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = history.Length > 0 ? history[^1].Output : string.Empty,
            StepOutputHistory = new List<StepOutputHistoryEntry>(history),
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None,
        };
    }
}
