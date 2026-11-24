using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The Inference API migrates first on a new database and the WorkflowEngine only after the API is
/// healthy, so an API migration that alters a table the API maps with <c>ExcludeFromMigrations()</c>
/// (an engine-owned table such as <c>workflow_steps</c>) crashes every fresh install: the table does
/// not exist yet. That happened once (<c>AddDailiesRoomConfigJson</c>). A column on an engine table
/// belongs in an engine migration; this guard fails the build when an API migration addresses one
/// through a schema operation again.
/// </summary>
public class ApiMigrationOwnershipGuardTests
{
    [Fact]
    public void No_api_migration_alters_a_table_the_api_excludes_from_its_migrations()
    {
        using var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options);
        string[] excluded = db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(e => e.IsTableExcludedFromMigrations())
            .Select(e => e.GetTableName())
            .OfType<string>()
            .Distinct()
            .ToArray();
        excluded.Should().Contain("workflow_steps", "the guard must actually see the engine-owned tables");

        string migrations = Path.Combine(RepositoryRoot(), "inference/src/ReelBolt.Inference.Api/Migrations");
        var offenders = Directory.GetFiles(migrations, "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal) && !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .SelectMany(f => excluded
                .Where(table => Regex.IsMatch(File.ReadAllText(f), $"table:\\s*\"{Regex.Escape(table)}\""))
                .Select(table => $"{Path.GetFileName(f)} -> {table}"))
            .ToList();

        offenders.Should().BeEmpty("an engine-owned table is migrated by the WorkflowEngine, which runs after the API");
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(ApiMigrationOwnershipGuardTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln"))) return dir;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
