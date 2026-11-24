#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The storage gate itself: exactly at the quota passes, one byte over is <c>STORAGE_QUOTA_EXCEEDED</c>
/// (413) carrying the used/limit numbers, an unlimited plan never refuses, and the check is a live
/// aggregate plus the NET growth the caller is about to add.
/// </summary>
public class PlanGateStorageTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static readonly OrganizationEntitlementSet Trial = new() { Tier = "trial", StorageGb = 1 };

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"plan-gate-storage-{Guid.NewGuid():N}")
            .Options);

    private static Project AddProject(InferenceApiDbContext db, Guid org, long usedBytes)
    {
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OrganizationId = org };
        db.Projects.Add(project);
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "f",
            StorageKey = $"projects/{project.Id}/userFiles/f",
            SizeBytes = usedBytes
        });
        db.SaveChanges();
        return project;
    }

    [Fact]
    public void Exactly_at_the_quota_passes_and_one_byte_over_fails()
    {
        PlanGate.CheckStorage(Gb, 0, Trial).Should().BeNull("the boundary belongs to the customer, not the plan");
        PlanGate.CheckStorage(Gb - 1, 1, Trial).Should().BeNull();
        PlanGate.CheckStorage(0, Gb, Trial).Should().BeNull();

        PlanGateViolation? over = PlanGate.CheckStorage(Gb - 1, 2, Trial);

        over.Should().NotBeNull();
        over!.Code.Should().Be(PlanGate.StorageCode);
        over.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        over.Feature.Should().Be("storage");
        over.StatusCode.Should().Be(413);
        over.Limit.Should().Be(Gb);
        over.Current.Should().Be(Gb - 1);

        PlanGateViolation? atTheLine = PlanGate.CheckStorage(Gb, 1, Trial);
        atTheLine.Should().NotBeNull("one byte past the limit is over it");
        atTheLine!.Current.Should().Be(Gb);
    }

    [Fact]
    public void An_unlimited_plan_never_refuses()
    {
        PlanGate.CheckStorage(long.MaxValue / 4, long.MaxValue / 4, OrganizationEntitlementSet.Unlimited).Should().BeNull();
        PlanGate.CheckStorage(long.MaxValue, long.MaxValue, new OrganizationEntitlementSet { Tier = "enterprise" }).Should().BeNull();
    }

    [Fact]
    public async Task A_zero_or_negative_growth_never_consults_the_plan()
    {
        using InferenceApiDbContext db = NewDb();
        UploadsEntitlements entitlements = new(Trial);
        PlanGateService gate = new(db, entitlements);

        (await gate.CheckUploadAsync(Guid.NewGuid(), 0, CancellationToken.None)).Should().BeNull();
        (await gate.CheckUploadAsync(Guid.NewGuid(), -500, CancellationToken.None)).Should().BeNull();
        entitlements.Reads.Should().Be(0, "a rewrite that does not add bytes is not a quota question");
    }

    [Fact]
    public async Task The_check_is_the_live_total_plus_the_net_growth()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        AddProject(db, org, Gb - 1000);
        PlanGateService gate = new(db, new UploadsEntitlements(Trial));

        (await gate.CheckUploadAsync(org, 1000, CancellationToken.None)).Should().BeNull("the last 1,000 bytes fit exactly");
        PlanGateViolation? over = await gate.CheckUploadAsync(org, 1001, CancellationToken.None);

        over.Should().NotBeNull();
        over!.Code.Should().Be(PlanGate.StorageCode);
        over.StatusCode.Should().Be(413);
        over.Current.Should().Be(Gb - 1000);
        over.Limit.Should().Be(Gb);
        over.Message.Should().Contain("1 GB").And.Contain("trial");
    }

    [Theory]
    [InlineData(3_945_838_247L, "This file is 3.7 GB and your Trial plan stores up to 1 GB.")]
    [InlineData(1_073_741_825L, "This file is 1.1 GB and your Trial plan stores up to 1 GB.")]
    public void A_file_bigger_than_the_whole_plan_says_its_size_to_one_decimal(long bytes, string expected)
    {
        PlanGateViolation? over = PlanGate.CheckStorage(0, bytes, Trial);

        over.Should().NotBeNull();
        over!.Message.Should().StartWith(expected);
    }

    [Fact]
    public async Task The_usage_is_read_live_so_bytes_written_since_the_last_check_count()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        Project project = AddProject(db, org, 0);
        PlanGateService gate = new(db, new UploadsEntitlements(Trial));

        (await gate.CheckUploadAsync(org, Gb, CancellationToken.None)).Should().BeNull("the workspace is empty");

        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "filled",
            StorageKey = $"projects/{project.Id}/userFiles/filled",
            SizeBytes = Gb
        });
        await db.SaveChangesAsync();

        (await gate.CheckUploadAsync(org, 1, CancellationToken.None)).Should().NotBeNull(
            "the quota follows the database, not a snapshot from an earlier check");
    }

    [Fact]
    public async Task An_unlimited_plan_skips_the_usage_read()
    {
        using InferenceApiDbContext db = NewDb();
        UploadsEntitlements entitlements = new(new OrganizationEntitlementSet { Tier = "enterprise" });
        PlanGateService gate = new(db, entitlements);

        (await gate.CheckUploadAsync(Guid.NewGuid(), Gb * 100, CancellationToken.None)).Should().BeNull();
        entitlements.Reads.Should().Be(1);
    }
}
