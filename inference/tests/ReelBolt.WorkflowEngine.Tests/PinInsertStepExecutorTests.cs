using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-206: <c>StepType.PinInsert</c> — the deterministic pinning step.</summary>
public sealed class PinInsertStepExecutorTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-0000-0000-0000-000000000206");
    private const string AnalysisKey = "projects/11111111-0000-0000-0000-000000000206/agentFiles/analysis.json";
    private static readonly string RenderKey = $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/author.mp4";

    /// <summary>Two offered screens (r0, r1), one offered licence plate (r2, feature-tracked) and one region that was never offered (r9).</summary>
    private const string ArtifactJson = """
        {"insertRegions":[
           {"id":"r0","startSec":0,"endSec":4,"confidence":0.9,"colorName":"green","method":"chroma"},
           {"id":"r1","startSec":4,"endSec":8,"confidence":0.8,"colorName":"green","method":"chroma"},
           {"id":"r2","startSec":0,"endSec":8,"confidence":0.7,"colorName":"","method":"feature","label":"licence plate"},
           {"id":"r9","startSec":0,"endSec":1,"confidence":0.2,"colorName":"green","method":"chroma"}],
         "offeredInsertRegionIds":["r0","r1","r2"]}
        """;

    [Fact]
    public async Task A_catch_all_pin_puts_one_render_on_every_offered_screen_and_emits_a_plan_the_compile_reads()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"analysis":{"from":"Step","stepOrder":1},"pins":[{"content":{"from":"Step","stepOrder":2}}]}""");

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails);
        MotionGraphicsPlanOutput plan = JsonSerializer.Deserialize<MotionGraphicsPlanOutput>(result.Output, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        plan.Overlays.Should().BeEmpty("a PinInsert plan carries screen inserts only");
        plan.Inserts.Select(i => i.RegionId).Should().Equal("r0", "r1"); // screens only, never the licence plate
        plan.Inserts.Should().OnlyContain(i => i.RenderedAssetStorageKey == RenderKey);
        JsonNode.Parse(result.Output)!["pinInsert"]!["analysisStepOrder"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task An_explicit_pin_wins_over_the_catch_all_and_a_project_file_is_named_by_its_id_not_its_key()
    {
        Fixture f = new();
        Guid fileId = Guid.NewGuid();
        f.ProjectFiles.Add(new ProjectWorkspaceFile(fileId, ProjectId, "demo.mp4", null, "userFiles", $"projects/{ProjectId}/userFiles/demo.mp4", "video/mp4", 10, DateTime.UtcNow, null));

        StepExecutionResult result = await f.RunAsync($$$"""
            {"analysis":{"from":"Step","stepOrder":1},
             "pins":[{"content":{"from":"Step","stepOrder":2}},
                     {"regionId":"r1","content":{"from":"ProjectFile","projectFileId":"{{{fileId}}}"}}]}
            """);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails);
        MotionGraphicsPlanOutput plan = JsonSerializer.Deserialize<MotionGraphicsPlanOutput>(result.Output, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        plan.Inserts.Single(i => i.RegionId == "r0").RenderedAssetStorageKey.Should().Be(RenderKey);
        plan.Inserts.Single(i => i.RegionId == "r1").RenderedAssetStorageKey.Should().Be(fileId.ToString("D"));
    }

    [Fact]
    public async Task A_StepResult_of_this_project_resolves_and_a_foreign_one_never_does()
    {
        Fixture f = new();
        Guid mine = Guid.NewGuid();
        Guid foreign = Guid.NewGuid();
        string earlier = $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/earlier.mp4";
        await f.SeedResultAsync(mine, ProjectId, earlier);
        await f.SeedResultAsync(foreign, Guid.NewGuid(), $"projects/{Guid.NewGuid()}/outputFiles/{Guid.NewGuid():D}/theirs.mp4");

        StepExecutionResult ok = await f.RunAsync($$$"""{"analysis":{"from":"Step","stepOrder":1},"pins":[{"regionId":"r0","content":{"from":"StepResult","stepResultId":"{{{mine}}}"}}]}""");
        ok.Status.Should().Be(StepStatus.Completed, because: ok.ErrorDetails);
        ok.Output.Should().Contain(earlier);

        StepExecutionResult refused = await f.RunAsync($$$"""{"analysis":{"from":"Step","stepOrder":1},"pins":[{"regionId":"r0","content":{"from":"StepResult","stepResultId":"{{{foreign}}}"}}]}""");
        refused.Status.Should().Be(StepStatus.Failed);
        refused.FailureCode.Should().Be("PIN_INSERT_UNRESOLVED");
        refused.FailureClass.Should().Be(FailureClass.UserInput);
        refused.Output.Should().NotContain("theirs.mp4");
    }

    [Fact]
    public async Task RequireApplied_fails_on_a_region_the_analysis_did_not_offer_and_names_it()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"analysis":{"from":"Step","stepOrder":1},"pins":[{"regionId":"r9","content":{"from":"Step","stepOrder":2}}]}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("PIN_INSERT_UNRESOLVED");
        result.ErrorDetails.Should().Contain("r9").And.Contain("r0, r1, r2");
    }

    [Fact]
    public async Task RequireApplied_false_skips_what_does_not_resolve_and_reports_it()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""
            {"analysis":{"from":"Step","stepOrder":1},"requireApplied":false,
             "pins":[{"regionId":"r9","content":{"from":"Step","stepOrder":2}},{"regionId":"r0","content":{"from":"Step","stepOrder":2}}]}
            """);

        result.Status.Should().Be(StepStatus.Completed);
        JsonNode output = JsonNode.Parse(result.Output)!;
        output["inserts"]!.AsArray().Should().ContainSingle();
        output["pinInsert"]!["skipped"]![0]!["regionId"]!.GetValue<string>().Should().Be("r9");
    }

    [Fact]
    public async Task Content_that_is_not_a_video_or_image_and_a_later_step_are_refused()
    {
        Fixture f = new();
        f.History.Add(new StepOutputHistoryEntry(3, "Voice", "{}", OutputStorageKey: $"projects/{ProjectId}/outputFiles/x/voice.mp3"));

        StepExecutionResult audio = await f.RunAsync("""{"analysis":{"from":"Step","stepOrder":1},"pins":[{"regionId":"r0","content":{"from":"Step","stepOrder":3}}]}""", stepOrder: 4);
        audio.FailureCode.Should().Be("PIN_INSERT_UNRESOLVED");
        audio.ErrorDetails.Should().Contain("not a video or an image");

        StepExecutionResult later = await f.RunAsync("""{"analysis":{"from":"Step","stepOrder":1},"pins":[{"regionId":"r0","content":{"from":"Step","stepOrder":5}}]}""", stepOrder: 4);
        later.ErrorDetails.Should().Contain("not an earlier step");
    }

    [Fact]
    public async Task No_analysis_and_no_offered_screens_are_user_input_failures()
    {
        Fixture f = new();
        StepExecutionResult missing = await f.RunAsync("""{"analysis":{"from":"Step","stepOrder":7},"pins":[{"content":{"from":"Step","stepOrder":2}}]}""");
        missing.FailureCode.Should().Be("ANALYSIS_NOT_FOUND");

        f.Artifact = """{"insertRegions":[],"offeredInsertRegionIds":[]}""";
        StepExecutionResult none = await f.RunAsync("""{"analysis":{"from":"Step","stepOrder":1},"pins":[{"content":{"from":"Step","stepOrder":2}}]}""");
        none.FailureCode.Should().Be("PIN_INSERT_NO_REGIONS");
        none.FailureClass.Should().Be(FailureClass.UserInput);

        // What the step panel reads ("What went wrong" = ErrorDetails): the plain sentence, no technical text.
        none.ErrorDetails.Should().Contain("We could not find a screen we could follow in your clip").And.Contain("large, steady and stays in view")
            .And.NotContain("detectInsertRegions").And.NotContain("offered no tracked screens");
        none.Output.Should().NotContain("detectInsertRegions");
    }

    [Fact]
    public async Task An_invalid_config_is_refused_before_anything_is_read()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"analysis":{"from":"Step","stepOrder":1},"pins":[]}""");

        result.FailureCode.Should().Be("CONFIG_INVALID");
        f.Files.Verify(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_same_inputs_give_byte_identical_output()
    {
        Fixture f = new();
        const string config = """{"analysis":{"from":"Previous"},"pins":[{"content":{"from":"Step","stepOrder":2}}]}""";
        f.History.Reverse(); // Previous = the analysis, the last entry

        (await f.RunAsync(config)).Output.Should().Be((await f.RunAsync(config)).Output);
    }

    private sealed class Fixture
    {
        public Mock<IProjectFileWorkspace> Files { get; } = new();
        public List<ProjectWorkspaceFile> ProjectFiles { get; } = [];
        public string Artifact { get; set; } = ArtifactJson;

        public List<StepOutputHistoryEntry> History { get; } =
        [
            new(1, "Analyze", "{}", ArtifactStorageKey: AnalysisKey),
            new(2, "Author", "{}", OutputStorageKey: RenderKey)
        ];

        private readonly ServiceProvider _provider;

        public Fixture()
        {
            string db = "pininsert-" + Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(db));
            _provider = services.BuildServiceProvider();

            Files.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(() => ProjectFiles);
            Files.Setup(w => w.DownloadStorageKeyToFileAsync(ProjectId, AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, path, _) => File.WriteAllTextAsync(path, Artifact));
        }

        public async Task SeedResultAsync(Guid id, Guid projectId, string outputKey)
        {
            using IServiceScope scope = _provider.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId };
            db.WorkflowExecutions.Add(execution);
            db.WorkflowStepResults.Add(new WorkflowStepResult { Id = id, WorkflowExecutionId = execution.Id, OutputStorageKey = outputKey, Status = StepStatus.Completed });
            await db.SaveChangesAsync();
        }

        public async Task<StepExecutionResult> RunAsync(string configJson, int stepOrder = 3)
        {
            var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = stepOrder, StepType = StepType.PinInsert, PinInsertConfigJson = configJson };
            var executor = new PinInsertStepExecutor(
                Files.Object,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new VideoEditingOptions { ScratchPath = Path.Combine(Path.GetTempPath(), "pininsert-tests") }),
                NullLogger<PinInsertStepExecutor>.Instance);
            return await executor.ExecuteAsync(new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = Guid.Parse("22222222-0000-0000-0000-000000000206"), ProjectId = ProjectId },
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                StepOutputHistory = History,
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            });
        }
    }
}
