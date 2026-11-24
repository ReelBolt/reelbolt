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
using ReelBolt.Inference.Api.Services.Archives;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Archives;

/// <summary>The security and QA review findings on the unpack pipeline: scans before derivatives, claim fencing, replace/delete mid-run.</summary>
public class ArchiveUnpackReviewTests
{
    private static byte[] B(string s) => UnpackHarness.Bytes(s);

    private static Mock<ICsmaHashMatcher> Matcher(Func<ProjectFile, CsamScanStatus> verdict)
    {
        Mock<ICsmaHashMatcher> m = new();
        m.Setup(x => x.ScanAsync(It.IsAny<ProjectFile>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectFile f, Guid? _, CancellationToken _) =>
            {
                CsamScanStatus status = verdict(f);
                f.CsamStatus = status;
                return new CsamScanOutcome(status, null, null, status == CsamScanStatus.Quarantined, null);
            });
        return m;
    }

    [Fact]
    public async Task Every_extracted_file_is_scanned_and_a_quarantined_one_gets_no_derivatives()
    {
        UnpackHarness h = new();
        Mock<ICsmaHashMatcher> matcher = Matcher(f => f.OriginalFileName == "bad.mp4" ? CsamScanStatus.Quarantined : CsamScanStatus.Clean);
        h.Csam = matcher.Object;
        h.AddUnpack(h.AddZip());
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("bad.mp4", [1, 2]), ("good.mp4", [3, 4]), ("a.ts", B("x")));
        List<Guid> derived = [];
        h.Publish.Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((e, _) => derived.Add(e.SourceProjectFileId!.Value))
            .Returns(Task.CompletedTask);

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed, "a held extracted file keeps the zip and fails the unpack");
        matcher.Verify(x => x.ScanAsync(It.IsAny<ProjectFile>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Exactly(3), "each extracted file, media or not");
        Dictionary<string, ProjectFile> rows = await h.Db.ProjectFiles.AsNoTracking().Where(f => f.SourceKind == "zip").ToDictionaryAsync(f => f.OriginalFileName);
        rows["bad.mp4"].CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        rows.Keys.Should().Equal("bad.mp4");
        derived.Should().NotContain(rows["bad.mp4"].Id, "a quarantined file gets no derivatives");
    }

    [Fact]
    public async Task A_zip_with_an_unsettled_verdict_is_scanned_first_and_a_quarantine_never_extracts()
    {
        UnpackHarness h = new();
        h.Csam = Matcher(_ => CsamScanStatus.Quarantined).Object;
        ProjectFile zip = h.AddZip();
        zip.CsamStatus = CsamScanStatus.Pending;
        h.Db.SaveChanges();
        h.AddUnpack(zip);

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.FailureCode.Should().Be("SOURCE_GONE");
        h.Gateway.Opened.Should().Be(0);
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeTrue("the evidence row stays");
    }

    [Fact]
    public async Task A_zip_with_an_unsettled_verdict_and_no_scan_waits_instead_of_extracting()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        zip.CsamStatus = CsamScanStatus.Scanning;
        h.Db.SaveChanges();
        h.AddUnpack(zip);

        ProjectArchiveUnpack? row = await h.RunNextAsync();

        row!.Status.Should().Be(ArchiveUnpackRunStatus.Pending);
        row.NextAttemptAt.Should().BeAfter(h.Now);
        h.Gateway.Opened.Should().Be(0);
    }

    [Fact]
    public async Task A_zip_quarantined_while_it_unpacks_is_kept_and_the_extracted_files_go()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.AddUnpack(zip);
        h.Gateway.Next = () =>
        {
            using InferenceApiDbContext other = h.NewDb();
            other.ProjectFiles.Single(f => f.Id == zip.Id).CsamStatus = CsamScanStatus.Quarantined;
            other.SaveChanges();
            return ScriptedGateway.Ok(null, ("a.js", B("1")));
        };

        ProjectArchiveUnpack? row = await h.RunNextAsync();

        row!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        row.FailureCode.Should().Be("SOURCE_GONE");
        (await h.Db.ProjectFiles.AsNoTracking().Select(f => f.Id).ToListAsync()).Should().Equal(zip.Id);
        h.Store.Objects.Keys.Should().Contain(zip.StorageKey).And.HaveCount(1);
    }

    [Fact]
    public async Task A_zip_deleted_by_the_user_mid_unpack_fails_as_source_gone_with_nothing_left()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.AddUnpack(zip);
        h.Gateway.Next = () =>
        {
            using InferenceApiDbContext other = h.NewDb();
            other.ProjectFiles.Remove(other.ProjectFiles.Single(f => f.Id == zip.Id));
            other.SaveChanges();
            return ScriptedGateway.Ok(null, ("a.js", B("1")));
        };

        ProjectArchiveUnpack? row = await h.RunNextAsync();

        row!.FailureCode.Should().Be("SOURCE_GONE");
        (await h.Db.ProjectFiles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_zip_replaced_while_it_unpacks_is_not_deleted_and_the_new_content_is_unpacked_next()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        ProjectArchiveUnpack unpack = h.AddUnpack(zip);
        string newKey = zip.StorageKey + ".v2";
        h.Gateway.Next = () =>
        {
            using InferenceApiDbContext other = h.NewDb();
            ProjectFile row = other.ProjectFiles.Single(f => f.Id == zip.Id);
            row.StorageKey = newKey;
            row.SizeBytes = 2000;
            row.UploadedAt = h.Now.AddMinutes(1);
            other.SaveChanges();
            h.Store.Objects[newKey] = new byte[2000];
            return ScriptedGateway.Ok(null, ("old.js", B("old")));
        };

        ProjectArchiveUnpack? row = await h.RunNextAsync();

        row!.Status.Should().Be(ArchiveUnpackRunStatus.Pending, "the new content is queued");
        row.Attempts.Should().Be(0);
        ProjectFile kept = await h.Db.ProjectFiles.AsNoTracking().SingleAsync(f => f.Id == zip.Id);
        kept.StorageKey.Should().Be(newKey);
        h.Store.Objects.Should().ContainKey(newKey);
        (await h.Db.ProjectFiles.AnyAsync(f => f.SourceKind == "zip")).Should().BeFalse("the old content's files were rolled back");

        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("new.js", B("new")));
        ProjectArchiveUnpack? second = await h.RunNextAsync();

        second!.Status.Should().Be(ArchiveUnpackRunStatus.Done);
        (await h.Db.ProjectFiles.AsNoTracking().Where(f => f.SourceKind == "zip").Select(f => f.OriginalFileName).ToListAsync()).Should().Equal("new.js");
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeFalse();
        _ = unpack;
    }

    [Fact]
    public async Task A_run_that_lost_its_claim_to_a_second_replica_wipes_nothing()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        ProjectArchiveUnpack unpack = h.AddUnpack(zip);
        ScriptedGateway second = new() { Next = () => ScriptedGateway.Ok(null, ("b.js", B("2"))) };
        h.Gateway.Next = () =>
        {
            // The first run stalls past the stale window; a second replica reclaims the row and finishes it.
            h.Now = h.Now.Add(ArchiveUnpackProcessor.StaleClaim).AddMinutes(1);
            using InferenceApiDbContext other = h.NewDb();
            ArchiveUnpackProcessor b = h.Processor(other, second);
            Guid? id = b.ClaimNextAsync(CancellationToken.None).GetAwaiter().GetResult();
            id.Should().Be(unpack.Id);
            b.ProcessAsync(id!.Value, CancellationToken.None).GetAwaiter().GetResult();
            return ScriptedGateway.Ok(null, ("a.js", B("1")));
        };

        ProjectArchiveUnpack? row = await h.RunNextAsync();

        row!.Status.Should().Be(ArchiveUnpackRunStatus.Done, "the second run's result stands");
        (await h.Db.ProjectFiles.AsNoTracking().Where(f => f.SourceKind == "zip").Select(f => f.OriginalFileName).ToListAsync()).Should().Equal("b.js");
        ProjectFile kept = await h.Db.ProjectFiles.AsNoTracking().SingleAsync(f => f.SourceKind == "zip");
        h.Store.Objects.Keys.Should().Equal(new[] { kept.StorageKey }, "the stalled run removed its own unsaved object and nothing else");
    }

    [Fact]
    public async Task A_password_protected_zip_fails_with_its_own_code_and_keeps_the_zip()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Ok(new Dictionary<string, int> { ["ENTRY_ENCRYPTED"] = 3 });

        ProjectArchiveUnpack? row = await h.RunNextAsync();

        row!.FailureCode.Should().Be(ArchiveUnpackFailure.PasswordProtected).And.Be("ARCHIVE_PASSWORD_PROTECTED");
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeTrue();
    }

    [Fact]
    public void Zstd_is_an_archive_kind()
    {
        ProjectArchiveKinds.IsArchive("application/zstd", "x").Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_unpack_keeps_a_quarantined_row_and_its_object_and_removes_the_clean_ones()
    {
        UnpackHarness h = new();
        h.Csam = Matcher(f =>
        {
            if (f.OriginalFileName != "bad.js") return CsamScanStatus.Clean;
            string key = $"projects/{f.ProjectId}/quarantine/{f.Id}/bad.js";
            h.Store.Objects[key] = h.Store.Objects[f.StorageKey];
            h.Store.Objects.Remove(f.StorageKey);
            f.StorageKey = key;
            f.CsamQuarantineKey = key;
            f.CsamStatus = CsamScanStatus.Quarantined;
            h.Db.SaveChanges();
            return CsamScanStatus.Quarantined;
        }).Object;
        h.AddUnpack(h.AddZip());
        int total = ArchiveUnpackProcessor.BatchSize + 5;
        h.Gateway.Next = () => ScriptedGateway.Ok(null, [("bad.js", B("evidence")), .. Enumerable.Range(0, total - 1).Select(i => ($"f{i}.js", B("x")))]);
        h.Store.FailUploadAfter = ArchiveUnpackProcessor.BatchSize + 2;

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        List<ProjectFile> rows = await h.Db.ProjectFiles.AsNoTracking().Where(f => f.OriginalFileName.EndsWith(".js")).ToListAsync();
        ProjectFile kept = rows.Should().ContainSingle().Subject;
        kept.OriginalFileName.Should().Be("bad.js");
        kept.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        kept.SourceRevision.Should().StartWith("held:", "it is detached so no cleanup matches it");
        h.Store.Objects.Should().ContainKey(kept.StorageKey);
        h.Store.DeletedKeys.Should().NotContain(kept.StorageKey);
        h.Store.Objects.Keys.Should().HaveCount(2, "the zip and the quarantined object are all that is left");
        h.Db.StorageRetentionMarks.Should().BeEmpty();
        done.FilesCreated.Should().Be(1);
        done.BytesCreated.Should().Be(kept.SizeBytes);
    }

    [Fact]
    public async Task Stale_claim_cleanup_keeps_a_quarantined_leftover_and_removes_the_clean_one()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        ProjectArchiveUnpack unpack = h.AddUnpack(zip, ArchiveUnpackRunStatus.Running);
        unpack.ClaimedAt = h.Now - ArchiveUnpackProcessor.StaleClaim - TimeSpan.FromMinutes(1);
        unpack.Attempts = 1;
        string cleanKey = $"projects/{h.Project.Id}/userFiles/old/clean.js";
        string heldKey = $"projects/{h.Project.Id}/quarantine/held-file/bad.js";
        h.Store.Objects[cleanKey] = [1];
        h.Store.Objects[heldKey] = [2];
        ProjectFile Leftover(string name, string key, string revision) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            OriginalFileName = name,
            OriginalPath = "old/" + name,
            DirectoryPath = "old",
            StorageKey = key,
            MimeType = "text/plain",
            SourceKind = "zip",
            SourceRevision = revision,
        };
        ProjectFile held = Leftover("bad.js", heldKey, unpack.Id + ":123");
        held.CsamStatus = CsamScanStatus.Quarantined;
        held.CsamQuarantineKey = heldKey;
        h.Db.ProjectFiles.AddRange(Leftover("clean.js", cleanKey, unpack.Id.ToString()), held);
        h.Db.SaveChanges();
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("a.js", B("1")));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed, "a held leftover means the zip held a match: it is quarantined, not deleted");
        (await h.Db.ProjectFiles.AsNoTracking().SingleAsync(f => f.Id == zip.Id)).CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        h.Store.Objects.Should().ContainKey(heldKey).And.NotContainKey(cleanKey);
        h.Store.DeletedKeys.Should().NotContain(heldKey);
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == held.Id)).Should().BeTrue();
        (await h.Db.ProjectFiles.AnyAsync(f => f.OriginalFileName == "clean.js")).Should().BeFalse();
    }

    [Fact]
    public async Task A_held_extracted_file_keeps_the_zip_and_the_held_row_and_fails_the_unpack()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.Csam = Matcher(f =>
        {
            if (f.OriginalFileName != "bad.js") return CsamScanStatus.Clean;
            string key = $"projects/{f.ProjectId}/quarantine/{f.Id}/bad.js";
            h.Store.Objects[key] = h.Store.Objects[f.StorageKey];
            h.Store.Objects.Remove(f.StorageKey);
            f.StorageKey = key;
            f.CsamQuarantineKey = key;
            f.CsamStatus = CsamScanStatus.Quarantined;
            h.Db.SaveChanges();
            return CsamScanStatus.Quarantined;
        }).Object;
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("bad.js", B("evidence")), ("ok.js", B("fine")));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == zip.Id)).Should().BeTrue("the original upload is kept");
        h.Store.Objects.Should().ContainKey(zip.StorageKey);
        ProjectFile kept = (await h.Db.ProjectFiles.AsNoTracking().Where(f => f.SourceKind == "zip").ToListAsync()).Should().ContainSingle().Subject;
        kept.OriginalFileName.Should().Be("bad.js");
        h.Store.Objects.Should().ContainKey(kept.StorageKey);
    }

    [Fact]
    public async Task The_claim_time_is_refreshed_at_each_batch_commit()
    {
        UnpackHarness h = new();
        DateTime? seen = null;
        h.Csam = Matcher(_ =>
        {
            using ReelBolt.Inference.Api.Data.InferenceApiDbContext other = h.NewDb();
            seen ??= other.ProjectArchiveUnpacks.AsNoTracking().Single().ClaimedAt;
            return CsamScanStatus.Clean;
        }).Object;
        h.AddUnpack(h.AddZip());
        h.Gateway.Next = () =>
        {
            h.Now = h.Now.AddMinutes(15);
            return ScriptedGateway.Ok(null, ("a.js", B("1")));
        };

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Done);
        seen.Should().Be(h.Now, "the first batch commit refreshed the claim");
    }

    [Fact]
    public async Task A_held_extracted_file_quarantines_the_zip_and_publishes_and_notifies_nothing()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        string zipKey = zip.StorageKey;
        h.CsamQuarantine = new CsamQuarantineService(h.Store);
        Mock<IUserActivityNotifier> notifier = new();
        h.Notifier = notifier.Object;
        h.Csam = Matcher(f =>
        {
            if (f.OriginalFileName != "bad.js") return CsamScanStatus.Clean;
            string key = $"projects/{f.ProjectId}/quarantine/{f.Id}/bad.js";
            h.Store.Objects[key] = h.Store.Objects[f.StorageKey];
            h.Store.Objects.Remove(f.StorageKey);
            f.StorageKey = key;
            f.CsamQuarantineKey = key;
            f.CsamStatus = CsamScanStatus.Quarantined;
            h.Db.SaveChanges();
            return CsamScanStatus.Quarantined;
        }).Object;
        h.AddUnpack(zip);
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("bad.js", B("evidence")), ("good.mp4", [3, 4]), ("ok.js", B("fine")));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        ProjectFile kept = await h.Db.ProjectFiles.AsNoTracking().SingleAsync(f => f.Id == zip.Id);
        kept.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        kept.CsamQuarantineKey.Should().Be($"projects/{h.Project.Id}/quarantine/{zip.Id}/{System.IO.Path.GetFileName(zip.OriginalFileName)}".Replace(" ", "_"));
        kept.StorageKey.Should().Be(kept.CsamQuarantineKey);
        h.Store.Objects.Should().ContainKey(kept.StorageKey).And.NotContainKey(zipKey);
        h.Store.DeletedKeys.Should().NotContain(kept.StorageKey);
        Project project = await h.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == h.Project.Id);
        project.ContentQuarantinedAt.Should().NotBeNull();
        project.CsamOutputHoldAt.Should().NotBeNull();
        (await h.Db.ProjectFiles.AsNoTracking().Where(f => f.SourceKind == "zip").ToListAsync()).Should().ContainSingle().Which.OriginalFileName.Should().Be("bad.js");
        h.Publish.Verify(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()), Times.Never);
        notifier.Verify(n => n.FileCreatedAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_user_folder_named_quarantine_is_not_held_and_is_rolled_back_on_a_failure()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.Csam = Matcher(_ => CsamScanStatus.Clean).Object;
        h.AddUnpack(zip);
        // More than one batch (100 rows are saved at a time), so the first batch is committed when the second one fails
        // and the rollback has real rows to remove.
        (string Path, byte[] Data)[] entries = [.. Enumerable.Range(0, 105).Select(i => ($"quarantine/f{i}.js", B(i.ToString())))];
        h.Gateway.Next = () => ScriptedGateway.Ok(null, entries);
        h.Store.FailUploadAfter = 102;

        await h.RunNextAsync();

        List<ProjectFile> left = await h.Db.ProjectFiles.AsNoTracking().Where(f => f.SourceKind == "zip").ToListAsync();
        left.Should().BeEmpty("a folder named quarantine is ordinary content and is rolled back, including the saved first batch");
        h.Store.Objects.Keys.Should().ContainSingle("only the zip is left").Which.Should().Be(zip.StorageKey);
        (await h.Db.ProjectFiles.AsNoTracking().SingleAsync(f => f.Id == zip.Id)).CsamStatus.Should().NotBe(CsamScanStatus.Quarantined);
    }

    [Fact]
    public async Task A_held_row_left_by_an_earlier_attempt_still_quarantines_the_zip_instead_of_deleting_it()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.CsamQuarantine = new CsamQuarantineService(h.Store);
        h.Csam = Matcher(_ => CsamScanStatus.Clean).Object;
        ProjectArchiveUnpack unpack = h.AddUnpack(zip);
        string heldKey = $"projects/{h.Project.Id}/quarantine/prev/bad.js";
        h.Store.Objects[heldKey] = [2];
        h.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            OriginalFileName = "bad.js",
            OriginalPath = "old/bad.js",
            DirectoryPath = "old",
            StorageKey = heldKey,
            MimeType = "text/plain",
            SourceKind = "zip",
            SourceRevision = "held:" + unpack.Id + ":123",
            CsamStatus = CsamScanStatus.Quarantined,
            CsamQuarantineKey = heldKey,
        });
        h.Db.SaveChanges();
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("a.js", B("1")));

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        ProjectFile kept = await h.Db.ProjectFiles.AsNoTracking().SingleAsync(f => f.Id == zip.Id);
        kept.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        h.Store.Objects.Should().ContainKey(kept.StorageKey);
    }

    private static ProjectFile HeldLeftover(UnpackHarness h, ProjectArchiveUnpack unpack, string heldKey) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = h.Project.Id,
        OriginalFileName = "bad.js",
        OriginalPath = "old/bad.js",
        DirectoryPath = "old",
        StorageKey = heldKey,
        MimeType = "text/plain",
        SourceKind = "zip",
        SourceRevision = "held:" + unpack.Id + ":123",
        CsamStatus = CsamScanStatus.Quarantined,
        CsamQuarantineKey = heldKey,
    };

    private async Task AssertZipHeldAsync(UnpackHarness h, ProjectFile zip, ProjectFile held, string heldKey)
    {
        ProjectFile kept = await h.Db.ProjectFiles.AsNoTracking().SingleAsync(f => f.Id == zip.Id);
        kept.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        h.Store.Objects.Should().ContainKey(kept.StorageKey);
        h.Store.DeletedKeys.Should().NotContain(kept.StorageKey).And.NotContain(heldKey);
        h.Store.Objects.Should().ContainKey(heldKey);
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == held.Id)).Should().BeTrue();
        Project project = await h.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == h.Project.Id);
        project.ContentQuarantinedAt.Should().NotBeNull();
        project.CsamOutputHoldAt.Should().NotBeNull();
        h.Publish.Verify(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_abandoned_write_with_a_held_row_quarantines_the_zip_and_keeps_the_evidence()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.CsamQuarantine = new CsamQuarantineService(h.Store);
        h.Csam = Matcher(_ => CsamScanStatus.Clean).Object;
        ProjectArchiveUnpack unpack = h.AddUnpack(zip);
        string heldKey = $"projects/{h.Project.Id}/quarantine/prev/bad.js";
        h.Store.Objects[heldKey] = [2];
        ProjectFile held = HeldLeftover(h, unpack, heldKey);
        h.Db.ProjectFiles.Add(held);
        h.Db.SaveChanges();
        h.Gateway.Next = () => ScriptedGateway.Ok(null, ("a.js", B("1")), ("b.js", B("2")), ("c.js", B("3")));
        h.Store.FailUploadAfter = 1;

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        done.FailureCode.Should().Be(ArchiveUnpackFailure.UnpackBlocked);
        await AssertZipHeldAsync(h, zip, held, heldKey);
    }

    [Fact]
    public async Task The_max_attempts_failure_with_a_held_row_quarantines_the_zip_and_keeps_the_evidence()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.CsamQuarantine = new CsamQuarantineService(h.Store);
        h.Csam = Matcher(_ => CsamScanStatus.Clean).Object;
        ProjectArchiveUnpack unpack = h.AddUnpack(zip);
        unpack.Attempts = ArchiveUnpackProcessor.MaxAttempts;
        string heldKey = $"projects/{h.Project.Id}/quarantine/prev/bad.js";
        h.Store.Objects[heldKey] = [2];
        ProjectFile held = HeldLeftover(h, unpack, heldKey);
        h.Db.ProjectFiles.Add(held);
        h.Db.SaveChanges();

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().Be(ArchiveUnpackRunStatus.Failed);
        done.FailureCode.Should().Be(ArchiveUnpackFailure.UnpackBlocked);
        h.Gateway.Opened.Should().Be(0);
        await AssertZipHeldAsync(h, zip, held, heldKey);
    }

    [Fact]
    public async Task A_retry_later_exit_with_a_held_row_also_quarantines_the_zip()
    {
        UnpackHarness h = new();
        ProjectFile zip = h.AddZip();
        h.CsamQuarantine = new CsamQuarantineService(h.Store);
        h.Csam = Matcher(_ => CsamScanStatus.Clean).Object;
        ProjectArchiveUnpack unpack = h.AddUnpack(zip);
        string heldKey = $"projects/{h.Project.Id}/quarantine/prev/bad.js";
        h.Store.Objects[heldKey] = [2];
        ProjectFile held = HeldLeftover(h, unpack, heldKey);
        h.Db.ProjectFiles.Add(held);
        h.Db.SaveChanges();
        h.Gateway.Next = () => throw new System.Net.Http.HttpRequestException("worker down");

        ProjectArchiveUnpack? done = await h.RunNextAsync();

        done!.Status.Should().NotBe(ArchiveUnpackRunStatus.Running, "the attempt ended through a non-completing exit");
        await AssertZipHeldAsync(h, zip, held, heldKey);
    }
}
