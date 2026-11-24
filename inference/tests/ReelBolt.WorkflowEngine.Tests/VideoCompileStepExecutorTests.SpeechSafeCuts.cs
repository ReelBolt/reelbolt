using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Tests.Quality;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Executor-level helpers for the speech-safe cuts tests (docs/video-editing.md "Speech-safe cuts").</summary>
public partial class VideoCompileStepExecutorTests
{
    internal static VideoAnalysisArtifact BuildTalkingHeadArtifact(SyntheticTalkingHead head) =>
        BuildArtifact(
            shots: head.Shots.ToArray(),
            segments: head.Words.ToArray(),
            offeredIds: head.Shots.Select(s => s.Id).Concat(head.Words.Select(w => w.Id)).ToArray(),
            durationSec: head.DurationSec);

    internal static string BuildTalkingHeadDecision(SyntheticTalkingHead head) =>
        BuildDecisionJson(head.KeptRanges.Select(r => ($"s{r.FirstShot}", $"s{r.LastShot}", "keep")).ToArray());

    internal static async Task<(JsonObject Output, List<(double, double)> EdlSpans)> CompileTalkingHeadAsync(
        VideoAnalysisArtifact artifact, string decisionJson, bool snap)
    {
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: c => c with { SnapCutsToSpeech = snap, PrePaddingMs = 0, PostPaddingMs = 0 });
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        List<(double, double)> spans = edl.GetProperty("segments").EnumerateArray()
            .Select(s => (s.GetProperty("requestedStartSec").GetDouble(), s.GetProperty("requestedEndSec").GetDouble()))
            .ToList();
        return (JsonNode.Parse(result.Output)!.AsObject(), spans);
    }
}
