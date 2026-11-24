using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;
using ReelBolt.WorkflowEngine.Data.BiReadOnlyRole;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The operator script (<c>infra/powerbi/postgres-readonly-role.sql</c>) IS the artifact that creates
/// the <c>powerbi_ro</c> role and applies its grants now that no migration does (initiative
/// <c>powerbi-readonly-role</c>); <see cref="PowerBiReadOnlyRoleSql"/> and
/// <see cref="PowerBiReadOnlyRoleEngineSql"/> are the machine-readable registry the guard test
/// (<see cref="PowerBiReadOnlyRoleGuardTests"/>) reasons about. This test ties the two together: it
/// extracts the two GENERATED blocks from the script and asserts each equals the corresponding
/// constant's <c>Up</c> exactly, after normalising line endings and trailing whitespace. Without it
/// the registry the guard checks could drift from what the script actually applies, and the guard
/// would keep passing while the deployed grants changed.
/// <para>
/// The construction is the one <see cref="ApiMigrationOwnershipGuardTests"/> uses to find the
/// repository root.
/// </para>
/// </summary>
public class PowerBiReadOnlyRoleScriptDriftTests
{
    private const string ScriptRelativePath = "infra/powerbi/postgres-readonly-role.sql";

    [Fact]
    public void The_api_generated_block_equals_the_api_registry_up_sql()
    {
        string block = ExtractBlock(ReadScript(), "api");
        block.Should().NotBeNullOrWhiteSpace("the api GENERATED block must not be empty");
        Normalise(block).Should().Be(
            Normalise(PowerBiReadOnlyRoleSql.Up),
            "the api GENERATED block in {0} must be exactly PowerBiReadOnlyRoleSql.Up - edit the constant, not the script",
            ScriptRelativePath);
    }

    [Fact]
    public void The_engine_generated_block_equals_the_engine_registry_up_sql()
    {
        string block = ExtractBlock(ReadScript(), "engine");
        block.Should().NotBeNullOrWhiteSpace("the engine GENERATED block must not be empty");
        Normalise(block).Should().Be(
            Normalise(PowerBiReadOnlyRoleEngineSql.Up),
            "the engine GENERATED block in {0} must be exactly PowerBiReadOnlyRoleEngineSql.Up - edit the constant, not the script",
            ScriptRelativePath);
    }

    private static string ReadScript()
    {
        string path = Path.Combine(RepositoryRoot(), ScriptRelativePath);
        File.Exists(path).Should().BeTrue("the operator script must exist at {0}", path);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The lines strictly between <c>-- &gt;&gt;&gt; BEGIN GENERATED: {name}</c> and
    /// <c>-- &lt;&lt;&lt; END GENERATED: {name}</c>, joined with "\n". The marker lines themselves are
    /// not part of the block.
    /// </summary>
    private static string ExtractBlock(string script, string name)
    {
        string[] lines = NormaliseNewlines(script).Split('\n');
        string begin = $"-- >>> BEGIN GENERATED: {name}";
        string end = $"-- <<< END GENERATED: {name}";

        int start = Array.IndexOf(lines, begin);
        start.Should().BeGreaterThanOrEqualTo(0, "the script must carry the '{0}' begin marker", begin);
        int stop = Array.IndexOf(lines, end);
        stop.Should().BeGreaterThan(start, "the script must carry the '{0}' end marker after the begin marker", end);

        return string.Join("\n", lines[(start + 1)..stop]);
    }

    private static string Normalise(string text) =>
        string.Join("\n", NormaliseNewlines(text).Split('\n').Select(line => line.TrimEnd()));

    private static string NormaliseNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(PowerBiReadOnlyRoleScriptDriftTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln"))) return dir;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
