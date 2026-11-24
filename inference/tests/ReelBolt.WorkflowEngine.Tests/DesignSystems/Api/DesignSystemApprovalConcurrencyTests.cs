using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// DSM-04: two requests that both read the same system before either saves must not both win. Two
/// DbContexts share one in-memory Sqlite connection; the second context is primed with a stale read
/// of the system before the first one commits, which is the interleaving a pair of simultaneous HTTP
/// requests produces.
/// </summary>
public sealed class DesignSystemApprovalConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _a;
    private readonly InferenceApiDbContext _b;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Project _project;
    private readonly DesignSystem _system;
    private readonly DesignSystemVersion _current;

    public DesignSystemApprovalConcurrencyTests()
    {
        (_connection, _a) = WorkflowAssistantControllerTests.CreateSqliteContext();
        _b = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

        _project = new Project { Id = Guid.NewGuid(), Name = "p", OwnerId = _userId, OrganizationId = Guid.NewGuid() };
        _a.Projects.Add(_project);
        _system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "Design system",
            CreatedByUserId = _userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _a.DesignSystems.Add(_system);
        _a.SaveChanges();
        _current = AddVersion(1, DesignVersionState.Approved, baseVersionId: null);
        _a.SaveChanges();
        _system.CurrentVersionId = _current.Id;
        _a.SaveChanges();
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _connection.Dispose();
    }

    private static DesignSystemService Service(InferenceApiDbContext db) => new(
        db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(), NullLogger<DesignSystemService>.Instance);

    private DesignSystemVersion AddVersion(int number, DesignVersionState state, Guid? baseVersionId)
    {
        JsonObject document = new() { ["schema"] = DesignDocument.Schema };
        var version = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = _system.Id,
            VersionNumber = number,
            State = state,
            DocumentJson = CanonicalJson.Serialize(document),
            DocumentDigest = CanonicalJson.Digest(document),
            Origin = DesignVersionOrigin.Manual,
            BaseVersionId = baseVersionId,
            CreatedByUserId = _userId,
            CreatedAt = DateTime.UtcNow,
            ApprovedByUserId = state == DesignVersionState.Approved ? _userId : null,
            ApprovedAt = state == DesignVersionState.Approved ? DateTime.UtcNow : null,
        };
        _a.DesignSystemVersions.Add(version);
        return version;
    }

    /// <summary>Reads the system and a version in <paramref name="db"/> so a later write there works from stale state.</summary>
    private static async Task PrimeAsync(InferenceApiDbContext db, Guid systemId, params Guid[] versionIds)
    {
        await db.DesignSystems.SingleAsync(s => s.Id == systemId);
        foreach (Guid id in versionIds)
            await db.DesignSystemVersions.SingleAsync(v => v.Id == id);
    }

    private async Task<int> ApprovedCountAsync()
    {
        using var fresh = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);
        return await fresh.DesignSystemVersions
            .CountAsync(v => v.DesignSystemId == _system.Id && v.State == DesignVersionState.Approved);
    }

    [Fact]
    public async Task Two_simultaneous_approvals_of_different_drafts_leave_exactly_one_approved()
    {
        DesignSystemVersion d2 = AddVersion(2, DesignVersionState.Draft, _current.Id);
        DesignSystemVersion d3 = AddVersion(3, DesignVersionState.Draft, _current.Id);
        await _a.SaveChangesAsync();
        await PrimeAsync(_b, _system.Id, d3.Id);

        DesignSystemWriteResult first = await Service(_a).ApproveAsync(_project.Id, d2.Id, _userId, CancellationToken.None);
        DesignSystemWriteResult second = await Service(_b).ApproveAsync(_project.Id, d3.Id, _userId, CancellationToken.None);

        first.Status.Should().Be(DesignSystemWriteStatus.Ok);
        second.Status.Should().Be(DesignSystemWriteStatus.StaleBase);
        (await ApprovedCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Two_simultaneous_approvals_of_the_same_draft_leave_exactly_one_approved()
    {
        DesignSystemVersion d2 = AddVersion(2, DesignVersionState.Draft, _current.Id);
        await _a.SaveChangesAsync();
        await PrimeAsync(_b, _system.Id, d2.Id);

        DesignSystemWriteResult first = await Service(_a).ApproveAsync(_project.Id, d2.Id, _userId, CancellationToken.None);
        DesignSystemWriteResult second = await Service(_b).ApproveAsync(_project.Id, d2.Id, _userId, CancellationToken.None);

        first.Status.Should().Be(DesignSystemWriteStatus.Ok);
        second.Status.Should().Be(DesignSystemWriteStatus.StaleBase);
        (await ApprovedCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Approving_an_already_approved_version_is_rejected_deterministically()
    {
        DesignSystemVersion d2 = AddVersion(2, DesignVersionState.Draft, _current.Id);
        await _a.SaveChangesAsync();
        DesignSystemService service = Service(_a);

        (await service.ApproveAsync(_project.Id, d2.Id, _userId, CancellationToken.None)).Status
            .Should().Be(DesignSystemWriteStatus.Ok);
        (await service.ApproveAsync(_project.Id, d2.Id, _userId, CancellationToken.None)).Status
            .Should().Be(DesignSystemWriteStatus.NotDraft);
        (await service.ApproveAsync(_project.Id, _current.Id, _userId, CancellationToken.None)).Status
            .Should().Be(DesignSystemWriteStatus.NotDraft);
        (await ApprovedCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Approving_and_rejecting_one_proposal_at_once_cannot_disagree()
    {
        DesignSystemVersion d2 = AddVersion(2, DesignVersionState.Draft, _current.Id);
        var proposal = new DesignSystemChangeProposal
        {
            Id = Guid.NewGuid(),
            DesignSystemId = _system.Id,
            BaseVersionId = _current.Id,
            ProposedVersionId = d2.Id,
            Trigger = DesignProposalTrigger.SourceChanged,
            DriftScore = 0.25,
            DiffJson = "{}",
            Status = DesignProposalStatus.Pending,
            ProposedDigest = "deadbeef",
            CreatedAt = DateTime.UtcNow,
        };
        _a.DesignSystemChangeProposals.Add(proposal);
        await _a.SaveChangesAsync();
        await _b.DesignSystems.SingleAsync(s => s.Id == _system.Id);
        await _b.DesignSystemChangeProposals.SingleAsync(p => p.Id == proposal.Id);
        await _b.DesignSystemVersions.SingleAsync(v => v.Id == d2.Id);

        DesignSystemProposalResult approved = await Service(_a)
            .ApproveProposalAsync(_project.Id, proposal.Id, _userId, null, CancellationToken.None);
        DesignSystemProposalResult rejected = await Service(_b)
            .RejectProposalAsync(_project.Id, proposal.Id, _userId, null, CancellationToken.None);

        approved.Status.Should().Be(DesignSystemProposalDecisionStatus.Ok);
        rejected.Status.Should().Be(DesignSystemProposalDecisionStatus.StaleBase);

        using var fresh = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);
        (await fresh.DesignSystemChangeProposals.SingleAsync(p => p.Id == proposal.Id)).Status
            .Should().Be(DesignProposalStatus.Approved);
        (await fresh.DesignSystemVersions.SingleAsync(v => v.Id == d2.Id)).State
            .Should().Be(DesignVersionState.Approved);
        (await fresh.DesignSystems.SingleAsync(s => s.Id == _system.Id)).CurrentVersionId.Should().Be(d2.Id);
    }
}
