using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Controllers.Filters;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The ConditionalGet filter, the ?detail=summary step-result mapping and the single step-result endpoint.</summary>
public sealed class ConditionalGetFilterTests : IDisposable
{
    private static readonly Guid Org = Guid.Parse("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherOrg = Guid.Parse("33333333-0000-0000-0000-0000000000b3");

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();

    public ConditionalGetFilterTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>().UseSqlite(_connection).Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // --- filter -----------------------------------------------------------------------------------

    private static async Task<(HttpContext Http, bool NextCalled)> RunAsync(string method, IActionResult result, string? ifNoneMatch = null, Action<HttpContext>? prepare = null)
    {
        ServiceCollection services = new();
        services.AddOptions();
        services.AddControllers();
        DefaultHttpContext http = new() { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = method;
        if (ifNoneMatch is not null) http.Request.Headers.IfNoneMatch = ifNoneMatch;
        http.Response.Body = new MemoryStream();
        prepare?.Invoke(http);
        ActionContext actionContext = new(http, new RouteData(), new ActionDescriptor());
        ResultExecutingContext context = new(actionContext, [], result, new object());
        bool nextCalled = false;
        await new ConditionalGetAttribute().OnResultExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResultExecutedContext(actionContext, [], result, new object()));
        });
        return (http, nextCalled);
    }

    private static string Body(HttpContext http)
    {
        http.Response.Body.Position = 0;
        return Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
    }

    [Fact]
    public async Task A_200_get_gets_a_weak_etag_private_no_cache_and_the_serialized_body()
    {
        (HttpContext http, bool next) = await RunAsync("GET", new OkObjectResult(new { name = "a" }));

        next.Should().BeFalse("the body is written once by the filter, not serialized again");
        http.Response.StatusCode.Should().Be(200);
        http.Response.Headers.ETag.ToString().Should().MatchRegex("^W/\"[A-Za-z0-9_-]+\"$");
        http.Response.Headers.CacheControl.ToString().Should().Be("private, no-cache");
        http.Response.Headers.Vary.ToString().Should().Contain("Cookie").And.Contain("Authorization");
        Body(http).Should().Be("{\"name\":\"a\"}");
    }

    [Fact]
    public async Task An_action_that_chose_its_own_cache_window_keeps_it_and_vary_is_appended()
    {
        (HttpContext http, _) = await RunAsync("GET", new OkObjectResult(new { name = "a" }), prepare: h =>
        {
            h.Response.Headers.CacheControl = "private, max-age=2";
            h.Response.Headers.Vary = "Origin";
        });

        http.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=2");
        http.Response.Headers.Vary.ToString().Should().Contain("Origin").And.Contain("Cookie").And.Contain("Authorization");
        http.Response.Headers.ETag.ToString().Should().StartWith("W/\"");
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("strong")]
    [InlineData("list")]
    [InlineData("star")]
    public async Task A_matching_if_none_match_gets_304_with_the_etag_and_no_body(string form)
    {
        (HttpContext first, _) = await RunAsync("GET", new OkObjectResult(new { name = "a" }));
        string etag = first.Response.Headers.ETag.ToString();
        string header = form switch
        {
            "exact" => etag,
            "strong" => etag[2..],
            "list" => "\"other\", " + etag,
            _ => "*",
        };

        (HttpContext http, _) = await RunAsync("GET", new OkObjectResult(new { name = "a" }), header);

        http.Response.StatusCode.Should().Be(304);
        http.Response.Headers.ETag.ToString().Should().Be(etag);
        Body(http).Should().BeEmpty();
    }

    [Fact]
    public async Task A_different_body_has_a_different_etag_and_a_stale_tag_gets_the_full_body()
    {
        (HttpContext a, _) = await RunAsync("GET", new OkObjectResult(new { name = "a" }));
        (HttpContext b, _) = await RunAsync("GET", new OkObjectResult(new { name = "b" }), a.Response.Headers.ETag.ToString());

        b.Response.StatusCode.Should().Be(200);
        b.Response.Headers.ETag.ToString().Should().NotBe(a.Response.Headers.ETag.ToString());
        Body(b).Should().Be("{\"name\":\"b\"}");
    }

    [Fact]
    public async Task Non_get_and_non_200_results_pass_through_untouched()
    {
        (HttpContext post, bool postNext) = await RunAsync("POST", new OkObjectResult(new { name = "a" }));
        (HttpContext missing, bool missingNext) = await RunAsync("GET", new NotFoundObjectResult(new { name = "a" }));
        (HttpContext file, bool fileNext) = await RunAsync("GET", new NoContentResult());

        foreach ((HttpContext http, bool next) in new[] { (post, postNext), (missing, missingNext), (file, fileNext) })
        {
            next.Should().BeTrue();
            http.Response.Headers.ETag.ToString().Should().BeEmpty();
        }
    }

    // --- summary mapping --------------------------------------------------------------------------

    private static WorkflowStepResult HeavyResult(string? outputJson) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowStepId = Guid.NewGuid(),
        Output = "big output",
        InputJson = "{\"in\":1}",
        OutputJson = outputJson,
        ToolCallsJson = "[1]",
        ReasoningJson = "[2]",
        ChatTranscriptJson = "[3]",
        TokensUsed = 7,
        Status = StepStatus.Completed,
    };

    [Fact]
    public void Default_mapping_is_unchanged_and_summary_omits_the_heavy_fields()
    {
        WorkflowStepResult r = HeavyResult("{\"x\":1}");

        StepResultResponse full = WorkflowsController.MapStepResult(r);
        StepResultResponse summary = WorkflowsController.MapStepResult(r, summary: true);

        full.Output.Should().Be("big output");
        full.InputJson.Should().Be("{\"in\":1}");
        full.OutputJson.Should().Be("{\"x\":1}");
        full.ToolCallsJson.Should().Be("[1]");
        full.DetailOmitted.Should().BeFalse();
        summary.Output.Should().BeEmpty();
        summary.InputJson.Should().BeNull();
        summary.OutputJson.Should().BeNull();
        summary.ToolCallsJson.Should().BeNull();
        summary.ReasoningJson.Should().BeNull();
        summary.ChatTranscriptJson.Should().BeNull();
        summary.DetailOmitted.Should().BeTrue();
        summary.TokensUsed.Should().Be(7);
        summary.Status.Should().Be("Completed");
    }

    [Fact]
    public void Summary_keeps_only_the_warnings_of_an_output_json()
    {
        WorkflowStepResult r = HeavyResult("{\"big\":\"zzz\",\"warnings\":[\"w1\",\"w2\"]}");

        WorkflowsController.MapStepResult(r, summary: true).OutputJson.Should().Be("{\"warnings\":[\"w1\",\"w2\"]}");
        WorkflowsController.SummarizeOutputJson("not json").Should().BeNull();
        WorkflowsController.SummarizeOutputJson("[1]").Should().BeNull();
    }

    // --- step-results endpoint --------------------------------------------------------------------

    [Fact]
    public async Task GetStepResult_returns_the_full_result_and_404s_across_tenants_and_mismatched_runs()
    {
        Project mine = NewProject(Org);
        Project theirs = NewProject(OtherOrg);
        WorkflowExecution myRun = NewRun(mine);
        WorkflowExecution myOtherRun = NewRun(mine);
        WorkflowExecution theirRun = NewRun(theirs);
        WorkflowStepResult myResult = HeavyResult("{\"x\":1}");
        myResult.WorkflowExecutionId = myRun.Id;
        WorkflowStepResult theirResult = HeavyResult("{\"x\":1}");
        theirResult.WorkflowExecutionId = theirRun.Id;
        _db.Projects.AddRange(mine, theirs);
        _db.WorkflowExecutions.AddRange(myRun, myOtherRun, theirRun);
        _db.WorkflowStepResults.AddRange(myResult, theirResult);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        ActionResult<StepResultResponse> ok = await Controller().GetStepResult(mine.Id, myRun.Id, myResult.Id, CancellationToken.None);
        StepResultResponse body = ok.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<StepResultResponse>().Subject;
        body.Output.Should().Be("big output");
        body.DetailOmitted.Should().BeFalse();

        (await Controller().GetStepResult(theirs.Id, theirRun.Id, theirResult.Id, CancellationToken.None))
            .Result.Should().NotBeOfType<OkObjectResult>("another tenant's project is not readable");
        (await Controller().GetStepResult(mine.Id, theirRun.Id, theirResult.Id, CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>("a run of another project is not reachable through mine");
        (await Controller().GetStepResult(mine.Id, myOtherRun.Id, myResult.Id, CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>("the result belongs to a different run");
    }

    private Project NewProject(Guid organizationId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "p",
        OwnerId = _userId,
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static WorkflowExecution NewRun(Project project) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Guid.NewGuid(),
        ProjectId = project.Id,
        OrganizationId = project.OrganizationId,
        Status = ExecutionStatus.Passed
    };

    private sealed class FakeCurrentUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
    }

    private WorkflowsController Controller()
    {
        var user = new FakeCurrentUser(_userId, Org);
        return new WorkflowsController(
            _db,
            user,
            new ReelBolt.Inference.Api.Services.Auth.ProjectAccessService(_db, user),
            Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
    }
}
