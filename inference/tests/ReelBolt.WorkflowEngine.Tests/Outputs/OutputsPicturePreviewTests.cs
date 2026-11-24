#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Inference.Api.Services.Storage;
using SkiaSharp;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>The <c>?w=</c> WebP preview and the ETag/Cache-Control headers on picture downloads.</summary>
public sealed class OutputsPicturePreviewTests
{
    private static byte[] Png(int size)
    {
        using SKBitmap bmp = new(size, size);
        Random rng = new(7);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                bmp.SetPixel(x, y, new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
        using SKImage img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static (OutputsController C, SurfaceWorld W, Guid Id, Mock<IAmazonS3> S3) Setup(string name, byte[] body, string etag = "abc")
    {
        SurfaceWorld w = new();
        var result = w.LaterResult;
        result.OutputStorageKey = $"projects/{w.Project.Id}/outputFiles/{name}";
        w.Db.SaveChanges();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = body.Length, ETag = $"\"{etag}\"" });
        s3.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(body) });
        Mock<IFileStorageService> storage = new();
        storage.SetupGet(s => s.BucketName).Returns("reelforge");
        OutputsController c = new(w.Db, w.User, w.Access(), s3.Object, storage.Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (c, w, result.Id, s3);
    }

    private static byte[] Read(IActionResult r)
    {
        using MemoryStream ms = new();
        r.Should().BeOfType<FileStreamResult>().Subject.FileStream.CopyTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1, 160)]
    [InlineData(160, 160)]
    [InlineData(161, 320)]
    [InlineData(600, 640)]
    [InlineData(1280, 1280)]
    [InlineData(1281, 1920)]
    [InlineData(1920, 1920)]
    [InlineData(1921, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(null, null)]
    public void SnapWidth_picks_the_next_allowed_width(int? asked, int? expected) =>
        ImageThumbnailer.SnapWidth(asked).Should().Be(expected);

    [Fact]
    public async Task Thumbnail_is_a_smaller_webp_with_cache_headers()
    {
        byte[] png = Png(1000);
        var (c, w, id, _) = Setup("pic.png", png, "t1");
        IActionResult r = await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "300");
        byte[] body = Read(r);
        r.Should().BeOfType<FileStreamResult>().Subject.ContentType.Should().Be("image/webp");
        body.Length.Should().BeLessThan(png.Length);
        using SKBitmap decoded = SKBitmap.Decode(body);
        decoded.Width.Should().Be(320);
        c.Response.Headers.ETag.ToString().Should().Be("\"t1-w320\"");
        c.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=86400");
        c.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline");
    }

    [Fact]
    public async Task Full_image_gets_etag_and_one_hour_cache()
    {
        byte[] png = Png(64);
        var (c, w, id, _) = Setup("pic.png", png, "f1");
        IActionResult r = await c.DownloadOutput(w.Project.Id, id, CancellationToken.None);
        Read(r).Should().Equal(png);
        c.Response.Headers.ETag.ToString().Should().Be("\"f1\"");
        c.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=3600");
    }

    [Fact]
    public async Task Narrower_source_is_served_as_the_original()
    {
        byte[] png = Png(100);
        var (c, w, id, _) = Setup("pic.png", png, "n1");
        IActionResult r = await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "640");
        Read(r).Should().Equal(png);
        r.Should().BeOfType<FileStreamResult>().Subject.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task Corrupt_image_falls_back_to_the_original()
    {
        byte[] junk = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
        var (c, w, id, _) = Setup("pic.png", junk, "c1");
        IActionResult r = await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "160");
        Read(r).Should().Equal(junk);
    }

    [Fact]
    public async Task Matching_If_None_Match_answers_304_without_fetching_the_body()
    {
        var (c, w, id, s3) = Setup("pic.png", Png(64), "m1");
        c.Request.Headers.IfNoneMatch = "\"m1-w320\"";
        IActionResult r = await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "320");
        r.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(304);
        s3.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Width_is_ignored_with_a_Range_header()
    {
        byte[] png = Png(1000);
        var (c, w, id, _) = Setup("pic.png", png, "r1");
        c.Request.Headers.Range = "bytes=0-9";
        IActionResult r = await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "160");
        r.Should().BeOfType<FileStreamResult>().Subject.ContentType.Should().Be("image/png");
        c.Response.StatusCode.Should().Be(206);
    }

    [Fact]
    public async Task Video_ignores_width_and_gets_no_cache_headers()
    {
        var (c, w, id, _) = Setup("clip.mp4", [1, 2, 3, 4], "v1");
        IActionResult r = await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "160");
        r.Should().BeOfType<FileStreamResult>().Subject.ContentType.Should().Be("video/mp4");
        c.Response.Headers.ContainsKey("ETag").Should().BeFalse();
        c.Response.Headers.ContainsKey("Cache-Control").Should().BeFalse();
    }

    private static byte[] BigSolidPng(int width, int height)
    {
        using SKBitmap bmp = new(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        bmp.Erase(SKColors.Black);
        using SKImage img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static async Task AssertSecondRequestSkipsFetch(byte[] body, string w)
    {
        var (c, wd, id, s3) = Setup("pic.png", body, "neg-" + Guid.NewGuid().ToString("N"));
        Read(await c.DownloadOutput(wd.Project.Id, id, CancellationToken.None, null, w)).Should().Equal(body);
        Read(await c.DownloadOutput(wd.Project.Id, id, CancellationToken.None, null, w)).Should().Equal(body);
        // One body read per request: the remembered "no preview" verdict means the second request streams the
        // original once and does not fetch it a second time for a decode attempt (that would be 3 reads).
        s3.Verify(s => s.GetObjectAsync(It.Is<GetObjectRequest>(r => r.Key.EndsWith("/pic.png")), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public Task Corrupt_image_is_fetched_only_once() =>
        AssertSecondRequestSkipsFetch(Enumerable.Range(0, 500).Select(i => (byte)i).ToArray(), "160");

    [Fact]
    public Task Narrower_image_is_fetched_only_once() => AssertSecondRequestSkipsFetch(Png(100), "640");

    [Fact]
    public Task Oversized_pixel_count_is_fetched_only_once() => AssertSecondRequestSkipsFetch(BigSolidPng(6001, 6000), "160");

    [Fact]
    public void Create_refuses_more_than_36_megapixels()
    {
        byte[] big = BigSolidPng(6001, 6000);
        ImageThumbnailer.Create("b", "k", "e-" + Guid.NewGuid().ToString("N"), 160, big).Should().BeNull();
        byte[] ok = BigSolidPng(6000, 6000);
        ImageThumbnailer.Create("b", "k", "e-" + Guid.NewGuid().ToString("N"), 160, ok).Should().NotBeNull();
    }

    [Fact]
    public async Task With_both_slots_held_the_original_is_served_without_hanging()
    {
        byte[] png = Png(1000);
        var (c, w, id, s3) = Setup("pic.png", png, "slot-" + Guid.NewGuid().ToString("N"));
        (await ImageThumbnailer.WaitForSlotAsync(CancellationToken.None)).Should().BeTrue();
        (await ImageThumbnailer.WaitForSlotAsync(CancellationToken.None)).Should().BeTrue();
        try
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            IActionResult r = await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "160");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
            Read(r).Should().Equal(png);
            r.Should().BeOfType<FileStreamResult>().Subject.ContentType.Should().Be("image/png");
        }
        finally
        {
            ImageThumbnailer.ReleaseSlot();
            ImageThumbnailer.ReleaseSlot();
        }
    }

    private static (ProjectFileMediaController C, SurfaceWorld W, Guid Id, Mock<IAmazonS3> S3) SetupFile(string mime, string name, byte[] body, string etag)
    {
        SurfaceWorld w = new();
        w.Upload.MimeType = mime;
        w.Upload.OriginalFileName = name;
        w.Db.SaveChanges();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = body.Length, ETag = $"\"{etag}\"" });
        s3.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(body) });
        ProjectFileMediaController c = new(w.Db, w.User, w.Access(), s3.Object, new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (c, w, w.Upload.Id, s3);
    }

    [Fact]
    public async Task Project_file_preview_is_a_smaller_webp_with_cache_headers()
    {
        byte[] png = Png(1000);
        var (c, w, id, _) = SetupFile("image/png", "pic.png", png, "pf1");
        IActionResult r = await c.Stream(w.Project.Id, id, CancellationToken.None, "300");
        byte[] body = Read(r);
        r.Should().BeOfType<FileStreamResult>().Subject.ContentType.Should().Be("image/webp");
        body.Length.Should().BeLessThan(png.Length);
        c.Response.Headers.ETag.ToString().Should().Be("\"pf1-w320\"");
        c.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=86400");
    }

    [Fact]
    public async Task Project_file_video_ignores_width_and_has_no_cache_headers()
    {
        var (c, w, id, _) = SetupFile("video/mp4", "clip.mp4", [1, 2, 3, 4], "pv1");
        IActionResult r = await c.Stream(w.Project.Id, id, CancellationToken.None, "160");
        r.Should().BeOfType<FileStreamResult>().Subject.ContentType.Should().Be("video/mp4");
        c.Response.Headers.ContainsKey("ETag").Should().BeFalse();
        c.Response.Headers.ContainsKey("Cache-Control").Should().BeFalse();
    }

    [Fact]
    public async Task Project_file_matching_If_None_Match_answers_304()
    {
        var (c, w, id, s3) = SetupFile("image/png", "pic.png", Png(64), "pm1");
        c.Request.Headers.IfNoneMatch = "\"pm1\"";
        IActionResult r = await c.Stream(w.Project.Id, id, CancellationToken.None);
        r.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(304);
        s3.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- stored previews, pre-warm, cleanup ---------------------------------------------------

    private static byte[] NoisePng(int width, int height)
    {
        using SKBitmap bmp = new(width, height);
        Random rng = new(11);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bmp.SetPixel(x, y, new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
        using SKImage img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private sealed class FakeStore
    {
        public Dictionary<string, (byte[] Bytes, string Etag)> Stored { get; } = [];
        public List<string> Puts { get; } = [];
        public List<string> Deleted { get; } = [];
        public int SourceReads { get; set; }
        public string Etag { get; set; }
        public bool Exists { get; set; } = true;
        public Action? AfterPut { get; set; }
        public Mock<IAmazonS3> S3 { get; } = new();

        public FakeStore(string sourceKey, byte[] body, string etag)
        {
            Etag = etag;
            S3.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), sourceKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, CancellationToken _) => Exists
                    ? new GetObjectMetadataResponse { ContentLength = body.Length, ETag = $"\"{Etag}\"" }
                    : throw new AmazonS3Exception("none") { ErrorCode = "NoSuchKey", StatusCode = System.Net.HttpStatusCode.NotFound });
            S3.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((GetObjectRequest r, CancellationToken _) =>
                {
                    if (r.Key == sourceKey)
                    {
                        SourceReads++;
                        return new GetObjectResponse { ResponseStream = new MemoryStream(body) };
                    }
                    if (!Stored.TryGetValue(r.Key, out var hit)) throw new AmazonS3Exception("none") { ErrorCode = "NoSuchKey", StatusCode = System.Net.HttpStatusCode.NotFound };
                    GetObjectResponse resp = new() { ResponseStream = new MemoryStream(hit.Bytes) };
                    resp.Metadata[ImagePreviewStore.EtagMetadata] = hit.Etag;
                    return resp;
                });
            S3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PutObjectRequest r, CancellationToken _) =>
                {
                    using MemoryStream ms = new();
                    r.InputStream.CopyTo(ms);
                    Stored[r.Key] = (ms.ToArray(), r.Metadata[ImagePreviewStore.EtagMetadata]);
                    Puts.Add(r.Key);
                    AfterPut?.Invoke();
                    return new PutObjectResponse();
                });
            S3.Setup(s => s.DeleteObjectsAsync(It.IsAny<DeleteObjectsRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DeleteObjectsRequest r, CancellationToken _) =>
                {
                    Deleted.AddRange(r.Objects.Select(o => o.Key));
                    foreach (var o in r.Objects) Stored.Remove(o.Key);
                    return new DeleteObjectsResponse();
                });
        }
    }

    private static OutputsController ControllerFor(SurfaceWorld w, IAmazonS3 s3)
    {
        Mock<IFileStorageService> storage = new();
        storage.SetupGet(s => s.BucketName).Returns("reelforge");
        return new OutputsController(w.Db, w.User, w.Access(), s3, storage.Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static (SurfaceWorld W, string Key, Guid Id) PictureWorld()
    {
        SurfaceWorld w = new();
        string key = $"projects/{w.Project.Id}/outputFiles/pic.png";
        w.LaterResult.OutputStorageKey = key;
        w.Db.SaveChanges();
        return (w, key, w.LaterResult.Id);
    }

    [Fact]
    public async Task First_view_decodes_and_writes_the_preview_through_once()
    {
        byte[] png = Png(800);
        var (w, key, id) = PictureWorld();
        FakeStore fake = new(key, png, "wt1");
        OutputsController c = ControllerFor(w, fake.S3.Object);
        Read(await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "320")).Length.Should().BeLessThan(png.Length);
        Read(await ControllerFor(w, fake.S3.Object).DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "320")).Should().NotBeEmpty();
        fake.Puts.Should().Equal(ImagePreviewStore.Key(w.Project.Id, key, 320));
        fake.SourceReads.Should().Be(1);
    }

    [Fact]
    public async Task Stored_preview_is_served_without_reading_the_source_or_decoding()
    {
        var (w, key, id) = PictureWorld();
        FakeStore fake = new(key, Png(800), "st1");
        byte[] marker = [9, 9, 9, 9];
        fake.Stored[ImagePreviewStore.Key(w.Project.Id, key, 320)] = (marker, "st1");
        OutputsController c = ControllerFor(w, fake.S3.Object);
        Read(await c.DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "320")).Should().Equal(marker);
        fake.SourceReads.Should().Be(0);
        fake.Puts.Should().BeEmpty();
        c.Response.Headers.ETag.ToString().Should().Be("\"st1-w320\"");
    }

    [Fact]
    public async Task Stored_preview_of_an_older_source_is_rebuilt_and_rewritten()
    {
        byte[] png = Png(800);
        var (w, key, id) = PictureWorld();
        FakeStore fake = new(key, png, "new1");
        fake.Stored[ImagePreviewStore.Key(w.Project.Id, key, 320)] = ([9, 9, 9, 9], "old1");
        Read(await ControllerFor(w, fake.S3.Object).DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "320"))
            .Length.Should().BeGreaterThan(4);
        fake.SourceReads.Should().Be(1);
        fake.Stored[ImagePreviewStore.Key(w.Project.Id, key, 320)].Etag.Should().Be("new1");
    }

    private static ConsumeContext<WorkflowStepCompleted> Ctx(Guid? projectId, string? key, string status = "Completed")
    {
        Mock<ConsumeContext<WorkflowStepCompleted>> ctx = new();
        ctx.SetupGet(x => x.Message).Returns(new WorkflowStepCompleted { ProjectId = projectId, OutputStorageKey = key, StepStatus = status });
        ctx.SetupGet(x => x.CancellationToken).Returns(CancellationToken.None);
        return ctx.Object;
    }

    private static ImagePreviewPrewarmConsumer Consumer(IAmazonS3 s3, SurfaceWorld w) => new(
        s3, new ConfigurationBuilder().Build(), w.Db, null, NullLogger<ImagePreviewPrewarmConsumer>.Instance);

    private static readonly int[] Three = [320, 480, 1920];

    [Fact]
    public async Task Prewarm_stores_the_three_previews_for_a_finished_picture()
    {
        var (w, key, _) = PictureWorld();
        Guid project = w.Project.Id;
        FakeStore fake = new(key, NoisePng(2100, 64), "pw1");
        await Consumer(fake.S3.Object, w).Consume(Ctx(project, key));
        fake.Puts.Should().BeEquivalentTo(Three.Select(x => ImagePreviewStore.Key(project, key, x)));
        // A second completion finds all three valid and writes nothing.
        fake.Puts.Clear();
        await Consumer(fake.S3.Object, w).Consume(Ctx(project, key));
        fake.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Prewarm_skips_widths_not_smaller_than_the_source()
    {
        var (w, key, _) = PictureWorld();
        FakeStore fake = new(key, NoisePng(400, 64), "pw2");
        await Consumer(fake.S3.Object, w).Consume(Ctx(w.Project.Id, key));
        fake.Puts.Should().Equal(ImagePreviewStore.Key(w.Project.Id, key, 320));
    }

    [Fact]
    public async Task Prewarm_ignores_non_image_failed_projectless_and_missing_project_steps()
    {
        var (w, key, _) = PictureWorld();
        Guid project = w.Project.Id;
        FakeStore fake = new(key, Png(64), "pw3");
        ImagePreviewPrewarmConsumer consumer = Consumer(fake.S3.Object, w);
        await consumer.Consume(Ctx(project, $"projects/{project}/outputFiles/clip.mp4"));
        await consumer.Consume(Ctx(project, key, "Failed"));
        await consumer.Consume(Ctx(null, key));
        Guid other = Guid.NewGuid();
        await consumer.Consume(Ctx(other, $"projects/{other}/outputFiles/pic.png"));
        fake.S3.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Prewarm_is_skipped_for_a_content_quarantined_project()
    {
        var (w, key, _) = PictureWorld();
        w.Project.ContentQuarantinedAt = DateTime.UtcNow;
        w.Db.SaveChanges();
        FakeStore fake = new(key, NoisePng(2100, 64), "q1");
        await Consumer(fake.S3.Object, w).Consume(Ctx(w.Project.Id, key));
        fake.S3.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Prewarm_is_skipped_for_a_held_output_but_not_for_a_later_one()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        string heldKey = $"projects/{w.Project.Id}/outputFiles/earlier.png";
        string freeKey = $"projects/{w.Project.Id}/outputFiles/later.png";
        w.EarlierResult.OutputStorageKey = heldKey;
        w.EarlierFile.StorageKey = heldKey;
        w.LaterResult.OutputStorageKey = freeKey;
        w.LaterFile.StorageKey = freeKey;
        w.Db.SaveChanges();
        FakeStore held = new(heldKey, NoisePng(2100, 64), "h1");
        await Consumer(held.S3.Object, w).Consume(Ctx(w.Project.Id, heldKey));
        held.S3.Invocations.Should().BeEmpty();
        FakeStore free = new(freeKey, NoisePng(2100, 64), "h2");
        await Consumer(free.S3.Object, w).Consume(Ctx(w.Project.Id, freeKey));
        free.Puts.Should().HaveCount(3);
    }

    [Fact]
    public async Task Prewarm_is_skipped_for_a_quarantined_file_row()
    {
        var (w, key, _) = PictureWorld();
        w.LaterFile.StorageKey = key;
        w.LaterFile.CsamStatus = CsamScanStatus.Quarantined;
        w.Db.SaveChanges();
        FakeStore fake = new(key, NoisePng(2100, 64), "q2");
        await Consumer(fake.S3.Object, w).Consume(Ctx(w.Project.Id, key));
        fake.S3.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Prewarm_deletes_what_it_wrote_when_the_source_vanishes_meanwhile()
    {
        var (w, key, _) = PictureWorld();
        FakeStore fake = new(key, NoisePng(2100, 64), "v1");
        fake.AfterPut = () => fake.Exists = false;
        await Consumer(fake.S3.Object, w).Consume(Ctx(w.Project.Id, key));
        fake.Stored.Should().BeEmpty();
        fake.Deleted.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Prewarm_deletes_what_it_wrote_when_the_source_changes_meanwhile()
    {
        var (w, key, _) = PictureWorld();
        FakeStore fake = new(key, NoisePng(2100, 64), "v2");
        fake.AfterPut = () => fake.Etag = "v2-changed";
        await Consumer(fake.S3.Object, w).Consume(Ctx(w.Project.Id, key));
        fake.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Prewarm_deletes_what_it_wrote_when_the_output_becomes_held_meanwhile()
    {
        var (w, key, _) = PictureWorld();
        FakeStore fake = new(key, NoisePng(2100, 64), "v3");
        fake.AfterPut = () =>
        {
            w.Project.ContentQuarantinedAt = DateTime.UtcNow;
            w.Db.SaveChanges();
        };
        await Consumer(fake.S3.Object, w).Consume(Ctx(w.Project.Id, key));
        fake.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_through_deletes_the_preview_when_the_source_vanishes_during_the_request()
    {
        var (w, key, id) = PictureWorld();
        FakeStore fake = new(key, Png(800), "wv1");
        fake.AfterPut = () => fake.Exists = false;
        await ControllerFor(w, fake.S3.Object).DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "320");
        fake.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_through_deletes_the_preview_when_the_source_changes_during_the_request()
    {
        var (w, key, id) = PictureWorld();
        FakeStore fake = new(key, Png(800), "wc1");
        fake.AfterPut = () => fake.Etag = "wc1-changed";
        await ControllerFor(w, fake.S3.Object).DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "320");
        fake.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Stored_preview_is_not_served_once_the_source_is_gone()
    {
        var (w, key, id) = PictureWorld();
        FakeStore fake = new(key, Png(800), "gone1");
        fake.Stored[ImagePreviewStore.Key(w.Project.Id, key, 320)] = ([9, 9, 9, 9], "gone1");
        fake.Exists = false;
        IActionResult r = await ControllerFor(w, fake.S3.Object).DownloadOutput(w.Project.Id, id, CancellationToken.None, null, "320");
        r.Should().BeOfType<NotFoundObjectResult>();
        fake.S3.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Held_output_is_refused_and_its_stored_preview_is_not_read()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        string key = $"projects/{w.Project.Id}/outputFiles/earlier.png";
        w.EarlierResult.OutputStorageKey = key;
        w.EarlierFile.StorageKey = key;
        w.Db.SaveChanges();
        FakeStore fake = new(key, Png(800), "hold1");
        fake.Stored[ImagePreviewStore.Key(w.Project.Id, key, 320)] = ([9, 9, 9, 9], "hold1");
        IActionResult r = await ControllerFor(w, fake.S3.Object).DownloadOutput(w.Project.Id, w.EarlierResult.Id, CancellationToken.None, null, "320");
        r.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        fake.S3.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quarantined_project_file_is_not_found_and_its_stored_preview_is_not_read(bool csam)
    {
        var (c, w, id, s3) = SetupFile("image/png", "pic.png", Png(800), "pq1");
        if (csam) w.Upload.CsamStatus = CsamScanStatus.Quarantined;
        else w.Upload.ModerationStatus = FileModerationStatus.Quarantined;
        w.Db.SaveChanges();
        IActionResult r = await c.Stream(w.Project.Id, id, CancellationToken.None, "320");
        r.Should().BeOfType<NotFoundResult>();
        s3.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_picture_removes_its_stored_previews()
    {
        Guid project = Guid.NewGuid();
        string key = $"projects/{project}/outputFiles/pic.png";
        FakeStore fake = new(key, Png(64), "d1");
        S3FileStorageService storage = new(fake.S3.Object, new ConfigurationBuilder().Build(), NullLogger<S3FileStorageService>.Instance);
        await storage.DeleteAsync(project, key, CancellationToken.None);
        fake.Deleted.Should().BeEquivalentTo(ImageThumbnailer.Widths.Select(x => ImagePreviewStore.Key(project, key, x)));
        fake.Deleted.Clear();
        await storage.DeleteManyAsync(project, [key, $"projects/{project}/outputFiles/clip.mp4"], CancellationToken.None);
        fake.Deleted.Should().Contain(ImagePreviewStore.Key(project, key, 1920));
        fake.Deleted.Count(k => k.Contains("/previews/")).Should().Be(ImageThumbnailer.Widths.Length);
    }
}
