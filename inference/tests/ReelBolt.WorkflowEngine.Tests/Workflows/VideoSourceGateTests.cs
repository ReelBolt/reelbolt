using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>The run-start footage check: refused before credits on Execute, Retry and Resume paths, in plain words.</summary>
public sealed class VideoSourceGateTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private sealed class FakeUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "t@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }

    private sealed class Fixture
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Project Project { get; }
        public Guid WorkflowId { get; } = Guid.NewGuid();
        public RunStartGate Gate { get; }

        public Fixture()
        {
            Guid userId = Guid.NewGuid();
            Guid orgId = TestTenancy.OrgOf(userId);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = userId, OrganizationId = orgId };
            Db.Projects.Add(Project);
            Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = WorkflowId, Name = "W", ProjectId = Project.Id });
            var designBuild = new Mock<IDesignBuildEstimateGate>();
            designBuild
                .Setup(g => g.CheckAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesignBuildGateResult.NotApplicable);
            Gate = new RunStartGate(Db, new FakeUser(userId, orgId), null, null, designBuild.Object);
        }

        public Guid AddFile(string name, long size, double? durationSec = null)
        {
            var file = new ProjectFile { Id = Guid.NewGuid(), ProjectId = Project.Id, OriginalFileName = name, StorageKey = "k/" + name, SizeBytes = size };
            Db.ProjectFiles.Add(file);
            if (durationSec is { } d)
            {
                Db.MediaDerivatives.Add(new MediaDerivative
                {
                    Id = Guid.NewGuid(),
                    ProjectId = Project.Id,
                    SourceStorageKey = file.StorageKey,
                    SourceProbeJson = new MediaSourceProbe(d, 30, 1, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
                });
            }
            return file.Id;
        }

        public WorkflowStep AddAnalyze(int order, string sourceJson, string extra = "")
        {
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = WorkflowId,
                StepOrder = order,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = "{\"source\":" + sourceJson + extra + "}"
            };
            Db.WorkflowSteps.Add(step);
            return step;
        }

        public async Task<ActionResult?> RefuseAsync(RunStartPricing pricing, RunBrief? brief = null)
        {
            await Db.SaveChangesAsync();
            return await Gate.RefuseAsync(Project, WorkflowId, null, pricing, null, CancellationToken.None, brief: brief);
        }
    }

    private static string Pinned(Guid id) => "{\"kind\":\"ProjectFile\",\"projectFileId\":\"" + id + "\"}";

    private static (int Status, string Json) Read(ActionResult result)
    {
        var o = (ObjectResult)result;
        return (o.StatusCode ?? 200, System.Text.Json.JsonSerializer.Serialize(o.Value));
    }

    [Fact]
    public async Task An_open_ProjectFile_slot_is_judged_on_the_briefs_video_files_at_Run_and_in_lint()
    {
        var f = new Fixture();
        Guid big = f.AddFile("huge.mp4", 40 * Gb);
        f.AddAnalyze(1, "{\"kind\":\"ProjectFile\"}");
        RunBrief brief = new(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [big])]);

        var (status, json) = Read((await f.RefuseAsync(RunStartPricing.SavedWorkflow, brief))!);
        status.Should().Be(422);
        json.Should().Contain("huge.mp4");

        (await f.Gate.RefuseAsync(f.Project, f.WorkflowId, null, RunStartPricing.SavedWorkflow, null, CancellationToken.None, brief: null))
            .Should().BeNull("with no brief the open slot names no file yet");

        var gate = new VideoSourceGate(f.Db, VideoSourceLimits.Default);
        (await gate.FindAllAsync(f.Project, f.WorkflowId, null, brief, CancellationToken.None))
            .Should().ContainSingle().Which.FileName.Should().Be("huge.mp4");
    }

    [Fact]
    public async Task Execute_refuses_a_pinned_file_over_the_size_limit_in_whole_gigabytes()
    {
        var f = new Fixture();
        f.AddAnalyze(1, Pinned(f.AddFile("holiday.mp4", 40 * Gb)));

        (int status, string json) = Read((await f.RefuseAsync(RunStartPricing.SavedWorkflow))!);

        status.Should().Be(422);
        json.Should().Contain("SOURCE_TOO_LARGE").And.Contain("holiday.mp4").And.Contain("40 GB").And.Contain("30 GB")
            .And.Contain("\"limitGb\":30").And.Contain("Nothing was spent");
        json.Should().NotContain("MaxInputBytes").And.NotContain("bytes");
    }

    [Fact]
    public async Task A_file_within_the_limits_passes()
    {
        var f = new Fixture();
        f.AddAnalyze(1, Pinned(f.AddFile("ok.mp4", 5 * Gb, durationSec: 600)));

        (await f.RefuseAsync(RunStartPricing.SavedWorkflow)).Should().BeNull();
    }

    [Fact]
    public async Task A_stored_probe_longer_than_the_limit_is_refused_in_whole_minutes()
    {
        var f = new Fixture();
        f.AddAnalyze(1, Pinned(f.AddFile("long.mp4", 2 * Gb, durationSec: 7500)));

        (int status, string json) = Read((await f.RefuseAsync(RunStartPricing.SavedWorkflow))!);

        status.Should().Be(422);
        json.Should().Contain("SOURCE_TOO_LONG").And.Contain("125 minutes").And.Contain("60 minutes").And.Contain("\"limitMinutes\":60");
    }

    [Fact]
    public async Task The_old_pinned_defaults_in_saved_workflows_do_not_refuse_phone_footage()
    {
        var f = new Fixture();
        f.AddAnalyze(1, Pinned(f.AddFile("phone.mp4", 3 * Gb, durationSec: 2400)), ",\"maxInputBytes\":2000000000,\"maxDurationSeconds\":1800");

        (await f.RefuseAsync(RunStartPricing.SavedWorkflow)).Should().BeNull();
    }

    [Fact]
    public async Task A_lower_limit_the_author_chose_still_applies()
    {
        var f = new Fixture();
        f.AddAnalyze(1, Pinned(f.AddFile("a.mp4", 3 * Gb)), ",\"maxInputBytes\":1073741824");

        (int status, string json) = Read((await f.RefuseAsync(RunStartPricing.SavedWorkflow))!);

        status.Should().Be(422);
        json.Should().Contain("3 GB").And.Contain("1 GB");
    }

    [Fact]
    public async Task A_file_chosen_for_a_brief_slot_is_checked()
    {
        var f = new Fixture();
        f.AddAnalyze(1, "{\"kind\":\"BriefFiles\",\"role\":\"Video\"}");
        Guid big = f.AddFile("slot.mp4", 35 * Gb);
        var brief = new RunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [big])]);

        (int status, string json) = Read((await f.RefuseAsync(RunStartPricing.SavedWorkflow, brief))!);

        status.Should().Be(422);
        json.Should().Contain("slot.mp4");
    }

    [Fact]
    public async Task Retry_judges_the_runs_own_steps()
    {
        var f = new Fixture();
        WorkflowStep step = f.AddAnalyze(1, Pinned(f.AddFile("retry.mp4", 40 * Gb)));

        (int status, _) = Read((await f.RefuseAsync(RunStartPricing.AllOf([step])))!);

        status.Should().Be(422);
    }

    [Fact]
    public async Task Resume_ignores_steps_that_will_not_run_again()
    {
        var f = new Fixture();
        WorkflowStep done = f.AddAnalyze(1, Pinned(f.AddFile("done.mp4", 40 * Gb)));
        WorkflowStep next = f.AddAnalyze(2, Pinned(f.AddFile("fine.mp4", 1 * Gb)));

        (await f.RefuseAsync(RunStartPricing.Charged([done, next], [2]))).Should().BeNull();
        (await f.RefuseAsync(RunStartPricing.Charged([done, next], [1, 2]))).Should().NotBeNull();
    }

    [Fact]
    public async Task A_step_output_source_is_left_to_the_engine()
    {
        var f = new Fixture();
        f.AddAnalyze(1, "{\"kind\":\"PreviousStepOutput\"}");

        (await f.RefuseAsync(RunStartPricing.SavedWorkflow)).Should().BeNull();
    }
}
