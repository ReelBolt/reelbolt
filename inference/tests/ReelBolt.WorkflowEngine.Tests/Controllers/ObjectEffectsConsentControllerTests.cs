#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Moq;
using ReelBolt.Shared.IntegrationEvents;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>The project-level object-effects egress consent: View reads, Edit + verified email grants, cross-tenant is 404.</summary>
public class ObjectEffectsConsentControllerTests
{
    private sealed class FakeUser(Guid userId, OrganizationRole role = OrganizationRole.Member, bool verified = true) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public bool EmailVerified { get; } = verified;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => role;
    }

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Project _project;

    public ObjectEffectsConsentControllerTests()
    {
        _project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = _owner, OrganizationId = TestTenancy.OrgOf(_owner) };
        _db.Projects.Add(_project);
        _db.SaveChanges();
    }

    private ObjectEffectsConsentController Controller(Guid? user = null, OrganizationRole role = OrganizationRole.Member, bool verified = true)
    {
        var u = new FakeUser(user ?? _owner, role, verified);
        return new(_db, new ProjectAccessService(_db, u), u, _publish.Object);
    }

    private static ObjectEffectsConsentResponse Body(ActionResult<ObjectEffectsConsentResponse> r) =>
        (ObjectEffectsConsentResponse)((OkObjectResult)r.Result!).Value!;

    [Fact]
    public async Task Default_is_not_consented_then_put_grants_and_is_idempotent_and_delete_clears()
    {
        Body(await Controller().Get(_project.Id, default)).Should().Be(new ObjectEffectsConsentResponse(false, null));

        ObjectEffectsConsentResponse first = Body(await Controller().Grant(_project.Id, default));
        first.Consented.Should().BeTrue();
        first.ConsentedAt.Should().NotBeNull();

        ObjectEffectsConsentResponse second = Body(await Controller().Grant(_project.Id, default));
        second.ConsentedAt.Should().Be(first.ConsentedAt, "granting twice keeps the original timestamp");
        Body(await Controller().Get(_project.Id, default)).Consented.Should().BeTrue();
        (await _db.Projects.AsNoTracking().SingleAsync()).ObjectEffectsConsentAt.Should().Be(first.ConsentedAt);

        Body(await Controller().Revoke(_project.Id, default)).Should().Be(new ObjectEffectsConsentResponse(false, null));
        Body(await Controller().Revoke(_project.Id, default)).Consented.Should().BeFalse();
        (await _db.Projects.AsNoTracking().SingleAsync()).ObjectEffectsConsentAt.Should().BeNull();
    }

    [Fact]
    public async Task Cross_tenant_is_404_on_every_action()
    {
        Guid stranger = Guid.NewGuid();
        (await Controller(stranger).Get(_project.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await Controller(stranger).Grant(_project.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await Controller(stranger).Revoke(_project.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await _db.Projects.AsNoTracking().SingleAsync()).ObjectEffectsConsentAt.Should().BeNull();
    }

    [Fact]
    public async Task A_role_below_edit_cannot_grant_or_revoke()
    {
        (await Controller(_owner, OrganizationRole.Billing).Grant(_project.Id, default)).Result.Should().BeOfType<ForbidResult>();
        (await Controller(_owner, OrganizationRole.Billing).Revoke(_project.Id, default)).Result.Should().BeOfType<ForbidResult>();
        (await _db.Projects.AsNoTracking().SingleAsync()).ObjectEffectsConsentAt.Should().BeNull();
    }

    [Fact]
    public async Task Granting_needs_a_verified_email_but_reading_does_not()
    {
        ActionResult<ObjectEffectsConsentResponse> r = await Controller(verified: false).Grant(_project.Id, default);
        ((ObjectResult)r.Result!).StatusCode.Should().Be(403);
        (await _db.Projects.AsNoTracking().SingleAsync()).ObjectEffectsConsentAt.Should().BeNull();
        Body(await Controller(verified: false).Get(_project.Id, default)).Consented.Should().BeFalse();
    }

    private ObjectEffectRequest Row(string status, Guid? executionId = null)
    {
        ObjectEffectRequest row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = _project.OrganizationId,
            ProjectId = _project.Id,
            ExecutionId = executionId ?? Guid.NewGuid(),
            SourceKind = ObjectEffectRequest.SourceFile,
            SourceId = Guid.NewGuid().ToString("D"),
            Prompt = "car",
            Effect = "Blur",
            Status = status,
            CreatedByUserId = _owner,
            CreatedAt = DateTime.UtcNow
        };
        _db.ObjectEffectRequests.Add(row);
        return row;
    }

    [Fact]
    public async Task Withdrawing_consent_stops_in_flight_analyses_and_fails_their_rows()
    {
        await Controller().Grant(_project.Id, default);
        ObjectEffectRequest running = Row(ObjectEffectRequest.StatusQueued);
        ObjectEffectRequest noRunYet = Row(ObjectEffectRequest.StatusQueued);
        ObjectEffectRequest finishedRun = Row(ObjectEffectRequest.StatusQueued);
        ObjectEffectRequest done = Row(ObjectEffectRequest.StatusSucceeded);
        Guid definition = Guid.NewGuid();
        _db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = definition, ProjectId = _project.Id, Name = "w", Kind = WorkflowDefinitionKind.ObjectAnalysis, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.WorkflowExecutions.Add(new WorkflowExecution { Id = running.ExecutionId, WorkflowDefinitionId = definition, ProjectId = _project.Id, OrganizationId = _project.OrganizationId, Status = ExecutionStatus.Running });
        _db.WorkflowExecutions.Add(new WorkflowExecution { Id = finishedRun.ExecutionId, WorkflowDefinitionId = definition, ProjectId = _project.Id, OrganizationId = _project.OrganizationId, Status = ExecutionStatus.Passed });
        await _db.SaveChangesAsync();
        List<Guid> stopped = [];
        _publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionStopRequested>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionStopRequested, CancellationToken>((e, _) => stopped.Add(e.ExecutionId))
            .Returns(Task.CompletedTask);

        Body(await Controller().Revoke(_project.Id, default)).Consented.Should().BeFalse();

        stopped.Should().BeEquivalentTo([running.ExecutionId, noRunYet.ExecutionId]);
        ObjectEffectRequest[] rows = await _db.ObjectEffectRequests.AsNoTracking().ToArrayAsync();
        ObjectEffectRequest Of(ObjectEffectRequest r) => rows.Single(x => x.Id == r.Id);
        Of(running).Status.Should().Be("failed");
        Of(running).FailureCode.Should().Be("CONSENT_WITHDRAWN");
        Of(running).FailureMessage.Should().NotBeNullOrWhiteSpace();
        Of(noRunYet).Status.Should().Be("failed");
        Of(finishedRun).Status.Should().Be("queued", "a run that already finished is left for the status read to settle");
        Of(done).Status.Should().Be("succeeded");
    }

    [Fact]
    public async Task Withdrawing_consent_in_another_project_does_not_touch_this_ones_runs()
    {
        ObjectEffectRequest mine = Row(ObjectEffectRequest.StatusQueued);
        await _db.SaveChangesAsync();
        Guid stranger = Guid.NewGuid();
        (await Controller(stranger).Revoke(_project.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await _db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == mine.Id)).Status.Should().Be("queued");
        _publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionStopRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
