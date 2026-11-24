using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

public class StorageUsageSamplerTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static (StorageUsageSampler sampler, IServiceProvider sp, Mock<IAmazonS3> s3) Build()
    {
        string dbName = Guid.NewGuid().ToString();
        var s3 = new Mock<IAmazonS3>();
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddSingleton(s3.Object);
        IServiceProvider sp = services.BuildServiceProvider();
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MinIO:BucketName"] = "b"
        }).Build();
        var sampler = new StorageUsageSampler(sp.GetRequiredService<IServiceScopeFactory>(), cfg, NullLogger<StorageUsageSampler>.Instance);
        return (sampler, sp, s3);
    }

    private static S3Object Obj(long size) => new() { Size = size };

    [Fact]
    public async Task Samples_each_org_with_pagination_and_is_idempotent_within_a_day()
    {
        var (sampler, sp, s3) = Build();
        Guid orgA = Guid.NewGuid(), orgB = Guid.NewGuid();
        Guid pA1 = Guid.NewGuid(), pA2 = Guid.NewGuid(), pB = Guid.NewGuid();
        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.AddRange(
                new Project { Id = pA1, Name = "a1", OrganizationId = orgA },
                new Project { Id = pA2, Name = "a2", OrganizationId = orgA },
                new Project { Id = pB, Name = "b", OrganizationId = orgB });
            await db.SaveChangesAsync();
        }

        // pA1 spans two pages (1 GB + 1 GB), pA2 is 2 GB, pB is 512 MB.
        s3.Setup(c => c.ListObjectsV2Async(It.Is<ListObjectsV2Request>(r => r.Prefix == $"projects/{pA1}/" && r.ContinuationToken == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response { S3Objects = [Obj(Gb)], IsTruncated = true, NextContinuationToken = "t" });
        s3.Setup(c => c.ListObjectsV2Async(It.Is<ListObjectsV2Request>(r => r.Prefix == $"projects/{pA1}/" && r.ContinuationToken == "t"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response { S3Objects = [Obj(Gb)], IsTruncated = false });
        s3.Setup(c => c.ListObjectsV2Async(It.Is<ListObjectsV2Request>(r => r.Prefix == $"projects/{pA2}/"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response { S3Objects = [Obj(Gb), Obj(Gb)], IsTruncated = false });
        s3.Setup(c => c.ListObjectsV2Async(It.Is<ListObjectsV2Request>(r => r.Prefix == $"projects/{pB}/"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response { S3Objects = [Obj(Gb / 2)], IsTruncated = false });

        var now = new DateTime(2026, 10, 6, 2, 0, 0, DateTimeKind.Utc);
        (await sampler.SampleOnceAsync(now, CancellationToken.None)).Should().Be(2);
        (await sampler.SampleOnceAsync(now.AddHours(3), CancellationToken.None)).Should().Be(0);

        using IServiceScope check = sp.CreateScope();
        List<UsageEvent> rows = await check.ServiceProvider.GetRequiredService<InferenceApiDbContext>().UsageEvents.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.OrganizationId == orgA).Quantity.Should().Be(4m);
        rows.Single(r => r.OrganizationId == orgB).Quantity.Should().Be(0.5m);
        rows.Should().OnlyContain(r => r.Kind == UsageKind.StorageGbDay && r.BillingMode == UsageBillingMode.Platform);
        rows.Select(r => r.IdempotencyKey).Should().Contain($"storage:{orgA}:2026-10-06");

        // A new day writes new rows.
        (await sampler.SampleOnceAsync(now.AddDays(1), CancellationToken.None)).Should().Be(2);
    }
}
