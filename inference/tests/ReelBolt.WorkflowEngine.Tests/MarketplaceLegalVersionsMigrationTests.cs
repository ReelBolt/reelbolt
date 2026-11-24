using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Migrations;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The recipe marketplace legal bump (Terms + Privacy 2026-11-10, Terms = re-acceptance) moves each row
/// only while it still holds the previously shipped default and was not published by an operator.
/// </summary>
public class MarketplaceLegalVersionsMigrationTests
{
    private static string[] Sql(IReadOnlyList<MigrationOperation> ops) =>
        ops.OfType<SqlOperation>().Select(o => o.Sql).ToArray();

    private static string[] UpSql() => Sql(new MarketplaceLegalVersions().UpOperations);

    [Theory]
    [InlineData("Terms")]
    [InlineData("Privacy")]
    public void Up_MovesTheDocument_FromTheShippedDefault_OnlyWhenNotOperatorPublished(string document)
    {
        string sql = UpSql().Single(s => s.Contains($"document = '{document}'"));

        sql.Should().Contain("SET version = '2026-11-10'");
        sql.Should().Contain("version = '2026-11-05'");
        sql.Should().Contain("updated_by IN ('config', 'migration:AvatarsLegalVersions')");
    }

    [Fact]
    public void Down_RevertsOnlyRowsThisMigrationMoved()
    {
        string[] down = Sql(new MarketplaceLegalVersions().DownOperations);

        down.Should().HaveCount(2);
        down.Should().OnlyContain(s => s.Contains("updated_by = 'migration:MarketplaceLegalVersions'")
            && s.Contains("version = '2026-11-10'"));
    }

    [Fact]
    public void FreshInstalls_StartAtTheSameVersions()
    {
        LegalDocumentSeeder.DefaultTermsVersion.Should().Be("2026-11-17");
        // AvatarLibraryLegalVersions moved both to 2026-11-17 (objects, pictures, workspace library).
        LegalDocumentSeeder.DefaultPrivacyVersion.Should().Be("2026-11-17");
    }
}
