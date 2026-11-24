#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The one definition of "how much storage a workspace uses": the sum of <c>project_files.size_bytes</c>
/// over the organization's projects, the plan's limit beside it, and the biggest projects first. The
/// number a person sees is the number the quota enforces, so this is asserted rather than assumed.
/// </summary>
public class StorageUsageServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"storage-usage-{Guid.NewGuid():N}")
            .Options);

    private static IOrganizationEntitlements Fixed(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> mock = new();
        mock.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return mock.Object;
    }

    private static Project AddProject(InferenceApiDbContext db, Guid org, string name)
    {
        Project project = new() { Id = Guid.NewGuid(), Name = name, OrganizationId = org };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static void AddFile(InferenceApiDbContext db, Guid projectId, long sizeBytes)
    {
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = $"f{Guid.NewGuid():N}",
            StorageKey = $"projects/{projectId}/userFiles/{Guid.NewGuid():N}",
            SizeBytes = sizeBytes
        });
        db.SaveChanges();
    }

    private static void AddLibraryAsset(InferenceApiDbContext db, Guid org, long sizeBytes)
    {
        Guid libraryId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        db.DesignSystemLibraries.Add(new DesignSystemLibrary
        {
            Id = libraryId,
            OrganizationId = org,
            Name = $"library-{libraryId:N}",
            DesignSystemId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(),
        });
        db.DesignSystemLibraryAssets.Add(new DesignSystemLibraryAsset
        {
            Id = assetId,
            LibraryId = libraryId,
            Name = $"asset-{assetId:N}.woff2",
            MimeType = "font/woff2",
            SizeBytes = sizeBytes,
            Sha256 = new string('a', 64),
            StorageKey = $"design-libraries/{org}/{libraryId}/assets/{assetId}/asset.woff2",
            CreatedByUserId = Guid.NewGuid(),
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Used_bytes_sums_only_the_organizations_own_project_files()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        Guid otherOrg = Guid.NewGuid();
        Project one = AddProject(db, org, "one");
        Project two = AddProject(db, org, "two");
        Project foreign = AddProject(db, otherOrg, "foreign");
        AddFile(db, one.Id, 10);
        AddFile(db, two.Id, 20);
        AddFile(db, foreign.Id, 999);

        StorageUsageService service = new(db, Fixed(new OrganizationEntitlementSet()));

        (await service.GetUsedBytesAsync(org, CancellationToken.None)).Should().Be(30);
        (await service.GetUsedBytesAsync(otherOrg, CancellationToken.None)).Should().Be(999);
        (await service.GetUsedBytesAsync(Guid.NewGuid(), CancellationToken.None)).Should().Be(0,
            "a workspace with no projects uses nothing");
    }

    [Fact]
    public async Task Used_bytes_include_the_organizations_library_asset_bytes()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        Guid otherOrg = Guid.NewGuid();
        AddFile(db, AddProject(db, org, "p").Id, 10);
        AddLibraryAsset(db, org, 40);
        AddLibraryAsset(db, otherOrg, 999);
        StorageUsageService service = new(db, Fixed(new OrganizationEntitlementSet()));

        (await service.GetUsedBytesAsync(org, CancellationToken.None)).Should().Be(50,
            "library asset bytes count against the same quota as project files");
        (await service.GetUsedBytesAsync(otherOrg, CancellationToken.None)).Should().Be(999,
            "another organization's assets never count");
    }

    [Fact]
    public async Task An_empty_workspace_uses_nothing_rather_than_null()
    {
        using InferenceApiDbContext db = NewDb();
        AddProject(db, Guid.NewGuid(), "empty");
        StorageUsageService service = new(db, Fixed(new OrganizationEntitlementSet()));

        (await service.GetUsageAsync(Guid.NewGuid(), CancellationToken.None)).UsedBytes.Should().Be(0);
    }

    [Fact]
    public async Task The_limit_is_the_plans_storage_in_binary_gigabytes()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        Project project = AddProject(db, org, "p");
        AddFile(db, project.Id, Gb / 2);
        StorageUsageService service = new(db, Fixed(new OrganizationEntitlementSet { Tier = "creator", StorageGb = 15 }));

        StorageUsage usage = await service.GetUsageAsync(org, CancellationToken.None);

        usage.UsedBytes.Should().Be(Gb / 2);
        usage.LimitBytes.Should().Be(15 * Gb);
    }

    [Fact]
    public async Task A_plan_with_no_storage_limit_reports_unlimited()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        AddFile(db, AddProject(db, org, "p").Id, 4096);
        StorageUsageService service = new(db, Fixed(new OrganizationEntitlementSet { Tier = "enterprise" }));

        StorageUsage usage = await service.GetUsageAsync(org, CancellationToken.None);

        usage.LimitBytes.Should().BeNull("absent from the plan means unlimited");
        StorageUsageService.LimitBytesOf(OrganizationEntitlementSet.Unlimited).Should().BeNull();
        StorageUsageService.LimitBytesOf(OrganizationEntitlementSet.Unlimited with { StorageGb = 1 }).Should().Be(Gb);
    }

    [Fact]
    public async Task The_biggest_projects_come_first_with_the_name_breaking_ties()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        Guid otherOrg = Guid.NewGuid();
        Project alpha = AddProject(db, org, "Alpha");
        Project beta = AddProject(db, org, "Beta");
        Project big = AddProject(db, org, "Big");
        Project foreign = AddProject(db, otherOrg, "Foreign");
        AddFile(db, alpha.Id, 30);
        AddFile(db, beta.Id, 30);
        AddFile(db, big.Id, 100);
        AddFile(db, foreign.Id, 10 * Gb);
        StorageUsageService service = new(db, Fixed(new OrganizationEntitlementSet()));

        IReadOnlyList<ProjectStorageUsage> top = await service.GetTopProjectsAsync(org, 20, CancellationToken.None);

        top.Select(p => p.Name).Should().Equal("Big", "Alpha", "Beta");
        top.Select(p => p.UsedBytes).Should().Equal(100, 30, 30);
        top.Select(p => p.ProjectId).Should().NotContain(foreign.Id);
    }

    [Fact]
    public async Task By_project_lists_at_most_the_requested_number()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        for (int i = 0; i < 5; i++) AddFile(db, AddProject(db, org, $"p{i}").Id, i + 1);
        StorageUsageService service = new(db, Fixed(new OrganizationEntitlementSet()));

        (await service.GetTopProjectsAsync(org, 2, CancellationToken.None)).Should().HaveCount(2);
        (await service.GetTopProjectsAsync(org, 0, CancellationToken.None)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(512, "512 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1 MB")]
    [InlineData(15L * 1024 * 1024 * 1024, "15 GB")]
    public void Format_bytes_is_binary_and_short(long bytes, string expected)
    {
        StorageUsageService.FormatBytes(bytes).Should().Be(expected);
    }
}
