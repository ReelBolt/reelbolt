#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// <c>GET /api/v1/storage/usage</c>: the caller's ACTIVE workspace, never one they name, and 404 for a
/// token with no active organization or role.
/// </summary>
public class StorageUsageControllerTests
{
    private sealed class User(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    [Fact]
    public async Task Usage_returns_the_active_workspaces_numbers_and_its_biggest_projects()
    {
        Guid org = Guid.NewGuid();
        Guid bigProject = Guid.NewGuid();
        Guid smallProject = Guid.NewGuid();
        Mock<IStorageUsageService> storage = new();
        storage.Setup(s => s.GetUsageAsync(org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageUsage(1234, 15L * 1024 * 1024 * 1024));
        storage.Setup(s => s.GetTopProjectsAsync(org, StorageUsageController.TopProjects, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectStorageUsage>
            {
                new(bigProject, "Big", 1000),
                new(smallProject, "Small", 234)
            });
        StorageUsageController controller = new(new User(Guid.NewGuid(), org, OrganizationRole.Member), storage.Object);

        ActionResult<StorageUsageResponse> result = await controller.Usage(CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        StorageUsageResponse body = ok.Value.Should().BeOfType<StorageUsageResponse>().Subject;
        body.UsedBytes.Should().Be(1234);
        body.LimitBytes.Should().Be(15L * 1024 * 1024 * 1024);
        body.ByProject.Should().HaveCount(2);
        body.ByProject[0].Should().Be(new ProjectStorageUsageResponse(bigProject, "Big", 1000));
        body.ByProject[1].Should().Be(new ProjectStorageUsageResponse(smallProject, "Small", 234));

        storage.Verify(s => s.GetUsageAsync(org, It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.GetTopProjectsAsync(org, StorageUsageController.TopProjects, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Usage_serves_the_zip_unpack_limit_from_the_one_constant_the_worker_enforces()
    {
        Guid org = Guid.NewGuid();
        Mock<IStorageUsageService> storage = new();
        storage.Setup(s => s.GetUsageAsync(org, It.IsAny<CancellationToken>())).ReturnsAsync(new StorageUsage(0, null));
        storage.Setup(s => s.GetTopProjectsAsync(org, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectStorageUsage>());
        StorageUsageController controller = new(new User(Guid.NewGuid(), org, OrganizationRole.Member), storage.Object);

        StorageUsageResponse body = (StorageUsageResponse)((OkObjectResult)(await controller.Usage(CancellationToken.None)).Result!).Value!;

        body.ZipUnpackLimits.Should().NotBeNull();
        body.ZipUnpackLimits!.MaxMb.Should().Be(UnpackLimits.MaxArchiveBytes / (1024 * 1024));
        body.ZipUnpackLimits.MaxMb.Should().Be(64);
    }

    [Fact]
    public async Task An_unlimited_plan_reports_a_null_limit()
    {
        Guid org = Guid.NewGuid();
        Mock<IStorageUsageService> storage = new();
        storage.Setup(s => s.GetUsageAsync(org, It.IsAny<CancellationToken>())).ReturnsAsync(new StorageUsage(4096, null));
        storage.Setup(s => s.GetTopProjectsAsync(org, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectStorageUsage>());
        StorageUsageController controller = new(new User(Guid.NewGuid(), org, OrganizationRole.Owner), storage.Object);

        StorageUsageResponse body = (StorageUsageResponse)((OkObjectResult)(await controller.Usage(CancellationToken.None)).Result!).Value!;

        body.LimitBytes.Should().BeNull();
        body.ByProject.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Usage_is_404_for_a_caller_with_no_active_workspace(bool withRole)
    {
        Guid org = Guid.NewGuid();
        Mock<IStorageUsageService> storage = new();
        User user = withRole
            ? new User(Guid.NewGuid(), null, OrganizationRole.Member)
            : new User(Guid.NewGuid(), org, null);
        StorageUsageController controller = new(user, storage.Object);

        ActionResult<StorageUsageResponse> result = await controller.Usage(CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        storage.Verify(s => s.GetUsageAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        storage.Verify(s => s.GetTopProjectsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_project_list_is_capped_at_the_controllers_constant()
    {
        StorageUsageController.TopProjects.Should().Be(20);
    }
}
