#nullable enable
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Shared.Storage;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class PresignedUrlServiceTests
{
    private static IConfiguration Config(string? publicEndpoint) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MinIO:Endpoint"] = "http://garage:3900",
            ["MinIO:PublicEndpoint"] = publicEndpoint,
            ["MinIO:AccessKey"] = "AK",
            ["MinIO:SecretKey"] = "SK-SK-SK-SK-SK-SK-SK",
            ["MinIO:BucketName"] = "bkt",
            ["MinIO:Region"] = "garage"
        }).Build();

    private static PresignedUrlService RealSigner(string? publicEndpoint)
    {
        IConfiguration c = Config(publicEndpoint);
        return new PresignedUrlService(
            new Mock<IAmazonS3>().Object,
            S3ClientFactory.Create(c, S3ClientFactory.ResolvePublicEndpoint(c)),
            "bkt",
            S3ClientFactory.ResolvePublicEndpoint(c));
    }

    [Fact]
    public async Task Get_UsesPublicHost_NotInternalEndpoint()
    {
        Guid p = Guid.NewGuid();
        string url = await RealSigner("https://s3.example.com").CreateGetAsync(p, $"projects/{p}/a/b.mp4", TimeSpan.FromMinutes(10));
        url.Should().StartWith("https://s3.example.com/bkt/projects/");
        url.Should().NotContain("garage:3900");
        url.Should().Contain("X-Amz-Signature");
    }

    [Fact]
    public async Task Get_FallsBackToEndpoint_WhenPublicEndpointBlank()
    {
        Guid p = Guid.NewGuid();
        string url = await RealSigner("").CreateGetAsync(p, $"projects/{p}/x", TimeSpan.FromMinutes(1));
        url.Should().StartWith("http://garage:3900/bkt/");
    }

    [Fact]
    public async Task Put_TargetsRunnerStagingKey()
    {
        Guid job = Guid.NewGuid();
        PresignedPut put = await RealSigner("https://s3.example.com").CreatePutAsync(job, 2, "video/mp4", TimeSpan.FromMinutes(30));
        put.Key.Should().Be($"runner-staging/{job}/2");
        put.Url.Should().Contain($"/bkt/runner-staging/{job}/2?");
    }

    [Theory]
    [InlineData("projects/OTHER/file")]
    [InlineData("runner-staging/x/1")]
    [InlineData("projects/{p}/../other/file")]
    [InlineData("projects/{p}//file")]
    [InlineData("projects/{p}/")]
    [InlineData("")]
    public async Task Get_RejectsKeysOutsideProjectPrefix(string key)
    {
        Guid p = Guid.NewGuid();
        key = key.Replace("{p}", p.ToString());
        Func<Task> act = () => RealSigner(null).CreateGetAsync(p, key, TimeSpan.FromMinutes(1));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Ttl_IsCapped()
    {
        Guid p = Guid.NewGuid();
        PresignedUrlService s = RealSigner(null);
        await ((Func<Task>)(() => s.CreateGetAsync(p, $"projects/{p}/a", TimeSpan.FromMinutes(16)))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await ((Func<Task>)(() => s.CreatePutAsync(Guid.NewGuid(), 0, "x/y", TimeSpan.FromMinutes(31)))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await ((Func<Task>)(() => s.CreateGetAsync(p, $"projects/{p}/a", TimeSpan.Zero))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        // Exactly at the cap is allowed.
        await s.CreateGetAsync(p, $"projects/{p}/a", TimeSpan.FromMinutes(15));
        await s.CreatePutAsync(Guid.NewGuid(), 0, "x/y", TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void ForcePathStyle_DefaultsTrue_AndRegionIsApplied()
    {
        using IAmazonS3 c = S3ClientFactory.Create(Config(null), "http://garage:3900");
        ((AmazonS3Config)c.Config).ForcePathStyle.Should().BeTrue();
        c.Config.AuthenticationRegion.Should().Be("garage");
    }

    [Fact]
    public void AddReelBoltS3_RegistersKeyedPresignClientOnPublicHost()
    {
        ServiceCollection services = new();
        services.AddReelBoltS3(Config("https://s3.example.com"));
        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAmazonS3>().Config.ServiceURL.Should().StartWith("http://garage:3900");
        sp.GetRequiredKeyedService<IAmazonS3>("presign").Config.ServiceURL.Should().StartWith("https://s3.example.com");
        sp.GetRequiredService<IPresignedUrlService>().Should().NotBeNull();
    }

    private static (PresignedUrlService svc, Mock<IAmazonS3> s3) Promoter(long size)
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectMetadataAsync("bkt", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = size });
        s3.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CopyObjectResponse());
        s3.Setup(x => x.DeleteObjectAsync("bkt", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());
        return (new PresignedUrlService(s3.Object, new Mock<IAmazonS3>().Object, "bkt", "http://x"), s3);
    }

    [Fact]
    public async Task Promote_CopiesThenDeletesStaging_WhenWithinCap()
    {
        (PresignedUrlService svc, Mock<IAmazonS3> s3) = Promoter(100);
        long bytes = await svc.PromoteAsync("runner-staging/j/1", "projects/p/outputFiles/e/a.mp4", 100);
        bytes.Should().Be(100);
        s3.Verify(x => x.CopyObjectAsync(It.Is<CopyObjectRequest>(r =>
            r.SourceKey == "runner-staging/j/1" && r.DestinationKey == "projects/p/outputFiles/e/a.mp4"), It.IsAny<CancellationToken>()), Times.Once);
        s3.Verify(x => x.DeleteObjectAsync("bkt", "runner-staging/j/1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Promote_RejectsOversize_DeletesStaging_AndNeverCopies()
    {
        (PresignedUrlService svc, Mock<IAmazonS3> s3) = Promoter(101);
        Func<Task> act = () => svc.PromoteAsync("runner-staging/j/1", "projects/p/outputFiles/e/a.mp4", 100);
        (await act.Should().ThrowAsync<PresignedObjectTooLargeException>()).Which.ActualBytes.Should().Be(101);
        s3.Verify(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        s3.Verify(x => x.DeleteObjectAsync("bkt", "runner-staging/j/1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Promote_JudgesTheCapOnTheCopy_WhenStagingWasUnderTheCapButTheCopyIsOver()
    {
        // A re-PUT through the still-live staging URL between the first HEAD and the copy: the staging HEAD was fine.
        (PresignedUrlService svc, Mock<IAmazonS3> s3) = Promoter(50);
        s3.Setup(x => x.GetObjectMetadataAsync("bkt", "projects/p/outputFiles/e/a.mp4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 500 });
        Func<Task> act = () => svc.PromoteAsync("runner-staging/j/1", "projects/p/outputFiles/e/a.mp4", 100);
        (await act.Should().ThrowAsync<PresignedObjectTooLargeException>()).Which.ActualBytes.Should().Be(500);
        s3.Verify(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        s3.Verify(x => x.DeleteObjectAsync("bkt", "projects/p/outputFiles/e/a.mp4", It.IsAny<CancellationToken>()), Times.Once);
        s3.Verify(x => x.DeleteObjectAsync("bkt", "runner-staging/j/1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Promote_DeletesTheCopy_WhenTheCopysHeadThrows()
    {
        (PresignedUrlService svc, Mock<IAmazonS3> s3) = Promoter(50);
        InvalidOperationException failure = new("HEAD of the copy failed");
        s3.Setup(x => x.GetObjectMetadataAsync("bkt", "projects/p/outputFiles/e/a.mp4", It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        Func<Task> act = () => svc.PromoteAsync("runner-staging/j/1", "projects/p/outputFiles/e/a.mp4", 100);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        s3.Verify(x => x.DeleteObjectAsync("bkt", "projects/p/outputFiles/e/a.mp4", It.IsAny<CancellationToken>()), Times.Once,
            "an unmeasured copy is never kept");
    }

    [Theory]
    [InlineData("projects/p/a", "projects/p/b")]
    [InlineData("runner-staging/j/1", "runner-staging/j/2")]
    [InlineData("runner-staging/j/1", "other/b")]
    public async Task Promote_EnforcesPrefixes(string staging, string final)
    {
        (PresignedUrlService svc, Mock<IAmazonS3> s3) = Promoter(1);
        Func<Task> act = () => svc.PromoteAsync(staging, final, 10);
        await act.Should().ThrowAsync<ArgumentException>();
        s3.VerifyNoOtherCalls();
    }

    /// <summary>
    /// magic-blur-fetch-fix regression pin: the presigned GET a vendor downloads, signed exactly as the
    /// cloud signs it (R2: https account host, region <c>auto</c>, path style). An anonymous GET of this
    /// shape answered 206 against the prod bucket on 2026-11-02, so any later change to it (an SDK bump
    /// adding a checksum or x-id parameter, a signed header beyond <c>host</c>, virtual-host style, a
    /// region other than the configured one, http) must be deliberate and re-proved against R2.
    /// </summary>
    [Fact]
    public async Task Get_OnR2_IsAPlainPathStyleSigV4QueryUrl_SignedForHostOnly()
    {
        const string r2 = "https://0123456789abcdef0123456789abcdef.eu.r2.cloudflarestorage.com";
        IConfiguration c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MinIO:Endpoint"] = r2,
            ["MinIO:PublicEndpoint"] = r2,
            ["MinIO:AccessKey"] = "AKIDR2EXAMPLE",
            ["MinIO:SecretKey"] = "r2-secret-r2-secret-r2-secret",
            ["MinIO:BucketName"] = "reelbolt",
            ["MinIO:Region"] = "auto",
            ["MinIO:ForcePathStyle"] = "true"
        }).Build();
        PresignedUrlService svc = new(
            new Mock<IAmazonS3>().Object,
            S3ClientFactory.Create(c, S3ClientFactory.ResolvePublicEndpoint(c)),
            "reelbolt",
            S3ClientFactory.ResolvePublicEndpoint(c));
        Guid projectId = Guid.Parse("d5076778-245c-4ed8-9c3e-44698656eb03");
        string key = $"projects/{projectId}/userFiles/dd1abe6d-dc63-4350-8261-6e25a659f75c.mp4";

        Uri url = new(await svc.CreateGetAsync(projectId, key, TimeSpan.FromMinutes(10)));

        url.Scheme.Should().Be("https");
        url.Host.Should().Be("0123456789abcdef0123456789abcdef.eu.r2.cloudflarestorage.com", "path style keeps the bucket out of the host");
        url.AbsolutePath.Should().Be("/reelbolt/" + key);
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        foreach (string pair in url.Query.TrimStart('?').Split('&'))
        {
            string[] kv = pair.Split('=', 2);
            query[kv[0]] = Uri.UnescapeDataString(kv[1]);
        }

        query.Keys.Should().BeEquivalentTo(
            ["X-Amz-Algorithm", "X-Amz-Credential", "X-Amz-Date", "X-Amz-Expires", "X-Amz-SignedHeaders", "X-Amz-Signature"],
            "no checksum, x-id, security-token or other parameter a vendor's plain GET would have to satisfy");
        query["X-Amz-Algorithm"].Should().Be("AWS4-HMAC-SHA256");
        query["X-Amz-Credential"].Should().MatchRegex(@"^AKIDR2EXAMPLE/\d{8}/auto/s3/aws4_request$");
        query["X-Amz-SignedHeaders"].Should().Be("host", "a vendor sends no other header we could have signed");
        query["X-Amz-Expires"].Should().Be("600");
        query["X-Amz-Signature"].Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
