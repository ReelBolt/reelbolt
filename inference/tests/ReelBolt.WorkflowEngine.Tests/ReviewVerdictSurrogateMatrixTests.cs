using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

// SEC-18 / QA-SQF-45: escaped and raw lone surrogates at every position, through every score and brief entry
// point and the ReviewLoop executor itself: nothing throws, the step completes, and a sub-8 review loops back.
public class ReviewVerdictSurrogateMatrixTests
{
    private readonly ITestOutputHelper _out;
    public ReviewVerdictSurrogateMatrixTests(ITestOutputHelper output) => _out = output;

    // (label, surrogate text as it appears in the reviewer's output)
    private static readonly (string Label, string Text)[] Surrogates =
    [
        ("esc-high", "\\ud800"),
        ("esc-low", "\\udc00"),
        ("raw-high", "\ud800"),
        ("raw-low", "\udc00"),
    ];

    // (label, template with {K} = score key, {V} = real score, {S} = surrogate, readable-verdict?)
    // readable == true: the verdict object is still readable and its real score stands.
    private static readonly (string Label, string Template)[] Positions =
    [
        ("root-key", "{\"{K}\":{V},\"{S}\":1}"),
        ("in-score-key", "{\"{K}\":{V},\"{K}{S}\":10}"),
        ("in-score-key-only", "{\"{K}{S}\":10}"),
        ("string-value", "{\"{K}\":{V},\"summary\":\"{S}\"}"),
        ("criterion-name", "{\"{K}\":{V},\"criteria\":[{\"name\":\"{S}\",\"score\":{V},\"feedback\":\"x\"}]}"),
        ("fence", "```json\n{\"{K}\":{V},\"summary\":\"{S}\"}\n```"),
        ("fence-root-key", "```json\n{\"{K}\":{V},\"{S}\":1}\n```"),
        ("prose", "Here is my review {S} of it:\n{\"{K}\":{V},\"summary\":\"ok\"}"),
        ("prose-after", "{\"{K}\":{V},\"summary\":\"ok\"}\nThanks {S}"),
    ];

    public static IEnumerable<object[]> Matrix()
    {
        foreach (AgentType agent in new[] { AgentType.ReviewAgent, AgentType.VideoReviewAgent })
            for (int si = 0; si < Surrogates.Length; si++)
                for (int pi = 0; pi < Positions.Length; pi++)
                    foreach (int v in new[] { 5, 9 })
                        yield return new object[] { agent, si, pi, v };
    }

    private static readonly MethodInfo SelectReviewJson = typeof(ReviewLoopStepExecutor)
        .GetMethod("SelectReviewJson", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Probe(AgentType agent, int si, int pi, int realScore)
    {
        string key = agent == AgentType.ReviewAgent ? "overallScore" : "score";
        string output = Positions[pi].Template.Replace("{K}", key).Replace("{V}", realScore.ToString()).Replace("{S}", Surrogates[si].Text);
        string label = $"{agent}/{Surrogates[si].Label}/{Positions[pi].Label}/{realScore}";
        var thrown = new List<string>();
        int? parse = null, scanScore = null, execScore = null;
        void Try(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { thrown.Add($"{name}:{ex.GetType().Name}"); }
        }

        Try("ParseReviewScore", () => parse = ReviewLoopStepExecutor.ParseReviewScore(output, agent));
        Try("ScanReviewVerdicts", () =>
        {
            var scan = ReviewLoopStepExecutor.ScanReviewVerdicts(output, agent);
            scanScore = scan.Verdict is null || scan.UnaccountedKeyCount > 0 ? 0 : (int)Math.Floor(scan.Verdict.Score);
        });
        ReviewLoopStepExecutor.CappedReviewScore? scored = null;
        Try("ScoreReview", () => scored = ReviewLoopStepExecutor.ScoreReview(output, "{\"ok\":true}", agent));
        Try("ExtractFeedbackSummary", () => ReviewLoopStepExecutor.ExtractFeedbackSummary(output, 8, null, agent));
        Try("ExtractFeedbackSummary+scored", () => ReviewLoopStepExecutor.ExtractFeedbackSummary(output, 8, scored, agent));
        Try("SelectReviewJson", () =>
        {
            try { SelectReviewJson.Invoke(null, [output, agent]); }
            catch (TargetInvocationException tie) { throw tie.InnerException!; }
        });
        // WorkflowExecutorService.cs:722 then :744-747, the exact call sequence.
        Try("WES:722", () => ReviewLoopStepExecutor.ParseReviewScore(output, agent));
        Try("WES:744", () =>
        {
            var s = ReviewLoopStepExecutor.ScoreReview(output, "{\"ok\":true}", agent);
            ReviewLoopStepExecutor.ExtractFeedbackSummary(output, 8, s, agent);
        });

        StepExecutionResult? result = null;
        try
        {
            StepExecutionContext context = Context(agent);
            result = await Executor(output, agent).ExecuteAsync(context);
            // Loop-back (score < 8) => NextStepIndex 0; advance => 2.
            execScore = result.NextStepIndex == 0 ? -1 : 99;
        }
        catch (Exception ex) { thrown.Add($"ExecuteAsync:{ex.GetType().Name}"); }

        _out.WriteLine($"{label} parse={parse} scan={scanScore} exec={(execScore == -1 ? "loop" : execScore == 99 ? "advance" : "n/a")} status={result?.Status} thrown=[{string.Join(",", thrown)}]");

        thrown.Should().BeEmpty(label);
        result!.Status.Should().Be(StepStatus.Completed, label);
        if (realScore < 8)
        {
            parse.Should().BeLessThan(8, label);
            result.NextStepIndex.Should().Be(0, label + " must loop back");
        }
    }

    [Fact]
    public void Trace_raw()
    {
        try { ReviewLoopStepExecutor.ScanReviewVerdicts("{\"overallScore\":9,\"summary\":\"\ud800\"}", AgentType.ReviewAgent); _out.WriteLine("TRACE no-throw"); }
        catch (Exception ex) { _out.WriteLine("TRACE " + ex.ToString().Replace("\n", " | ")); }
    }

    private static StepExecutionContext Context(AgentType agent)
    {
        var loopTarget = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.VideoStoryEditor, Name = "VideoStoryEditor" }
        };
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = loopTarget.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.ReviewLoop,
            MinScore = 8,
            MaxIterations = 3,
            LoopTargetStepOrder = 2,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = agent, Name = agent.ToString() }
        };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [loopTarget, step],
            AccumulatedOutput = "{\"ok\":true}",
            StepOutputHistory = [new StepOutputHistoryEntry(4, "VideoCompile", "{\"ok\":true}")],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static ReviewLoopStepExecutor Executor(string reviewerOutput, AgentType agentType)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(agentType.ToString());
        agent.Setup(a => a.AgentType).Returns(agentType);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = reviewerOutput, Success = true, TokensUsed = 120 });
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);
        return new ReviewLoopStepExecutor(registry.Object, accessor.Object, new Mock<IDecisionGate>().Object,
            NullLogger<ReviewLoopStepExecutor>.Instance);
    }
}
