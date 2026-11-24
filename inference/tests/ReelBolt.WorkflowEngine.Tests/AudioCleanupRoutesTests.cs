#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The hidden AudioCleanup workflow's routes: start, estimate and status poll, and the sizing of the hold they share.</summary>
public class AudioCleanupRoutesTests
{
    private const string Model = "fal-ai/elevenlabs/audio-isolation";

    [Fact]
    public async Task A_foreign_project_gets_404_on_every_route()
    {
        using Harness h = await Harness.CreateAsync();
        AudioCleanupController stranger = h.Controller(userId: Guid.NewGuid());
        AudioCleanupRequestBody body = new("Hum");

        (await stranger.Start(h.ProjectId, h.MediaRef, body, default)).Should().BeOfType<NotFoundResult>();
        (await stranger.Estimate(h.ProjectId, h.MediaRef, body, default)).Should().BeOfType<NotFoundResult>();
        (await stranger.Status(h.ProjectId, h.MediaRef, Guid.NewGuid(), default)).Should().BeOfType<NotFoundResult>();
        h.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_or_other_media_is_404_before_anything_is_queued()
    {
        using Harness h = await Harness.CreateAsync();
        (await h.Controller().Start(h.ProjectId, $"file:{Guid.NewGuid()}", new("Hum"), default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().Start(h.ProjectId, "step_result:nope", new("Hum"), default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().Estimate(h.ProjectId, $"file:{Guid.NewGuid()}", new("Hum"), default)).Should().BeOfType<NotFoundResult>();
        h.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_local_target_queues_the_hidden_workflow_in_the_free_bucket_without_a_price_even_with_an_empty_balance()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 0);
        ObjectResult result = (await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new("hum"), default)).Should().BeOfType<ObjectResult>().Subject;

        result.StatusCode.Should().Be(202);
        AudioCleanupStartedResponse started = result.Value.Should().BeOfType<AudioCleanupStartedResponse>().Subject;
        started.Target.Should().Be("Hum");
        started.Hosted.Should().BeFalse();
        h.Guard.Buckets.Should().Equal(RunRateBucket.Free);
        h.Queued.Should().ContainSingle().Which.Kind.Should().Be(WorkflowDefinitionKind.AudioCleanup);
        h.Prices.Invocations.Should().BeEmpty("a local target is never priced");
        WorkflowStep step = await h.Db.WorkflowSteps.SingleAsync();
        step.StepType.Should().Be(StepType.AudioCleanup);
        AudioCleanupStepConfig config = JsonSerializer.Deserialize<AudioCleanupStepConfig>(step.AudioCleanupConfigJson!, AudioCleanupStepConfig.JsonOptions)!;
        config.MaxSourceMinutes.Should().BeNull();
        config.Source.ProjectFileId.Should().Be(h.FileId);
        JsonSerializer.Serialize(result.Value).Should().NotContain("projects/").And.NotContain("storage");
    }

    [Fact]
    public async Task A_4m10s_source_holds_five_minutes_and_the_estimate_equals_that_hold()
    {
        using Harness h = await Harness.CreateAsync(durationSec: 250, availableMillis: 100_000);
        AudioCleanupController controller = h.Controller(billing: true);
        AudioCleanupRequestBody body = new("IsolateVoice", AllowSourceMediaEgress: true);

        AudioCleanupEstimateResponse estimate = (await controller.Estimate(h.ProjectId, h.MediaRef, body, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<AudioCleanupEstimateResponse>().Subject;
        ObjectResult started = (await controller.Start(h.ProjectId, h.MediaRef, body, default)).Should().BeOfType<ObjectResult>().Subject;

        started.StatusCode.Should().Be(202);
        h.Guard.Buckets.Should().Equal(RunRateBucket.Paid);
        string json = (await h.Db.WorkflowSteps.SingleAsync()).AudioCleanupConfigJson!;
        AudioCleanupStepConfig.JsonOptions.Should().NotBeNull();
        JsonSerializer.Deserialize<AudioCleanupStepConfig>(json, AudioCleanupStepConfig.JsonOptions)!.MaxSourceMinutes.Should().Be(5);

        long engineHold = (await AudioCleanupCreditPricing.TryStepHoldMillisAsync(json, h.Providers.Object, h.Prices.Object))!.Value;
        engineHold.Should().Be(5_000, "5 started minutes at the test price");
        estimate.Credits.Should().Be(engineHold / 1000.0, "estimate == reservation");
        estimate.Hosted.Should().BeTrue();
        estimate.Seconds.Should().Be(250);
        estimate.Affordable.Should().BeTrue();
        estimate.BalanceCredits.Should().Be(100);
    }

    [Fact]
    public async Task A_31_minute_source_is_refused_before_anything_is_queued()
    {
        using Harness h = await Harness.CreateAsync(durationSec: 31 * 60);
        ObjectResult refused = (await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new("RemoveMusic", true), default))
            .Should().BeOfType<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(413);
        JsonSerializer.Serialize(refused.Value).Should().Contain("SOURCE_TOO_LONG");
        h.Queued.Should().BeEmpty();
        h.Guard.Calls.Should().Be(0);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_local_source_over_the_sixty_minute_cap_is_413_AUDIO_SOURCE_TOO_LONG_before_anything_is_queued()
    {
        using Harness h = await Harness.CreateAsync(durationSec: 61 * 60);
        ObjectResult refused = (await h.Controller().Start(h.ProjectId, h.MediaRef, new("Hum"), default))
            .Should().BeOfType<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(413);
        JsonSerializer.Serialize(refused.Value).Should().Contain("AUDIO_SOURCE_TOO_LONG");
        h.Queued.Should().BeEmpty();
        h.Guard.Calls.Should().Be(0);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_route_honours_a_lowered_Cleanup_Local_MaxSourceMinutes()
    {
        using Harness h = await Harness.CreateAsync(durationSec: 11 * 60, localMaxMinutes: 10);
        ObjectResult refused = (await h.Controller().Start(h.ProjectId, h.MediaRef, new("Wind"), default))
            .Should().BeOfType<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(413);
        JsonSerializer.Serialize(refused.Value).Should().Contain("AUDIO_SOURCE_TOO_LONG");
        h.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_hosted_target_without_the_egress_consent_is_400_and_a_local_one_ignores_it()
    {
        using Harness h = await Harness.CreateAsync();
        BadRequestObjectResult refused = (await h.Controller().Start(h.ProjectId, h.MediaRef, new("IsolateVoice", false), default))
            .Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(refused.Value).Should().Contain("EGRESS_REFUSED");
        h.Queued.Should().BeEmpty();

        ((ObjectResult)await h.Controller().Start(h.ProjectId, h.MediaRef, new("Echo", false), default)).StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task A_plan_without_the_feature_is_402_PLAN_NOT_ENTITLED_before_the_consent_check()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectResult refused = (await h.Controller(entitled: false).Start(h.ProjectId, h.MediaRef, new("IsolateVoice", false), default))
            .Should().BeOfType<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(402);
        JsonSerializer.Serialize(refused.Value).Should().Contain("PLAN_NOT_ENTITLED");
        h.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Too_few_credits_is_402_INSUFFICIENT_CREDITS_before_the_queue()
    {
        using Harness h = await Harness.CreateAsync(durationSec: 250, availableMillis: 4_999);
        ObjectResult refused = (await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new("IsolateVoice", true), default))
            .Should().BeOfType<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(402);
        JsonSerializer.Serialize(refused.Value).Should().Contain("INSUFFICIENT_CREDITS");
        h.Queued.Should().BeEmpty();
        h.Guard.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_rate_limited_start_is_429_and_a_plan_concurrency_cap_is_the_plan_refusal()
    {
        using Harness limited = await Harness.CreateAsync(grant: false);
        ObjectResult rate = (await limited.Controller().Start(limited.ProjectId, limited.MediaRef, new("Wind"), default)).Should().BeOfType<ObjectResult>().Subject;
        rate.StatusCode.Should().Be(429);
        JsonSerializer.Serialize(rate.Value).Should().Contain(RunRateRefusal.Code);

        using Harness capped = await Harness.CreateAsync(capViolation: new PlanGateViolation("PLAN_LIMIT", "concurrent_executions", "cap", 429));
        ObjectResult plan = (await capped.Controller().Start(capped.ProjectId, capped.MediaRef, new("Wind"), default)).Should().BeOfType<ObjectResult>().Subject;
        plan.StatusCode.Should().Be(429);
        JsonSerializer.Serialize(plan.Value).Should().Contain("concurrent_executions");
    }

    [Fact]
    public async Task Resuming_or_retrying_a_hidden_audio_cleanup_run_is_refused_with_its_own_code()
    {
        using Harness h = await Harness.CreateAsync();
        Guid executionId = ((AudioCleanupStartedResponse)((ObjectResult)await h.Controller().Start(h.ProjectId, h.MediaRef, new("Hum"), default)).Value!).ExecutionId;
        WorkflowExecution execution = await h.Db.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
        execution.Status = ExecutionStatus.Failed;
        await h.Db.SaveChangesAsync();

        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(h.OwnerId);
        user.SetupGet(u => u.Email).Returns("t@example.com");
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(h.OwnerId));
        user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
        user.SetupGet(u => u.EmailVerified).Returns(true);
        WorkflowsController workflows = new(
            h.Db, user.Object, TestTenancy.Access(h.Db, user.Object), Mock.Of<MassTransit.IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(h.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowsController>.Instance);

        ConflictObjectResult resume = (await workflows.ResumeExecution(h.ProjectId, execution.WorkflowDefinitionId, executionId, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(resume.Value).Should().Contain("AUDIO_CLEANUP_RESUME_NOT_SUPPORTED");
        ConflictObjectResult retry = (await workflows.RetryExecution(h.ProjectId, execution.WorkflowDefinitionId, executionId, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(retry.Value).Should().Contain("AUDIO_CLEANUP_RETRY_NOT_SUPPORTED");
    }

    [Fact]
    public async Task Bad_targets_are_400()
    {
        using Harness h = await Harness.CreateAsync();
        foreach (AudioCleanupRequestBody? body in new AudioCleanupRequestBody?[] { null, new(null), new("Karaoke"), new("7"), new("Hum", false, "Gif") })
            (await h.Controller().Start(h.ProjectId, h.MediaRef, body, default)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task The_status_poll_reports_queued_ready_unchanged_and_failed_and_404s_other_runs()
    {
        using Harness h = await Harness.CreateAsync();
        AudioCleanupController controller = h.Controller();
        Guid executionId = ((AudioCleanupStartedResponse)((ObjectResult)await controller.Start(h.ProjectId, h.MediaRef, new("Hum"), default)).Value!).ExecutionId;

        AudioCleanupStatusResponse queued = await Poll(controller, h, executionId);
        (queued.State, queued.Target).Should().Be(("queued", "Hum"));

        WorkflowExecution execution = await h.Db.WorkflowExecutions.SingleAsync();
        Guid cleanFile = Guid.NewGuid();
        h.Db.ProjectFiles.Add(new ProjectFile { Id = cleanFile, ProjectId = h.ProjectId, OriginalFileName = "c.mp4", MimeType = "video/mp4", StorageKey = "k", StorageBucket = "b", CsamStatus = CsamScanStatus.Clean });
        execution.Status = ExecutionStatus.Passed;
        h.Db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = (await h.Db.WorkflowSteps.SingleAsync()).Id,
            Status = StepStatus.Completed,
            Output = $$"""{"status":"completed","target":"Hum","method":"local:hum","applied":true,"durationSec":90.5,"outputFileId":"{{cleanFile}}"}"""
        });
        await h.Db.SaveChangesAsync();
        AudioCleanupStatusResponse ready = await Poll(controller, h, executionId);
        (ready.State, ready.Method, ready.Applied, ready.DurationSec, ready.OutputFileId).Should().Be(("ready", "local:hum", true, 90.5, cleanFile));

        WorkflowStepResult row = await h.Db.WorkflowStepResults.SingleAsync();
        row.Output = """{"status":"completed","method":"local:hum","applied":false,"durationSec":90,"outputFileId":null}""";
        await h.Db.SaveChangesAsync();
        AudioCleanupStatusResponse unchanged = await Poll(controller, h, executionId);
        (unchanged.State, unchanged.Applied, unchanged.OutputFileId).Should().Be(("ready", false, null));
        unchanged.Message.Should().NotBeNullOrWhiteSpace();

        execution.Status = ExecutionStatus.Failed;
        row.Output = """{"status":"failed","errorCode":"SOURCE_TOO_LONG","message":"too long"}""";
        await h.Db.SaveChangesAsync();
        AudioCleanupStatusResponse failed = await Poll(controller, h, executionId);
        (failed.State, failed.FailureCode, failed.Message).Should().Be(("failed", "SOURCE_TOO_LONG", "too long"));

        (await controller.Status(h.ProjectId, h.MediaRef, Guid.NewGuid(), default)).Should().BeOfType<NotFoundResult>();
        WorkflowDefinition standard = new() { Id = Guid.NewGuid(), ProjectId = h.ProjectId, Name = "s", Kind = WorkflowDefinitionKind.Standard };
        WorkflowExecution other = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = standard.Id, ProjectId = h.ProjectId, Status = ExecutionStatus.Passed, CorrelationId = "c" };
        h.Db.WorkflowDefinitions.Add(standard);
        h.Db.WorkflowExecutions.Add(other);
        await h.Db.SaveChangesAsync();
        (await controller.Status(h.ProjectId, h.MediaRef, other.Id, default)).Should().BeOfType<NotFoundResult>("only AudioCleanup runs are readable here");
    }

    [Fact]
    public async Task Estimate_with_billing_off_or_a_local_target_costs_nothing_and_an_unpriced_model_is_unavailable()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 0);
        AudioCleanupEstimateResponse off = await EstimateOf(h.Controller(billing: false), h, new("IsolateVoice", true));
        (off.BillingEnabled, off.Affordable, off.Hosted).Should().Be((false, true, true));

        AudioCleanupEstimateResponse local = await EstimateOf(h.Controller(billing: true), h, new("Wind"));
        (local.Credits, local.Affordable, local.Hosted).Should().Be((0d, true, false));

        using Harness unpriced = await Harness.CreateAsync(providerResolves: false);
        AudioCleanupEstimateResponse none = await EstimateOf(unpriced.Controller(billing: true), unpriced, new("IsolateVoice"));
        (none.Credits, none.Affordable).Should().Be((null, false));
        none.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

    private static async Task<AudioCleanupStatusResponse> Poll(AudioCleanupController c, Harness h, Guid id) =>
        (await c.Status(h.ProjectId, h.MediaRef, id, default)).Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<AudioCleanupStatusResponse>().Subject;

    private static async Task<AudioCleanupEstimateResponse> EstimateOf(AudioCleanupController c, Harness h, AudioCleanupRequestBody body) =>
        (await c.Estimate(h.ProjectId, h.MediaRef, body, default)).Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<AudioCleanupEstimateResponse>().Subject;

    private sealed class RecordingGuard(bool grant) : IAssistantExecutionGuard
    {
        public List<RunRateBucket> Buckets { get; } = [];
        public int Calls { get; private set; }

        public async Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            Calls++;
            Buckets.Add(bucket);
            if (!grant) return new RunRateDecision(false, bucket, 10, 10, 3600, 120);
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }
    }

    private sealed class RecordingExecutions(InferenceApiDbContext db, PlanGateViolation? cap) : IWorkflowExecutionService
    {
        public List<WorkflowDefinition> Queued { get; } = [];

        public Task<WorkflowExecutionResponse> ExecuteAsync(
            Guid projectId, Guid workflowId, Guid initiatedByUserId, string? userRequest, ExecutionInitiator initiator,
            CancellationToken ct, bool regenerateVideoClips = false, RunChoices? runChoices = null) => throw new NotSupportedException();

        public async Task<WorkflowExecutionResponse> QueueAsync(
            Guid projectId, Guid workflowId, Guid initiatedByUserId, string? userRequest, ExecutionInitiator initiator,
            CancellationToken ct, bool regenerateVideoClips, RunChoices? runChoices, WorkflowDefinitionKind kind, Guid? executionId,
            Func<CancellationToken, Task>? beforePublish = null)
        {
            if (cap is not null) throw new PlanLimitException(cap);
            Queued.Add(await db.WorkflowDefinitions.SingleAsync(w => w.Id == workflowId && w.Kind == kind, ct));
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                ProjectId = projectId,
                InitiatedByUserId = initiatedByUserId,
                Status = ExecutionStatus.Queued,
                StartedAt = DateTime.UtcNow,
                CorrelationId = "c"
            };
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync(ct);
            if (beforePublish is not null) await beforePublish(ct);
            return new WorkflowExecutionResponse(
                execution.Id, workflowId, "Queued", execution.StartedAt, null, 0, null, "c", null,
                new List<StepResultResponse>(), new List<ReviewScoreResponse>());
        }
    }

    private sealed class Harness : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid FileId { get; init; }
        public string MediaRef => $"file:{FileId}";
        public required FakeNonWorkflowCharges Charges { get; init; }
        public required RecordingGuard Guard { get; init; }
        public required RecordingExecutions Executions { get; init; }
        public List<WorkflowDefinition> Queued => Executions.Queued;
        public Mock<IInferenceProviderResolver> Providers { get; } = new();
        public Mock<IModelPriceService> Prices { get; } = new();
        public IConfiguration? Configuration { get; init; }

        public static async Task<Harness> CreateAsync(
            double durationSec = 90, long availableMillis = 10_000, bool grant = true, PlanGateViolation? capViolation = null, bool providerResolves = true,
            int? localMaxMinutes = null)
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"cleanup-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            InferenceApiDbContext db = new(options);
            Guid ownerId = Guid.NewGuid();
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = ownerId,
                OrganizationId = TestTenancy.OrgOf(ownerId),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = "talk.mp4",
                MimeType = "video/mp4",
                StorageKey = $"projects/{project.Id}/userFiles/talk.mp4",
                StorageBucket = "b",
                SizeBytes = 10,
                CsamStatus = CsamScanStatus.Clean
            };
            db.ProjectFiles.Add(file);
            db.MediaDerivatives.Add(new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                SourceProjectFileId = file.Id,
                SourceKind = MediaSourceKind.ProjectFile,
                SourceStorageKey = file.StorageKey,
                CacheKey = Guid.NewGuid().ToString("N"),
                Kind = MediaDerivativeKind.Thumbnail,
                StorageKey = "x",
                StorageBucket = "b",
                SourceProbeJson = new MediaSourceProbe(durationSec, 30, 1, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
            });
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "placeholder", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            await db.SaveChangesAsync();

            Harness h = new()
            {
                Db = db,
                ProjectId = project.Id,
                OwnerId = ownerId,
                FileId = file.Id,
                Charges = new FakeNonWorkflowCharges { AvailableMillis = availableMillis },
                Guard = new RecordingGuard(grant),
                Executions = new RecordingExecutions(db, capViolation),
                Configuration = localMaxMinutes is { } minutes
                    ? new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            [AudioCleanupCreditPricing.LocalMaxSourceMinutesKey] =
                                minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        })
                        .Build()
                    : null
            };
            h.Providers.Setup(p => p.ResolveAudioIsolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(providerResolves
                    ? new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation, "", Model, "k", 60, AccountId: Guid.NewGuid())
                    : null);
            h.Prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
            return h;
        }

        public AudioCleanupController Controller(bool billing = false, Guid? userId = null, bool entitled = true)
        {
            Guid user = userId ?? OwnerId;
            Mock<ICurrentUser> current = new();
            current.SetupGet(u => u.UserId).Returns(user);
            current.SetupGet(u => u.Email).Returns("t@example.com");
            current.SetupGet(u => u.IsAuthenticated).Returns(true);
            current.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(user));
            current.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            current.SetupGet(u => u.EmailVerified).Returns(true);

            Mock<IWorkflowCostEstimator> estimator = new();
            estimator.SetupGet(e => e.BillingEnabled).Returns(billing);
            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(new ValueTask<OrganizationEntitlementSet>(OrganizationEntitlementSet.Unlimited with { AudioCleanupAllowed = entitled }));

            return new AudioCleanupController(
                Db, TestTenancy.Access(Db, current.Object), current.Object, Executions, Guard, Providers.Object,
                Prices.Object, entitlements.Object, Charges, estimator.Object, configuration: Configuration)
            {
                ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
            };
        }

        public void Dispose() => Db.Dispose();
    }
}
