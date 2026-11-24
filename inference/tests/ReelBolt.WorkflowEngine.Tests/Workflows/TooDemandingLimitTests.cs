using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>The daily limit on repeat refunded SOURCE_TOO_DEMANDING runs: 429 TOO_DEMANDING_LIMIT before credits.</summary>
public sealed class TooDemandingLimitTests
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
        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public Guid WorkflowId { get; } = Guid.NewGuid();
        public RunStartGate Gate { get; }
        public VideoSourceGate Source { get; }

        public Fixture(int limit = 3, InferenceApiDbContext? db = null)
        {
            Db = db ?? new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Guid userId = Guid.NewGuid();
            Guid orgId = TestTenancy.OrgOf(userId);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = userId, OrganizationId = orgId };
            Db.Projects.Add(Project);
            Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = WorkflowId, Name = "W", ProjectId = Project.Id });
            var designBuild = new Mock<IDesignBuildEstimateGate>();
            designBuild
                .Setup(g => g.CheckAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesignBuildGateResult.NotApplicable);
            Source = new VideoSourceGate(Db, VideoSourceLimits.Default, limit);
            Gate = new RunStartGate(Db, new FakeUser(userId, orgId), null, null, designBuild.Object, sourceGate: Source);
        }

        public WorkflowStep Analyze(string name, long size, double? durationSec = null, int height = 2160)
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
                    SourceProbeJson = new MediaSourceProbe(d, 30, 1, height * 16 / 9, height, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
                });
            }
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = WorkflowId,
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = "{\"source\":{\"kind\":\"ProjectFile\",\"projectFileId\":\"" + file.Id + "\"}}"
            };
            Db.WorkflowSteps.Add(step);
            return step;
        }

        public void Fail(Project project, int count, TimeSpan age, string code = "SOURCE_TOO_DEMANDING")
        {
            for (int i = 0; i < count; i++)
            {
                var exec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = WorkflowId, ProjectId = project.Id, OrganizationId = project.OrganizationId };
                Db.WorkflowExecutions.Add(exec);
                Db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = exec.Id,
                    WorkflowStepId = Guid.NewGuid(),
                    FailureCode = code,
                    ExecutedAt = DateTime.UtcNow - age
                });
            }
        }

        public async Task<ActionResult?> RefuseAsync(RunStartPricing pricing)
        {
            await Db.SaveChangesAsync();
            return await Gate.RefuseAsync(Project, WorkflowId, null, pricing, null, CancellationToken.None);
        }
    }

    private static void AssertLimit(ActionResult? r)
    {
        var o = r.Should().BeAssignableTo<ObjectResult>().Subject;
        o.StatusCode.Should().Be(429);
        System.Text.Json.JsonSerializer.Serialize(o.Value).Should()
            .Contain("TOO_DEMANDING_LIMIT").And.Contain("Nothing was spent");
    }

    [Fact]
    public async Task At_the_limit_a_heavy_run_is_refused_with_429_and_retry_after()
    {
        var f = new Fixture();
        f.Analyze("big.mp4", 2 * Gb, 600);
        f.Fail(f.Project, 3, TimeSpan.FromHours(1));
        ActionResult? r = await f.RefuseAsync(RunStartPricing.SavedWorkflow);
        AssertLimit(r);
        var ctx = new DefaultHttpContextForTest();
        await r!.ExecuteResultAsync(ctx.Action);
        ctx.Http.Response.Headers.RetryAfter.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Under_the_limit_is_allowed()
    {
        var f = new Fixture();
        f.Analyze("big.mp4", 2 * Gb, 600);
        f.Fail(f.Project, 2, TimeSpan.FromHours(1));
        (await f.RefuseAsync(RunStartPricing.SavedWorkflow)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_failures_are_not_counted()
    {
        var f = new Fixture();
        f.Analyze("big.mp4", 2 * Gb, 600);
        var other = new Project { Id = Guid.NewGuid(), Name = "O", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        f.Db.Projects.Add(other);
        f.Fail(other, 5, TimeSpan.FromHours(1));
        (await f.RefuseAsync(RunStartPricing.SavedWorkflow)).Should().BeNull();
    }

    [Fact]
    public async Task Failures_older_than_24_hours_and_other_codes_are_not_counted()
    {
        var f = new Fixture();
        f.Analyze("big.mp4", 2 * Gb, 600);
        f.Fail(f.Project, 3, TimeSpan.FromHours(25));
        f.Fail(f.Project, 3, TimeSpan.FromHours(1), "ENCODE_FAILED");
        (await f.RefuseAsync(RunStartPricing.SavedWorkflow)).Should().BeNull();
    }

    [Fact]
    public async Task A_small_source_run_is_allowed_even_at_the_limit()
    {
        var f = new Fixture();
        f.Analyze("small.mp4", 200 * 1024 * 1024, 60, height: 720);
        f.Fail(f.Project, 3, TimeSpan.FromHours(1));
        (await f.RefuseAsync(RunStartPricing.SavedWorkflow)).Should().BeNull();
    }

    [Fact]
    public async Task A_big_file_without_a_probe_counts_as_heavy_by_size()
    {
        var f = new Fixture();
        f.Analyze("noprobe.mp4", 2 * Gb);
        f.Fail(f.Project, 3, TimeSpan.FromHours(1));
        AssertLimit(await f.RefuseAsync(RunStartPricing.SavedWorkflow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_zero_or_negative_setting_disables_the_limit(int setting)
    {
        var f = new Fixture(setting);
        f.Analyze("big.mp4", 2 * Gb, 600);
        f.Fail(f.Project, 10, TimeSpan.FromHours(1));
        (await f.RefuseAsync(RunStartPricing.SavedWorkflow)).Should().BeNull();
    }

    [Fact]
    public async Task A_read_failure_allows_the_run()
    {
        var f = new Fixture();
        WorkflowStep step = f.Analyze("big.mp4", 2 * Gb, 600);
        f.Fail(f.Project, 3, TimeSpan.FromHours(1));
        await f.Db.SaveChangesAsync();
        f.Db.Dispose();
        (await f.Source.CheckTooDemandingLimitAsync(f.Project, f.WorkflowId, [step], null, null, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Retry_and_Resume_are_judged_on_the_runs_own_steps()
    {
        var f = new Fixture();
        WorkflowStep step = f.Analyze("big.mp4", 2 * Gb, 600);
        f.Fail(f.Project, 3, TimeSpan.FromHours(1));
        AssertLimit(await f.RefuseAsync(RunStartPricing.AllOf([step])));
        AssertLimit(await f.RefuseAsync(RunStartPricing.Charged([step], [1])));
        (await f.RefuseAsync(RunStartPricing.Charged([step], [2]))).Should().BeNull();
    }

    [Fact]
    public async Task The_assistant_path_asks_the_gate_with_no_steps_and_gets_the_same_refusal()
    {
        var f = new Fixture();
        f.Analyze("big.mp4", 2 * Gb, 600);
        f.Fail(f.Project, 3, TimeSpan.FromHours(1));
        await f.Db.SaveChangesAsync();
        ObjectResult? r = await f.Source.CheckTooDemandingLimitAsync(f.Project, f.WorkflowId, null, null, null, CancellationToken.None);
        r.Should().NotBeNull();
        r!.StatusCode.Should().Be(429);
        VideoSourceGate.TooDemandingLimitMessage.Should().Contain("Nothing was spent");
    }

    private sealed class DefaultHttpContextForTest
    {
        public Microsoft.AspNetCore.Http.DefaultHttpContext Http { get; } = new()
        {
            RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddSingleton(new Mock<Microsoft.AspNetCore.Mvc.Infrastructure.IActionResultExecutor<ObjectResult>>().Object)
                .BuildServiceProvider()
        };
        public ActionContext Action => new(Http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
    }
}
