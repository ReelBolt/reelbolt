using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-024 (S-03, S-04, QA-P0-7 save time): the save-time StepResult project check decides the step type and
/// reads the config exactly as saving and the engine do, so a numeric or padded step type or a differently
/// cased property cannot skip it; and a same-project result that is unfinished or not a video is refused with
/// its own message, while foreign and missing keep the one message.
/// </summary>
public sealed class StepResultSourceSaveCheckTests
{
    private static readonly Guid Id = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");

    private static CreateWorkflowStepRequest Step(string stepType, string configJson) =>
        JsonSerializer.Deserialize<CreateWorkflowStepRequest>(new JsonObject
        {
            ["agentDefinitionId"] = Guid.NewGuid().ToString(),
            ["stepOrder"] = 1,
            ["stepType"] = stepType,
            ["videoAnalyzeConfigJson"] = configJson
        }.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string SourceConfig(string property = "source") =>
        $$$"""{"version":1,"{{{property}}}":{"kind":"StepResult","stepResultId":"{{{Id}}}"}}""";

    private static string SourcesConfig(string property) =>
        $$"""{"version":1,"source":{"kind":"PreviousStepOutput"},"{{property}}":[{"kind":"StepResult","stepResultId":"{{Id}}"}]}""";

    private static async Task<IReadOnlyList<string>> CheckAsync(CreateWorkflowStepRequest step) =>
        await StepConfigSaveValidator.ValidateStepResultSourcesAsync([step], (_, _) => Task.FromResult(false), CancellationToken.None);

    [Theory]
    [InlineData("6")]
    [InlineData(" VideoAnalyze ")]
    [InlineData("videoanalyze")]
    public async Task A_step_type_saving_stores_as_VideoAnalyze_cannot_skip_the_project_check(string stepType)
    {
        Enum.Parse<StepType>("6").Should().Be(StepType.VideoAnalyze, "the numeric form the save path accepts");

        IReadOnlyList<string> errors = await CheckAsync(Step(stepType, SourceConfig()));

        errors.Should().ContainSingle().Which.Should().Contain("not found in this project");
    }

    [Theory]
    [InlineData("Sources")]
    [InlineData("SOURCES")]
    public async Task A_differently_cased_sources_list_cannot_skip_the_project_check(string property)
    {
        IReadOnlyList<string> errors = await CheckAsync(Step("VideoAnalyze", SourcesConfig(property)));

        errors.Should().ContainSingle().Which.Should().Contain("not found in this project");
    }

    [Theory]
    [InlineData("Source")]
    [InlineData("SOURCE")]
    public async Task A_differently_cased_single_source_cannot_skip_the_project_check(string property)
    {
        IReadOnlyList<string> errors = await CheckAsync(Step("VideoAnalyze", SourceConfig(property)));

        errors.Should().ContainSingle().Which.Should().Contain("not found in this project");
    }

    /// <summary>S-65: an ExtractFrame StepResult source is project-checked at save (the run time already refuses it),
    /// in any step-type spelling the save path stores and any member case the engine binds.</summary>
    [Theory]
    [InlineData("ExtractFrame", "source", "kind")]
    [InlineData(" extractframe ", "Source", "Kind")]
    [InlineData("19", "SOURCE", "KIND")]
    public async Task An_extract_frame_step_result_source_is_project_checked_at_save(string stepType, string property, string kindMember)
    {
        Enum.Parse<StepType>("19").Should().Be(StepType.ExtractFrame, "the numeric form the save path accepts");
        CreateWorkflowStepRequest step = ExtractFrameStep(stepType,
            $$$"""{"{{{property}}}":{"{{{kindMember}}}":"StepResult","stepResultId":"{{{Id}}}"},"at":"Last"}""");

        IReadOnlyList<string> foreign = await CheckAsync(step);
        IReadOnlyList<string> image = StepConfigSaveValidator.ValidateStepResultSources([step], _ => StepResultSourceVerdict.Image);

        foreign.Should().ContainSingle().Which.Should().Be($"Step 1 (ExtractFrame): step result '{Id}' was not found in this project.");
        image.Should().ContainSingle().Which.Should().Contain("did not produce a video");
        StepConfigSaveValidator.ValidateStepResultSources([step], _ => StepResultSourceVerdict.Usable).Should().BeEmpty();
    }

    private static CreateWorkflowStepRequest ExtractFrameStep(string stepType, string configJson) =>
        JsonSerializer.Deserialize<CreateWorkflowStepRequest>(new JsonObject
        {
            ["agentDefinitionId"] = Guid.NewGuid().ToString(),
            ["stepOrder"] = 1,
            ["stepType"] = stepType,
            ["extractFrameConfigJson"] = configJson
        }.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>QA-P0-21: the save-time check and the run-time resolver (<c>StepResultSourceResolver</c>)
    /// agree on both axes — an image a run-time consumer takes (VideoAnalyze's <c>allowImages: true</c>) is
    /// accepted at save, and an <c>.avi</c> the run-time resolver refuses is refused at save.</summary>
    [Fact]
    public async Task The_save_check_agrees_with_the_run_time_resolver_on_images_and_avi()
    {
        // VideoAnalyze resolves its source with allowImages: true, so an image output is accepted at save.
        CreateWorkflowStepRequest analyze = Step("VideoAnalyze", SourceConfig());
        StepConfigSaveValidator.ValidateStepResultSources([analyze], _ => StepResultSourceVerdict.Image)
            .Should().BeEmpty();

        // ExtractFrame resolves with allowImages: false, so save still refuses an image for it.
        CreateWorkflowStepRequest frame = ExtractFrameStep("ExtractFrame",
            $$$"""{"source":{"kind":"StepResult","stepResultId":"{{{Id}}}"},"at":"Last"}""");
        StepConfigSaveValidator.ValidateStepResultSources([frame], _ => StepResultSourceVerdict.Image)
            .Should().ContainSingle().Which.Should().Contain("did not produce a video");

        // .avi is not one of the run-time resolver's video extensions, so the stored row is not a video.
        World w = NewWorld();
        using (w.Db)
        {
            string outputs = $"projects/{w.ProjectId}/outputFiles/{w.ExecutionId}";
            Guid avi = AddResult(w, StepStatus.Completed, $"{outputs}/clip.avi");
            Guid mp4 = AddResult(w, StepStatus.Completed, $"{outputs}/clip.mp4");

            (await StepConfigSaveValidator.StepResultSourceVerdictOfAsync(w.Db, w.ProjectId, avi, CancellationToken.None))
                .Should().Be(StepResultSourceVerdict.NotVideo);
            (await StepConfigSaveValidator.StepResultSourceVerdictOfAsync(w.Db, w.ProjectId, mp4, CancellationToken.None))
                .Should().Be(StepResultSourceVerdict.Usable);
        }
    }

    [Fact]
    public void The_sync_twin_applies_the_same_step_type_and_casing_rules()
    {
        StepConfigSaveValidator.ValidateStepResultSources([Step("6", SourcesConfig("Sources"))], _ => false)
            .Should().ContainSingle().Which.Should().Contain("not found in this project");
    }

    [Fact]
    public async Task An_unfinished_and_a_non_video_result_get_their_own_messages_and_missing_keeps_the_one_message()
    {
        async Task<string> MessageFor(StepResultSourceVerdict verdict) =>
            (await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
                [Step("VideoAnalyze", SourceConfig())], (_, _) => Task.FromResult(verdict), CancellationToken.None)).Single();

        string missing = await MessageFor(StepResultSourceVerdict.NotFound);
        string unfinished = await MessageFor(StepResultSourceVerdict.NotFinished);
        string notVideo = await MessageFor(StepResultSourceVerdict.NotVideo);

        missing.Should().Contain("not found in this project");
        unfinished.Should().Contain("has not finished successfully").And.NotBe(missing);
        notVideo.Should().Contain("did not produce a video").And.NotBe(missing).And.NotBe(unfinished);
        (await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
                [Step("VideoAnalyze", SourceConfig())], (_, _) => Task.FromResult(StepResultSourceVerdict.Usable), CancellationToken.None))
            .Should().BeEmpty();
    }

    // ---- the verdict from the stored row ----

    private sealed record World(InferenceApiDbContext Db, Guid ProjectId, Guid ExecutionId);

    private static World NewWorld()
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid projectId = Guid.NewGuid();
        var exec = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId, WorkflowDefinitionId = Guid.NewGuid(), Status = ExecutionStatus.Passed };
        db.WorkflowExecutions.Add(exec);
        db.SaveChanges();
        return new World(db, projectId, exec.Id);
    }

    private static Guid AddResult(World w, StepStatus status, string? key, Guid? executionId = null)
    {
        var r = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId ?? w.ExecutionId,
            WorkflowStepId = Guid.NewGuid(),
            Status = status,
            OutputStorageKey = key
        };
        w.Db.WorkflowStepResults.Add(r);
        w.Db.SaveChanges();
        return r.Id;
    }

    [Fact]
    public async Task The_stored_row_decides_usable_unfinished_not_video_and_not_found()
    {
        World w = NewWorld();
        using (w.Db)
        {
            string outputs = $"projects/{w.ProjectId}/outputFiles/{w.ExecutionId}";
            Guid render = AddResult(w, StepStatus.Completed, $"{outputs}/final.mp4");
            Guid clip = AddResult(w, StepStatus.Completed, $"projects/{w.ProjectId}/agentFiles/generated/{w.ExecutionId}/step-2-v1.mp4");
            Guid failed = AddResult(w, StepStatus.Failed, $"{outputs}/half.mp4");
            Guid document = AddResult(w, StepStatus.Completed, $"{outputs}/report.json");
            Guid noOutput = AddResult(w, StepStatus.Completed, null);
            Guid outsidePrefix = AddResult(w, StepStatus.Completed, $"projects/{w.ProjectId}/agentFiles/workspace/raw.mp4");

            var other = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid() };
            w.Db.WorkflowExecutions.Add(other);
            w.Db.SaveChanges();
            Guid foreign = AddResult(w, StepStatus.Completed, $"projects/{other.ProjectId}/outputFiles/{other.Id}/final.mp4", other.Id);

            async Task<StepResultSourceVerdict> Verdict(Guid id) =>
                await StepConfigSaveValidator.StepResultSourceVerdictOfAsync(w.Db, w.ProjectId, id, CancellationToken.None);

            (await Verdict(render)).Should().Be(StepResultSourceVerdict.Usable);
            (await Verdict(clip)).Should().Be(StepResultSourceVerdict.Usable);
            (await Verdict(failed)).Should().Be(StepResultSourceVerdict.NotFinished);
            (await Verdict(document)).Should().Be(StepResultSourceVerdict.NotVideo);
            (await Verdict(noOutput)).Should().Be(StepResultSourceVerdict.NotVideo);
            (await Verdict(outsidePrefix)).Should().Be(StepResultSourceVerdict.NotVideo);
            (await Verdict(foreign)).Should().Be(StepResultSourceVerdict.NotFound);
            (await Verdict(Guid.NewGuid())).Should().Be(StepResultSourceVerdict.NotFound);
            StepConfigSaveValidator.StepResultSourceVerdictOf(w.Db, w.ProjectId, failed).Should().Be(StepResultSourceVerdict.NotFinished);
        }
    }
}
