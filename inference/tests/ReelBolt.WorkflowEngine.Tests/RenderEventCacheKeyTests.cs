using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-110 hold/cache: when events are offered, the compile's step-cache key covers the sidecar digest (through
/// the analysis output it reads), and an analysis offering a pinned render's events depends on the step that
/// rendered it, so a re-render is a cache miss for both.
/// </summary>
public class RenderEventCacheKeyTests
{
    private static List<WorkflowStep> Steps(bool offerSfx, string insertRef = """{"from":"Step","stepOrder":2}""") =>
    [
        new WorkflowStep { StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = """{"version":1,"source":{"kind":"ProjectFile","projectFileId":"0d9a3f2e-7b1c-4c54-8d0e-2f5a6b7c8d90"}}""" },
        new WorkflowStep { StepOrder = 2, StepType = StepType.Agent },
        new WorkflowStep
        {
            StepOrder = 3,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = $$"""{"version":1,"source":{"kind":"ProjectFile","projectFileId":"0d9a3f2e-7b1c-4c54-8d0e-2f5a6b7c8d90"},"offerSfxClips":{{(offerSfx ? "true" : "false")}},"detectInsertRegions":true}"""
        },
        new WorkflowStep { StepOrder = 4, StepType = StepType.Agent },
        new WorkflowStep
        {
            StepOrder = 5,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = $$"""{"version":1,"decision":{"source":"Previous"},"analysisStepOrder":3,"enableSfx":true,"enableInserts":true,"insertContentRef":{{insertRef}}}"""
        }
    ];

    [Fact]
    public void An_analysis_offering_events_depends_on_the_render_a_downstream_compile_pins()
    {
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(Steps(offerSfx: true));

        graph.DependsOn(3).Keys.Should().Contain(2);
    }

    [Fact]
    public void Without_sfx_offering_the_analysis_graph_is_unchanged()
    {
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(Steps(offerSfx: false));

        graph.DependsOn(3).Keys.Should().NotContain(2);
    }

    [Fact]
    public void A_per_plate_reference_counts_too()
    {
        List<WorkflowStep> steps = Steps(offerSfx: true);
        steps[4].VideoCompileConfigJson =
            """{"version":1,"decision":{"source":"Previous"},"analysisStepOrder":3,"enableSfx":true,"enableInserts":true,"insertContents":[{"regionId":"r0","insertContentRef":{"from":"Step","stepOrder":2}}]}""";

        WorkflowStepDependencyGraph.Build(steps).DependsOn(3).Keys.Should().Contain(2);
    }

    [Fact]
    public void The_compile_key_moves_with_the_offered_events_digest()
    {
        List<WorkflowStep> steps = Steps(offerSfx: true);
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(steps);
        WorkflowStep compile = steps.Single(s => s.StepOrder == 5);

        static List<StepOutputHistoryEntry> History(string digest) =>
        [
            new(3, "Analyze", "{\"view\":{},\"meta\":{\"renderEvents\":{\"count\":1,\"digest\":\"" + digest + "\"}}}", null, "projects/p/agentFiles/video-analysis/run/step-3-analysis.json"),
            new(4, "Sound designer", """{"cues":[]}""")
        ];

        StepUpstreamDigest.Compute(graph, compile, History("aa")).Should().NotBe(StepUpstreamDigest.Compute(graph, compile, History("bb")));
        StepUpstreamDigest.Compute(graph, compile, History("aa")).Should().Be(StepUpstreamDigest.Compute(graph, compile, History("aa")));
    }

    // S-63: the engine binds the reference with the Web options (member names in any case, a padded kind, a
    // number written as a string), so the graph must read every spelling the engine accepts.
    [Theory]
    [InlineData("""{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContentRef":{"from":" Step ","stepOrder":1}}""")]
    [InlineData("""{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContentRef":{"from":"step","stepOrder":"1"}}""")]
    [InlineData("""{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContentRef":{"From":"Step ","StepOrder":"1"}}""")]
    [InlineData("""{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContents":[{"regionId":"r0","insertContentRef":{"from":" step","stepOrder":"1"}}]}""")]
    public void A_compile_depends_on_an_insert_reference_written_in_any_spelling_the_engine_accepts(string compileConfig)
    {
        VideoCompileStepConfig? bound = JsonSerializer.Deserialize<VideoCompileStepConfig>(compileConfig, EngineOptions);
        InsertContentRef reference = (bound!.InsertContentRef ?? bound.InsertContents![0].InsertContentRef)!;
        reference.Kind.Should().Be(InsertContentRef.StepKind, "the engine accepts this spelling");
        reference.StepOrder.Should().Be(1);

        WorkflowStepDependencyGraph.Build(IsolatedRenderSteps(compileConfig)).DependsOn(3).Keys.Should().Contain(1);
    }

    [Theory]
    [InlineData("""{"from":" Step ","stepOrder":2}""")]
    [InlineData("""{"from":"step","stepOrder":"2"}""")]
    public void An_analysis_offering_events_depends_on_a_pinned_render_written_in_any_spelling_the_engine_accepts(string insertRef) =>
        WorkflowStepDependencyGraph.Build(Steps(offerSfx: true, insertRef)).DependsOn(3).Keys.Should().Contain(2);

    [Fact]
    public void An_unreadable_insert_reference_depends_on_every_earlier_step() =>
        WorkflowStepDependencyGraph.Build(IsolatedRenderSteps(
                """{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContentRef":{"from":"Step","stepOrder":"one"}}"""))
            .DependsOn(3).Keys.Should().Contain(1);

    private static readonly JsonSerializerOptions EngineOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static List<WorkflowStep> IsolatedRenderSteps(string compileConfig) =>
    [
        // Step 1 is the render a compile pins; step 2 reads nothing from the run; the compile's decision names step 2.
        new WorkflowStep { StepOrder = 1, StepType = StepType.TimelineRender },
        new WorkflowStep { StepOrder = 2, StepType = StepType.VideoGenerate, VideoGenerateConfigJson = """{"inlinePrompt":"x"}""" },
        new WorkflowStep { StepOrder = 3, StepType = StepType.VideoCompile, VideoCompileConfigJson = compileConfig }
    ];

    [Theory]
    [InlineData("""{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContentRef":{"from":"Step","stepOrder":1}}""")]
    [InlineData("""{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContents":[{"regionId":"r0","insertContentRef":{"from":"Step","stepOrder":1}}]}""")]
    public void A_compile_depends_on_the_non_adjacent_step_its_insert_reference_names(string compileConfig)
    {
        List<WorkflowStep> steps = IsolatedRenderSteps(compileConfig);
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(steps);
        WorkflowStep compile = steps.Single(s => s.StepOrder == 3);

        graph.DependsOn(3).Keys.Should().Contain(1);

        static List<StepOutputHistoryEntry> History(string renderKey) =>
        [
            new(1, "Render", "{}", renderKey, null),
            new(2, "Other", """{"x":1}""")
        ];

        StepUpstreamDigest.Compute(graph, compile, History("renders/a.mp4"))
            .Should().NotBe(StepUpstreamDigest.Compute(graph, compile, History("renders/b.mp4")),
                "re-rendering the referenced step must be a compile cache miss");
    }

    [Theory]
    [InlineData("""{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContentRef":{"from":"ProjectFile","projectFileId":"0d9a3f2e-7b1c-4c54-8d0e-2f5a6b7c8d90"}}""")]
    [InlineData("""{"version":1,"decision":{"from":"Step","stepOrder":2},"insertContentRef":{"from":"StepResult","stepResultId":"0d9a3f2e-7b1c-4c54-8d0e-2f5a6b7c8d90"}}""")]
    public void A_project_file_or_step_result_insert_reference_adds_no_step_dependency(string compileConfig) =>
        WorkflowStepDependencyGraph.Build(IsolatedRenderSteps(compileConfig)).DependsOn(3).Keys.Should().NotContain(1);
}
