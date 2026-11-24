using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Migrations;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class PrivacySafetyScreeningVersionMigrationTests
{
    private static string[] Sql(IReadOnlyList<MigrationOperation> ops) =>
        ops.OfType<SqlOperation>().Select(o => o.Sql).ToArray();

    [Fact]
    public void Up_MovesOnlyPrivacy_FromTheShippedDefault_OnlyWhenNotOperatorPublished()
    {
        string only = Sql(new PrivacySafetyScreeningVersion().UpOperations).Single();

        only.Should().Contain("SET version = '2026-10-31'");
        only.Should().Contain("document = 'Privacy'");
        only.Should().NotContain("'Terms'");
        only.Should().Contain("version = '2026-10-26'");
        only.Should().Contain("updated_by IN ('config', 'migration:LaunchAlphaLegalVersions')");
    }

    [Fact]
    public void Down_RevertsOnlyTheRowThisMigrationMoved()
    {
        string only = Sql(new PrivacySafetyScreeningVersion().DownOperations).Single();

        only.Should().Contain("updated_by = 'migration:PrivacySafetyScreeningVersion'");
        only.Should().Contain("SET version = '2026-10-26'");
    }
}
