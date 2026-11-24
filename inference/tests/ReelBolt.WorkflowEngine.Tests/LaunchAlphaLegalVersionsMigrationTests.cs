using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Migrations;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The legal-version bump moves a row only while it holds a previously shipped default and was not
/// published by an operator (updated_by 'config' or an earlier migration marker).
/// </summary>
public class LaunchAlphaLegalVersionsMigrationTests
{
    private static string[] UpSql() => Sql(new LaunchAlphaLegalVersions().UpOperations);

    private static string[] Sql(System.Collections.Generic.IReadOnlyList<MigrationOperation> ops) =>
        ops.OfType<SqlOperation>().Select(o => o.Sql).ToArray();

    [Fact]
    public void Up_MovesTerms_FromBothEarlierDefaults_OnlyWhenNotOperatorPublished()
    {
        string terms = UpSql().Single(s => s.Contains("document = 'Terms'"));

        terms.Should().Contain("SET version = '2026-10-26'");
        terms.Should().Contain("version IN ('2026-10', '2026-10-22')");
        terms.Should().Contain("updated_by IN ('config', 'migration:AddCsamScanning')");
    }

    [Fact]
    public void Up_MovesPrivacy_FromBothEarlierDefaults_OnlyWhenNotOperatorPublished()
    {
        string privacy = UpSql().Single(s => s.Contains("document = 'Privacy'"));

        privacy.Should().Contain("SET version = '2026-10-26'");
        privacy.Should().Contain("version IN ('2026-10', '2026-10-12')");
        privacy.Should().Contain("updated_by IN ('config', 'migration:AddPromotionsAndMarketingConsent')");
    }

    [Fact]
    public void Down_RevertsOnlyRowsThisMigrationMoved()
    {
        string[] down = Sql(new LaunchAlphaLegalVersions().DownOperations);

        down.Should().HaveCount(2);
        down.Should().OnlyContain(s => s.Contains("updated_by = 'migration:LaunchAlphaLegalVersions'")
            && s.Contains("version = '2026-10-26'"));
    }
}
