using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Uploads;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The per-replica half of <see cref="OrganizationStorageLock"/>: the in-process semaphore still
/// serialises two guards of the same replica, a second replica's instance is not serialised by it,
/// and a guard disposed without committing releases the organisation on every exit path. The
/// cross-replica guarantee itself is the PostgreSQL advisory lock, pinned by the Postgres tests.
/// </summary>
public class OrganizationStorageLockTests
{
    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    [Fact]
    public async Task Two_guards_of_the_same_replica_serialise_and_a_second_replica_does_not_share_them()
    {
        Guid organizationId = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        using InferenceApiDbContext otherDb = NewDb();

        OrganizationStorageLock oneReplica = new();
        await using OrganizationStorageGuard first = await oneReplica.AcquireAsync(db, organizationId, CancellationToken.None);

        using CancellationTokenSource waiting = new(TimeSpan.FromSeconds(5));
        Task<OrganizationStorageGuard> second = oneReplica.AcquireAsync(db, organizationId, waiting.Token);
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("the per-replica fast path is held");

        OrganizationStorageLock otherReplica = new();
        await using OrganizationStorageGuard third = await otherReplica.AcquireAsync(otherDb, organizationId, CancellationToken.None);
        third.Should().NotBeNull("a second replica's lock instance is not serialised by the first's semaphore");

        await first.DisposeAsync();
        await using OrganizationStorageGuard secondGuard = await second;
        secondGuard.Should().NotBeNull();
    }

    [Fact]
    public async Task A_guard_that_is_disposed_without_commit_releases_the_organisation()
    {
        Guid organizationId = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        OrganizationStorageLock lock_ = new();

        await using (await lock_.AcquireAsync(db, organizationId, CancellationToken.None))
        {
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await using OrganizationStorageGuard again = await lock_.AcquireAsync(db, organizationId, cts.Token);
        again.Should().NotBeNull("a disposed-uncommitted guard released the semaphore");
    }

    [Fact]
    public async Task A_guard_whose_commit_throws_still_releases_the_organisation_for_the_next_acquirer()
    {
        // An InMemory context cannot be made to fail a commit, so drive the guard through the
        // IDbContextTransaction seam: a commit that throws, then a rollback that also throws.
        SemaphoreSlim gate = new(1, 1);
        await gate.WaitAsync(); // as AcquireAsync would have taken it before handing the guard the gate

        Mock<IDbContextTransaction> transaction = new();
        transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the commit failed"));
        transaction.Setup(t => t.RollbackAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the transaction is already completed"));

        OrganizationStorageGuard guard = new(gate, transaction.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CommitAsync(CancellationToken.None));

        // Disposing must not let the rollback's failure mask the commit failure, and it must release the gate.
        await guard.DisposeAsync();

        transaction.Verify(t => t.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        gate.CurrentCount.Should().Be(1, "the organisation is free for the next acquirer");
    }
}
