using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Workflows;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Middleware;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>
/// product-analytics T-001: each server emitter fires from the path it is wired to, and a failing log
/// never fails the request.
/// </summary>
public class ProductEmitterWiringTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static ICurrentUser User()
    {
        var u = new Mock<ICurrentUser>();
        u.SetupGet(x => x.UserId).Returns(UserId);
        u.SetupGet(x => x.Email).Returns("t@example.com");
        u.SetupGet(x => x.IsAuthenticated).Returns(true);
        u.SetupGet(x => x.EmailVerified).Returns(true);
        u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(UserId));
        u.SetupGet(x => x.OrgRole).Returns(OrganizationRole.Owner);
        return u.Object;
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProductEventEmitter Emitter(RecordingProductEventLog log) =>
        new(log, NullLogger<ProductEventEmitter>.Instance);

    // ----------------------------------------------------------------- credits exhausted (shared 402 filter)

    private static ResultExecutingContext Ctx(IActionResult result)
    {
        var http = new DefaultHttpContext();
        var actionCtx = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(actionCtx, new List<IFilterMetadata>(), result, new object());
    }

    [Fact]
    public async Task A_402_insufficient_credits_records_credits_exhausted_once_per_user()
    {
        var log = new RecordingProductEventLog();
        var filter = new CreditsExhaustedResultFilter(Emitter(log), User());
        for (int i = 0; i < 2; i++)
        {
            await filter.OnResultExecutionAsync(
                Ctx(new ObjectResult(new { code = "INSUFFICIENT_CREDITS", required = 3 }) { StatusCode = 402 }),
                () => Task.FromResult<ResultExecutedContext>(null!));
        }

        log.Rows.Should().ContainSingle().Which.Event.Should().Be("billing.credits_exhausted");
        log.Rows[0].Source.Should().Be("server");
    }

    [Fact]
    public async Task A_dictionary_body_is_recognised_too()
    {
        var log = new RecordingProductEventLog();
        var filter = new CreditsExhaustedResultFilter(Emitter(log), User());
        await filter.OnResultExecutionAsync(
            Ctx(new ObjectResult(new Dictionary<string, object?> { ["code"] = "INSUFFICIENT_CREDITS" }) { StatusCode = 402 }),
            () => Task.FromResult<ResultExecutedContext>(null!));
        log.Rows.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(403, "INSUFFICIENT_CREDITS")]
    [InlineData(200, "INSUFFICIENT_CREDITS")]
    public async Task Other_results_record_nothing(int status, string code)
    {
        var log = new RecordingProductEventLog();
        var filter = new CreditsExhaustedResultFilter(Emitter(log), User());
        await filter.OnResultExecutionAsync(
            Ctx(new ObjectResult(new { code }) { StatusCode = status }),
            () => Task.FromResult<ResultExecutedContext>(null!));
        log.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData(402, "resolution", "resolution")]
    [InlineData(429, "audio_generation", "audio_generation")]
    [InlineData(402, "Bad feature!", "other")]
    public async Task A_plan_refusal_appends_plan_gate_hit_with_the_closed_feature_token(int status, string feature, string expected)
    {
        var log = new RecordingProductEventLog();
        var filter = new CreditsExhaustedResultFilter(Emitter(log), User());
        await filter.OnResultExecutionAsync(
            Ctx(new ObjectResult(new { code = "PLAN_NOT_ENTITLED", feature, message = "x" }) { StatusCode = status }),
            () => Task.FromResult<ResultExecutedContext>(null!));
        ProductEvent row = log.Rows.Should().ContainSingle().Subject;
        row.Event.Should().Be("billing.plan_gate_hit");
        row.Detail.Should().Be(expected);
        row.Source.Should().Be("server");
    }

    private sealed class ThrowingBody
    {
        public int Reads;
        public string Code { get { Reads++; throw new InvalidOperationException("deferred"); } }
    }

    [Fact]
    public async Task A_success_result_is_never_serialized_and_a_throwing_body_never_breaks_a_refusal()
    {
        var log = new RecordingProductEventLog();
        var filter = new CreditsExhaustedResultFilter(Emitter(log), User());
        var ok = new ThrowingBody();
        await filter.OnResultExecutionAsync(
            Ctx(new ObjectResult(ok) { StatusCode = 200 }), () => Task.FromResult<ResultExecutedContext>(null!));
        ok.Reads.Should().Be(0);

        bool nextCalled = false;
        await filter.OnResultExecutionAsync(
            Ctx(new ObjectResult(new ThrowingBody()) { StatusCode = 402 }),
            () => { nextCalled = true; return Task.FromResult<ResultExecutedContext>(null!); });
        nextCalled.Should().BeTrue();
        log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_log_does_not_stop_a_plan_refusal_response()
    {
        var log = new RecordingProductEventLog { Throw = true };
        var filter = new CreditsExhaustedResultFilter(Emitter(log), User());
        bool nextCalled = false;
        await filter.OnResultExecutionAsync(
            Ctx(new ObjectResult(new { code = "PLAN_NOT_ENTITLED", feature = "resolution" }) { StatusCode = 402 }),
            () => { nextCalled = true; return Task.FromResult<ResultExecutedContext>(null!); });
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_log_does_not_stop_the_response()
    {
        var log = new RecordingProductEventLog { Throw = true };
        var filter = new CreditsExhaustedResultFilter(Emitter(log), User());
        bool nextCalled = false;
        await filter.OnResultExecutionAsync(
            Ctx(new ObjectResult(new { code = "INSUFFICIENT_CREDITS" }) { StatusCode = 402 }),
            () => { nextCalled = true; return Task.FromResult<ResultExecutedContext>(null!); });
        nextCalled.Should().BeTrue();
    }

    // ----------------------------------------------------------------- project created

    [Fact]
    public async Task Creating_a_project_records_project_created_and_a_failing_log_does_not_fail_it()
    {
        foreach (bool failing in new[] { false, true })
        {
            InferenceApiDbContext db = NewDb();
            ICurrentUser user = User();
            var log = new RecordingProductEventLog { Throw = failing };
            var provisioning = new WorkflowTemplateProvisioningService(
                db, NullLogger<WorkflowTemplateProvisioningService>.Instance,
                new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["WorkflowTemplates:EnableAutoStarter"] = "false" }).Build());
            var controller = new ProjectsController(
                db, user, TestTenancy.Access(db, user), provisioning, null, Emitter(log));

            ActionResult<ProjectResponse> result = await controller.Create(
                new CreateProjectRequest("My reel", null), CancellationToken.None);

            result.Result.Should().BeOfType<CreatedAtActionResult>("the request succeeds whether or not the event was recorded");
            if (failing) log.Rows.Should().BeEmpty();
            else
            {
                ProductEvent row = log.Rows.Single();
                row.Event.Should().Be("activation.project_created");
                row.UserId.Should().Be(UserId);
                row.OrganizationId.Should().Be(TestTenancy.OrgOf(UserId));
            }
        }
    }

    // ----------------------------------------------------------------- output downloaded

    [Fact]
    public async Task Downloading_an_output_records_output_downloaded()
    {
        InferenceApiDbContext db = NewDb();
        ICurrentUser user = User();
        Guid orgId = TestTenancy.OrgOf(UserId);
        var project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = UserId, OrganizationId = orgId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var wf = new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "w", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var run = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = wf.Id, ProjectId = project.Id, Status = ExecutionStatus.Passed, StartedAt = DateTime.UtcNow };
        var step = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/a.mp4",
            ExecutedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(wf);
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(step);
        await db.SaveChangesAsync();

        // The object store is missing the key, so the download itself ends 404 - the marker is recorded
        // before a byte is streamed, which is the behaviour pinned here.
        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        var missing = new AmazonS3Exception("gone") { ErrorCode = "NoSuchKey", StatusCode = System.Net.HttpStatusCode.NotFound };
        s3.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(missing);
        s3.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(missing);
        s3.Setup(s => s.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(missing);
        var log = new RecordingProductEventLog();
        var controller = new OutputsController(
            db, user, TestTenancy.Access(db, user), s3.Object,
            Mock.Of<ReelBolt.Inference.Api.Services.Storage.IFileStorageService>(),
            new ConfigurationBuilder().Build(), null, Emitter(log))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await controller.DownloadOutput(project.Id, step.Id, CancellationToken.None);

        ProductEvent row = log.Rows.Single();
        row.Event.Should().Be("activation.output_downloaded");
        row.UserId.Should().Be(UserId);
    }

    // ----------------------------------------------------------------- change requested

    [Fact]
    public async Task A_created_revision_records_change_requested_and_a_refusal_does_not()
    {
        InferenceApiDbContext db = NewDb();
        ICurrentUser user = User();
        var project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = UserId, OrganizationId = TestTenancy.OrgOf(UserId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        foreach (bool created in new[] { true, false })
        {
            var revisions = new Mock<ICompositionRevisionService>();
            revisions.Setup(r => r.RequestAsync(
                    It.IsAny<Project>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(),
                    It.IsAny<CreateCompositionRevisionRequest>(), It.IsAny<CancellationToken>(), It.IsAny<ExecutionInitiator>()))
                .ReturnsAsync(created
                    ? CompositionRevisionRequestResult.Ok(new CompositionRevisionResponse(
                        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Queued", "x", null, "m", null, null, null, null,
                        DateTime.UtcNow, null, null))
                    : CompositionRevisionRequestResult.Refuse(402, "INSUFFICIENT_CREDITS", "no"));
            var log = new RecordingProductEventLog();
            var controller = new CompositionRevisionsController(
                TestTenancy.Access(db, user), revisions.Object, user, null, Emitter(log))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };

            var response = await controller.Create(project.Id, Guid.NewGuid(), new CreateCompositionRevisionRequest("make it blue"), CancellationToken.None);
            response.Result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(created ? 201 : 402, "got {0}", response.Result);

            if (created) log.Rows.Single().Event.Should().Be("product.change_requested");
            else log.Rows.Should().BeEmpty();
        }
    }

    // ----------------------------------------------------------------- template chosen

    [Fact]
    public async Task Applying_a_template_records_template_chosen_with_the_catalog_key_and_an_unknown_key_records_nothing()
    {
        InferenceApiDbContext db = NewDb();
        ICurrentUser user = User();
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.StarterTemplateKey)!;
        foreach (AgentType type in template.Steps.Select(st => st.AgentType).Distinct())
        {
            db.AgentDefinitions.Add(new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = type.ToString(),
                Description = "d",
                SystemPrompt = "p",
                AgentType = type,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        var project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = UserId, OrganizationId = TestTenancy.OrgOf(UserId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var log = new RecordingProductEventLog();
        var controller = new WorkflowsController(
            db, user, TestTenancy.Access(db, user), Mock.Of<MassTransit.IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), NullLogger<WorkflowsController>.Instance,
            productEvents: Emitter(log));

        (await controller.ApplyTemplate(project.Id, "not-a-template", null, CancellationToken.None)).Result
            .Should().BeOfType<ConflictObjectResult>();
        log.Rows.Should().BeEmpty();

        (await controller.ApplyTemplate(project.Id, WorkflowTemplateCatalog.StarterTemplateKey, null, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>();
        ProductEvent row = log.Rows.Single();
        row.Event.Should().Be("product.template_chosen");
        row.Detail.Should().Be(WorkflowTemplateCatalog.StarterTemplateKey);
        row.Source.Should().Be("server");
    }
}
