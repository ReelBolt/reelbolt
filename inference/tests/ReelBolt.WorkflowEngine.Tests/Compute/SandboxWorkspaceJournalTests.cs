using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The workspace journal: the durable record that lets a session which outlived its runner be rebuilt.
/// Two properties matter and both are pinned here — the FOLD (a history of writes and deletes has to
/// become the workspace's final state, because the runner's hydrate op restores a set of files and has
/// no notion of a delete) and the KEY SPACE (everything under the project prefix, so D2's presigning
/// boundary is what authorizes a hydrate URL).
/// </summary>
public class SandboxWorkspaceJournalTests
{
    private static readonly Guid Project = Guid.Parse("9a000000-0000-4000-8000-000000000001");
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-000000000002");

    private static (SandboxWorkspaceJournal Journal, FakeSandboxJournalStore Store) Build()
    {
        FakeSandboxJournalStore store = new();
        return (new SandboxWorkspaceJournal(store), store);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Every_write_is_stored_under_the_project_prefix_with_its_digest()
    {
        (SandboxWorkspaceJournal journal, FakeSandboxJournalStore store) = Build();

        SandboxJournalEntry entry = await journal.RecordWriteAsync(Project, Execution, "src/root.tsx", Bytes("<html/>"));

        entry.Sequence.Should().Be(0);
        entry.Kind.Should().Be(SandboxJournalEntryKind.Write);
        entry.Bytes.Should().Be(7);
        entry.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Bytes("<html/>"))));
        entry.BlobKey.Should().Be(SandboxWorkspaceJournal.BlobKeyFor(Project, Execution, 0));

        store.Keys.Should().OnlyContain(k => k.StartsWith($"projects/{Project:D}/agentFiles/sandbox-journal/{Execution:D}/", StringComparison.Ordinal));
        store.Keys.Should().Contain(SandboxWorkspaceJournal.EntryKeyFor(Project, Execution, 0));
        store.Keys.Should().Contain(SandboxWorkspaceJournal.BlobKeyFor(Project, Execution, 0));
    }

    [Fact]
    public async Task Sequences_increase_so_an_ordinal_listing_is_the_journals_order()
    {
        (SandboxWorkspaceJournal journal, _) = Build();

        await journal.RecordWriteAsync(Project, Execution, "a.tsx", Bytes("a"));
        await journal.RecordWriteAsync(Project, Execution, "b.tsx", Bytes("b"));
        SandboxJournalEntry third = await journal.RecordWriteAsync(Project, Execution, "c.tsx", Bytes("c"));

        third.Sequence.Should().Be(2);

        List<SandboxJournalEntry> read = await journal.ReadEntriesAsync(Project, Execution);
        read.Select(e => e.Path).Should().ContainInOrder("a.tsx", "b.tsx", "c.tsx");
    }

    [Fact]
    public async Task The_last_write_of_a_path_wins_and_a_delete_removes_it()
    {
        (SandboxWorkspaceJournal journal, _) = Build();

        await journal.RecordWriteAsync(Project, Execution, "src/scene.tsx", Bytes("first"));
        await journal.RecordWriteAsync(Project, Execution, "src/scene.tsx", Bytes("second"));
        await journal.RecordWriteAsync(Project, Execution, "src/logo.png", Bytes("png"));
        await journal.RecordDeleteAsync(Project, Execution, "src/logo.png");

        IReadOnlyList<SandboxHydrateEntry> manifest = await journal.BuildManifestAsync(
            Project, Execution, TimeSpan.FromMinutes(15));

        manifest.Should().ContainSingle();
        manifest[0].Path.Should().Be("src/scene.tsx");
        manifest[0].Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Bytes("second"))));
    }

    [Fact]
    public async Task Deleting_a_directory_removes_everything_under_it()
    {
        (SandboxWorkspaceJournal journal, _) = Build();

        await journal.RecordWriteAsync(Project, Execution, "src/root.tsx", Bytes("root"));
        await journal.RecordDeleteAsync(Project, Execution, "src");

        IReadOnlyList<SandboxHydrateEntry> manifest = await journal.BuildManifestAsync(
            Project, Execution, TimeSpan.FromMinutes(15));

        manifest.Should().BeEmpty("a hydrate manifest is the final state, and the final state has no src/");
    }

    [Fact]
    public async Task A_manifest_entry_carries_a_presigned_get_the_runner_can_actually_fetch()
    {
        (SandboxWorkspaceJournal journal, FakeSandboxJournalStore store) = Build();
        await journal.RecordWriteAsync(Project, Execution, "src/root.tsx", Bytes("<html/>"));

        IReadOnlyList<SandboxHydrateEntry> manifest = await journal.BuildManifestAsync(
            Project, Execution, TimeSpan.FromMinutes(15));

        manifest.Should().ContainSingle();
        store.PresignedKeys.Should().ContainSingle().Which.Should().Be(SandboxWorkspaceJournal.BlobKeyFor(Project, Execution, 0));
        store.FetchByUrl(manifest[0].Url).Should().Equal(Bytes("<html/>"));
    }

    [Fact]
    public async Task A_manifest_is_ordered_by_path_so_two_hydrations_fetch_in_the_same_order()
    {
        (SandboxWorkspaceJournal journal, _) = Build();

        await journal.RecordWriteAsync(Project, Execution, "z.tsx", Bytes("z"));
        await journal.RecordWriteAsync(Project, Execution, "a.tsx", Bytes("a"));

        IReadOnlyList<SandboxHydrateEntry> manifest = await journal.BuildManifestAsync(
            Project, Execution, TimeSpan.FromMinutes(15));

        manifest.Select(e => e.Path).Should().ContainInOrder("a.tsx", "z.tsx");
    }

    [Fact]
    public async Task A_journal_written_by_an_earlier_process_continues_where_it_stopped()
    {
        // The engine is horizontally scaled and an execution can be recovered after a restart, so the
        // sequence cursor is re-derived from the stored entries: entry 0 must never be overwritten by
        // a later process, or the journal's ORDER — the only thing that makes a delete-then-write fold
        // correct — would be lost.
        (SandboxWorkspaceJournal first, FakeSandboxJournalStore store) = Build();
        await first.RecordWriteAsync(Project, Execution, "a.tsx", Bytes("a"));
        await first.RecordDeleteAsync(Project, Execution, "a.tsx");

        SandboxWorkspaceJournal second = new(store);
        SandboxJournalEntry next = await second.RecordWriteAsync(Project, Execution, "b.tsx", Bytes("b"));

        next.Sequence.Should().Be(2);

        List<SandboxJournalEntry> read = await second.ReadEntriesAsync(Project, Execution);
        read.Select(e => e.Sequence).Should().ContainInOrder(0, 1, 2);
    }

    [Fact]
    public async Task The_ttl_is_capped_at_the_protocol_and_D2_limit()
    {
        (SandboxWorkspaceJournal journal, _) = Build();

        Func<Task> tooLong = () => journal.BuildManifestAsync(Project, Execution, TimeSpan.FromMinutes(16));

        await tooLong.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task An_empty_journal_produces_an_empty_manifest_rather_than_an_error()
    {
        (SandboxWorkspaceJournal journal, _) = Build();

        IReadOnlyList<SandboxHydrateEntry> manifest = await journal.BuildManifestAsync(
            Project, Execution, TimeSpan.FromMinutes(15));

        manifest.Should().BeEmpty();
    }

    [Fact]
    public async Task Sealing_deletes_every_object_and_forgets_the_cursor()
    {
        (SandboxWorkspaceJournal journal, FakeSandboxJournalStore store) = Build();
        await journal.RecordWriteAsync(Project, Execution, "a.tsx", Bytes("a"));
        await journal.RecordWriteAsync(Project, Execution, "b.tsx", Bytes("b"));

        await journal.SealAsync(Project, Execution);

        store.Keys.Should().BeEmpty("the journal only spans one execution, so nothing may outlive it");

        // And the cursor is gone with it: the next execution starts at 0 again.
        SandboxJournalEntry next = await journal.RecordWriteAsync(Project, Execution, "c.tsx", Bytes("c"));
        next.Sequence.Should().Be(0);
    }

    [Fact]
    public async Task A_torn_entry_degrades_the_hydration_instead_of_failing_it()
    {
        (SandboxWorkspaceJournal journal, FakeSandboxJournalStore store) = Build();
        await journal.RecordWriteAsync(Project, Execution, "a.tsx", Bytes("a"));

        // A half-written or foreign entry object. Recovery is the path this exists to serve, so an
        // entry this build cannot read loses one file rather than taking the session down.
        await store.PutAsync(
            SandboxWorkspaceJournal.EntryKeyFor(Project, Execution, 7),
            Encoding.UTF8.GetBytes("{ this is not json"),
            "application/json",
            default);

        IReadOnlyList<SandboxHydrateEntry> manifest = await journal.BuildManifestAsync(
            Project, Execution, TimeSpan.FromMinutes(15));

        manifest.Should().ContainSingle().Which.Path.Should().Be("a.tsx");
    }
}
