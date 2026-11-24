#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Services.Archives;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Archives;

public class ArchiveUnpackProcessorTests
{
    private static byte[] B(string s) => UnpackHarness.Bytes(s);

    [Fact]
    public async Task Happy_path_writes_files_under_the_zip_name_strips_one_top_folder_and_removes_the_zip()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip("My App.zip", directory: "uploads");
        ProjectArchiveUnpack unpack = h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Ok(
            new Dictionary<string, int> { ["EXCLUDED_DEPENDENCIES"] = 3 },
            ("app/src/index.ts", B("export {}")), ("app/README.md", B("# hi")), ("app/logo.png", [1, 2, 3]), ("app/Dockerfile", B("FROM x")));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Done);
        done.TargetDirectory.Should().Be("uploads/My App");
        done.FilesCreated.Should().Be(4);
        done.BytesCreated.Should().Be(9 + 4 + 3 + 6);
        done.SourceFileId.Should().BeNull();
        done.SkippedJson.Should().Contain("EXCLUDED_DEPENDENCIES");

        List<ProjectFile> rows = await h.Db.ProjectFiles.AsNoTracking().Where(f => f.SourceKind == "zip").ToListAsync();
        rows.Select(r => r.OriginalPath).Should().BeEquivalentTo(
            "uploads/My App/src/index.ts", "uploads/My App/README.md", "uploads/My App/logo.png", "uploads/My App/Dockerfile");
        rows.Should().OnlyContain(r => r.SourceRevision!.StartsWith(unpack.Id.ToString() + ":") && r.Category == "userFiles");
        rows.Should().OnlyContain(r => r.CsamStatus == CsamScanStatus.Pending && r.ModerationStatus == FileModerationStatus.Unmoderated);
        rows.Should().OnlyContain(r => r.SummaryStatus == SummaryStatus.Done && r.IndexingStatus == FileIndexingStatus.NotIndexed);
        rows.Single(r => r.OriginalFileName == "index.ts").MimeType.Should().StartWith("text/");
        rows.Single(r => r.OriginalFileName == "README.md").MimeType.Should().StartWith("text/");
        rows.Single(r => r.OriginalFileName == "Dockerfile").MimeType.Should().Be("text/plain");
        rows.Single(r => r.OriginalFileName == "logo.png").MimeType.Should().Be("image/png");

        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeFalse("the zip goes after a fully successful unpack");
        h.Store.DeletedKeys.Should().Contain(zip.StorageKey);
        h.Publish.Verify(p => p.Publish(It.Is<ProjectFileIndexingRequested>(e => e.FileId == zip.Id && e.Operation == "Delete"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_taken_folder_gets_a_numbered_name_and_mixed_top_level_is_kept()
    {
        UnpackHarness h = new();
        h.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            OriginalFileName = "a.txt",
            OriginalPath = "app/a.txt",
            DirectoryPath = "app",
            StorageKey = "k",
            MimeType = "text/plain"
        });
        h.Db.SaveChanges();
        ProjectFile zip = h.AddZip("app.zip");
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("one/a.js", B("1")), ("two/b.js", B("2")));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.TargetDirectory.Should().Be("app (2)");
        (await h.Db.ProjectFiles.AsNoTracking().Where(f => f.SourceKind == "zip").Select(f => f.OriginalPath).ToListAsync())
            .Should().BeEquivalentTo("app (2)/one/a.js", "app (2)/two/b.js");
    }

    [Fact]
    public async Task A_path_that_fails_the_name_rules_is_skipped_never_written()
    {
        UnpackHarness h = new();
        h.AddUnpack(h.AddZip());
        string longName = new string('x', 300) + ".js";
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("ok.js", B("1")), (longName, B("2")), ("bad\u0001name.js", B("3")));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Done);
        done.FilesCreated.Should().Be(1);
        done.SkippedJson.Should().Contain("\"PATH_INVALID\":2");
        h.Store.Objects.Keys.Count(k => k.Contains("/zip") || k.EndsWith(".js")).Should().Be(1);
    }

    [Fact]
    public async Task Quota_refusal_on_the_net_total_writes_nothing_and_keeps_the_zip()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip(size: 1000);
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("a.js", new byte[5000]), ("b.js", new byte[500]));
        long? asked = null;
        h.PlanGate.Setup(p => p.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, long, CancellationToken>((_, n, _) => asked ??= n)
            .ReturnsAsync(new PlanGateViolation("STORAGE_LIMIT", "storage", "full", 413));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        done.FailureCode.Should().Be("STORAGE_QUOTA_EXCEEDED");
        asked.Should().Be(5500 - 1000, "the zip is deleted on success, so only the net growth is checked");
        (await h.Db.ProjectFiles.CountAsync(f => f.SourceKind == "zip")).Should().Be(0);
        h.Store.Objects.Keys.Should().ContainSingle(k => k == zip.StorageKey);
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_net_shrink_is_not_checked_against_the_plan()
    {
        UnpackHarness h = new();
        h.AddUnpack(h.AddZip(size: 1000));
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("a.js", new byte[100]));

        (await h.RunNextAsync())!.Status.Should().Be(ArchiveUnpackRunStatus.Done);
        h.PlanGate.Verify(p => p.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failure_while_writing_removes_what_was_written_and_keeps_the_zip()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("a.js", B("1")), ("b.js", B("2")), ("c.js", B("3")));
        h.Store.FailUploadAfter = 2;

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        done.FailureCode.Should().Be("WRITE_FAILED");
        done.FilesCreated.Should().Be(0);
        (await h.Db.ProjectFiles.CountAsync(f => f.SourceKind == "zip")).Should().Be(0);
        h.Store.Objects.Keys.Should().BeEquivalentTo([zip.StorageKey], "the object written before the failure is removed too");
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_after_a_batch_was_saved_deletes_the_saved_rows_and_objects()
    {
        UnpackHarness h = new();
        h.AddUnpack(h.AddZip());
        int total = ArchiveUnpackProcessor.BatchSize + 5;
        h.Gateway.Next = () => ScriptedGateway.Ok(null, [.. Enumerable.Range(0, total).Select(i => ($"f{i}.js", B("x")))]);
        h.Store.FailUploadAfter = ArchiveUnpackProcessor.BatchSize + 2;

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        (await h.Db.ProjectFiles.CountAsync(f => f.SourceKind == "zip")).Should().Be(0);
        h.Store.Objects.Keys.Should().HaveCount(1, "only the zip's own object is left");
        h.Db.StorageRetentionMarks.Should().BeEmpty("every mark is dropped once its object is gone");
    }

    [Fact]
    public async Task Busy_goes_back_to_pending_with_the_workers_retry_after()
    {
        UnpackHarness h = new();
        h.AddUnpack(h.AddZip());
        h.Gateway.Next = () => ScriptedGateway.Status(ArchiveUnpackStatus.Busy, 7);

        ProjectArchiveUnpack? row = await h.RunNextAsync();

        row!.Status.Should().Be(ArchiveUnpackRunStatus.Pending);
        row.NextAttemptAt.Should().Be(h.Now.AddSeconds(7));
        row.Attempts.Should().Be(0, "a busy worker is not a failed attempt");
        (await h.Processor().ClaimNextAsync(CancellationToken.None)).Should().BeNull("it is not due yet");
    }

    [Theory]
    [InlineData(ArchiveUnpackStatus.Unavailable, "WORKER_UNAVAILABLE")]
    [InlineData(ArchiveUnpackStatus.BadResponse, "WORKER_BAD_RESPONSE")]
    public async Task An_unavailable_worker_is_retried_with_backoff_then_fails(ArchiveUnpackStatus status, string code)
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Status(status);

        ProjectArchiveUnpack? row = null;
        for (int attempt = 1; attempt < ArchiveUnpackProcessor.MaxAttempts; attempt++)
        {
            row = await h.RunNextAsync();
            row!.Status.Should().Be(ArchiveUnpackRunStatus.Pending);
            row.Attempts.Should().Be(attempt);
            row.NextAttemptAt.Should().BeAfter(h.Now);
            h.Now = row.NextAttemptAt!.Value.AddSeconds(1);
        }

        row = await h.RunNextAsync();
        row!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        row.FailureCode.Should().Be(code);
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeTrue("a failed unpack keeps the zip");
    }

    [Fact]
    public async Task A_stale_running_claim_is_cleaned_up_and_run_again()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        ProjectArchiveUnpack unpack = h.AddUnpack(zip, ArchiveUnpackRunStatus.Running);
        unpack.ClaimedAt = h.Now - ArchiveUnpackProcessor.StaleClaim - TimeSpan.FromMinutes(1);
        unpack.Attempts = 1;
        string leftoverKey = $"projects/{h.Project.Id}/userFiles/old/leftover.js";
        h.Store.Objects[leftoverKey] = [1];
        h.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            OriginalFileName = "leftover.js",
            OriginalPath = "old/leftover.js",
            DirectoryPath = "old",
            StorageKey = leftoverKey,
            MimeType = "text/plain",
            SourceKind = "zip",
            SourceRevision = unpack.Id.ToString()
        });
        h.Db.SaveChanges();
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("a.js", B("1")));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Done);
        done.Attempts.Should().Be(2);
        h.Store.DeletedKeys.Should().Contain(leftoverKey);
        (await h.Db.ProjectFiles.Where(f => f.SourceKind == "zip").Select(f => f.OriginalFileName).ToListAsync()).Should().Equal("a.js");
    }

    [Fact]
    public async Task A_fresh_running_claim_is_left_alone()
    {
        UnpackHarness h = new();
        ProjectArchiveUnpack unpack = h.AddUnpack(h.AddZip(), ArchiveUnpackRunStatus.Running);
        unpack.ClaimedAt = h.Now - TimeSpan.FromMinutes(1);
        h.Db.SaveChanges();

        (await h.Processor().ClaimNextAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_stale_claim_that_used_up_its_attempts_fails_as_interrupted()
    {
        UnpackHarness h = new();
        ProjectArchiveUnpack unpack = h.AddUnpack(h.AddZip(), ArchiveUnpackRunStatus.Running);
        unpack.ClaimedAt = h.Now - TimeSpan.FromHours(1);
        unpack.Attempts = ArchiveUnpackProcessor.MaxAttempts - 1;
        h.Db.SaveChanges();

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        done.FailureCode.Should().Be("UNPACK_INTERRUPTED");
        h.Gateway.Opened.Should().Be(0);
    }

    [Fact]
    public async Task Two_claims_of_one_row_cannot_both_win()
    {
        UnpackHarness h = new();
        h.AddUnpack(h.AddZip());

        Guid? first = await h.Processor().ClaimNextAsync(CancellationToken.None);
        Guid? second = await h.Processor().ClaimNextAsync(CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Theory]
    [InlineData(ArchiveUnpackStatus.NotZip, "NOT_A_ZIP")]
    [InlineData(ArchiveUnpackStatus.TooLarge, "ARCHIVE_TOO_LARGE")]
    public async Task Worker_refusals_fail_with_their_code_and_keep_the_zip(ArchiveUnpackStatus status, string code)
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Status(status);

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.FailureCode.Should().Be(code);
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task An_aborted_archive_fails_with_the_abort_reason()
    {
        UnpackHarness h = new();
        h.AddUnpack(h.AddZip());
        h.Gateway.Next = () => ScriptedGateway.Aborted("BOMB_SUSPECTED");

        (await h.RunNextAsync())!.FailureCode.Should().Be("BOMB_SUSPECTED");
    }

    [Fact]
    public async Task An_archive_over_the_cap_is_refused_without_calling_the_worker()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        zip.SizeBytes = 65L * 1024 * 1024;
        h.Db.SaveChanges();
        h.AddUnpack(zip);

        (await h.RunNextAsync())!.FailureCode.Should().Be("ARCHIVE_TOO_LARGE");
        h.Gateway.Opened.Should().Be(0);
    }

    [Fact]
    public async Task Nothing_to_unpack_fails_with_the_skipped_counts_and_keeps_the_zip()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Ok(new Dictionary<string, int> { ["EXCLUDED_DEPENDENCIES"] = 40 });

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.FailureCode.Should().Be("NOTHING_TO_UNPACK");
        done.SkippedJson.Should().Contain("\"EXCLUDED_DEPENDENCIES\":40");
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_vanished_or_quarantined_zip_fails_as_source_gone()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.AddUnpack(zip);
        zip.CsamStatus = CsamScanStatus.Quarantined;
        h.Db.SaveChanges();

        (await h.RunNextAsync())!.FailureCode.Should().Be("SOURCE_GONE");
        h.Gateway.Opened.Should().Be(0);
    }

    [Fact]
    public async Task A_media_file_gets_its_derivative_request()
    {
        UnpackHarness h = new();
        h.AddUnpack(h.AddZip());
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("clip.mp4", [0, 0, 0]));

        await h.RunNextAsync();

        h.Publish.Verify(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("a.ts", "text/")]
    [InlineData("a.tsx", "text/")]
    [InlineData("a.js", "text/")]
    [InlineData("a.jsx", "text/")]
    [InlineData("a.mjs", "text/")]
    [InlineData("a.cjs", "text/")]
    [InlineData("a.vue", "text/")]
    [InlineData("a.svelte", "text/")]
    [InlineData("a.py", "text/")]
    [InlineData("a.go", "text/")]
    [InlineData("a.rb", "text/")]
    [InlineData("a.java", "text/")]
    [InlineData("a.cs", "text/")]
    [InlineData("a.php", "text/")]
    [InlineData("a.css", "text/")]
    [InlineData("a.scss", "text/")]
    [InlineData("a.html", "text/")]
    [InlineData("a.md", "text/")]
    [InlineData("a.json", "text/")]
    [InlineData("a.yml", "text/")]
    [InlineData("a.yaml", "text/")]
    [InlineData("a.toml", "text/")]
    [InlineData(".env.example", "text/")]
    [InlineData("Makefile", "text/")]
    [InlineData("a.mp3", "audio/")]
    [InlineData("a.png", "image/")]
    [InlineData("a.bin", "application/octet-stream")]
    public void Common_source_extensions_resolve_to_a_code_mime(string name, string prefix) =>
        ArchiveUnpackPaths.ResolveMimeType(name).Should().StartWith(prefix);

    [Fact]
    public void Folder_names_are_sanitised_and_a_single_top_folder_is_found()
    {
        ArchiveUnpackPaths.SanitizeFolderName("a/b\\My App.v2.zip").Should().Be("My App.v2");
        ArchiveUnpackPaths.SanitizeFolderName("..zip").Should().Be("archive");
        ArchiveUnpackPaths.SharedTopFolder(["a/x.js", "a/b/y.js"]).Should().Be("a");
        ArchiveUnpackPaths.SharedTopFolder(["a/x.js", "readme.md"]).Should().BeNull();
        ArchiveUnpackPaths.SharedTopFolder(["a/x.js", "b/y.js"]).Should().BeNull();
        ArchiveUnpackPaths.TryBuildPath("t", "../evil.js", null, out _).Should().BeFalse();
        ArchiveUnpackPaths.TryBuildPath("t", "a/../../evil.js", null, out _).Should().BeFalse();
        ArchiveUnpackPaths.TryBuildPath("t", "/abs.js", null, out string abs).Should().BeTrue();
        abs.Should().Be("t/abs.js");
    }
}
