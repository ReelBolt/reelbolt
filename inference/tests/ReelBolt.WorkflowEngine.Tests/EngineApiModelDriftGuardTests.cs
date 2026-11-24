using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The engine mirrors API-owned tables (ExcludeFromMigrations). If a column's provider type drifts
/// (e.g. enum stored as text by the API but mapped as int by the engine) every entity read throws
/// InvalidCastException at runtime. These tests compare the two EF models.
/// </summary>
public class EngineApiModelDriftGuardTests
{
    private static IModel BuildApi() =>
        new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite("Data Source=:memory:").Options).Model;

    private static IModel BuildEngine() =>
        new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseSqlite("Data Source=:memory:").Options).Model;

    private static Dictionary<string, IEntityType> ByTable(IModel model) =>
        model.GetEntityTypes()
            .Where(t => t.GetTableName() is not null && t.FindPrimaryKey() is not null)
            .GroupBy(t => t.GetTableName()!)
            .ToDictionary(g => g.Key, g => g.First());

    private static System.Type ProviderType(IProperty p) =>
        System.Nullable.GetUnderlyingType(p.GetTypeMapping().Converter?.ProviderClrType ?? p.ClrType)
        ?? p.GetTypeMapping().Converter?.ProviderClrType ?? p.ClrType;

    private static (string Provider, bool Nullable) Describe(IProperty p) =>
        ((ProviderType(p)).FullName!,
            p.IsColumnNullable());

    [Fact]
    public void EngineModel_MapsProjectFileModerationStatus_AsString()
    {
        var prop = BuildEngine().FindEntityType(typeof(ProjectFile))!
            .FindProperty(nameof(ProjectFile.ModerationStatus))!;
        ProviderType(prop).Should().Be(typeof(string));
    }

    [Fact]
    public void SharedTables_HaveSameColumnProviderTypesInBothModels()
    {
        var api = ByTable(BuildApi());
        var engine = ByTable(BuildEngine());
        var mismatches = new List<string>();

        foreach (var (table, engineType) in engine.Where(e => api.ContainsKey(e.Key)))
        {
            var apiType = api[table];
            var apiCols = apiType.GetProperties()
                .Where(p => p.GetColumnName() is not null)
                .ToDictionary(p => p.GetColumnName()!);
            foreach (var p in engineType.GetProperties().Where(p => p.GetColumnName() is not null))
            {
                if (!apiCols.TryGetValue(p.GetColumnName()!, out var ap)) continue;
                var e = Describe(p);
                var a = Describe(ap);
                if (e.Provider != a.Provider)
                    mismatches.Add($"{table}.{p.GetColumnName()}: engine {e.Provider} vs api {a.Provider}");
                else if (e.Nullable != a.Nullable)
                    mismatches.Add($"{table}.{p.GetColumnName()}: engine nullable={e.Nullable} vs api nullable={a.Nullable}");
            }
        }

        mismatches.Should().BeEmpty();
    }
}
