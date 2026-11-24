using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// The composition-revision library's storage and transfer layers (docs/design/composition-revisions.md):
/// capture → store → restore, against real tar.gz bytes, with the digest, dedupe, lineage and failure
/// rules each layer promises.
/// </summary>
public class CompositionWorkspaceTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    private static FakeWorkspaceSession FilmSession(Guid executionId)
    {
        FakeWorkspaceSession session = new(executionId);
        session.Files["package.json"] = Encoding.UTF8.GetBytes("{\"name\":\"film\"}");
        session.Files["src/root.tsx"] = Encoding.UTF8.GetBytes("export const Root = () => null;");
        session.Files["src/Intro.tsx"] = Encoding.UTF8.GetBytes("export const Intro = () => <h1>Hi</h1>;");
        session.Files["public/logo.png"] = [0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3];
        session.Files["node_modules/remotion/index.js"] = Encoding.UTF8.GetBytes("// never archived");
        return session;
    }

    private static CompositionCaptureRequest Origin(Guid executionId, string? render = "projects/p/outputFiles/x/promo.mp4") => new()
    {
        ProjectId = Project,
        OrganizationId = Org,
        ExecutionId = executionId,
        CompositionId = "FinalVideo",
        Fps = 30,
        Width = 1920,
        Height = 1080,
        DurationInFrames = 300,
        RenderOutputStorageKey = render
    };

    [Fact]
    public async Task A_capture_stores_the_archive_under_the_project_prefix_with_the_engine_computed_digest()
    {
        using CompositionHarness h = new();
        Guid execution = Guid.NewGuid();
        Guid user = Guid.NewGuid();
        await h.SeedExecutionAsync(execution, Project, user);

        CompositionCaptureResult result = await h.Capture.CaptureAsync(FilmSession(execution), Origin(execution), CancellationToken.None);

        result.Captured.Should().BeTrue();
        CompositionWorkspaceVersion version = result.Version!;
        version.VersionNumber.Should().Be(1);
        version.ParentVersionId.Should().BeNull();
        version.CreatedByExecutionId.Should().Be(execution);
        version.CreatedByUserId.Should().Be(user, "an origin capture records the run's initiator");
        version.Instruction.Should().BeNull();
        CompositionWorkspaceKeys.BelongsTo(version.StorageKey, Project).Should().BeTrue();
        h.Objects.Should().ContainKey(version.StorageKey);
        version.Sha256.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(h.Objects[version.StorageKey])));
        version.SizeBytes.Should().Be(h.Objects[version.StorageKey].LongLength);

        using WorkflowEngineDbContext db = h.Db();
        CompositionWorkspace workspace = await db.CompositionWorkspaces.SingleAsync();
        workspace.OriginExecutionId.Should().Be(execution);
        workspace.LatestVersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task An_unchanged_origin_re_render_is_deduplicated_but_a_changed_one_appends_a_child()
    {
        using CompositionHarness h = new();
        Guid execution = Guid.NewGuid();
        await h.SeedExecutionAsync(execution, Project);
        FakeWorkspaceSession session = FilmSession(execution);

        CompositionCaptureResult first = await h.Capture.CaptureAsync(session, Origin(execution), CancellationToken.None);
        CompositionCaptureResult again = await h.Capture.CaptureAsync(session, Origin(execution), CancellationToken.None);
        again.Deduplicated.Should().BeTrue();
        again.Version!.Id.Should().Be(first.Version!.Id);

        session.Files["src/Intro.tsx"] = Encoding.UTF8.GetBytes("export const Intro = () => <h1>Hello</h1>;");
        CompositionCaptureResult changed = await h.Capture.CaptureAsync(session, Origin(execution), CancellationToken.None);
        changed.Deduplicated.Should().BeFalse();
        changed.Version!.VersionNumber.Should().Be(2);
        changed.Version.ParentVersionId.Should().Be(first.Version.Id);
    }

    [Fact]
    public async Task A_capture_inside_a_revision_lineage_always_appends_and_advances_the_parent()
    {
        using CompositionHarness h = new();
        Guid origin = Guid.NewGuid();
        Guid revisionRun = Guid.NewGuid();
        await h.SeedExecutionAsync(origin, Project);
        await h.SeedExecutionAsync(revisionRun, Project);
        CompositionWorkspaceVersion v1 = (await h.Capture.CaptureAsync(FilmSession(origin), Origin(origin), CancellationToken.None)).Version!;

        Guid requester = Guid.NewGuid();
        CompositionLineage lineage = new()
        {
            WorkspaceId = v1.WorkspaceId,
            ParentVersionId = v1.Id,
            Instruction = "Bigger title",
            RevisionId = Guid.NewGuid(),
            RequestedByUserId = requester
        };

        // Same bytes as v1 — a revision still records its own version (it carries an instruction and a render).
        CompositionWorkspaceVersion v2 = (await h.Capture.CaptureAsync(FilmSession(revisionRun), Origin(revisionRun) with { Lineage = lineage }, CancellationToken.None)).Version!;
        CompositionWorkspaceVersion v3 = (await h.Capture.CaptureAsync(FilmSession(revisionRun), Origin(revisionRun) with { Lineage = lineage }, CancellationToken.None)).Version!;

        v2.VersionNumber.Should().Be(2);
        v2.ParentVersionId.Should().Be(v1.Id);
        v2.Instruction.Should().Be("Bigger title");
        v2.CreatedByUserId.Should().Be(requester);
        v2.RevisionId.Should().Be(lineage.RevisionId);
        v3.ParentVersionId.Should().Be(v2.Id, "a second render in the same revision chains onto the first");
        lineage.LastCapturedVersionId.Should().Be(v3.Id);
        lineage.ParentVersionId.Should().Be(v3.Id);
    }

    [Fact]
    public async Task Restore_round_trips_the_tree_into_a_fresh_sandbox_and_keeps_its_node_modules()
    {
        using CompositionHarness h = new();
        Guid origin = Guid.NewGuid();
        await h.SeedExecutionAsync(origin, Project);
        FakeWorkspaceSession source = FilmSession(origin);
        CompositionWorkspaceVersion v1 = (await h.Capture.CaptureAsync(source, Origin(origin), CancellationToken.None)).Version!;

        FakeWorkspaceSession fresh = new(Guid.NewGuid());
        fresh.Files["node_modules/remotion/index.js"] = Encoding.UTF8.GetBytes("// template copy");
        fresh.Files["src/root.tsx"] = Encoding.UTF8.GetBytes("// the template's root, replaced by the restore");
        fresh.Files["src/TemplateOnly.tsx"] = Encoding.UTF8.GetBytes("// removed by the restore");

        CompositionRestoreResult restored = await h.Restorer.RestoreAsync(fresh, v1, CancellationToken.None);

        restored.Files.Should().Be(4);
        restored.Warnings.Should().BeEmpty();
        fresh.Files.Keys.Where(k => !k.StartsWith("node_modules/", StringComparison.Ordinal))
            .Should().BeEquivalentTo(source.Files.Keys.Where(k => !k.StartsWith("node_modules/", StringComparison.Ordinal)));
        foreach (string key in source.Files.Keys.Where(k => !k.StartsWith("node_modules/", StringComparison.Ordinal)))
            fresh.Files[key].Should().Equal(source.Files[key]);
        Encoding.UTF8.GetString(fresh.Files["node_modules/remotion/index.js"]).Should().Be("// template copy");
    }

    [Fact]
    public async Task Restore_refuses_an_archive_that_no_longer_matches_its_digest()
    {
        using CompositionHarness h = new();
        Guid origin = Guid.NewGuid();
        await h.SeedExecutionAsync(origin, Project);
        CompositionWorkspaceVersion v1 = (await h.Capture.CaptureAsync(FilmSession(origin), Origin(origin), CancellationToken.None)).Version!;
        h.Objects[v1.StorageKey] = FakeWorkspaceSession.Pack(new Dictionary<string, byte[]> { ["src/evil.tsx"] = [1] });

        FakeWorkspaceSession fresh = new(Guid.NewGuid());
        Func<Task> act = () => h.Restorer.RestoreAsync(fresh, v1, CancellationToken.None);

        (await act.Should().ThrowAsync<CompositionWorkspaceException>()).Which.Code.Should().Be(CompositionWorkspaceException.ArchiveCorrupt);
        fresh.Imports.Should().Be(0, "nothing reaches the sandbox from an archive that failed verification");
    }

    [Fact]
    public async Task Restore_reports_a_missing_archive_with_its_own_code()
    {
        using CompositionHarness h = new();
        Guid origin = Guid.NewGuid();
        await h.SeedExecutionAsync(origin, Project);
        CompositionWorkspaceVersion v1 = (await h.Capture.CaptureAsync(FilmSession(origin), Origin(origin), CancellationToken.None)).Version!;
        h.Objects.TryRemove(v1.StorageKey, out _);

        Func<Task> act = () => h.Restorer.RestoreAsync(new FakeWorkspaceSession(Guid.NewGuid()), v1, CancellationToken.None);

        (await act.Should().ThrowAsync<CompositionWorkspaceException>()).Which.Code.Should().Be(CompositionWorkspaceException.ArchiveMissing);
    }

    [Fact]
    public async Task Packages_the_template_lacks_are_installed_and_a_refused_install_becomes_a_warning()
    {
        using CompositionHarness h = new();
        Guid origin = Guid.NewGuid();
        await h.SeedExecutionAsync(origin, Project);
        CompositionWorkspaceVersion v1 = (await h.Capture.CaptureAsync(FilmSession(origin), Origin(origin), CancellationToken.None)).Version!;

        FakeWorkspaceSession online = new(Guid.NewGuid());
        online.ExtraDependencies.Add("lottie-web@^5.12.2");
        CompositionRestoreResult installed = await h.Restorer.RestoreAsync(online, v1, CancellationToken.None);
        online.Installs.Single().Should().Equal("lottie-web@5.12.2");
        installed.Warnings.Should().BeEmpty();

        FakeWorkspaceSession offline = new(Guid.NewGuid())
        {
            InstallFailure = new SandboxRequestFailedException(HttpStatusCode.Conflict, "Sandbox API request failed (409): no network egress")
        };
        offline.ExtraDependencies.Add("lottie-web@^5.12.2");
        CompositionRestoreResult warned = await h.Restorer.RestoreAsync(offline, v1, CancellationToken.None);
        warned.Warnings.Should().ContainSingle().Which.Should().Contain("lottie-web@^5.12.2");
    }

    [Theory]
    [InlineData("lottie-web@^5.12.2", "lottie-web@5.12.2")]
    [InlineData("@scope/pkg@~1.0.0", "@scope/pkg@1.0.0")]
    [InlineData("left-pad@1.3.0", "left-pad@1.3.0")]
    [InlineData("thing@https://example.com/x.tgz", "thing")]
    [InlineData("other@>=1 <2", "other")]
    [InlineData("bare", "bare")]
    public void Dependencies_are_turned_into_specs_the_sandbox_accepts(string declared, string expected)
        => CompositionWorkspaceRestorer.ToInstallSpec(declared).Should().Be(expected);

    [Fact]
    public async Task A_session_that_cannot_move_a_workspace_is_reported_as_unsupported_never_thrown()
    {
        using CompositionHarness h = new();
        Guid execution = Guid.NewGuid();
        await h.SeedExecutionAsync(execution, Project);

        CompositionCaptureResult result = await h.Capture.TryCaptureAsync(new TransferlessSession(execution), Origin(execution), CancellationToken.None);

        result.Captured.Should().BeFalse();
        result.Outcome.Should().Be("unsupported");
    }

    [Fact]
    public async Task A_failing_capture_is_swallowed_by_TryCapture_and_counted_as_failed()
    {
        using CompositionHarness h = new();
        Guid execution = Guid.NewGuid();
        await h.SeedExecutionAsync(execution, Project);
        FakeWorkspaceSession gone = FilmSession(execution);
        gone.Exists = false;

        CompositionCaptureResult result = await h.Capture.TryCaptureAsync(gone, Origin(execution), CancellationToken.None);

        result.Captured.Should().BeFalse();
        result.Outcome.Should().Be("failed");
    }

    [Fact]
    public async Task Spooling_refuses_an_archive_over_the_cap_and_leaves_no_scratch_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rb-spool-" + Guid.NewGuid().ToString("N"));
        Func<Task> act = () => CompositionArchiveFile.SpoolAsync(new MemoryStream(new byte[2048]), dir, 1024, CancellationToken.None);

        (await act.Should().ThrowAsync<CompositionWorkspaceException>()).Which.Code.Should().Be(CompositionWorkspaceException.ArchiveTooLarge);
        Directory.GetFiles(dir).Should().BeEmpty();
        Directory.Delete(dir);
    }

    [Fact]
    public void Archive_keys_are_project_scoped_and_traversal_is_never_trusted()
    {
        Guid ws = Guid.NewGuid();
        Guid version = Guid.NewGuid();
        string key = CompositionWorkspaceKeys.ArchiveKey(Project, ws, version);

        key.Should().Be($"projects/{Project}/agentFiles/workspaces/{ws}/{version}.tar.gz");
        CompositionWorkspaceKeys.BelongsTo(key, Project).Should().BeTrue();
        CompositionWorkspaceKeys.BelongsTo(key, Guid.NewGuid()).Should().BeFalse();
        CompositionWorkspaceKeys.BelongsTo($"projects/{Project}/agentFiles/workspaces/../../other.tar.gz", Project).Should().BeFalse();
        CompositionWorkspaceKeys.BelongsTo($"projects/{Project}/outputFiles/x.mp4", Project).Should().BeFalse();
    }

    [Fact]
    public void A_Revise_step_is_never_cached_and_never_retried_by_the_outer_loop()
    {
        new StepCachePolicy().IsCacheable(new WorkflowStep { StepType = StepType.Revise }).Should().BeFalse();
        FailureClassifier.ForCode(CompositionWorkspaceException.Unavailable).Should().Be(FailureClass.PlatformInternal);
        FailureClassifier.ForCode(CompositionRevisionRunner.NotRendered).Should().Be(FailureClass.PlatformProviderError);
    }
}
