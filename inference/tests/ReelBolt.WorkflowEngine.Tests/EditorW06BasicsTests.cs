#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Editor v2 remediation W0.6 — the server half of the "missing basics": deleting a root review
/// comment keeps its thread (api D9), and the colour card, title and in/out range the web editor now
/// creates are documents the validator accepts (P5-GEN-03, P4-OPS-12).
/// </summary>
public class EditorW06BasicsTests
{
    private static InferenceApiDbContext CreateContext(string name) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(name).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static ICurrentUser Caller(Guid userId, Guid orgId, string email)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(userId);
        user.SetupGet(u => u.Email).Returns(email);
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrganizationId).Returns(orgId);
        user.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
        return user.Object;
    }

    private static EditTimelinesController Controller(InferenceApiDbContext db, ICurrentUser caller) =>
        new(db, caller, TestTenancy.Access(db, caller), new Mock<IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static IReadOnlyList<EditTimelineCommentResponse> List(ActionResult<IReadOnlyList<EditTimelineCommentResponse>> result) =>
        (IReadOnlyList<EditTimelineCommentResponse>)((OkObjectResult)result.Result!).Value!;

    private static EditTimelineCommentResponse Created(ActionResult<EditTimelineCommentResponse> result) =>
        (EditTimelineCommentResponse)((ObjectResult)result.Result!).Value!;

    [Fact]
    public async Task Deleting_a_root_comment_reparents_its_replies_onto_the_oldest_reply()
    {
        Guid owner = Guid.NewGuid();
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        EditTimelineEntity timeline = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "T", DocumentJson = "{}", Revision = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(timeline);
        db.SaveChanges();

        EditTimelinesController asAuthor = Controller(db, Caller(owner, project.OrganizationId, "author@example.com"));
        EditTimelinesController asColleague = Controller(db, Caller(Guid.NewGuid(), project.OrganizationId, "colleague@example.com"));

        EditTimelineCommentResponse root = Created(await asAuthor.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Cut this pause", 90, 15, null), CancellationToken.None));
        EditTimelineCommentResponse first = Created(await asColleague.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Agreed", 0, null, root.Id), CancellationToken.None));
        // CreatedAt orders the replies; make the second strictly later.
        await Task.Delay(5);
        EditTimelineCommentResponse second = Created(await asColleague.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("And trim the tail", 0, null, root.Id), CancellationToken.None));
        (await asAuthor.UpdateComment(project.Id, timeline.Id, root.Id, new UpdateEditTimelineCommentRequest(null, true), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();

        // A stranger still gets 404, never 403.
        (await Controller(db, Caller(Guid.NewGuid(), Guid.NewGuid(), "x@example.com")).DeleteComment(project.Id, timeline.Id, root.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();

        (await asAuthor.DeleteComment(project.Id, timeline.Id, root.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        IReadOnlyList<EditTimelineCommentResponse> list = List(await asAuthor.ListComments(project.Id, timeline.Id, CancellationToken.None));
        list.Select(c => c.Id).Should().BeEquivalentTo(new[] { first.Id, second.Id });
        EditTimelineCommentResponse heir = list.Single(c => c.Id == first.Id);
        heir.ParentId.Should().BeNull("the oldest reply becomes the thread's first comment");
        heir.Frame.Should().Be(90);
        heir.DurationFrames.Should().Be(15);
        heir.Resolved.Should().BeTrue("the thread keeps its resolved state");
        heir.Body.Should().Be("Agreed");
        list.Single(c => c.Id == second.Id).ParentId.Should().Be(first.Id);

        // Every visible comment belongs to a visible thread: nothing names the removed root.
        list.Should().OnlyContain(c => c.ParentId == null || list.Any(p => p.Id == c.ParentId));
    }

    [Fact]
    public async Task Deleting_a_root_whose_oldest_reply_was_already_removed_promotes_the_oldest_visible_reply()
    {
        Guid owner = Guid.NewGuid();
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        EditTimelineEntity timeline = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "T", DocumentJson = "{}", Revision = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(timeline);
        db.SaveChanges();
        EditTimelinesController asAuthor = Controller(db, Caller(owner, project.OrganizationId, "author@example.com"));

        EditTimelineCommentResponse root = Created(await asAuthor.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Root", 40, null, null), CancellationToken.None));
        EditTimelineCommentResponse gone = Created(await asAuthor.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Removed reply", 0, null, root.Id), CancellationToken.None));
        await Task.Delay(5);
        EditTimelineCommentResponse kept = Created(await asAuthor.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Kept reply", 0, null, root.Id), CancellationToken.None));
        (await asAuthor.DeleteComment(project.Id, timeline.Id, gone.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await asAuthor.DeleteComment(project.Id, timeline.Id, root.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        IReadOnlyList<EditTimelineCommentResponse> list = List(await asAuthor.ListComments(project.Id, timeline.Id, CancellationToken.None));
        EditTimelineCommentResponse heir = list.Should().ContainSingle().Which;
        heir.Id.Should().Be(kept.Id);
        heir.ParentId.Should().BeNull("a removed reply cannot carry the thread");
        heir.Frame.Should().Be(40);
    }

    [Fact]
    public async Task Deleting_a_reply_leaves_the_thread_alone()
    {
        Guid owner = Guid.NewGuid();
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        EditTimelineEntity timeline = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "T", DocumentJson = "{}", Revision = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(timeline);
        db.SaveChanges();
        EditTimelinesController asAuthor = Controller(db, Caller(owner, project.OrganizationId, "author@example.com"));

        EditTimelineCommentResponse root = Created(await asAuthor.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Root", 10, null, null), CancellationToken.None));
        EditTimelineCommentResponse reply = Created(await asAuthor.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Reply", 0, null, root.Id), CancellationToken.None));
        (await asAuthor.DeleteComment(project.Id, timeline.Id, reply.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        IReadOnlyList<EditTimelineCommentResponse> list = List(await asAuthor.ListComments(project.Id, timeline.Id, CancellationToken.None));
        list.Should().ContainSingle().Which.Id.Should().Be(root.Id);
    }

    [Fact]
    public void The_colour_card_title_and_in_out_range_the_editor_creates_validate()
    {
        Guid projectId = Guid.NewGuid();
        // The exact item shapes `web/lib/editing/structure/add-at-playhead.ts` builds, plus a stored in/out range.
        string json = $$"""
        {
          "schemaVersion": 2, "id": "{{Guid.NewGuid()}}", "projectId": "{{projectId}}", "name": "W06",
          "settings": { "width": 1920, "height": 1080, "rate": { "num": 30, "den": 1 }, "sampleRate": 48000, "pixelAspect": { "num": 1, "den": 1 }, "aspectPolicy": "fit", "startTimecodeFrames": 0 },
          "tracks": [
            { "id": "{{Guid.NewGuid()}}", "kind": "video", "name": "V1", "height": 64, "locked": false, "enabled": true, "mute": false, "solo": false, "items": [
              { "id": "{{Guid.NewGuid()}}", "type": "generatorClip", "name": "Colour card", "recordIn": 0, "recordOut": 90, "generatorType": "color", "generatorConfig": { "color": "#000000" }, "enabled": true, "provenance": null },
              { "id": "{{Guid.NewGuid()}}", "type": "titleClip", "name": "Title", "recordIn": 90, "recordOut": 180, "text": "Title", "fontSize": 64, "color": "#ffffff", "enabled": true, "template": null, "provenance": null }
            ] }
          ],
          "markers": [],
          "inOut": { "inFrame": 30, "outFrame": 120 }
        }
        """;
        EditTimeline doc = JsonSerializer.Deserialize<EditTimeline>(json, EditTimelineJson.Options)!;
        doc.InOut.Should().NotBeNull();
        ReelBolt.Shared.Editing.ValidationResult result = EditTimelineValidator.Validate(doc, new EditTimelineValidationContext(projectId, new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid>(), new Dictionary<Guid, Guid>()));
        result.IsValid.Should().BeTrue(string.Join("; ", result.Violations.Select(v => $"{v.Path}: {v.Message}")));
    }
}
