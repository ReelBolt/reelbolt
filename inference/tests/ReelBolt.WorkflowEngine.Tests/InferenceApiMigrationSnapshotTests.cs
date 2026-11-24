using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Guards the Inference API migration snapshot against drift, configured the way its Program.cs
/// does (Npgsql, history table <c>__EFMigrationsHistory_Api</c>). The workflow tables are
/// ExcludeFromMigrations in this context, so mapping a workflow_steps column (e.g.
/// VideoGenerateConfigJson as jsonb) must not produce a pending API migration. Never opens a
/// connection — <c>HasPendingModelChanges</c> is a pure model diff.
/// </summary>
public class InferenceApiMigrationSnapshotTests
{
    [Fact]
    public void InferenceApiDbContext_has_no_pending_model_changes()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=none;Username=x;Password=y",
                o => o.MigrationsHistoryTable("__EFMigrationsHistory_Api"))
            .Options;

        using var db = new InferenceApiDbContext(options);

        db.Database.HasPendingModelChanges().Should().BeFalse(
            because: "every API-owned model change must ship with an Inference API migration");
    }
}
