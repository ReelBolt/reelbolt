using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Zero-downtime deploys run the old and the new release against one database, so a migration may only
/// EXPAND the schema. This text scan fails any migration newer than <see cref="Baseline"/> whose Up()
/// contracts it, unless the file carries a "// contract-migration: reason" comment.
/// </summary>
public class MigrationExpandOnlyGuardTests
{
    /// <summary>The newest migration at the commit that introduced this guard. Older files are not scanned.</summary>
    private const string Baseline = "20261028181725_AllowMultiTargetStepChangeRequests.cs";

    private static readonly string[] MigrationDirs =
    [
        "inference/src/ReelBolt.Inference.Api/Migrations",
        "inference/src/ReelBolt.WorkflowEngine/Migrations",
    ];

    private const string Explanation =
        "Deploys are blue-green, so the old release keeps running against the new schema: expand first (add nullable/defaulted things), " +
        "and contract (drop, rename, tighten) only in a later release once nothing reads the old shape. If this migration really is a contract step, " +
        "add a comment `// contract-migration(<CheckName>): <reason, naming the release that stopped using it>` to the file, naming only the check(s) it exempts; see docs/ci.md.";

    [Fact]
    public void Baseline_constant_names_a_real_migration_file()
    {
        MigrationDirs.Any(d => File.Exists(Path.Combine(RepositoryRoot(), d, Baseline))).Should().BeTrue(
            "the baseline constant must name a real migration so the scan cannot silently cover nothing");
    }

    [Fact]
    public void No_new_migration_contracts_the_schema_without_a_justification()
    {
        var offenders = new List<string>();
        foreach (string dir in MigrationDirs)
        {
            foreach (string file in Directory.GetFiles(Path.Combine(RepositoryRoot(), dir), "2*.cs"))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".Designer.cs", StringComparison.Ordinal)) continue;
                if (string.CompareOrdinal(name, Baseline) <= 0) continue;
                offenders.AddRange(Scan(File.ReadAllText(file)).Select(v => $"{name}: {v}"));
            }
        }

        offenders.Should().BeEmpty(Explanation);
    }

    // ---- the scanner ----

    private static readonly string[] ContractingCalls =
    [
        "DropColumn", "DropTable", "RenameColumn", "RenameTable", "DropForeignKey", "AlterColumn", "AddCheckConstraint",
        "DropIndex", "DropPrimaryKey", "DropUniqueConstraint", "DropCheckConstraint", "RenameIndex", "DeleteData",
    ];


    private static readonly Regex Marker = new(@"//[ \t]*contract-migration\((?<checks>[\w, ]+)\):[ \t]*\S", RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new(
        "\"\"\"(?:.|\\n)*?\"\"\"|\\$?@\\$?\"(?:[^\"]|\"\")*\"|\\$?\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled);

    /// <summary>Builder methods that only expand (or are flagged by their own check above). Anything else on the builder, including extension methods, needs a hand review.</summary>
    private static readonly string[] KnownBuilderMethods =
    [
        "AddColumn", "CreateTable", "CreateIndex", "AddForeignKey", "AddUniqueConstraint", "AddPrimaryKey", "Sql",
        "InsertData", "UpdateData", "EnsureSchema", "CreateSequence", "AlterDatabase",
        .. ContractingCalls,
    ];

    internal static IReadOnlyList<string> Scan(string source)
    {
        // A marker is check specific: `// contract-migration(DropColumn, Sql): reason`. It exempts only the named checks.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Marker.Matches(source))
            foreach (string c in m.Groups["checks"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                allowed.Add(c);

        var found = new List<string>();

        void Flag(string check, string message)
        {
            if (!allowed.Contains(check)) found.Add($"[{check}] {message}");
        }

        // Comments are blanked and, for the brace match, string contents too; both keep the original length.
        string code = Mask(source, blankStrings: false);
        string blank = Mask(source, blankStrings: true);

        Match sig = Regex.Match(blank, @"\bvoid\s+Up\s*\(\s*[\w.]+\s+(?<p>\w+)\s*\)\s*\{");
        if (!sig.Success)
        {
            Flag("HelperSql", "no Up(MigrationBuilder x) { ... } found to scan: review by hand");
            return found;
        }

        int open = sig.Index + sig.Length - 1;
        int close = open;
        for (int depth = 0; close < blank.Length; close++)
        {
            if (blank[close] == '{') depth++;
            else if (blank[close] == '}' && --depth == 0) break;
        }

        string up = code[(open + 1)..Math.Min(close, code.Length)];
        string builder = sig.Groups["p"].Value;

        foreach (Match m in Regex.Matches(up, @"\b(" + string.Join("|", ContractingCalls) + @")\b"))
            Flag(m.Value, $"{m.Value} contracts or changes existing data/schema");

        var createdTables = new HashSet<string>(
            Regex.Matches(up, @"CreateTable\(\s*name:\s*""(?<t>[^""]+)""").Select(m => m.Groups["t"].Value));

        var newNullableColumns = new HashSet<string>(
            Calls(up, "AddColumn").Where(c => Regex.IsMatch(c.Args, @"nullable:\s*true\b")).Select(c =>
                $"{Regex.Match(c.Args, @"table:\s*""(?<t>[^""]+)""").Groups["t"].Value}.{Regex.Match(c.Args, @"name:\s*""(?<n>[^""]+)""").Groups["n"].Value}"));

        foreach ((string _, string args) in Calls(up, "AddColumn"))
        {
            bool nullable = Regex.IsMatch(args, @"nullable:\s*true\b");
            bool realDefault = Regex.IsMatch(args, @"defaultValueSql:") || Regex.IsMatch(args, @"defaultValue:\s*(?!null\b)\S");
            if (!Regex.IsMatch(args, @"nullable:"))
                Flag("AddColumn", "AddColumn without a nullable: argument defaults to NOT NULL and breaks the old release's inserts");
            else if (!nullable && !realDefault)
                Flag("AddColumn", "AddColumn nullable: false without a real defaultValue/defaultValueSql (defaultValue: null is none) breaks the old release's inserts");
        }

        foreach ((string call, string args) in Calls(up, "CreateIndex", "AddUniqueConstraint", "AddPrimaryKey", "AddForeignKey"))
        {
            bool unique = call != "CreateIndex" || Regex.IsMatch(args, @"unique:\s*true");
            string table = Regex.Match(args, @"table:\s*""(?<t>[^""]+)""").Groups["t"].Value;
            // A foreign key on a nullable column this same migration adds cannot be violated: every existing row is NULL.
            string fkColumn = Regex.Match(args, @"column:\s*""(?<c>[^""]+)""").Groups["c"].Value;
            if (call == "AddForeignKey" && newNullableColumns.Contains($"{table}.{fkColumn}")) continue;
            if (unique && !createdTables.Contains(table))
                Flag(call switch { "CreateIndex" => "UniqueIndex", _ => call }, $"{call} adds a constraint to the existing table '{table}' that the old release may violate");
        }

        foreach ((string _, string arg) in Calls(up, "Sql"))
        {
            string rest = StringLiteral.Replace(arg, string.Empty);
            bool interpolated = Regex.IsMatch(arg, @"\$@?""[^""]*\{") || Regex.IsMatch(arg, @"@\$""[^""]*\{");
            if (Regex.IsMatch(rest, @"[^\s+]") || interpolated)
            {
                Flag("HelperSql", "raw Sql( calls a helper: review by hand");
                if (!StringLiteral.IsMatch(arg)) continue;
            }

            ScanSqlText(string.Join(" ", StringLiteral.Matches(arg).Select(m => m.Value)), Flag, createdTables);
        }

        // The builder may only be used through a known method. A call to anything else on it (an extension such as
        // .Custom()), or the builder handed to a helper at any argument position, assigned or returned, is a hand review.
        string known = string.Join("|", KnownBuilderMethods);
        if (Regex.IsMatch(up, $@"(?<![.\w]){Regex.Escape(builder)}\b(?!\s*\.\s*(?:{known})\b)"))
            Flag("HelperSql", $"the MigrationBuilder '{builder}' is used other than through a known builder method (unknown/extension method, or passed on): review by hand");

        return found;
    }

    /// <summary>Copy of <paramref name="s"/> with comments (and optionally string contents) replaced by spaces, newlines kept.</summary>
    private static string Mask(string s, bool blankStrings)
    {
        var o = new System.Text.StringBuilder(s);
        void Blank(int from, int to)
        {
            for (int k = from; k < to && k < o.Length; k++) if (o[k] != '\n') o[k] = ' ';
        }

        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                int e = s.IndexOf('\n', i);
                e = e < 0 ? s.Length : e;
                Blank(i, e);
                i = e;
            }
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? s.Length : e + 2;
                Blank(i, e);
                i = e - 1;
            }
            else if (s[i] == '\'')
            {
                // Char literal such as 'x' or '"': skip it so a quote inside cannot open a string.
                int e = i + 1 < s.Length && s[i + 1] == '\\' ? i + 3 : i + 2;
                if (e < s.Length && s[e] == '\'') i = e;
            }
            else if (s[i] == '"')
            {
                int n = 1;
                while (i + n < s.Length && s[i + n] == '"') n++;
                int end;
                if (n >= 3)
                {
                    end = s.IndexOf(new string('"', n), i + n, StringComparison.Ordinal);
                    end = end < 0 ? s.Length : end;
                    if (blankStrings) Blank(i + n, end);
                    i = Math.Min(end + n - 1, s.Length - 1);
                }
                else
                {
                    end = SkipString(s, i);
                    if (blankStrings) Blank(i + 1, end);
                    i = end;
                }
            }
        }

        return o.ToString();
    }


    private static void ScanSqlText(string sql, Action<string, string> flag, ISet<string>? createdTables = null)
    {
        foreach (string stmt in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Regex.IsMatch(stmt, @"\bDROP\b|\bRENAME\b|\bTRUNCATE\b|\bDELETE\b|ALTER\s+.*\bTYPE\b|SET\s+NOT\s+NULL", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                flag("Sql", "raw Sql( contains a contracting statement");
            if (Regex.IsMatch(stmt, @"ALTER\s+TABLE", RegexOptions.IgnoreCase) && Regex.IsMatch(stmt, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(stmt, @"\bDEFAULT\b", RegexOptions.IgnoreCase))
                flag("Sql", "raw Sql( adds NOT NULL without a DEFAULT");
            if (Regex.IsMatch(stmt, @"ALTER\s+TABLE", RegexOptions.IgnoreCase) && Regex.IsMatch(stmt, @"\bCHECK\b", RegexOptions.IgnoreCase))
                flag("Sql", "raw Sql( adds a CHECK constraint to an existing table");
            if (Regex.IsMatch(stmt, @"ALTER\s+TABLE", RegexOptions.IgnoreCase) && Regex.IsMatch(stmt, @"\bFOREIGN\s+KEY\b|\bREFERENCES\b", RegexOptions.IgnoreCase))
                flag("Sql", "raw Sql( adds a foreign key to an existing table");
            if (Regex.IsMatch(stmt, @"CREATE\s+UNIQUE\s+INDEX|ADD\s+(CONSTRAINT\s+\S+\s+)?(UNIQUE|PRIMARY\s+KEY)\b", RegexOptions.IgnoreCase)
                && !TargetsCreatedTable(stmt, createdTables))
                flag("Sql", "raw Sql( adds a uniqueness rule to an existing table");
        }
    }

    /// <summary>True when the statement's table is one this same migration creates, so no old-release row can violate the rule.</summary>
    private static bool TargetsCreatedTable(string stmt, ISet<string>? createdTables)
    {
        if (createdTables is null || createdTables.Count == 0) return false;
        Match m = Regex.Match(stmt, @"\bON\s+(?:ONLY\s+)?\\?""?(?<t>\w+)|ALTER\s+TABLE\s+(?:ONLY\s+)?\\?""?(?<t>\w+)", RegexOptions.IgnoreCase);
        return m.Success && createdTables.Contains(m.Groups["t"].Value);
    }

    /// <summary>Every <c>Name&lt;generic&gt;(args)</c> call in <paramref name="text"/>, with balanced generics and parentheses.</summary>
    private static IEnumerable<(string Name, string Args)> Calls(string text, params string[] names)
    {
        foreach (Match m in Regex.Matches(text, @"\b(" + string.Join("|", names) + @")\b"))
        {
            int i = m.Index + m.Length;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i < text.Length && text[i] == '<')
            {
                for (int depth = 0; i < text.Length; i++)
                {
                    if (text[i] == '<') depth++;
                    else if (text[i] == '>' && --depth == 0) { i++; break; }
                }
            }

            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '(') continue;
            int start = ++i;
            for (int depth = 1; i < text.Length; i++)
            {
                if (text[i] == '"') { i = SkipString(text, i); continue; }
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) break;
            }

            yield return (m.Value, text[start..Math.Min(i, text.Length)]);
        }
    }

    private static int SkipString(string text, int i)
    {
        bool verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 1] == '$' && text[i - 2] == '@'));
        for (i++; i < text.Length; i++)
        {
            if (!verbatim && text[i] == '\\') { i++; continue; }
            if (text[i] == '"')
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                return i;
            }
        }

        return i;
    }

    [Theory]
    [InlineData("DropColumn")]
    [InlineData("DropTable")]
    [InlineData("RenameColumn")]
    [InlineData("RenameTable")]
    [InlineData("DropForeignKey")]
    [InlineData("AlterColumn")]
    [InlineData("AddCheckConstraint")]
    [InlineData("DropIndex")]
    [InlineData("DropPrimaryKey")]
    [InlineData("DropUniqueConstraint")]
    public void Scanner_catches_contracting_builder_calls(string call)
    {
        Scan($"void Up(MigrationBuilder b){{ b.{call}(name: \"x\"); }}").Should().NotBeEmpty();
    }

    [Fact]
    public void Scanner_catches_non_nullable_columns_even_with_nested_generics()
    {
        Scan("void Up(MigrationBuilder b){ b.AddColumn<int>(name: \"a\", table: \"t\", nullable: false); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder b){ b.AddColumn<Dictionary<string, List<int>>>(name: \"a\", table: \"t\", nullable: false); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder b){ b.AddColumn<int>(name: \"a\", table: \"t\", nullable: false, defaultValueSql: \"0\"); }").Should().BeEmpty();
        Scan("void Up(MigrationBuilder b){ b.AddColumn<int>(name: \"a\", table: \"t\", nullable: true); }").Should().BeEmpty();
    }

    [Fact]
    public void Scanner_catches_uniqueness_added_to_existing_tables_only()
    {
        Scan("void Up(MigrationBuilder b){ b.CreateIndex(name: \"i\", table: \"t\", column: \"c\", unique: true); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder b){ b.AddUniqueConstraint(name: \"u\", table: \"t\", column: \"c\"); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder b){ b.AddPrimaryKey(name: \"pk\", table: \"t\", column: \"c\"); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder b){ b.CreateIndex(name: \"i\", table: \"t\", column: \"c\"); }").Should().BeEmpty();
        Scan("void Up(MigrationBuilder b){ b.CreateTable(name: \"t\", columns: null); b.CreateIndex(name: \"i\", table: \"t\", column: \"c\", unique: true); }")
            .Should().BeEmpty("the table is new, so no old-release row can violate it");
    }

    [Fact]
    public void Scanner_lets_raw_sql_uniqueness_on_a_table_the_same_migration_creates()
    {
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.CreateTable(name: \"t\", columns: null); migrationBuilder.Sql(\"CREATE UNIQUE INDEX i ON t ((1))\"); }")
            .Should().BeEmpty("the table is new");
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.CreateTable(name: \"t\", columns: null); migrationBuilder.Sql(\"CREATE UNIQUE INDEX i ON other (c)\"); }")
            .Should().NotBeEmpty("another table is old");
    }

    [Fact]
    public void Scanner_lets_a_foreign_key_on_a_nullable_column_the_same_migration_adds()
    {
        Scan("void Up(MigrationBuilder b){ b.AddColumn<Guid>(name: \"f\", table: \"t\", nullable: true); b.AddForeignKey(name: \"k\", table: \"t\", column: \"f\", principalTable: \"p\", principalColumn: \"id\"); }")
            .Should().BeEmpty("every existing row is NULL");
        Scan("void Up(MigrationBuilder b){ b.AddForeignKey(name: \"k\", table: \"t\", column: \"f\", principalTable: \"p\", principalColumn: \"id\"); }")
            .Should().NotBeEmpty("an existing column may hold values");
    }

    [Theory]
    [InlineData("DROP INDEX x")]
    [InlineData("alter table t rename to u")]
    [InlineData("ALTER TABLE t ALTER COLUMN c TYPE text")]
    [InlineData("ALTER TABLE t ALTER c SET NOT NULL")]
    [InlineData("ALTER TABLE t ADD COLUMN c int NOT NULL")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT k CHECK (c > 0)")]
    [InlineData("CREATE UNIQUE INDEX i ON t (c)")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT u UNIQUE (c)")]
    [InlineData("DELETE FROM t")]
    [InlineData("truncate t")]
    [InlineData("DROP\\nTABLE t")]
    public void Scanner_catches_contracting_raw_sql(string sql)
    {
        Scan($"void Up(MigrationBuilder migrationBuilder){{ migrationBuilder.Sql(\"{sql}\"); }}").Should().NotBeEmpty();
    }

    [Fact]
    public void Scanner_reads_every_statement_of_a_multi_statement_sql_string()
    {
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.Sql(\"CREATE INDEX i ON t (c); DROP INDEX old;\"); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.Sql(\"UPDATE t SET c = ('a;b'); DELETE FROM t WHERE x = 1;\"); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.Sql(@\"CREATE INDEX i ON t (c);\n  ALTER TABLE t DROP COLUMN c;\"); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.Sql(\"CREATE INDEX i ON t (c); CREATE INDEX j ON t (d);\"); }").Should().BeEmpty();
    }

    [Fact]
    public void Scanner_flags_helper_sql_in_any_form_but_accepts_a_literal_expand()
    {
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.Sql(SomeHelper.Grant); }").Should().Contain(v => v.Contains("review by hand"));
        Scan("void Up(MigrationBuilder migrationBuilder){ SomeHelper.Apply(migrationBuilder); }").Should().Contain(v => v.Contains("review by hand"));
        Scan("void Up(MigrationBuilder migrationBuilder){ Apply(migrationBuilder); }").Should().Contain(v => v.Contains("review by hand"));
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.Sql(\"CREATE INDEX i ON t (c);\" + Helper.Drop); }").Should().Contain(v => v.Contains("review by hand"));
        Scan("void Up(MigrationBuilder migrationBuilder){ migrationBuilder.Sql(\"CREATE INDEX i ON t (c)\"); }").Should().BeEmpty();
    }

    [Fact]
    public void Scanner_scans_up_even_when_down_comes_first_or_a_comment_mentions_it()
    {
        Scan("protected override void Down(MigrationBuilder b){ b.DropColumn(\"a\"); }\nprotected override void Up(MigrationBuilder b){ b.DropTable(name: \"t\"); }")
            .Should().ContainSingle(v => v.Contains("DropTable"));
        Scan("// void Down(MigrationBuilder b) is empty\nvoid Up(MigrationBuilder b){ b.DropTable(name: \"t\"); }\nvoid Down(MigrationBuilder b){ }")
            .Should().ContainSingle(v => v.Contains("DropTable"));
        Scan("void Up(MigrationBuilder b){ /* b.DropTable(\"t\") */ // b.DropColumn(\"c\")\n b.AddColumn<int>(name: \"a\", table: \"t\", nullable: true); }")
            .Should().BeEmpty("commented code is not scanned");
        Scan("void Up(MigrationBuilder b){ if (x) { b.DropTable(name: \"t\"); } var s = \"}\"; }\nvoid Down(MigrationBuilder b){ }")
            .Should().NotBeEmpty("a closing brace inside a nested block or string does not end Up early");
        Scan("void Down(MigrationBuilder b){ }").Should().Contain(v => v.Contains("review by hand"), "no Up to scan is itself a finding");
    }

    [Fact]
    public void Scanner_takes_the_builder_name_from_the_signature_and_flags_unknown_or_passed_use()
    {
        Scan("void Up(MigrationBuilder zz){ zz.Custom(); }").Should().Contain(v => v.Contains("review by hand"));
        Scan("void Up(MigrationBuilder zz){ Helper.Apply(x, zz); }").Should().Contain(v => v.Contains("review by hand"));
        Scan("void Up(MigrationBuilder zz){ Helper.Apply(zz, x); }").Should().Contain(v => v.Contains("review by hand"));
        Scan("void Up(MigrationBuilder zz){ var c = zz; }").Should().Contain(v => v.Contains("review by hand"));
        Scan("void Up(MigrationBuilder zz){ zz.CreateIndex(name: \"i\", table: \"t\", column: \"c\"); }").Should().BeEmpty();
    }

    [Fact]
    public void Scanner_flags_add_column_with_nullable_omitted_or_a_null_default()
    {
        Scan("void Up(MigrationBuilder b){ b.AddColumn<int>(name: \"a\", table: \"t\"); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder b){ b.AddColumn<string>(name: \"a\", table: \"t\", nullable: false, defaultValue: null); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder b){ b.AddColumn<string>(name: \"a\", table: \"t\", nullable: false, defaultValue: \"x\"); }").Should().BeEmpty();
    }

    [Fact]
    public void Scanner_flags_a_foreign_key_on_an_existing_table_only()
    {
        Scan("void Up(MigrationBuilder b){ b.AddForeignKey(name: \"fk\", table: \"t\", column: \"c\", principalTable: \"p\"); }").Should().NotBeEmpty();
        Scan("void Up(MigrationBuilder b){ b.CreateTable(name: \"t\", columns: null); b.AddForeignKey(name: \"fk\", table: \"t\", column: \"c\", principalTable: \"p\"); }").Should().BeEmpty();
        Scan("void Up(MigrationBuilder b){ b.Sql(\"ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (c) REFERENCES p (id)\"); }").Should().NotBeEmpty();
    }

    [Fact]
    public void Scanner_ignores_down()
    {
        Scan("void Up(MigrationBuilder b){ b.AddColumn<int>(name:\"a\", table:\"t\", nullable: true); }\nprotected override void Down(MigrationBuilder b){ b.DropColumn(\"a\"); }")
            .Should().BeEmpty("Down() is not scanned");
    }

    [Fact]
    public void Escape_hatch_is_check_specific_and_needs_a_reason()
    {
        Scan("// contract-migration(DropColumn): v0.9 stopped reading t.old\nvoid Up(MigrationBuilder b){ b.DropColumn(\"old\"); }").Should().BeEmpty();
        Scan("// contract-migration(DropColumn): v0.9 stopped reading t.old\nvoid Up(MigrationBuilder b){ b.DropColumn(\"old\"); b.DropTable(\"t\"); }")
            .Should().ContainSingle(v => v.Contains("DropTable"), "one marker must not exempt every other check");
        Scan("// contract-migration(DropColumn): ok\nvoid Up(MigrationBuilder b){ b.DropColumn(\"old\"); b.Sql(\"DELETE FROM t\"); }")
            .Should().NotBeEmpty();
        Scan("// contract-migration(DropColumn):\nvoid Up(MigrationBuilder b){ b.DropColumn(\"old\"); }").Should().NotBeEmpty("a marker with no reason does not count");
        Scan("// contract-migration: blanket\nvoid Up(MigrationBuilder b){ b.DropColumn(\"old\"); }").Should().NotBeEmpty("a marker must name the check it exempts");
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(MigrationExpandOnlyGuardTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln"))) return dir;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
