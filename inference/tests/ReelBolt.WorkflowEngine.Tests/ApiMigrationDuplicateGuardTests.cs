using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Replays every Inference API migration's Up operations in id order and fails when one ADDS a column, CREATES a table or
/// CREATES an index (by name) that an earlier migration already created and did not drop. On Postgres that is error 42701 /
/// 42P07 / 42P07 and the deploy's migrate step fails on every database. A migration generated against a stale model snapshot
/// once re-added <c>projects.csam_output_hold_at</c>, which an earlier migration had added.
/// <para>
/// An intentional re-add must be listed in <see cref="Allowed"/> with a reason; raw SQL (<c>migrationBuilder.Sql</c>) is not parsed.
/// </para>
/// </summary>
public class ApiMigrationDuplicateGuardTests
{
    /// <summary>Intentional re-adds: "migrationId|kind|name" to the reason. Empty on purpose.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal);

    internal static List<string> FindDuplicates(IEnumerable<(string Id, IReadOnlyList<MigrationOperation> Ops)> migrations)
    {
        Dictionary<string, string> created = new(StringComparer.Ordinal);
        List<string> duplicates = [];

        void Add(string id, string kind, string name)
        {
            string key = $"{kind}:{name}";
            if (created.TryGetValue(key, out string? earlier) && !Allowed.ContainsKey($"{id}|{kind}|{name}"))
                duplicates.Add($"{kind} {name}: created by {earlier} and again by {id}");
            created[key] = id;
        }

        foreach ((string id, IReadOnlyList<MigrationOperation> ops) in migrations.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            foreach (MigrationOperation op in ops)
            {
                switch (op)
                {
                    case AddColumnOperation add:
                        Add(id, "column", $"{add.Table}.{add.Name}");
                        break;
                    case DropColumnOperation drop:
                        created.Remove($"column:{drop.Table}.{drop.Name}");
                        break;
                    case CreateTableOperation table:
                        Add(id, "table", table.Name);
                        break;
                    case DropTableOperation dropTable:
                        created.Remove($"table:{dropTable.Name}");
                        break;
                    case CreateIndexOperation index:
                        Add(id, "index", index.Name);
                        break;
                    case DropIndexOperation dropIndex:
                        created.Remove($"index:{dropIndex.Name}");
                        break;
                }
            }
        }

        return duplicates;
    }

    private static List<(string Id, IReadOnlyList<MigrationOperation> Ops)> Committed() =>
        typeof(InferenceApiDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (Id: t.GetCustomAttribute<MigrationAttribute>()?.Id, Type: t))
            .Where(m => m.Id is not null)
            .Select(m => (m.Id!, ((Migration)Activator.CreateInstance(m.Type)!).UpOperations))
            .ToList();

    [Fact]
    public void No_api_migration_re_creates_a_column_table_or_index_an_earlier_one_created()
    {
        List<(string Id, IReadOnlyList<MigrationOperation> Ops)> all = Committed();
        all.Should().NotBeEmpty();

        FindDuplicates(all).Should().BeEmpty("a migration that re-creates an existing column, table or index fails on Postgres");
    }

    [Fact]
    public void The_guard_catches_a_re_added_column_table_and_index()
    {
        MigrationBuilder first = new(null);
        first.AddColumn<DateTime>(name: "csam_output_hold_at", table: "projects", nullable: true);
        first.CreateTable(name: "t", columns: c => new { id = c.Column<Guid>(nullable: false) });
        first.CreateIndex(name: "ix_t", table: "t", column: "id");
        MigrationBuilder second = new(null);
        second.AddColumn<DateTime>(name: "csam_output_hold_at", table: "projects", nullable: true);
        second.CreateTable(name: "t", columns: c => new { id = c.Column<Guid>(nullable: false) });
        second.CreateIndex(name: "ix_t", table: "t", column: "id");

        List<string> found = FindDuplicates([("1", first.Operations), ("2", second.Operations)]);

        found.Should().HaveCount(3);
        found.Should().Contain(f => f.StartsWith("column projects.csam_output_hold_at") && f.Contains("again by 2"));
    }

    [Fact]
    public void A_column_dropped_in_between_may_be_added_again()
    {
        MigrationBuilder one = new(null);
        one.AddColumn<string>(name: "c", table: "t", nullable: true);
        MigrationBuilder two = new(null);
        two.DropColumn(name: "c", table: "t");
        MigrationBuilder three = new(null);
        three.AddColumn<string>(name: "c", table: "t", nullable: true);

        FindDuplicates([("1", one.Operations), ("2", two.Operations), ("3", three.Operations)]).Should().BeEmpty();
    }
}
