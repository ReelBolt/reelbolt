#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// Issue 8: the startup pass that re-types <c>.ts</c> rows stored as video before the write paths
/// read their bytes. Only the MIME changes, only when the bytes prove it, and a second pass is a no-op.
/// </summary>
public class TransportStreamMimeBackfillTests
{
    private sealed record Fixture(InferenceApiDbContext Db, InMemoryFileStorage Storage, TransportStreamMimeBackfill Backfill, Guid ProjectId);

    private static Fixture New()
    {
        InferenceApiDbContext db = GitHubTestDb.New(new TestTenant());
        InMemoryFileStorage storage = new();
        return new Fixture(db, storage, new TransportStreamMimeBackfill(db, storage, NullLogger<TransportStreamMimeBackfill>.Instance), Guid.NewGuid());
    }

    private static ProjectFile Add(Fixture f, string name, string mime, byte[]? bytes, CsamScanStatus csam = CsamScanStatus.NotScanned)
    {
        ProjectFile row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = f.ProjectId,
            OriginalFileName = name,
            OriginalPath = name,
            Category = "userFiles",
            StorageKey = $"projects/{f.ProjectId}/userFiles/{Guid.NewGuid():N}-{name}",
            MimeType = mime,
            SizeBytes = bytes?.Length ?? 0,
            SummaryStatus = SummaryStatus.Done,
            IndexingStatus = FileIndexingStatus.NotIndexed,
            CsamStatus = csam,
            UploadedAt = DateTime.UtcNow,
        };
        if (bytes is not null)
            f.Storage.Objects[row.StorageKey] = bytes;
        f.Db.ProjectFiles.Add(row);
        f.Db.SaveChanges();
        return row;
    }

    private static string MimeOf(Fixture f, Guid id) => f.Db.ProjectFiles.AsNoTracking().IgnoreQueryFilters().Single(r => r.Id == id).MimeType;

    [Fact]
    public async Task Typescript_rows_stored_as_video_are_re_typed_and_nothing_else_changes()
    {
        Fixture f = New();
        ProjectFile proxy = Add(f, "proxy.ts", "video/vnd.dlna.mpeg-tts", TransportStreamMimeSnifferTests.TypeScript());
        ProjectFile config = Add(f, "vitest.config.mts", "video/mp2t", TransportStreamMimeSnifferTests.TypeScript());
        ProjectFile stream = Add(f, "broadcast.ts", "video/vnd.dlna.mpeg-tts", TransportStreamMimeSnifferTests.MpegTs());
        ProjectFile mp4 = Add(f, "clip.mp4", "video/mp4", [0x00, 0x00, 0x00, 0x18]);

        TransportStreamMimeBackfillResult result = await f.Backfill.RunOnceAsync(batchSize: 1, CancellationToken.None);

        result.Should().Be(new TransportStreamMimeBackfillResult(Examined: 3, Retyped: 2, Unreadable: 0));
        MimeOf(f, proxy.Id).Should().Be("text/plain");
        MimeOf(f, config.Id).Should().Be("text/plain");
        MimeOf(f, stream.Id).Should().Be("video/vnd.dlna.mpeg-tts");
        MimeOf(f, mp4.Id).Should().Be("video/mp4");
        f.Db.ProjectFiles.AsNoTracking().IgnoreQueryFilters().Single(r => r.Id == proxy.Id).SummaryStatus
            .Should().Be(SummaryStatus.Done, "the pass changes the type only and queues no model call");
    }

    [Fact]
    public async Task A_second_pass_re_types_nothing()
    {
        Fixture f = New();
        Add(f, "paint.ts", "video/vnd.dlna.mpeg-tts", TransportStreamMimeSnifferTests.TypeScript());

        await f.Backfill.RunOnceAsync(batchSize: 10, CancellationToken.None);
        TransportStreamMimeBackfillResult second = await f.Backfill.RunOnceAsync(batchSize: 10, CancellationToken.None);

        second.Should().Be(new TransportStreamMimeBackfillResult(0, 0, 0));
    }

    [Fact]
    public async Task A_quarantined_row_is_never_read_and_a_missing_object_is_left_alone()
    {
        Fixture f = New();
        ProjectFile quarantined = Add(f, "a.ts", "video/mp2t", TransportStreamMimeSnifferTests.TypeScript(), CsamScanStatus.Quarantined);
        ProjectFile missing = Add(f, "b.ts", "video/mp2t", bytes: null);

        TransportStreamMimeBackfillResult result = await f.Backfill.RunOnceAsync(batchSize: 10, CancellationToken.None);

        result.Should().Be(new TransportStreamMimeBackfillResult(Examined: 1, Retyped: 0, Unreadable: 1));
        MimeOf(f, quarantined.Id).Should().Be("video/mp2t");
        MimeOf(f, missing.Id).Should().Be("video/mp2t");
    }
}
