using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Feedback;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// T-019: the in-product bug / feedback channel. The tests that carry the ticket are
/// <see cref="Submit_stores_exactly_the_context_that_was_attached"/> (minimisation, asserted at the
/// stored payload) and the two 404s (another user's project or execution cannot be attached — 404,
/// never 403).
/// </summary>
public class FeedbackControllerTests
{
    private static InferenceApiDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    private sealed class FakeCurrentUser(Guid userId, bool isAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = isAdmin;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static FeedbackController CreateController(InferenceApiDbContext db, Guid userId, bool isAdmin = false)
    {
        var user = new FakeCurrentUser(userId, isAdmin);
        return new FeedbackController(db, TestTenancy.Access(db, user), user);
    }

    private static Project AddProject(InferenceApiDbContext db, Guid ownerId, Guid? organizationId = null)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = ownerId,
            OrganizationId = organizationId ?? TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static WorkflowExecution AddExecution(InferenceApiDbContext db, Project project)
    {
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        db.WorkflowExecutions.Add(execution);
        db.SaveChanges();
        return execution;
    }

    private static JsonElement ContextOf(InferenceApiDbContext db, Guid reportId) =>
        JsonDocument.Parse(db.FeedbackReports.Single(r => r.Id == reportId).ContextJson).RootElement;

    private static FeedbackRequest WithDiagnostics(string json) => new("bug", "Broken.",
        new FeedbackContextDto("/x", null, null, null, JsonDocument.Parse(json).RootElement.Clone()));

    [Fact]
    public async Task Submit_stores_valid_diagnostics_under_the_diagnostics_key()
    {
        using InferenceApiDbContext db = CreateContext();
        IActionResult result = await CreateController(db, Guid.NewGuid())
            .Submit(WithDiagnostics("{\"viewport\":{\"w\":390},\"keys\":[\"a\",\"b\"]}"), CancellationToken.None);

        Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
        FeedbackReport row = Assert.Single(db.FeedbackReports);
        Assert.Equal("{\"page\":\"/x\",\"diagnostics\":{\"viewport\":{\"w\":390},\"keys\":[\"a\",\"b\"]}}", row.ContextJson);
    }

    [Fact]
    public async Task Submit_without_diagnostics_leaves_context_json_unchanged()
    {
        using InferenceApiDbContext db = CreateContext();
        var request = new FeedbackRequest("bug", "Broken.", new FeedbackContextDto("/x", null, null, null));
        await CreateController(db, Guid.NewGuid()).Submit(request, CancellationToken.None);
        Assert.Equal("{\"page\":\"/x\"}", Assert.Single(db.FeedbackReports).ContextJson);

        using InferenceApiDbContext db2 = CreateContext();
        await CreateController(db2, Guid.NewGuid()).Submit(WithDiagnostics("null"), CancellationToken.None);
        Assert.Equal("{\"page\":\"/x\"}", Assert.Single(db2.FeedbackReports).ContextJson);
    }

    [Theory]
    [InlineData("[1]", "not_object")]
    [InlineData("\"text\"", "not_object")]
    [InlineData("5", "not_object")]
    [InlineData("{\"a\":1,\"a\":2}", "invalid")]
    [InlineData("{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":1}}}}}}}}}", "too_deep")]
    public async Task Submit_drops_bad_diagnostics_but_still_stores_the_report(string json, string reason)
    {
        using InferenceApiDbContext db = CreateContext();
        IActionResult result = await CreateController(db, Guid.NewGuid()).Submit(WithDiagnostics(json), CancellationToken.None);
        Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
        FeedbackReport row = Assert.Single(db.FeedbackReports);
        Assert.Equal("{\"page\":\"/x\",\"diagnosticsDropped\":\"" + reason + "\"}", row.ContextJson);
    }

    [Fact]
    public async Task Submit_accepts_diagnostics_at_depth_eight_and_drops_over_the_byte_limit()
    {
        using InferenceApiDbContext db = CreateContext();
        string eight = "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":1}}}}}}}}";
        await CreateController(db, Guid.NewGuid()).Submit(WithDiagnostics(eight), CancellationToken.None);

        string big = "{\"k\":\"" + new string('x', FeedbackService.MaxDiagnosticsBytes) + "\"}";
        IActionResult tooBig = await CreateController(db, Guid.NewGuid()).Submit(WithDiagnostics(big), CancellationToken.None);
        Assert.Equal(201, Assert.IsType<ObjectResult>(tooBig).StatusCode);

        var rows = db.FeedbackReports.OrderBy(r => r.CreatedAt).Select(r => r.ContextJson).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("{\"page\":\"/x\",\"diagnostics\":" + eight + "}", rows[0]);
        Assert.Equal("{\"page\":\"/x\",\"diagnosticsDropped\":\"too_large\"}", rows[1]);
    }

    [Fact]
    public async Task Submit_stores_the_report_with_the_attached_context()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project project = AddProject(db, callerId);
        WorkflowExecution execution = AddExecution(db, project);

        var request = new FeedbackRequest("bug", "  The render button does nothing.  ", new FeedbackContextDto(
            "/projects/" + project.Id,
            project.Id,
            execution.Id,
            new[]
            {
                new FeedbackTurnDto("user", "render my video"),
                new FeedbackTurnDto("assistant", "starting the render")
            }));

        IActionResult result = await CreateController(db, callerId).Submit(request, CancellationToken.None);

        ObjectResult created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        var body = Assert.IsType<FeedbackCreatedResponse>(created.Value);

        FeedbackReport row = Assert.Single(db.FeedbackReports);
        Assert.Equal(body.Id, row.Id);
        Assert.Equal(callerId, row.UserId);
        Assert.Equal(FeedbackReport.BugKind, row.Kind);
        Assert.Equal("The render button does nothing.", row.Message, ignoreCase: false);

        JsonElement context = ContextOf(db, row.Id);
        Assert.Equal("/projects/" + project.Id, context.GetProperty("page").GetString());
        Assert.Equal(project.Id.ToString(), context.GetProperty("projectId").GetString());
        Assert.Equal(execution.Id.ToString(), context.GetProperty("executionId").GetString());
        Assert.Equal(2, context.GetProperty("assistantTurns").GetArrayLength());
        Assert.Equal("user", context.GetProperty("assistantTurns")[0].GetProperty("role").GetString());
    }

    /// <summary>
    /// The minimisation promise, asserted at the payload the server stores: context the user did not
    /// attach is not collected. The request carries only a page, and the stored row must carry only a
    /// page — no project id invented from the route, no execution id, no assistant turns, and nothing
    /// about the caller that the client never sent.
    /// </summary>
    [Fact]
    public async Task Submit_stores_exactly_the_context_that_was_attached()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();

        var request = new FeedbackRequest("feedback", "Love the new editor.", new FeedbackContextDto(
            "/dashboard", null, null, null));

        IActionResult result = await CreateController(db, callerId).Submit(request, CancellationToken.None);

        Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);

        JsonElement context = ContextOf(db, db.FeedbackReports.Single().Id);
        List<string> keys = context.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "page" }, keys);
    }

    [Fact]
    public async Task Submit_with_no_context_at_all_stores_an_empty_object()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();

        IActionResult result = await CreateController(db, callerId)
            .Submit(new FeedbackRequest("feedback", "Just a note.", null), CancellationToken.None);

        Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("{}", db.FeedbackReports.Single().ContextJson);
    }

    [Fact]
    public async Task Submit_with_another_users_project_id_is_not_found()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Project foreign = AddProject(db, otherUserId); // in another organization

        var request = new FeedbackRequest("bug", "Broken.", new FeedbackContextDto(null, foreign.Id, null, null));

        IActionResult result = await CreateController(db, callerId).Submit(request, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(db.FeedbackReports);
    }

    [Fact]
    public async Task Submit_with_an_execution_the_caller_cannot_see_is_not_found()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        WorkflowExecution foreignExecution = AddExecution(db, AddProject(db, otherUserId));

        var request = new FeedbackRequest("bug", "Broken.", new FeedbackContextDto(null, null, foreignExecution.Id, null));

        IActionResult result = await CreateController(db, callerId).Submit(request, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(db.FeedbackReports);
    }

    [Fact]
    public async Task Submit_with_an_execution_that_does_not_match_the_attached_project_is_not_found()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project mine = AddProject(db, callerId);
        Project other = AddProject(db, callerId);
        WorkflowExecution executionOfOther = AddExecution(db, other);

        var request = new FeedbackRequest(
            "bug", "Broken.", new FeedbackContextDto(null, mine.Id, executionOfOther.Id, null));

        IActionResult result = await CreateController(db, callerId).Submit(request, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(db.FeedbackReports);
    }

    [Fact]
    public async Task Submit_with_an_unknown_execution_id_is_not_found()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();

        var request = new FeedbackRequest("bug", "Broken.", new FeedbackContextDto(null, null, Guid.NewGuid(), null));

        IActionResult result = await CreateController(db, callerId).Submit(request, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(db.FeedbackReports);
    }

    [Fact]
    public async Task Submit_is_rate_limited_per_user_per_window()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < FeedbackService.MaxPerWindow; i++)
        {
            db.FeedbackReports.Add(new FeedbackReport
            {
                Id = Guid.NewGuid(),
                UserId = callerId,
                OrganizationId = TestTenancy.OrgOf(callerId),
                Kind = FeedbackReport.FeedbackKind,
                Message = $"earlier {i}",
                ContextJson = "{}",
                CreatedAt = now.AddMinutes(-i)
            });
        }
        db.SaveChanges();

        IActionResult result = await CreateController(db, callerId)
            .Submit(new FeedbackRequest("feedback", "one more", null), CancellationToken.None);

        ObjectResult refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, refused.StatusCode);
        Assert.Equal(FeedbackService.MaxPerWindow, db.FeedbackReports.Count(r => r.UserId == callerId));
    }

    [Fact]
    public async Task A_row_outside_the_window_does_not_count_against_the_limit()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        for (int i = 0; i < FeedbackService.MaxPerWindow; i++)
        {
            db.FeedbackReports.Add(new FeedbackReport
            {
                Id = Guid.NewGuid(),
                UserId = callerId,
                OrganizationId = TestTenancy.OrgOf(callerId),
                Kind = FeedbackReport.FeedbackKind,
                Message = "old",
                ContextJson = "{}",
                CreatedAt = DateTime.UtcNow - FeedbackService.Window - TimeSpan.FromMinutes(5)
            });
        }
        db.SaveChanges();

        IActionResult result = await CreateController(db, callerId)
            .Submit(new FeedbackRequest("feedback", "fresh", null), CancellationToken.None);

        Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Submit_rejects_a_kind_that_is_not_bug_or_feedback(string? kind)
    {
        using InferenceApiDbContext db = CreateContext();

        IActionResult result = await CreateController(db, Guid.NewGuid())
            .Submit(new FeedbackRequest(kind, "hello", null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.FeedbackReports);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Submit_rejects_an_empty_message(string? message)
    {
        using InferenceApiDbContext db = CreateContext();

        IActionResult result = await CreateController(db, Guid.NewGuid())
            .Submit(new FeedbackRequest("bug", message, null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.FeedbackReports);
    }

    [Fact]
    public async Task Operators_read_the_sink_but_other_users_cannot()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        await CreateController(db, callerId).Submit(
            new FeedbackRequest("bug", "the sink has a row", null), CancellationToken.None);

        IActionResult forbidden = await CreateController(db, callerId).ListForOperators(null, CancellationToken.None);
        Assert.IsType<ForbidResult>(forbidden);

        IActionResult listed = await CreateController(db, Guid.NewGuid(), isAdmin: true)
            .ListForOperators(null, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(listed);
        var reports = Assert.IsAssignableFrom<IReadOnlyList<FeedbackReportDto>>(ok.Value);
        Assert.Contains(reports, r => r.Message == "the sink has a row");
    }
}
