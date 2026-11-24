using System;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Migrations;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// The persistence half of the control: the columns, their defaults, the audit table and its
/// indexes. The audit table is API-owned and carries no tenant filter (it is reached through a
/// project file), which the tenancy inventory's unfiltered list relies on.
/// </summary>
public class CsamSchemaTests
{
    [Fact]
    public void NewRowsStartPending_WhileTheDatabaseDefaultIsNotScanned()
    {
        using InferenceApiDbContext db = CsamTestSupport.NewDb();

        new ProjectFile().CsamStatus.Should().Be(CsamScanStatus.Pending, "a new row is work for the scan path");

        IProperty status = db.Model.FindEntityType(typeof(ProjectFile))!
            .FindProperty(nameof(ProjectFile.CsamStatus))!;
        status.GetDefaultValue().Should().Be(CsamScanStatus.NotScanned,
            "legacy rows are not swept as unscanned work");
        status.GetMaxLength().Should().Be(20);
        status.GetProviderClrType().Should().Be(typeof(string), "the enum is stored by name");
    }

    [Fact]
    public void ProjectFiles_HaveTheScanLookupIndex()
    {
        using InferenceApiDbContext db = CsamTestSupport.NewDb();

        IEntityType files = db.Model.FindEntityType(typeof(ProjectFile))!;
        files.GetIndexes().Should().Contain(index =>
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(ProjectFile.ProjectId), nameof(ProjectFile.CsamStatus) }));
    }

    [Fact]
    public void CsamScanAudits_IsAnUnfilteredApiOwnedTable_WithItsQueryIndexes()
    {
        using InferenceApiDbContext db = CsamTestSupport.NewDb();

        IEntityType audit = db.Model.FindEntityType(typeof(CsamScanAudit))!;
        audit.GetTableName().Should().Be("csam_scan_audits");
        audit.GetDeclaredQueryFilters().Should().BeEmpty(
            "the table is reached only through a project file and has no caller-chosen id of its own");

        audit.FindProperty(nameof(CsamScanAudit.PdqHashBase64))!.GetMaxLength().Should().Be(64);
        audit.FindProperty(nameof(CsamScanAudit.ContentSha256Hex))!.GetMaxLength().Should().Be(64);
        audit.FindProperty(nameof(CsamScanAudit.ProjectFileId))!.IsNullable.Should().BeTrue();
        audit.FindProperty(nameof(CsamScanAudit.ProjectId))!.IsNullable.Should().BeTrue(
            "the evidence outlives a deleted project");
        audit.FindProperty(nameof(CsamScanAudit.ProjectFileName))!.GetMaxLength().Should().Be(260);

        // SET NULL, never cascade: deleting a file must not take its audit away.
        audit.GetForeignKeys().Should().ContainSingle()
            .Which.DeleteBehavior.Should().Be(DeleteBehavior.SetNull);
        audit.GetForeignKeys().Single().PrincipalEntityType.ClrType.Should().Be(typeof(ProjectFile));

        audit.GetIndexes().Should().Contain(index =>
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(CsamScanAudit.ProjectFileId), nameof(CsamScanAudit.CreatedAt) }));
        audit.GetIndexes().Should().Contain(index =>
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(CsamScanAudit.Status), nameof(CsamScanAudit.CreatedAt) }));
    }

    [Fact]
    public void AddCsamScanning_IsAnApiMigration()
    {
        Migration migration = new AddCsamScanning();

        migration.Should().NotBeNull();
        typeof(AddCsamScanning).Namespace.Should().Be("ReelBolt.Inference.Api.Migrations");
    }
}
