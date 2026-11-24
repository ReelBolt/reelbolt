#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Middleware;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Observability;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ReviewLinks;

/// <summary>Notification coalescing, cascades, member-side regressions, telemetry redaction and the no-token-in-logs pipeline test.</summary>
public class ReviewLinkLifecycleTests
{
    // ─── notifier ─────────────────────────────────────────────────────────────────

    private static ReviewLinkOptions Fast => new() { GuestCommentsPerMinute = 1000 };

    private static async Task Comment(ReviewWorld w, string token, string name, string body, Mock<IUserActivityNotifier> notifier)
    {
        IActionResult r = await w.Public(null, Fast, notifier: notifier).Controller.AddComment(
            token, new PublicCreateReviewCommentRequest(name, body, 5, null, null), CancellationToken.None);
        r.Should_BeStatus(201);
    }

    private static List<string> Details(Mock<IUserActivityNotifier> n) =>
        n.Invocations.Where(i => i.Method.Name == nameof(IUserActivityNotifier.GuestReviewActivityAsync))
            .Select(i => (string)i.Arguments[4]).ToList();

    [Fact]
    public async Task Guest_notes_notify_the_link_creator_at_most_once_per_ten_minutes_with_a_counted_line()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync(label: "Boss");
        Mock<IUserActivityNotifier> notifier = new();

        await Comment(w, token, "Ana", "SECRET BODY TEXT one", notifier);
        Details(notifier).Should().Equal("1 new note from Ana (guest)");
        notifier.Verify(n => n.GuestReviewActivityAsync(w.OrgId, w.Project.Id, w.UserId, "Client cut (Boss)", "1 new note from Ana (guest)", It.IsAny<CancellationToken>()), Times.Once);

        w.Clock.Now = w.Clock.Now.AddMinutes(1);
        await Comment(w, token, "Ben", "two", notifier);
        w.Clock.Now = w.Clock.Now.AddMinutes(1);
        await Comment(w, token, "Cy", "three", notifier);
        Details(notifier).Should().HaveCount(1, "inside the ten minute window nothing more is sent");

        w.Clock.Now = w.Clock.Now.AddMinutes(10);
        await Comment(w, token, "Dan", "four", notifier);
        Details(notifier).Should().Equal("1 new note from Ana (guest)", "3 new notes, latest from Dan (guest)");
        Details(notifier).Should().NotContain(d => d.Contains("BODY") || d.Contains("three"), "the line carries names and counts, never note text");
        (await w.Seed.EditReviewLinks.AsNoTracking().SingleAsync()).LastNotifiedAt.Should().Be(w.Clock.Now.UtcDateTime);
    }

    [Fact]
    public async Task A_decision_always_notifies_but_not_twice_inside_a_minute()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        Mock<IUserActivityNotifier> notifier = new();

        await Comment(w, token, "Ana", "hello", notifier);
        w.Clock.Now = w.Clock.Now.AddSeconds(30);
        (await w.Public(null, Fast, notifier: notifier).Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None)).Should_BeStatus(201);
        Details(notifier).Should().HaveCount(1, "the one-minute floor");

        w.Clock.Now = w.Clock.Now.AddSeconds(40);
        (await w.Public(null, Fast, notifier: notifier).Controller.Decide(token, new PublicReviewDecisionRequest("changesRequested", "Ana", "fix"), CancellationToken.None)).Should_BeStatus(201);
        w.Clock.Now = w.Clock.Now.AddMinutes(2);
        (await w.Public(null, Fast, notifier: notifier).Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None)).Should_BeStatus(201);

        Details(notifier).Should().Equal("1 new note from Ana (guest)", "Ana (guest) asked for changes", "Ana (guest) approved");
    }

    [Fact]
    public async Task A_failing_notifier_never_fails_the_guest_request()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        Mock<IUserActivityNotifier> notifier = new();
        notifier.Setup(n => n.GuestReviewActivityAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));

        await Comment(w, token, "Ana", "hello", notifier);
        (await w.Public(null, Fast, notifier: notifier).Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None)).Should_BeStatus(201);
        (await w.Seed.EditTimelineComments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_notifier_publishes_a_review_activity_event_for_the_creator_only_and_swallows_bus_failures()
    {
        Mock<IPublishEndpoint> publish = new();
        UserActivityNotificationRequested? sent = null;
        publish.Setup(p => p.Publish(It.IsAny<UserActivityNotificationRequested>(), It.IsAny<CancellationToken>()))
            .Callback<UserActivityNotificationRequested, CancellationToken>((m, _) => sent = m)
            .Returns(Task.CompletedTask);
        using ReviewWorld w = new();
        UserActivityNotifier notifier = new(publish.Object, w.Seed, NullLogger<UserActivityNotifier>.Instance);
        Guid org = Guid.NewGuid(), project = Guid.NewGuid(), creator = Guid.NewGuid();

        await notifier.GuestReviewActivityAsync(org, project, creator, "Client cut", "3 new notes, latest from Ana (guest)");

        sent.Should().NotBeNull();
        sent!.Type.Should().Be("review_activity").And.Be(UserActivityNotificationRequested.ReviewActivity);
        sent.RecipientUserId.Should().Be(creator);
        sent.ActorUserId.Should().BeNull();
        sent.OrganizationId.Should().Be(org);
        sent.ProjectId.Should().Be(project);
        sent.Subject.Should().Be("Client cut");
        sent.Details.Should().Be("3 new notes, latest from Ana (guest)");

        publish.Setup(p => p.Publish(It.IsAny<UserActivityNotificationRequested>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("down"));
        Func<Task> again = () => notifier.GuestReviewActivityAsync(org, project, creator, "T", "d");
        await again.Should().NotThrowAsync();
    }

    [Fact]
    public void The_event_contract_is_additive_review_activity_and_recipient_are_the_only_new_members()
    {
        UserActivityNotificationRequested.FileCreated.Should().Be("file_created");
        UserActivityNotificationRequested.GraphicsEdited.Should().Be("graphics_edited");
        UserActivityNotificationRequested.ReviewActivity.Should().Be("review_activity");
        typeof(UserActivityNotificationRequested).GetProperty(nameof(UserActivityNotificationRequested.RecipientUserId))!.PropertyType.Should().Be(typeof(Guid?));
        new UserActivityNotificationRequested().RecipientUserId.Should().BeNull("every existing producer leaves it unset");
    }

    // ─── cascades ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Trash_purge_removes_the_links_decisions_and_guest_comments_of_a_purged_timeline_only()
    {
        using ReviewWorld w = new();
        (Guid doomedLink, string t1) = await w.CreateLinkAsync();
        await Comment(w, t1, "Ana", "x", new Mock<IUserActivityNotifier>());
        await w.Public(null, Fast).Controller.Decide(t1, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None);

        Guid keepTimeline = Guid.NewGuid();
        w.Seed.EditTimelines.Add(new EditTimelineEntity { Id = keepTimeline, ProjectId = w.Project.Id, Name = "Keep", DocumentJson = "{}" });
        Guid keepLink = Guid.NewGuid();
        w.Seed.EditReviewLinks.Add(new EditReviewLink { Id = keepLink, ProjectId = w.Project.Id, OrganizationId = w.OrgId, TimelineId = keepTimeline, CreatedByUserId = w.UserId, TokenHash = ReviewLinkTokens.Hash(ReviewLinkTokens.Generate()), RenderExecutionId = Guid.NewGuid(), ExpiresAt = DateTime.UtcNow.AddDays(1) });
        w.Seed.EditReviewDecisions.Add(new EditReviewDecision { Id = Guid.NewGuid(), LinkId = keepLink, ProjectId = w.Project.Id, TimelineId = keepTimeline, Decision = EditReviewDecisionKind.Approved, GuestName = "K" });
        w.Seed.EditTimelines.Single(t => t.Id == w.Timeline.Id).DeletedAt = ReviewWorld.Now.UtcDateTime.AddDays(-60);
        w.Seed.SaveChanges();

        int purged = await TimelineTrashPurgeService.PurgeOnceAsync(w.Seed, ReviewWorld.Now.UtcDateTime, TimeSpan.FromDays(30), CancellationToken.None);

        purged.Should().Be(1);
        (await w.Seed.EditReviewLinks.IgnoreQueryFilters().Select(l => l.Id).ToListAsync()).Should().Equal(keepLink);
        (await w.Seed.EditReviewDecisions.Select(d => d.LinkId).ToListAsync()).Should().Equal(keepLink);
        (await w.Seed.EditTimelineComments.IgnoreQueryFilters().CountAsync(c => c.GuestLinkId == doomedLink)).Should().Be(0);
    }

    [Fact]
    public void Every_table_of_a_link_cascades_from_the_timeline_and_the_project_in_the_model()
    {
        using ReviewWorld w = new();
        var links = w.Seed.Model.FindEntityType(typeof(EditReviewLink))!;
        var fks = links.GetForeignKeys().ToList();
        fks.Should().Contain(f => f.PrincipalEntityType.ClrType == typeof(EditTimelineEntity) && f.DeleteBehavior == DeleteBehavior.Cascade);
        fks.Should().Contain(f => f.PrincipalEntityType.ClrType == typeof(Project) && f.DeleteBehavior == DeleteBehavior.Cascade);
        w.Seed.Model.FindEntityType(typeof(EditReviewDecision))!.GetForeignKeys()
            .Should().Contain(f => f.PrincipalEntityType.ClrType == typeof(EditReviewLink) && f.DeleteBehavior == DeleteBehavior.Cascade);
        links.FindProperty(nameof(EditReviewLink.TokenHash))!.ClrType.Should().Be(typeof(byte[]));
        links.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Single().Name == nameof(EditReviewLink.TokenHash));
        links.GetTableName().Should().Be("edit_review_links");
        w.Seed.Model.FindEntityType(typeof(EditReviewDecision))!.GetTableName().Should().Be("edit_review_decisions");
    }

    // ─── member side regressions ──────────────────────────────────────────────────

    private static EditTimelinesController MemberController(ReviewWorld w, Guid userId, OrganizationRole role = OrganizationRole.Member)
    {
        InferenceApiDbContext db = w.MemberDb();
        ICurrentUser user = ReviewWorld.User(userId, w.OrgId, role);
        return new EditTimelinesController(db, user, new ReelBolt.Inference.Api.Services.Auth.ProjectAccessService(db, user),
            new Mock<ReelBolt.Inference.Api.Services.Storage.IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object, clock: w.Clock);
    }

    [Fact]
    public async Task Members_see_guest_comments_resolve_and_delete_them_but_nobody_edits_the_text()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        PublicReviewCommentResponse guest = (PublicReviewCommentResponse)(await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", "from a guest", 20, null, null), CancellationToken.None)).Should_BeStatus(201).Value!;
        EditTimelineController_AddMember(w);

        EditTimelineCommentResponse[] list = ((IEnumerable<EditTimelineCommentResponse>)((OkObjectResult)(await MemberController(w, w.UserId).ListComments(w.Project.Id, w.Timeline.Id, CancellationToken.None)).Result!).Value!).ToArray();
        list.Should().HaveCount(2);
        list.Single(c => c.Id == guest.Id).Should().Match<EditTimelineCommentResponse>(c => c.Guest && c.GuestName == "Ana" && c.AuthorUserId == Guid.Empty && c.Body == "from a guest");
        list.Single(c => c.Id != guest.Id).Should().Match<EditTimelineCommentResponse>(c => !c.Guest && c.GuestName == null, "a member comment is unchanged");

        // Author-edit by anyone fails, including the link's own creator (Owner role).
        (await MemberController(w, w.UserId, OrganizationRole.Owner).UpdateComment(w.Project.Id, w.Timeline.Id, guest.Id, new UpdateEditTimelineCommentRequest("rewritten", null), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        // An Edit member resolves it, then deletes it.
        EditTimelineCommentResponse resolved = (EditTimelineCommentResponse)((OkObjectResult)(await MemberController(w, w.UserId).UpdateComment(w.Project.Id, w.Timeline.Id, guest.Id, new UpdateEditTimelineCommentRequest(null, true), CancellationToken.None)).Result!).Value!;
        resolved.Resolved.Should().BeTrue();
        resolved.Guest.Should().BeTrue();
        (await MemberController(w, w.UserId).DeleteComment(w.Project.Id, w.Timeline.Id, guest.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        PublicReviewResponse page = (PublicReviewResponse)((OkObjectResult)await w.Public().Controller.Get(token, CancellationToken.None)).Value!;
        page.Comments.Should().NotContain(c => c.Id == guest.Id, "a deleted guest note disappears from the guest page too");
    }

    private static void EditTimelineController_AddMember(ReviewWorld w)
    {
        w.Seed.EditTimelineComments.Add(new EditTimelineComment
        {
            Id = Guid.NewGuid(),
            ProjectId = w.Project.Id,
            TimelineId = w.Timeline.Id,
            AuthorUserId = w.UserId,
            AuthorName = "owner@example.com",
            Frame = 3,
            Body = "member note",
            CreatedAt = ReviewWorld.Now.UtcDateTime,
        });
        w.Seed.SaveChanges();
    }

    // ─── no token in logs / telemetry ─────────────────────────────────────────────

    [Fact]
    public void Telemetry_redacts_a_review_link_path_and_keeps_the_suffix()
    {
        string token = ReviewLinkTokens.Generate();
        SensitiveUrlRedactionProcessor processor = new();
        using Activity span = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").SetTag("url.path", $"/api/v1/review-links/{token}/media");
        span.SetTag("http.url", $"https://app.example.test/api/v1/review-links/{token}/comments?x=1");
        span.DisplayName = $"GET /api/v1/review-links/{token}";
        using Activity plain = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").SetTag("url.path", $"/api/v1/review-links/{token}");

        processor.OnEnd(span);
        processor.OnEnd(plain);

        span.TagObjects.Select(t => $"{t.Value}").Append(span.DisplayName).Should().NotContain(s => s.Contains(token));
        span.GetTagItem("url.path").Should().Be("/api/v1/review-links/{token}/media");
        plain.GetTagItem("url.path").Should().Be(SensitiveUrlRedactionProcessor.ReviewLinkPathRedacted);
    }

    [Fact]
    public async Task The_activity_middleware_ignores_the_anonymous_review_route()
    {
        Mock<IUserActivityNotifier> notifier = new();
        ServiceCollection services = new();
        services.AddSingleton(notifier.Object);
        DefaultHttpContext http = new() { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = "POST";
        http.Request.Path = $"/api/v1/review-links/{ReviewLinkTokens.Generate()}/comments";
        http.Response.StatusCode = 201;

        await new UserActivityNotificationMiddleware(_ => Task.CompletedTask, NullLogger<UserActivityNotificationMiddleware>.Instance).InvokeAsync(http);

        notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public void The_shipped_log_levels_keep_the_aspnetcore_route_logging_off()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReelBolt.Inference.Api"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        string json = File.ReadAllText(Path.Combine(dir!, "src", "ReelBolt.Inference.Api", "appsettings.json"));
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        string level = doc.RootElement.GetProperty("Logging").GetProperty("LogLevel").GetProperty("Microsoft.AspNetCore").GetString()!;
        level.Should().BeOneOf("Warning", "Error", "Critical", "None",
            "Information and below log route values (the token) from the hosting, routing and MVC categories");

        foreach (string file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            File.ReadAllText(Path.Combine(dir!, "src", "ReelBolt.Inference.Api", file)).Should().NotContainEquivalentOf("IncludeScopes",
                "the ASP.NET Core hosting scope carries the raw request path, and so the token; no log provider may record scopes");
        }

        JsonElement reviewLinks = doc.RootElement.GetProperty("ReviewLinks");
        reviewLinks.GetProperty("DefaultExpiryDays").GetInt32().Should().Be(14);
        reviewLinks.GetProperty("MaxExpiryDays").GetInt32().Should().Be(90);
        reviewLinks.GetProperty("MaxLinksPerTimeline").GetInt32().Should().Be(20);
        reviewLinks.GetProperty("MaxGuestCommentsPerLink").GetInt32().Should().Be(200);
        reviewLinks.GetProperty("GuestCommentsPerMinute").GetInt32().Should().Be(10);
    }

    private sealed class SingleTypePart(params Type[] types) : ApplicationPart, IApplicationPartTypeProvider
    {
        public override string Name => "review-links-test";
        public IEnumerable<TypeInfo> Types => types.Select(t => t.GetTypeInfo());
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
        public void Dispose() { }

        private sealed class Capture(CapturingProvider owner, string category) : ILogger
        {
            // Like the shipped providers (the console logger's IncludeScopes is off), scopes are not recorded:
            // ASP.NET Core's hosting scope carries the raw RequestPath, so a provider that records scopes WOULD
            // capture a token. The configuration guard test below pins that nothing in the app turns that on.
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue($"[{category}] {logLevel}: {formatter(state, exception)} {exception}");
        }
    }

    [Fact]
    public async Task The_whole_pipeline_serves_the_route_with_safe_headers_and_never_logs_the_token()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        string unknown = ReviewLinkTokens.Generate();

        CapturingProvider logs = new();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        // The shipped configuration: everything Information, the ASP.NET Core categories at Warning.
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        TestTenancy.MutableTenant anonymous = new();
        anonymous.ActAsOrganization(null);
        builder.Services.AddSingleton<ITenantContext>(anonymous);
        builder.Services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(w.DbNameForTests));
        builder.Services.AddSingleton<TimeProvider>(w.Clock);
        builder.Services.AddSingleton(w.S3.Object);
        builder.Services.AddSingleton(new Mock<IUserActivityNotifier>().Object);
        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = ReviewWorld.Bucket }).Build());
        builder.Services.AddSingleton<ReviewLinkWriteLimiter>();
        builder.Services.AddScoped<ReviewRenderResolver>();
        builder.Services.AddScoped<ReviewLinkResolver>();
        builder.Services.AddScoped<ReviewLinkActivityNotifier>();
        builder.Services.AddOptions<ReviewLinkOptions>();
        builder.Services.AddControllers().ConfigureApplicationPartManager(pm =>
        {
            pm.ApplicationParts.Clear();
            pm.ApplicationParts.Add(new SingleTypePart(typeof(ReviewLinksPublicController)));
        });
        await using WebApplication app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        string address = app.Urls.First();
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });

        // Unknown token: the uniform 404 with the safe headers.
        using HttpResponseMessage missing = await client.GetAsync($"{address}/api/v1/review-links/{unknown}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsStringAsync()).Should().Be("{}");
        missing.Headers.CacheControl!.ToString().Should().Contain("no-store").And.Contain("private");
        missing.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        missing.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        missing.Headers.Select(h => h.Key).Should().NotContain(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));

        // Malformed token: also the uniform 404 (and never reaches the database).
        using HttpResponseMessage malformed = await client.GetAsync($"{address}/api/v1/review-links/not-a-token");
        malformed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await malformed.Content.ReadAsStringAsync()).Should().Be("{}");

        // Live token: the page, a ranged media read, a comment and a decision.
        using HttpResponseMessage page = await client.GetAsync($"{address}/api/v1/review-links/{token}");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Headers.CacheControl!.ToString().Should().Contain("no-store");
        (await page.Content.ReadAsStringAsync()).Should().Contain("\"title\":\"Client cut\"").And.NotContain(token);

        using HttpRequestMessage ranged = new(HttpMethod.Get, $"{address}/api/v1/review-links/{token}/media");
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
        using HttpResponseMessage media = await client.SendAsync(ranged);
        media.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        media.Content.Headers.ContentType!.MediaType.Should().Be("video/mp4");
        (await media.Content.ReadAsByteArrayAsync()).Should().Equal(ReviewWorld.Bytes(1000).Take(100));

        using HttpResponseMessage comment = await client.PostAsJsonAsync($"{address}/api/v1/review-links/{token}/comments",
            new { name = "Ana", body = "Nice", frame = 12 });
        comment.StatusCode.Should().Be(HttpStatusCode.Created);
        using HttpResponseMessage decision = await client.PostAsJsonAsync($"{address}/api/v1/review-links/{token}/decision",
            new { decision = "approved", name = "Ana" });
        decision.StatusCode.Should().Be(HttpStatusCode.Created);

        // A non-JSON body is a 400, never a 500 and never an echo of the token.
        using StringContent junk = new("{not json", System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage bad = await client.PostAsync($"{address}/api/v1/review-links/{token}/comments", junk);
        ((int)bad.StatusCode).Should().Be(400);
        (await bad.Content.ReadAsStringAsync()).Should().NotContain(token);

        await app.StopAsync();

        logs.Lines.Should().NotContain(l => l.Contains(token) || l.Contains(unknown),
            "no log line, message or scope may carry a review token: " + string.Join(" | ", logs.Lines.Take(20)));
    }
}
