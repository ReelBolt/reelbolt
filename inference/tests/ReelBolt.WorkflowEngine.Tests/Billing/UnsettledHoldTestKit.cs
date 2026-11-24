#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Billing;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// S-3 test kit: one Sqlite database holding BOTH the ledger (owned by the Inference API) and the
/// engine's workflow tables, so the gate and the sweeper see step results and holds side by side,
/// as they do on Postgres.
/// </summary>
internal sealed class UnsettledHoldTestKit : IDisposable
{
    public const long HoldMillis = 20_000;

    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=False");

    public UnsettledHoldTestKit()
    {
        _connection.Open();
        Options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options;
        using (InferenceApiDbContext owner = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options))
        {
            owner.Database.EnsureCreated();
        }

        AddEngineTables(_connection);
        Factory = new LedgerFactory(Options);
    }

    /// <summary>
    /// Adds the engine's own tables (workflow_step_results among them) to a Sqlite database the API
    /// schema was created in: they are ExcludeFromMigrations on the API side, so its EnsureCreated
    /// leaves them out, while on Postgres both schemas share one database. Idempotent.
    /// </summary>
    public static void AddEngineTables(SqliteConnection connection)
    {
        using WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(connection).Options);
        string script = engine.Database.GenerateCreateScript();
        script = Regex.Replace(script, @"CREATE TABLE """, @"CREATE TABLE IF NOT EXISTS """);
        script = Regex.Replace(script, @"CREATE (UNIQUE )?INDEX """, m => $"CREATE {m.Groups[1].Value}INDEX IF NOT EXISTS \"");
        foreach (string statement in script.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }
    }

    public DbContextOptions<WorkflowEngineDbContext> Options { get; }
    public IDbContextFactory<WorkflowEngineDbContext> Factory { get; }
    public Guid Org { get; } = Guid.NewGuid();

    public void Dispose() => _connection.Dispose();

    public CreditGate Gate() => new(Factory, Microsoft.Extensions.Options.Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<CreditGate>.Instance);

    public async Task GrantAsync(long millis)
    {
        await using WorkflowEngineDbContext db = Factory.CreateDbContext();
        (await new CreditLedger(db).GrantAsync(Org, CreditGrantSource.Plan, millis, null, Guid.NewGuid().ToString("N"))).Ok.Should().BeTrue();
    }

    public async Task<CreditLedgerResult> BalanceAsync()
    {
        await using WorkflowEngineDbContext db = Factory.CreateDbContext();
        return await new CreditLedger(db).GetBalanceAsync(Org);
    }

    public async Task<IReadOnlyList<CreditLedgerEntry>> EntriesAsync()
    {
        await using WorkflowEngineDbContext db = Factory.CreateDbContext();
        return await db.CreditLedgerEntries.AsNoTracking().ToListAsync();
    }

    /// <summary>
    /// An execution with one step result in the given state, and a hold on it. <paramref name="leaseOwner"/>
    /// and <paramref name="leaseExpiresAt"/> stamp the execution's lease as the engine would.
    /// </summary>
    public async Task<(Guid ExecutionId, Guid StepResultId, Guid ReserveId)> HoldAsync(
        StepStatus stepStatus,
        ExecutionStatus executionStatus = ExecutionStatus.Running,
        long? unsettledMillis = null,
        FailureClass failureClass = FailureClass.None,
        long holdMillis = HoldMillis,
        string? leaseOwner = null,
        DateTime? leaseExpiresAt = null)
    {
        Guid executionId = Guid.NewGuid();
        Guid stepResultId = Guid.NewGuid();
        await using (WorkflowEngineDbContext db = Factory.CreateDbContext())
        {
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = Guid.NewGuid(),
                OrganizationId = Org,
                Status = executionStatus,
                LeaseOwner = leaseOwner,
                LeaseExpiresAt = leaseExpiresAt
            });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = stepResultId,
                WorkflowExecutionId = executionId,
                WorkflowStepId = Guid.NewGuid(),
                Status = stepStatus,
                FailureClass = failureClass,
                CreditsUnsettledMillis = unsettledMillis
            });
            await db.SaveChangesAsync();
        }

        CreditReservation r = await Gate().ReserveAsync(Org, executionId, stepResultId, holdMillis);
        r.Allowed.Should().BeTrue();
        return (executionId, stepResultId, r.ReservationId!.Value);
    }

    public async Task RecordUsageEventAsync(Guid executionId, Guid stepResultId, long creditsMillis, bool unpriced = false)
    {
        await using WorkflowEngineDbContext db = Factory.CreateDbContext();
        db.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = Org,
            ExecutionId = executionId,
            StepResultId = stepResultId,
            Kind = UsageKind.LlmOutputTokens,
            Quantity = 1,
            ModelName = "m",
            CreditsMillis = creditsMillis,
            Unpriced = unpriced,
            IdempotencyKey = Guid.NewGuid().ToString("N")
        });
        await db.SaveChangesAsync();
    }

    private sealed class LedgerFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }
}

/// <summary>A clock fixed at one instant.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
