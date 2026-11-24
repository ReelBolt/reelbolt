#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.FolderSync;

/// <summary>
/// <c>activation.source_connected</c> (docs/product-events.md): a person who links a computer folder to a
/// project has added material, exactly like a person who uploads a file. The desktop app signs the request
/// as a device, and a device acts as its pairing user, so the event is that user's. The files the folder
/// then sends are a sync and write nothing; the same holds for a repository and a cloud drive folder
/// (<c>ProjectGitHubSelectionTests</c>, <c>CloudStorageControllersTests</c>).
/// </summary>
public class DeviceFolderLinkSourceConnectedTests
{
    private const string SourceConnected = "activation.source_connected";

    private sealed class World
    {
        public World(OrganizationRole? role = OrganizationRole.Member)
        {
            Db = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Db.Projects.Add(new Project
            {
                Id = ProjectId,
                Name = "Promo film",
                OwnerId = UserId,
                OrganizationId = OrganizationId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            if (role is { } r)
            {
                Db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = OrganizationId, UserId = UserId, Role = r });
            }

            Db.SaveChanges();
            Device = new DeviceContext(Guid.NewGuid(), "Studio PC", OrganizationId, UserId);
        }

        public InferenceApiDbContext Db { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = Guid.NewGuid();
        public DeviceContext Device { get; }
        public RecordingProductEventEmitter Events { get; } = new();

        public DeviceFolderSyncController Controller(IProductEventEmitter? events)
        {
            LocalFolderSyncService sync = new(
                Db, Mock.Of<IFileStorageService>(), Mock.Of<IOrganizationEntitlements>(), NullLogger<LocalFolderSyncService>.Instance);
            DeviceFolderSyncController controller = new(
                Db, new DeviceProjectAccess(Db), sync, null!, null!, NullLogger<DeviceFolderSyncController>.Instance,
                productEvents: events);
            DefaultHttpContext http = new();
            http.Items[DeviceRequestAuthenticator.ItemKey] = Device;
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            return controller;
        }
    }

    private static int StatusOf<T>(ActionResult<T> result) => result.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200,
    };

    [Fact]
    public async Task Linking_a_computer_folder_records_it_for_the_pairing_user()
    {
        World w = new();

        ActionResult<DeviceFolderLinkResponse> created = await w.Controller(w.Events).CreateLink(
            new CreateDeviceFolderLinkRequest(w.ProjectId, "Footage", "D:\\Footage"), CancellationToken.None);

        StatusOf(created).Should().Be(201);
        w.Db.ProjectLocalFolderLinks.Count().Should().Be(1);
        w.Events.Firsts.Should().Equal((w.UserId, (Guid?)w.OrganizationId, SourceConnected));
        w.Events.Appends.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_link_records_nothing()
    {
        World w = new();
        DeviceFolderSyncController controller = w.Controller(w.Events);
        await controller.CreateLink(new CreateDeviceFolderLinkRequest(w.ProjectId, "Footage", "D:\\Footage"), CancellationToken.None);
        w.Events.Firsts.Clear();

        // The same target folder again, a folder name that cannot be used, and a project that is not there.
        StatusOf(await controller.CreateLink(
            new CreateDeviceFolderLinkRequest(w.ProjectId, "Footage", "E:\\Other"), CancellationToken.None)).Should().Be(409);
        StatusOf(await controller.CreateLink(
            new CreateDeviceFolderLinkRequest(w.ProjectId, "../up", "E:\\Other"), CancellationToken.None)).Should().Be(400);
        StatusOf(await controller.CreateLink(
            new CreateDeviceFolderLinkRequest(Guid.NewGuid(), "Clips", "E:\\Clips"), CancellationToken.None)).Should().Be(404);

        w.Events.Firsts.Should().BeEmpty();
        w.Db.ProjectLocalFolderLinks.Count().Should().Be(1);
    }

    [Fact]
    public async Task A_device_whose_pairing_user_may_no_longer_edit_links_nothing_and_records_nothing()
    {
        World w = new(role: null);

        StatusOf(await w.Controller(w.Events).CreateLink(
            new CreateDeviceFolderLinkRequest(w.ProjectId, "Footage", "D:\\Footage"), CancellationToken.None)).Should().Be(403);

        w.Events.Firsts.Should().BeEmpty();
        w.Db.ProjectLocalFolderLinks.Count().Should().Be(0);
    }

    [Fact]
    public async Task Without_an_emitter_the_link_is_still_created()
    {
        World w = new();

        StatusOf(await w.Controller(events: null).CreateLink(
            new CreateDeviceFolderLinkRequest(w.ProjectId, "Footage", "D:\\Footage"), CancellationToken.None)).Should().Be(201);
        w.Db.ProjectLocalFolderLinks.Count().Should().Be(1);
    }

    [Fact]
    public void Only_the_three_link_routes_name_the_event()
    {
        // The person's own action: linking a repository, a cloud drive folder or a computer folder. A sync
        // pass, a webhook, a change of the selection and an unlink are not the step, so no other file of the
        // API may name the event.
        ProductEventNames.SourceConnected.Should().Be(SourceConnected);
        string api = Path.Combine(RepositoryRoot(), "inference", "src", "ReelBolt.Inference.Api");
        string[] readers = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                string text = File.ReadAllText(path);
                return text.Contains("ProductEventNames.SourceConnected", StringComparison.Ordinal)
                    || (text.Contains("\"" + SourceConnected + "\"", StringComparison.Ordinal) && !path.EndsWith("ProductEventNames.cs", StringComparison.Ordinal));
            })
            .Select(path => Path.GetRelativePath(api, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        readers.Should().Equal(
            "Controllers/DeviceFolderSyncController.cs",
            "Controllers/ProjectCloudLinksController.cs",
            "Controllers/ProjectGitHubController.cs");
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(DeviceFolderLinkSourceConnectedTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
