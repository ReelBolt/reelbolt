using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Migrations;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The avatars legal bump (Terms + Privacy 2026-11-05, Terms = re-acceptance) moves each row only while
/// it still holds the previously shipped default and was not published by an operator.
/// </summary>
public class AvatarsLegalVersionsMigrationTests
{
    private static string[] Sql(IReadOnlyList<MigrationOperation> ops) =>
        ops.OfType<SqlOperation>().Select(o => o.Sql).ToArray();

    private static string[] UpSql() => Sql(new AvatarsLegalVersions().UpOperations);

    [Fact]
    public void Up_MovesTerms_FromTheShippedDefault_OnlyWhenNotOperatorPublished()
    {
        string terms = UpSql().Single(s => s.Contains("document = 'Terms'"));

        terms.Should().Contain("SET version = '2026-11-05'");
        terms.Should().Contain("version = '2026-10-26'");
        terms.Should().Contain("updated_by IN ('config', 'migration:LaunchAlphaLegalVersions')");
    }

    [Fact]
    public void Up_MovesPrivacy_FromTheShippedDefault_OnlyWhenNotOperatorPublished()
    {
        string privacy = UpSql().Single(s => s.Contains("document = 'Privacy'"));

        privacy.Should().Contain("SET version = '2026-11-05'");
        privacy.Should().Contain("version = '2026-10-31'");
        privacy.Should().Contain("updated_by IN ('config', 'migration:PrivacySafetyScreeningVersion')");
    }

    [Fact]
    public void Down_RevertsOnlyRowsThisMigrationMoved()
    {
        string[] down = Sql(new AvatarsLegalVersions().DownOperations);

        down.Should().HaveCount(2);
        down.Should().OnlyContain(s => s.Contains("updated_by = 'migration:AvatarsLegalVersions'")
            && s.Contains("version = '2026-11-05'"));
    }

    [Fact]
    public void FreshInstalls_StartAtTheSameVersions()
    {
        LegalDocumentSeeder.DefaultTermsVersion.Should().Be("2026-11-17");
        // AvatarLibraryLegalVersions moved both to 2026-11-17 (objects, pictures, workspace library).
        LegalDocumentSeeder.DefaultPrivacyVersion.Should().Be("2026-11-17");
    }
}
