using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// P3-03 behaviour coverage for the Phase-3 proposal routes and Draft locks: tenancy (foreign
/// proposal 404, Billing 403), the approve/reject proposal state machine through the same save as
/// the version approval, the Draft-only lock route (unknown path 400, re-digest) and the manual
/// drift check's 202 + enqueue. Direct controller construction on an InMemory context, the same
/// harness the other design-system API tests use.
/// </summary>
public sealed class ProposalRoutesTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000021");
    private static readonly Guid ForeignOrg = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000022");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _billingId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    public ProposalRoutesTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"proposal-routes-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // Tenancy
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approve_proposal_from_another_project_returns_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId, "Mine");
        Project other = SeedProject(HomeOrg, _ownerId, "Other");
        DesignSystem system = SeedSystem(other.Id);
        DesignSystemVersion proposed = SeedVersion(system.Id, 1, DesignVersionState.Draft, _ownerId);
        DesignSystemChangeProposal proposal = SeedProposal(system.Id, Guid.NewGuid(), proposed.Id);
        _db.SaveChanges();

        ActionResult<ProposalResponse> response = await Controller(Owner())
            .ApproveProposal(mine.Id, proposal.Id, null, CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Billing_role_cannot_approve_proposal_returns_403()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id);
        DesignSystemVersion proposed = SeedVersion(system.Id, 1, DesignVersionState.Draft, _ownerId);
        DesignSystemChangeProposal proposal = SeedProposal(system.Id, Guid.NewGuid(), proposed.Id);
        _db.SaveChanges();

        ActionResult<ProposalResponse> response = await Controller(
                new FakeUser(_billingId, HomeOrg, OrganizationRole.Billing))
            .ApproveProposal(project.Id, proposal.Id, null, CancellationToken.None);

        response.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Proposal_from_another_orgs_project_returns_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId, "Mine");
        DesignSystem system = SeedSystem(mine.Id);
        DesignSystemVersion proposed = SeedVersion(system.Id, 1, DesignVersionState.Draft, _ownerId);
        DesignSystemChangeProposal proposal = SeedProposal(system.Id, Guid.NewGuid(), proposed.Id);
        _db.SaveChanges();

        ActionResult<ProposalResponse> response = await Controller(
                new FakeUser(_strangerId, ForeignOrg, OrganizationRole.Member))
            .ApproveProposal(mine.Id, proposal.Id, null, CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundResult>();
    }

    // ---------------------------------------------------------------------------------------------------
    // Proposal approve / reject
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Double_approve_proposal_returns_409()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        (DesignSystem system, DesignSystemVersion current, DesignSystemVersion proposed) =
            SeedSystemWithCurrentAndProposed(project.Id);
        DesignSystemChangeProposal proposal = SeedProposal(system.Id, current.Id, proposed.Id);
        _db.SaveChanges();

        DesignSystemsController controller = Controller(Owner());

        ActionResult<ProposalResponse> first = await controller.ApproveProposal(
            project.Id, proposal.Id, null, CancellationToken.None);
        first.Result.Should().BeOfType<OkObjectResult>();

        ActionResult<ProposalResponse> second = await controller.ApproveProposal(
            project.Id, proposal.Id, null, CancellationToken.None);

        ConflictObjectResult conflict = second.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        Body(conflict.Value)["code"]!.GetValue<string>().Should().Be("PROPOSAL_DECIDED");
    }

    [Fact]
    public async Task Approve_proposal_supersedes_current_and_marks_proposal()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        (DesignSystem system, DesignSystemVersion current, DesignSystemVersion proposed) =
            SeedSystemWithCurrentAndProposed(project.Id);
        DesignSystemChangeProposal proposal = SeedProposal(system.Id, current.Id, proposed.Id);
        _db.SaveChanges();

        ActionResult<ProposalResponse> response = await Controller(Owner())
            .ApproveProposal(project.Id, proposal.Id, null, CancellationToken.None);

        ProposalResponse body = response.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ProposalResponse>().Subject;
        body.Status.Should().Be("Approved");

        _db.ChangeTracker.Clear();
        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == current.Id).State
            .Should().Be(DesignVersionState.Superseded);
        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == proposed.Id).State
            .Should().Be(DesignVersionState.Approved);
        _db.DesignSystems.AsNoTracking().Single(s => s.Id == system.Id).CurrentVersionId
            .Should().Be(proposed.Id);
    }

    [Fact]
    public async Task Approve_proposal_with_stale_base_returns_409_STALE_BASE()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id);
        DesignSystemVersion movedOn = SeedVersion(system.Id, 1, DesignVersionState.Approved, _ownerId);
        DesignSystemVersion reviewedAgainst = SeedVersion(system.Id, 2, DesignVersionState.Approved, _ownerId);
        system.CurrentVersionId = movedOn.Id;
        DesignSystemVersion proposed = SeedVersion(
            system.Id, 3, DesignVersionState.Draft, _ownerId, baseVersionId: reviewedAgainst.Id);
        DesignSystemChangeProposal proposal = SeedProposal(system.Id, reviewedAgainst.Id, proposed.Id);
        _db.SaveChanges();

        ActionResult<ProposalResponse> response = await Controller(Owner())
            .ApproveProposal(project.Id, proposal.Id, null, CancellationToken.None);

        ConflictObjectResult conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        Body(conflict.Value)["code"]!.GetValue<string>().Should().Be("STALE_BASE");
    }

    [Fact]
    public async Task Reject_proposal_marks_proposal_and_draft_rejected()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        (DesignSystem system, DesignSystemVersion current, DesignSystemVersion proposed) =
            SeedSystemWithCurrentAndProposed(project.Id);
        DesignSystemChangeProposal proposal = SeedProposal(system.Id, current.Id, proposed.Id);
        _db.SaveChanges();

        ActionResult<ProposalResponse> response = await Controller(Owner())
            .RejectProposal(project.Id, proposal.Id, new ApproveRejectRequest("not now"), CancellationToken.None);

        ProposalResponse body = response.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ProposalResponse>().Subject;
        body.Status.Should().Be("Rejected");
        body.DecisionNote.Should().Be("not now");

        _db.ChangeTracker.Clear();
        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == proposed.Id).State
            .Should().Be(DesignVersionState.Rejected);
        _db.DesignSystems.AsNoTracking().Single(s => s.Id == system.Id).CurrentVersionId
            .Should().Be(current.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // Draft locks
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lock_on_non_draft_returns_409()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id);
        DesignSystemVersion approved = SeedVersion(system.Id, 1, DesignVersionState.Approved, _ownerId);
        _db.SaveChanges();

        ActionResult<DesignVersionResponse> response = await Controller(Owner()).UpdateLocks(
            project.Id, approved.Id, new LockRequest(["color.primary"]), CancellationToken.None);

        ConflictObjectResult conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        Body(conflict.Value)["code"]!.GetValue<string>().Should().Be("NOT_DRAFT");
    }

    [Fact]
    public async Task Unknown_lock_path_returns_400()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id);
        DesignSystemVersion draft = SeedVersion(system.Id, 1, DesignVersionState.Draft, _ownerId);
        _db.SaveChanges();

        ActionResult<DesignVersionResponse> response = await Controller(Owner()).UpdateLocks(
            project.Id, draft.Id, new LockRequest(["color.missing"]), CancellationToken.None);

        BadRequestObjectResult badRequest = response.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonObject body = Body(badRequest.Value);
        body["code"]!.GetValue<string>().Should().Be("UNKNOWN_PATH");
        body["path"]!.GetValue<string>().Should().Be("color.missing");
    }

    [Fact]
    public async Task Lock_on_draft_sets_locked_and_redigests()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id);
        DesignSystemVersion draft = SeedVersion(system.Id, 1, DesignVersionState.Draft, _ownerId);
        string before = draft.DocumentDigest;
        _db.SaveChanges();

        ActionResult<DesignVersionResponse> response = await Controller(Owner()).UpdateLocks(
            project.Id, draft.Id, new LockRequest(["color.primary"]), CancellationToken.None);

        DesignVersionResponse body = response.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DesignVersionResponse>().Subject;

        body.DocumentDigest.Should().NotBe(before);
        body.Document["color"]!["primary"]!["$extensions"]!["com.reelbolt"]!["locked"]!
            .GetValue<bool>().Should().BeTrue();

        _db.ChangeTracker.Clear();
        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == draft.Id).DocumentDigest
            .Should().Be(body.DocumentDigest);
    }

    [Fact]
    public async Task Unlock_on_draft_clears_locked()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id);
        DesignSystemVersion draft = SeedVersion(
            system.Id, 1, DesignVersionState.Draft, _ownerId, locked: true);
        _db.SaveChanges();

        ActionResult<DesignVersionResponse> response = await Controller(Owner()).UpdateLocks(
            project.Id, draft.Id, new LockRequest(Unlock: ["color.primary"]), CancellationToken.None);

        DesignVersionResponse body = response.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DesignVersionResponse>().Subject;
        body.Document["color"]!["primary"]!["$extensions"]!["com.reelbolt"]!["locked"]!
            .GetValue<bool>().Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------------
    // Manual drift check
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_returns_202_and_enqueues_a_manual_check()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        var queue = new RecordingDriftQueue();
        _db.SaveChanges();

        IActionResult result = await Controller(Owner(), queue).Check(project.Id, CancellationToken.None);

        AcceptedResult accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        queue.Calls.Should().ContainSingle(call =>
            call.ProjectId == project.Id && call.Trigger == DesignDriftTrigger.Manual);
    }

    [Fact]
    public async Task Check_is_refused_for_an_organization_at_its_drift_cap_but_not_for_another()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        _db.SaveChanges();
        var queue = new DesignDriftQueue();
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, HomeOrg);

        IActionResult result = await Controller(Owner(), queue).Check(project.Id, CancellationToken.None);

        AcceptedResult accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        Body(accepted.Value)["status"]!.GetValue<string>().Should().Be("organizationbusy");
        queue.IsQueued(project.Id).Should().BeFalse();

        var roomy = new DesignDriftQueue();
        await Controller(Owner(), roomy).Check(project.Id, CancellationToken.None);
        roomy.IsQueued(project.Id).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    /// <summary>A drift queue that records what was asked for instead of filling its channel.</summary>
    private sealed class RecordingDriftQueue : DesignDriftQueue
    {
        public List<(Guid ProjectId, DesignDriftTrigger Trigger)> Calls { get; } = [];

        public override DesignDriftEnqueueResult Enqueue(
            Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths)
        {
            Calls.Add((projectId, trigger));
            return DesignDriftEnqueueResult.Queued;
        }

        public override DesignDriftEnqueueResult Enqueue(
            Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, Guid organizationId)
        {
            Calls.Add((projectId, trigger));
            return DesignDriftEnqueueResult.Queued;
        }
    }

    private ICurrentUser Owner() => new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner);

    private DesignSystemsController Controller(ICurrentUser user, DesignDriftQueue? queue = null) => new(
        _db,
        user,
        new ProjectAccessService(_db, user),
        new DesignSystemService(
            _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
            NullLogger<DesignSystemService>.Instance),
        Mock.Of<IDesignSystemIngestService>(),
        NullLogger<DesignSystemsController>.Instance,
        configuration: null,
        templates: null,
        executions: null,
        trialCredits: null,
        costEstimator: null,
        driftQueue: queue);

    private static JsonObject ValidDocument(bool locked = false) => new()
    {
        ["schema"] = DesignDocument.Schema,
        ["color"] = new JsonObject
        {
            ["primary"] = new JsonObject
            {
                ["$type"] = "color",
                ["$value"] = new JsonObject { ["hex"] = "#6d28d9", ["colorSpace"] = "srgb" },
                ["$extensions"] = new JsonObject
                {
                    ["com.reelbolt"] = new JsonObject
                    {
                        ["prov"] = new JsonObject { ["method"] = "manual" },
                        ["conf"] = 1.0,
                        ["locked"] = locked,
                    },
                },
            },
        },
    };

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    private Project SeedProject(Guid organizationId, Guid ownerId, string name = "Project")
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = ownerId,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private DesignSystem SeedSystem(Guid projectId)
    {
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = DesignSystemKinds.Design,
            Name = "Design system",
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.DesignSystems.Add(system);
        _db.SaveChanges();
        return system;
    }

    /// <summary>A system with one approved current version and one proposed Draft based on it.</summary>
    private (DesignSystem System, DesignSystemVersion Current, DesignSystemVersion Proposed)
        SeedSystemWithCurrentAndProposed(Guid projectId)
    {
        DesignSystem system = SeedSystem(projectId);
        DesignSystemVersion current = SeedVersion(system.Id, 1, DesignVersionState.Approved, _ownerId);
        system.CurrentVersionId = current.Id;
        DesignSystemVersion proposed = SeedVersion(
            system.Id, 2, DesignVersionState.Draft, _ownerId, baseVersionId: current.Id);
        _db.SaveChanges();
        return (system, current, proposed);
    }

    private DesignSystemVersion SeedVersion(
        Guid systemId, int versionNumber, DesignVersionState state, Guid userId,
        Guid? baseVersionId = null, bool locked = false)
    {
        JsonObject document = ValidDocument(locked);
        var version = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = systemId,
            VersionNumber = versionNumber,
            State = state,
            DocumentJson = CanonicalJson.Serialize(document),
            DocumentDigest = CanonicalJson.Digest(document),
            Origin = DesignVersionOrigin.Manual,
            BaseVersionId = baseVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
            ApprovedByUserId = state == DesignVersionState.Approved ? userId : null,
            ApprovedAt = state == DesignVersionState.Approved ? DateTime.UtcNow : null,
        };
        _db.DesignSystemVersions.Add(version);
        return version;
    }

    private DesignSystemChangeProposal SeedProposal(Guid systemId, Guid baseVersionId, Guid proposedVersionId)
    {
        var proposal = new DesignSystemChangeProposal
        {
            Id = Guid.NewGuid(),
            DesignSystemId = systemId,
            BaseVersionId = baseVersionId,
            ProposedVersionId = proposedVersionId,
            Trigger = DesignProposalTrigger.SourceChanged,
            DriftScore = 0.25,
            DiffJson = new JsonObject { ["score"] = 0.25 }.ToJsonString(),
            Status = DesignProposalStatus.Pending,
            ProposedDigest = "deadbeef",
            CreatedAt = DateTime.UtcNow,
        };
        _db.DesignSystemChangeProposals.Add(proposal);
        return proposal;
    }
}
