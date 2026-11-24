using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Migrations;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The avatar library legal bump (Terms + Privacy 2026-11-17, Terms = re-acceptance) moves each row only
/// while it still holds the previously shipped default and was not published by an operator.
/// </summary>
public class AvatarLibraryLegalVersionsMigrationTests
{
    private static string[] Sql(IReadOnlyList<MigrationOperation> ops) =>
        ops.OfType<SqlOperation>().Select(o => o.Sql).ToArray();

    [Fact]
    public void Up_MovesTerms_FromTheLiveDefault_OnlyWhenNotOperatorPublished()
    {
        string sql = Sql(new AvatarLibraryLegalVersions().UpOperations).Single(s => s.Contains("document = 'Terms'"));

        sql.Should().Contain("SET version = '2026-11-17'");
        sql.Should().Contain("version = '2026-11-10'");
        sql.Should().Contain("updated_by IN ('config', 'migration:MarketplaceLegalVersions')");
    }

    [Fact]
    public void Up_MovesPrivacy_FromTheLiveDefault_OnlyWhenNotOperatorPublished()
    {
        string sql = Sql(new AvatarLibraryLegalVersions().UpOperations).Single(s => s.Contains("document = 'Privacy'"));

        sql.Should().Contain("SET version = '2026-11-17'");
        sql.Should().Contain("version = '2026-11-11'");
        sql.Should().Contain("updated_by IN ('config', 'migration:AcquisitionPrivacyVersion')");
    }

    [Fact]
    public void Down_RestoresTheTwoPreviousVersions_OnlyForRowsThisMigrationMoved()
    {
        string[] down = Sql(new AvatarLibraryLegalVersions().DownOperations);

        down.Should().HaveCount(2);
        down.Should().OnlyContain(s => s.Contains("updated_by = 'migration:AvatarLibraryLegalVersions'")
            && s.Contains("version = '2026-11-17'"));
        down.Single(s => s.Contains("'Terms'")).Should().Contain("SET version = '2026-11-10'");
        down.Single(s => s.Contains("'Privacy'")).Should().Contain("SET version = '2026-11-11'");
    }

    [Fact]
    public void FreshInstalls_StartAtTheSameVersions()
    {
        LegalDocumentSeeder.DefaultTermsVersion.Should().Be("2026-11-17");
        LegalDocumentSeeder.DefaultPrivacyVersion.Should().Be("2026-11-17");
    }
}
