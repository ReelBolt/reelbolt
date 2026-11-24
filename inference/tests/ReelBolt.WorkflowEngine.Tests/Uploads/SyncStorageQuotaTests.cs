#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The quota as a background sync applies it: a file that does not fit is SKIPPED and counted (never
/// thrown), the growth written since the last save is carried into every check so a batch cannot each
/// pass against the same stale total, and the link ends with one plain-language sentence.
/// </summary>
public class SyncStorageQuotaTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static PlanGateViolation Violation(long used, long limit) =>
        new(PlanGate.StorageCode, "storage", "over quota", 413, limit, used);

    [Fact]
    public async Task A_file_that_fits_is_allowed_and_nothing_is_reported()
    {
        UploadsPlanGate gate = new();
        SyncStorageQuota quota = new(gate, Guid.NewGuid());

        (await quota.FitsAsync(10, CancellationToken.None)).Should().BeTrue();

        quota.Refused.Should().Be(0);
        quota.Message.Should().BeNull();
        gate.UploadChecks.Should().Equal([10L]);
    }

    [Fact]
    public async Task A_file_that_does_not_fit_is_counted_rather_than_thrown()
    {
        UploadsPlanGate gate = new();
        gate.Violations.Enqueue(Violation(used: 90, limit: 100));
        SyncStorageQuota quota = new(gate, Guid.NewGuid());

        // Awaiting is the assertion that it does not throw: a refused file is a skip, not a failure.
        bool fits = await quota.FitsAsync(20, CancellationToken.None);

        fits.Should().BeFalse("the sync carries on with the rest");
        quota.Refused.Should().Be(1);
        quota.Message.Should().Be(SyncStorageQuota.StorageFullMessage(90, 100, 1));
    }

    [Fact]
    public async Task Growth_written_since_the_last_save_is_carried_into_the_next_check()
    {
        UploadsPlanGate gate = new();
        SyncStorageQuota quota = new(gate, Guid.NewGuid());

        await quota.FitsAsync(10, CancellationToken.None);
        quota.Wrote(10);
        await quota.FitsAsync(5, CancellationToken.None);
        quota.Saved();
        await quota.FitsAsync(5, CancellationToken.None);

        gate.UploadChecks.Should().Equal([10L, 15L, 5L],
            "a batch of rows not yet saved must count against the quota that will hold them");
    }

    [Fact]
    public async Task A_file_that_does_not_grow_storage_always_fits_without_asking()
    {
        UploadsPlanGate gate = new();
        SyncStorageQuota quota = new(gate, Guid.NewGuid());

        (await quota.FitsAsync(0, CancellationToken.None)).Should().BeTrue();
        (await quota.FitsAsync(-4096, CancellationToken.None)).Should().BeTrue();
        gate.UploadChecks.Should().BeEmpty("a replacement never shrinks the workspace below its own size");
    }

    [Fact]
    public async Task Every_refusal_is_counted_and_the_message_uses_where_the_workspace_stood()
    {
        UploadsPlanGate gate = new();
        gate.Violations.Enqueue(Violation(used: 90, limit: 100));
        gate.Violations.Enqueue(Violation(used: 95, limit: 100));
        SyncStorageQuota quota = new(gate, Guid.NewGuid());

        await quota.FitsAsync(20, CancellationToken.None);
        quota.Wrote(5);
        await quota.FitsAsync(20, CancellationToken.None);

        quota.Refused.Should().Be(2);
        quota.Message.Should().Be(SyncStorageQuota.StorageFullMessage(100, 100, 2),
            "the usage at the last refusal plus what the sync wrote but has not saved yet");
    }

    [Fact]
    public async Task No_plan_gate_means_everything_fits()
    {
        SyncStorageQuota quota = new(null, Guid.NewGuid());

        (await quota.FitsAsync(long.MaxValue, CancellationToken.None)).Should().BeTrue();
        quota.Refused.Should().Be(0);
        quota.Message.Should().BeNull();
    }

    [Fact]
    public void The_message_is_a_plain_sentence_for_a_person()
    {
        SyncStorageQuota.StorageFullMessage(1024, Gb, 1)
            .Should().Be("Storage is full (1 KB of 1 GB used) — 1 file was not copied. Free space or upgrade, then sync again.");
        SyncStorageQuota.StorageFullMessage(2 * 1024, Gb, 3)
            .Should().Be("Storage is full (2 KB of 1 GB used) — 3 files were not copied. Free space or upgrade, then sync again.");
        SyncStorageQuota.StorageFullMessage(1024, null, 1)
            .Should().Be("Storage is full (1 KB used) — 1 file was not copied. Free space or upgrade, then sync again.",
                "an unlimited plan that still refused (a race) reads without a limit number");
    }
}
