#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;

/// <summary>
/// The HTTP edge (docs/design/cloud-drive-sync.md "HTTP API"): connections are owner-scoped (404 for
/// anybody else's), an unconfigured provider is 503, linking queues a sync and answers 201, "Sync now"
/// answers 202 and is throttled, a second link of one provider is 409.
/// </summary>
public class CloudStorageControllersTests
{
    private static int StatusOf<T>(ActionResult<T> result) => StatusOf(result.Result);

    private static int StatusOf(IActionResult? result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        null => 200,
        _ => 200
    };

    private static string? ErrorOf<T>(ActionResult<T> result) => ErrorOf(result.Result);

    private static string? ErrorOf(IActionResult? result) =>
        result is ObjectResult { Value: { } value } ? value.GetType().GetProperty("error")?.GetValue(value) as string : null;

    private static T Body<T>(ActionResult<T> result) => (T)((ObjectResult)result.Result!).Value!;

    private static CloudStorageController StorageController(CloudFixture f, Guid? userId = null, bool authenticated = true) =>
        new(f.Db, f.Registry, f.Connections, new CloudFakeCurrentUser(userId ?? f.UserId, f.OrganizationId, authenticated),
            NullLogger<CloudStorageController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static (ProjectCloudLinksController Controller, CloudSyncQueue Queue) LinksController(
        CloudFixture f, CloudSyncQueue? queue = null, ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? events = null)
    {
        CloudFakeCurrentUser user = new(f.UserId, f.OrganizationId);
        f.Tenant.BeginOrganizationScope(f.OrganizationId);
        queue ??= new CloudSyncQueue(f.Clock);
        ProjectCloudLinksController controller = new(
            f.Db, new ProjectAccessService(f.Db, user), f.Registry, f.Connections, queue, user,
            NullLogger<ProjectCloudLinksController>.Instance, events)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, queue);
    }

    // ---------------------------------------------------------------- /api/v1/cloud-storage

    [Fact]
    public void Providers_lists_all_three_with_their_configured_flag()
    {
        CloudFixture f = new();
        List<CloudStorageProviderResponse> providers = Body(StorageController(f).Providers()).ToList();

        providers.Select(p => p.Provider).Should().Equal("google-drive", "onedrive", "dropbox");
        providers.Single(p => p.Provider == "google-drive").Configured.Should().BeTrue();
        providers.Single(p => p.Provider == "dropbox").Configured.Should().BeFalse();
    }

    [Fact]
    public async Task Another_users_connection_is_404_everywhere_and_is_not_listed()
    {
        CloudFixture f = new();
        Guid stranger = Guid.NewGuid();
        CloudStorageConnection theirs = await f.AddConnectionAsync(userId: stranger, organizationId: f.OrganizationId);
        CloudStorageConnection otherWorkspace = await f.AddConnectionAsync(organizationId: Guid.NewGuid());
        CloudStorageController controller = StorageController(f);

        Body(await controller.Connections(CancellationToken.None)).Should().BeEmpty();
        foreach (CloudStorageConnection connection in new[] { theirs, otherWorkspace })
        {
            IActionResult deleted = await controller.Disconnect(connection.Id, CancellationToken.None);
            StatusOf(deleted).Should().Be(404);
            ErrorOf(deleted).Should().Be("connection_not_found");
            StatusOf(await controller.Drives(connection.Id, CancellationToken.None)).Should().Be(404);
            StatusOf(await controller.Folders(connection.Id, null, null, CancellationToken.None)).Should().Be(404);
        }

        (await f.Db.CloudStorageConnections.CountAsync()).Should().Be(2);
        f.Provider.TokensSeen.Should().BeEmpty("nothing was asked of the provider with somebody else's token");
    }

    [Fact]
    public async Task The_callers_own_connection_is_listed_browsed_and_disconnected()
    {
        CloudFixture f = new();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        CloudStorageController controller = StorageController(f);

        Body(await controller.Connections(CancellationToken.None)).Should().ContainSingle()
            .Which.Provider.Should().Be("google-drive");
        Body(await controller.Drives(mine.Id, CancellationToken.None)).Should().ContainSingle()
            .Which.Kind.Should().Be("personal");
        StatusOf(await controller.Folders(mine.Id, null, "folder-1", CancellationToken.None)).Should().Be(200);
        StatusOf(await controller.Disconnect(mine.Id, CancellationToken.None)).Should().Be(204);
    }

    [Fact]
    public async Task Browsing_rejects_ids_with_control_characters_or_too_long()
    {
        CloudFixture f = new();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        CloudStorageController controller = StorageController(f);

        ErrorOf(await controller.Folders(mine.Id, "drive\u0000", null, CancellationToken.None)).Should().Be("invalid_id");
        ErrorOf(await controller.Folders(mine.Id, null, new string('a', 1025), CancellationToken.None)).Should().Be("invalid_id");
        f.Provider.TokensSeen.Should().BeEmpty();
    }

    [Fact]
    public async Task Starting_an_unconfigured_provider_is_503_and_an_unknown_one_404()
    {
        CloudFixture unconfigured = new(configured: false);
        ActionResult<CloudOAuthStartResponse> refused = await StorageController(unconfigured)
            .Start("google-drive", new CloudOAuthStartRequest("/app/settings"), CancellationToken.None);
        StatusOf(refused).Should().Be(503);
        ErrorOf(refused).Should().Be("provider_not_configured");

        CloudFixture noBaseUrl = new(publicBaseUrl: "");
        ActionResult<CloudOAuthStartResponse> missing = await StorageController(noBaseUrl)
            .Start("google-drive", new CloudOAuthStartRequest(null), CancellationToken.None);
        StatusOf(missing).Should().Be(503);
        ErrorOf(missing).Should().Be("public_base_url_missing");

        StatusOf(await StorageController(new CloudFixture()).Start("box", null, CancellationToken.None)).Should().Be(404);

        CloudFixture configured = new();
        Body(await StorageController(configured).Start("google-drive", new CloudOAuthStartRequest(null), CancellationToken.None))
            .AuthorizeUrl.Should().StartWith("https://vendor.example/authorize?state=");
    }

    [Fact]
    public async Task The_callback_redirects_to_the_stored_path_with_the_outcome()
    {
        CloudFixture f = new();
        CloudStorageController controller = StorageController(f);
        CloudOAuthStartResponse start = Body(await controller.Start(
            "google-drive", new CloudOAuthStartRequest("/app/projects/p1?tab=cloud"), CancellationToken.None));
        string state = Uri.UnescapeDataString(new Uri(start.AuthorizeUrl).Query.Split('&')[0]["?state=".Length..]);

        IActionResult connected = await controller.Callback("google-drive", "code", state, null, CancellationToken.None);
        ((RedirectResult)connected).Url.Should().Be("/app/projects/p1?tab=cloud&cloud=connected&provider=google-drive");

        IActionResult replayed = await controller.Callback("google-drive", "code", state, null, CancellationToken.None);
        ((RedirectResult)replayed).Url.Should().Be("/app/settings?cloud=error&provider=google-drive&reason=invalid_state");

        IActionResult signedOut = await StorageController(f, authenticated: false).Callback("google-drive", "code", "whatever", null, CancellationToken.None);
        ((RedirectResult)signedOut).Url.Should().StartWith("/app/settings?cloud=error&provider=google-drive&reason=");
    }

    // ---------------------------------------------------------------- /api/v1/projects/{id}/cloud-links

    [Fact]
    public async Task Linking_a_folder_answers_201_and_queues_the_first_sync()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        (ProjectCloudLinksController controller, CloudSyncQueue queue) = LinksController(f);

        ActionResult<ProjectCloudLinkResponse> created = await controller.Create(
            project.Id, new CreateProjectCloudLinkRequest(mine.Id, null, "folder-1", ["scenes/", "intro.md"]), CancellationToken.None);

        StatusOf(created).Should().Be(201);
        ProjectCloudLinkResponse body = Body(created);
        body.Provider.Should().Be("google-drive");
        body.Syncing.Should().BeTrue();
        body.IncludePaths.Should().Equal("intro.md", "scenes");
        body.FolderDisplayPath.Should().Be("/Videos");
        queue.IsSyncing(body.Id).Should().BeTrue();
        f.Provider.FoldersRequested.Should().ContainSingle().Which.FolderId.Should().Be("folder-1");

        List<ProjectCloudLinkResponse> listed = Body(await controller.List(project.Id, CancellationToken.None)).ToList();
        listed.Should().ContainSingle().Which.AccountEmail.Should().Be("person@example.com");
    }

    [Fact]
    public async Task A_second_link_of_the_same_provider_is_409_already_linked()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        (ProjectCloudLinksController controller, _) = LinksController(f);
        await controller.Create(project.Id, new CreateProjectCloudLinkRequest(mine.Id), CancellationToken.None);

        ActionResult<ProjectCloudLinkResponse> again = await controller.Create(project.Id, new CreateProjectCloudLinkRequest(mine.Id), CancellationToken.None);

        StatusOf(again).Should().Be(409);
        ErrorOf(again).Should().Be("already_linked");
    }

    [Fact]
    public async Task Linking_a_cloud_folder_records_that_the_person_connected_a_source_and_a_refusal_or_a_sync_does_not()
    {
        // activation.source_connected: the person's own action, asked for once per user by the emitter.
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        CloudStorageConnection theirs = await f.AddConnectionAsync(userId: Guid.NewGuid());
        ReelBolt.WorkflowEngine.Tests.Services.Telemetry.RecordingProductEventEmitter events = new();
        (ProjectCloudLinksController controller, _) = LinksController(f, events: events);

        // Refusals first: somebody else's connection and a bad selection write nothing.
        ErrorOf(await controller.Create(project.Id, new CreateProjectCloudLinkRequest(theirs.Id), CancellationToken.None))
            .Should().Be("connection_not_found");
        ErrorOf(await controller.Create(project.Id, new CreateProjectCloudLinkRequest(mine.Id, IncludePaths: ["../etc"]), CancellationToken.None))
            .Should().Be("invalid_include_paths");
        events.Firsts.Should().BeEmpty();

        ActionResult<ProjectCloudLinkResponse> created = await controller.Create(
            project.Id, new CreateProjectCloudLinkRequest(mine.Id, null, "folder-1"), CancellationToken.None);
        StatusOf(created).Should().Be(201);
        events.Firsts.Should().Equal((f.UserId, (Guid?)f.OrganizationId, "activation.source_connected"));

        // A second link of the same provider is refused, and "Sync now" is a sync: neither is the step.
        StatusOf(await controller.Create(project.Id, new CreateProjectCloudLinkRequest(mine.Id), CancellationToken.None)).Should().Be(409);
        await controller.Sync(project.Id, Body(created).Id, CancellationToken.None);
        events.Firsts.Should().HaveCount(1);
        events.Appends.Should().BeEmpty();
    }

    [Fact]
    public async Task Linking_refuses_somebody_elses_connection_a_missing_folder_bad_paths_and_an_unconfigured_provider()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        CloudStorageConnection theirs = await f.AddConnectionAsync(userId: Guid.NewGuid());
        (ProjectCloudLinksController controller, _) = LinksController(f);

        ErrorOf(await controller.Create(project.Id, new CreateProjectCloudLinkRequest(theirs.Id), CancellationToken.None))
            .Should().Be("connection_not_found");
        ErrorOf(await controller.Create(project.Id, new CreateProjectCloudLinkRequest(mine.Id, IncludePaths: ["../etc"]), CancellationToken.None))
            .Should().Be("invalid_include_paths");
        ErrorOf(await controller.Create(project.Id, new CreateProjectCloudLinkRequest(mine.Id, FolderId: "x\u0007"), CancellationToken.None))
            .Should().Be("invalid_id");

        f.Provider.FolderFailure = new CloudStorageApiException("404", CloudStorageErrorKind.NotFound, HttpStatusCode.NotFound);
        ActionResult<ProjectCloudLinkResponse> gone = await controller.Create(project.Id, new CreateProjectCloudLinkRequest(mine.Id, FolderId: "gone"), CancellationToken.None);
        StatusOf(gone).Should().Be(404);
        ErrorOf(gone).Should().Be("folder_not_found");

        f.Provider.FolderFailure = null;
        f.Provider.IsConfigured = false;
        ActionResult<ProjectCloudLinkResponse> unconfigured = await controller.Create(project.Id, new CreateProjectCloudLinkRequest(mine.Id), CancellationToken.None);
        StatusOf(unconfigured).Should().Be(503);
        ErrorOf(unconfigured).Should().Be("provider_not_configured");

        (await f.Db.ProjectCloudLinks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Another_workspaces_project_is_404()
    {
        CloudFixture f = new();
        Project foreign = await f.AddProjectAsync(organizationId: Guid.NewGuid(), ownerId: Guid.NewGuid());
        CloudStorageConnection mine = await f.AddConnectionAsync();
        (ProjectCloudLinksController controller, _) = LinksController(f);

        StatusOf(await controller.Create(foreign.Id, new CreateProjectCloudLinkRequest(mine.Id), CancellationToken.None)).Should().Be(404);
        StatusOf(await controller.List(foreign.Id, CancellationToken.None)).Should().Be(404);
    }

    [Fact]
    public async Task Sync_now_answers_202_queued_then_already_syncing_then_429_within_the_cooldown()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, mine);
        (ProjectCloudLinksController controller, CloudSyncQueue queue) = LinksController(f);

        ActionResult<CloudSyncQueuedResponse> first = await controller.Sync(project.Id, link.Id, CancellationToken.None);
        StatusOf(first).Should().Be(202);
        Body(first).Status.Should().Be("queued");

        Body(await controller.Sync(project.Id, link.Id, CancellationToken.None)).Status.Should().Be("already_syncing");

        queue.Reader.TryRead(out Guid id);
        queue.TryBegin(id, out _);
        queue.Complete(id);
        f.Clock.Advance(TimeSpan.FromSeconds(3));

        ActionResult<CloudSyncQueuedResponse> tooSoon = await controller.Sync(project.Id, link.Id, CancellationToken.None);
        StatusOf(tooSoon).Should().Be(429);
        ErrorOf(tooSoon).Should().Be("sync_too_soon");
        controller.Response.Headers.RetryAfter.ToString().Should().Be("17");

        StatusOf(await controller.Sync(project.Id, Guid.NewGuid(), CancellationToken.None)).Should().Be(404);
    }

    [Fact]
    public async Task Changing_the_selection_queues_a_sync_only_when_it_actually_changed()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, mine, "[\"scenes\"]");
        (ProjectCloudLinksController controller, CloudSyncQueue queue) = LinksController(f);

        StatusOf(await controller.Update(project.Id, link.Id, new UpdateProjectCloudLinkRequest(["scenes/"]), CancellationToken.None)).Should().Be(200);
        queue.IsSyncing(link.Id).Should().BeFalse("the normalized selection is the same");

        ErrorOf(await controller.Update(project.Id, link.Id, new UpdateProjectCloudLinkRequest(["a/../b"]), CancellationToken.None))
            .Should().Be("invalid_include_paths");

        ProjectCloudLinkResponse changed = Body(await controller.Update(project.Id, link.Id, new UpdateProjectCloudLinkRequest([]), CancellationToken.None));
        changed.IncludePaths.Should().BeNull("an empty selection means every file");
        queue.IsSyncing(link.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Unlinking_answers_204_and_keeps_the_files()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection mine = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, mine);
        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "a.txt",
            OriginalPath = "a.txt",
            MimeType = "text/plain",
            SourceKind = f.SourceKind,
            SourceRevision = "r"
        });
        await f.Db.SaveChangesAsync();
        (ProjectCloudLinksController controller, _) = LinksController(f);

        StatusOf(await controller.Delete(project.Id, link.Id, CancellationToken.None)).Should().Be(204);

        (await f.Db.ProjectCloudLinks.CountAsync()).Should().Be(0);
        (await f.Db.ProjectFiles.CountAsync(r => r.ProjectId == project.Id)).Should().Be(1);
        StatusOf(await controller.Delete(project.Id, link.Id, CancellationToken.None)).Should().Be(404);
    }
}
