using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Guards the WorkflowEngine migration snapshot against drift: the current model must equal what
/// the committed migrations (and <c>WorkflowEngineDbContextModelSnapshot</c>) describe. Uses the
/// Npgsql provider so the compared model is the real relational one, but never opens a connection —
/// <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"/>'s
/// <c>HasPendingModelChanges</c> is a pure model diff.
/// </summary>
public class WorkflowEngineMigrationSnapshotTests
{
    [Fact]
    public void WorkflowEngineDbContext_has_no_pending_model_changes()
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseNpgsql("Host=localhost;Database=none;Username=x;Password=y",
                o => o.MigrationsHistoryTable("__EFMigrationsHistory_Workflow"))
            .Options;

        using var db = new WorkflowEngineDbContext(options);

        db.Database.HasPendingModelChanges().Should().BeFalse(
            because: "every model change must ship with a WorkflowEngine migration (dotnet-ef migrations add)");
    }
}
