#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// Drift guard, no Postgres needed: the engine logs in as a least-privilege role whose grants are the list
/// in <c>infra/postgres/engine-role.sql</c>. A table newly mapped by <see cref="WorkflowEngineDbContext"/>
/// with no entry there would be a permission error in production only, so it fails here first.
/// </summary>
public partial class EngineRoleSqlDriftTests
{
    private const string ScriptPath = "infra/postgres/engine-role.sql";

    [Fact]
    public void Every_table_the_engine_context_maps_is_owned_or_granted_in_the_engine_role_script()
    {
        string script = PsqlScriptRunner.Read(ScriptPath);
        HashSet<string> owned = QuotedNames(ArrayBody(script, "owned"));
        HashSet<string> granted = GrantEntry().Matches(ArrayBody(script, "grants")).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        owned.Should().NotBeEmpty();
        granted.Should().NotBeEmpty();

        using WorkflowEngineDbContext db = new(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql("Host=unused").Options);
        List<(string Table, bool EngineOwned)> mapped = db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .Select(e => (e.GetTableName()!, !e.IsTableExcludedFromMigrations()))
            .Distinct()
            .ToList();
        mapped.Should().NotBeEmpty();

        List<string> missing = [];
        foreach ((string table, bool engineOwned) in mapped)
        {
            if (engineOwned && !owned.Contains(table))
            {
                missing.Add($"{table} (the engine migrates it: add it to the 'owned' list)");
            }

            if (!granted.Contains(table))
            {
                missing.Add($"{table} (add '{table}: SELECT' plus only the writes the engine performs to the 'grants' list)");
            }
        }

        missing.Should().BeEmpty(
            $"every table WorkflowEngineDbContext maps needs an entry in {ScriptPath}, or the engine's database role "
            + "cannot touch it in production. Edit that file (and re-run it on each host).");
    }

    [Fact]
    public void The_runtime_role_is_granted_rows_only_and_owns_nothing()
    {
        string script = PsqlScriptRunner.Read(ScriptPath);
        string grants = ArrayBody(script, "grants");

        // Rows only: a TRIGGER, REFERENCES or TRUNCATE grant, or ALL, on the render host's login is the way back
        // to planting code that another login runs.
        foreach (string line in grants.Split('\n').Where(l => l.TrimStart().StartsWith('\'')))
        {
            line.Should().NotMatchRegex(@"\b(TRIGGER|REFERENCES|TRUNCATE|ALL)\b", line.Trim());
        }

        // Ownership and CREATE on the schema go to the owner role, never to the runtime role (variable r).
        script.Should().Contain("ALTER TABLE public.%I OWNER TO %I', t, own");
        script.Should().NotContain("OWNER TO %I', t, r");
        script.Should().Contain("'GRANT USAGE ON SCHEMA public TO %I', r");
        script.Should().NotContain("'GRANT USAGE, CREATE ON SCHEMA public TO %I', r");
    }

    [Fact]
    public void The_three_scripts_carry_the_same_closing_assertions()
    {
        static string Block(string path)
        {
            string script = PsqlScriptRunner.Read(path);
            int start = script.IndexOf("  roles text[] := string_to_array(current_setting('reelbolt.assert_roles'), ',');", StringComparison.Ordinal);
            start.Should().BeGreaterThan(-1, path);
            return script[start..script.IndexOf("END $$;", start, StringComparison.Ordinal)];
        }

        string engine = Block(ScriptPath);
        Block("infra/postgres/gateway-role.sql").Should().Be(engine);
        Block("infra/postgres/check-roles.sql").Should().Be(engine);
        // The block checks the engine tables by name, whoever owns them: the names are the script's 'owned' list.
        string script = PsqlScriptRunner.Read(ScriptPath);
        QuotedNames(ArrayBody(engine, "engine_tables")).Should().BeEquivalentTo(QuotedNames(ArrayBody(script, "owned")));
        QuotedNames(ArrayBody(script, "owned")).Should().HaveCount(11);
        // The block's privilege matrices are the two scripts' grant lists, entry for entry.
        Specs(ArrayBody(engine, "engine_matrix")).Should().Equal(Specs(ArrayBody(script, "grants")));
        Specs(ArrayBody(engine, "gateway_matrix")).Should().Equal(Specs(ArrayBody(PsqlScriptRunner.Read("infra/postgres/gateway-role.sql"), "grants")));
    }

    [Fact]
    public void Every_script_pins_search_path_first_in_its_transaction()
    {
        foreach (string path in new[] { ScriptPath, "infra/postgres/gateway-role.sql", "infra/postgres/check-roles.sql" })
        {
            string script = PsqlScriptRunner.Read(path);
            int begin = Regex.Match(script, @"\nBEGIN( TRANSACTION READ ONLY)?;\n").Index;
            begin.Should().BeGreaterThan(0, path);
            string firstStatement = script[(begin + 1)..].Split('\n').Skip(1).First(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal));
            firstStatement.Should().Be("SET LOCAL search_path = pg_catalog, pg_temp;", path);
        }
    }

    private static List<string> Specs(string body)
        => body.Split('\n').Select(l => l.Trim().TrimEnd(',')).Where(l => l.StartsWith('\'')).ToList();

    [Fact]
    public void The_engine_role_script_withholds_the_secret_columns()
    {
        string grants = ArrayBody(PsqlScriptRunner.Read(ScriptPath), "grants");
        string users = grants.Split('\n').Single(l => l.TrimStart().StartsWith("'application_users:", StringComparison.Ordinal));
        string keys = grants.Split('\n').Single(l => l.TrimStart().StartsWith("'api_keys:", StringComparison.Ordinal));

        users.Should().Contain("SELECT (").And.NotContain("password_hash").And.NotContain("signup_ip");
        keys.Should().Contain("SELECT (").And.NotContain("secret_hash");
    }

    /// <summary>The text between <c>name text[] := ARRAY[</c> and its closing <c>];</c>.</summary>
    private static string ArrayBody(string script, string name)
    {
        // The leading newline and indent keep 'grants' from matching inside 'owner_grants'.
        string open = $"\n  {name} text[] := ARRAY[";
        int start = script.IndexOf(open, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{ScriptPath} must declare the '{name}' array");
        int end = script.IndexOf("];", start, StringComparison.Ordinal);
        return script[(start + open.Length)..end];
    }

    private static HashSet<string> QuotedNames(string body)
        => QuotedName().Matches(body).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^\s*'(\w+)',?\s*$", RegexOptions.Multiline)]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"^\s*'(\w+): ", RegexOptions.Multiline)]
    private static partial Regex GrantEntry();
}
