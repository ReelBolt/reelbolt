using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Both services map the shared tables. A column the API stores as text (an enum with
/// <c>HasConversion&lt;string&gt;()</c>) that the engine maps without the same conversion is read as an
/// integer and every query on that table throws <see cref="InvalidCastException"/> at runtime, while
/// the InMemory provider used by the unit tests never notices. <c>project_files.moderation_status</c>
/// was exactly that: no media derivative, render or analysis could read a project file. This guard compares
/// the database type the two models give every column they both map.
/// </summary>
public class EngineApiColumnTypeDriftTests
{
    private static Dictionary<(string Table, string Column), string> ColumnTypes(IModel model)
    {
        var result = new Dictionary<(string, string), string>();
        foreach (IEntityType entity in model.GetEntityTypes())
        {
            string? table = entity.GetTableName();
            if (table is null) continue;
            StoreObjectIdentifier store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (IProperty property in entity.GetProperties())
            {
                string? column = property.GetColumnName(store);
                if (column is null) continue;
                Type provider = property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType;
                result[(table, column)] = (Nullable.GetUnderlyingType(provider) ?? provider).Name;
            }
        }

        return result;
    }

    [Fact]
    public void Every_column_both_services_map_has_the_same_provider_type()
    {
        using var api = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only").Options);
        using var engine = new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only").Options);

        Dictionary<(string Table, string Column), string> apiColumns = ColumnTypes(api.GetService<IDesignTimeModel>().Model);
        Dictionary<(string Table, string Column), string> engineColumns = ColumnTypes(engine.GetService<IDesignTimeModel>().Model);

        List<string> drift = engineColumns
            .Where(c => apiColumns.TryGetValue(c.Key, out string? apiType) && apiType != c.Value)
            .Select(c => $"{c.Key.Table}.{c.Key.Column}: engine reads {c.Value}, API stores {apiColumns[c.Key]}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        drift.Should().BeEmpty("a column mapped to a different database type in the two services is unreadable by one of them");
    }
}
