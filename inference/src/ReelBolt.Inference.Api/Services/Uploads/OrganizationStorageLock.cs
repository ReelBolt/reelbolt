using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// Serialises one organization's storage check-then-record: the quota preflight
/// (<c>PictureSourceCopies.CheckCopiesFitAsync</c>), the row writes that record the picture copies and
/// the deleted file rows, so a delete that is refused cannot race a delete that is admitted.
/// </summary>
/// <remarks>
/// <para>
/// The guarantee that holds <em>across replicas</em> is the PostgreSQL advisory lock
/// <c>pg_advisory_xact_lock(hashtext(org_id))</c> taken inside one transaction — the same key
/// <c>CreditLedger</c> and the assistant allowance hold take for an organization. It is held until
/// the guard's transaction commits or rolls back, so a second replica's check runs only after the
/// first replica's record is durable. On a relational provider without advisory locks (Sqlite) the
/// transaction is opened but no <c>pg_*</c> SQL is sent; the in-memory store cannot open a
/// transaction at all, so there the guard is the semaphore alone.
/// </para>
/// <para>
/// The per-replica <see cref="SemaphoreSlim"/> is only a cheap local fast path: it queues two guards
/// of the <em>same</em> process without each waiting on a pooled connection for the advisory lock,
/// and on its own it guarantees nothing across replicas. It is the only serialisation on a provider
/// without advisory locks, where the quota is therefore enforced per replica only.
/// </para>
/// <para>
/// Call sites: the three user-facing delete handlers (<c>ProjectFilesController.DeleteFolder</c>,
/// <c>ProjectFilesController.Delete</c>, <c>AssistantFileTools.ApplyDeleteProjectFile</c>),
/// <c>AssistantOutputTools.ApplySaveOutputAsFile</c>, and <c>UrlImportService</c>'s commit — which takes
/// the guard only for its authoritative quota re-check and row write, after its object upload (that byte
/// streaming holds no lock).
/// </para>
/// </remarks>
public class OrganizationStorageLock
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// One semaphore per organization that ever took the guard; never removed (a bounded, slow-growing key
    /// set). It is the per-replica fast path, and the only serialisation on a provider without advisory locks.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Takes the organization's guard: the per-replica semaphore, then one transaction holding the
    /// cross-replica advisory lock. Dispose the returned guard without committing to roll the
    /// transaction back; the semaphore is always released.
    /// </summary>
    public virtual async Task<OrganizationStorageGuard> AcquireAsync(InferenceApiDbContext db, Guid organizationId, CancellationToken ct)
    {
        SemaphoreSlim gate = _locks.GetOrAdd(organizationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // One transaction, whose only job is to scope the advisory lock below. It is opened on a
            // relational provider (Npgsql, and Sqlite in the tests); the in-memory store cannot open one at
            // all, so there the guard is the semaphore alone — the same fallback as a provider without
            // advisory locks. This is the house shape (see WorkflowStepReplacer.InTransactionAsync).
            IDbContextTransaction? owned = db.Database.CurrentTransaction is null && db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
                : null;

            try
            {
                if (db.Database.ProviderName == NpgsqlProvider)
                {
                    string lockKey = organizationId.ToString();
                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct).ConfigureAwait(false);
                }
            }
            catch
            {
                if (owned is not null)
                {
                    await owned.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            return new OrganizationStorageGuard(gate, owned);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }
}

/// <summary>
/// One organization's storage critical section, held by <see cref="OrganizationStorageLock"/>.
/// <see cref="CommitAsync"/> ends it successfully; <see cref="DisposeAsync"/> rolls an uncommitted
/// transaction back and always releases the per-replica semaphore, on every exit path (refusal,
/// exception, cancellation). The rollback is best-effort: a <see cref="CommitAsync"/> that threw must
/// not be followed by a rollback that masks the original exception.
/// </summary>
public sealed class OrganizationStorageGuard : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly IDbContextTransaction? _owned;
    private int _committed;
    private int _disposed;

    internal OrganizationStorageGuard(SemaphoreSlim gate, IDbContextTransaction? owned)
    {
        _gate = gate;
        _owned = owned;
    }

    /// <summary>Commits the guard's transaction (when it owns one) and marks the guard committed.</summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        if (_owned is not null)
        {
            await _owned.CommitAsync(ct).ConfigureAwait(false);
        }

        Volatile.Write(ref _committed, 1);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_owned is not null)
            {
                if (Volatile.Read(ref _committed) == 0)
                {
                    try
                    {
                        await _owned.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // A commit that threw may have left the transaction in a state that cannot be
                        // rolled back; that rollback failure must never mask the original exception. The
                        // transaction is still disposed below, which rolls an uncommitted one back.
                    }
                }

                await _owned.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
