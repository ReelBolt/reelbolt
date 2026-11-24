using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.IntegrationEvents;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// The pin service (SPEC 9.1): pins only Approved current versions, and the queue path writes and
/// carries exactly what it built (frozen at request time).
/// </summary>
public class DesignSystemPinServiceTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    private static string Digest(char c) => new(c, 64);

    private static DesignSystemVersion Version(int number, DesignVersionState state, string digest) => new()
    {
        Id = Guid.NewGuid(),
        VersionNumber = number,
        State = state,
        DocumentJson = "{}",
        DocumentDigest = digest,
        Origin = DesignVersionOrigin.Manual,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static DesignSystem AddSystem(InferenceApiDbContext db, Guid projectId, string kind, DesignSystemVersion version)
    {
        DesignSystem system = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = kind,
            Name = kind,
            CurrentVersionId = version.Id,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        version.DesignSystemId = system.Id;
        db.DesignSystems.Add(system);
        db.DesignSystemVersions.Add(version);
        return system;
    }

    private static async Task<(Guid ProjectId, Guid WorkflowId, Guid UserId)> SeedProjectAndWorkflowAsync(InferenceApiDbContext db)
    {
        Guid projectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Test Project",
            OwnerId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Test Workflow",
            ProjectId = projectId,
            RequiresUserInput = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (projectId, workflowId, userId);
    }

    [Fact]
    public async Task BuildPinJson_returns_null_when_no_approved_version()
    {
        using InferenceApiDbContext db = CreateContext();
        DesignSystemPinService service = new(db);
        Guid projectId = Guid.NewGuid();

        // No design systems at all.
        (await service.BuildPinJsonAsync(projectId, CancellationToken.None)).Should().BeNull();

        // A current version that is still a Draft contributes nothing.
        AddSystem(db, projectId, DesignSystemKinds.Design, Version(1, DesignVersionState.Draft, Digest('a')));
        await db.SaveChangesAsync();

        (await service.BuildPinJsonAsync(projectId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task BuildPinJson_ignores_drafts()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid projectId = Guid.NewGuid();
        string approvedDigest = Digest('b');
        AddSystem(db, projectId, DesignSystemKinds.Design, Version(1, DesignVersionState.Draft, Digest('a')));
        AddSystem(db, projectId, DesignSystemKinds.ProductMap, Version(3, DesignVersionState.Approved, approvedDigest));
        await db.SaveChangesAsync();

        DesignSystemPin? pin = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(db).BuildPinJsonAsync(projectId, CancellationToken.None));

        pin.Should().NotBeNull();
        pin!.Design.Should().BeNull();
        pin.ProductMap.Should().NotBeNull();
        pin.ProductMap!.VersionNumber.Should().Be(3);
        pin.ProductMap.Digest.Should().Be(approvedDigest);
    }

    [Fact]
    public async Task BuildPinJson_includes_both_kinds()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid projectId = Guid.NewGuid();
        string d1 = Digest('a');
        string d2 = Digest('b');
        DesignSystemVersion design = Version(7, DesignVersionState.Approved, d1);
        DesignSystemVersion productMap = Version(3, DesignVersionState.Approved, d2);
        AddSystem(db, projectId, DesignSystemKinds.Design, design);
        AddSystem(db, projectId, DesignSystemKinds.ProductMap, productMap);
        await db.SaveChangesAsync();

        DesignSystemPin? pin = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(db).BuildPinJsonAsync(projectId, CancellationToken.None));

        pin.Should().NotBeNull();
        pin!.V.Should().Be(1);
        pin.Design!.VersionId.Should().Be(design.Id);
        pin.Design.VersionNumber.Should().Be(7);
        pin.Design.Digest.Should().Be(d1);
        pin.ProductMap!.VersionId.Should().Be(productMap.Id);
        pin.ProductMap.VersionNumber.Should().Be(3);
        pin.ProductMap.Digest.Should().Be(d2);
    }

    [Fact]
    public async Task QueueAsync_writes_and_carries_pin()
    {
        using InferenceApiDbContext db = CreateContext();
        (Guid projectId, Guid workflowId, Guid userId) = await SeedProjectAndWorkflowAsync(db);
        string digest = Digest('a');
        DesignSystemVersion design = Version(7, DesignVersionState.Approved, digest);
        AddSystem(db, projectId, DesignSystemKinds.Design, design);
        await db.SaveChangesAsync();

        Mock<IPublishEndpoint> mockPublish = new();
        WorkflowExecutionService service = new(
            db,
            mockPublish.Object,
            NullLogger<WorkflowExecutionService>.Instance,
            designSystemPinService: new DesignSystemPinService(db));

        WorkflowExecutionResponse response = await service.ExecuteAsync(
            projectId, workflowId, userId, "brief", ExecutionInitiator.Human, CancellationToken.None);

        WorkflowExecution row = await db.WorkflowExecutions.SingleAsync(e => e.Id == response.Id);
        row.DesignSystemPinJson.Should().NotBeNull();
        DesignSystemPin? pin = DesignSystemPinJson.TryParse(row.DesignSystemPinJson);
        pin.Should().NotBeNull();
        pin!.V.Should().Be(1);
        pin.Design!.VersionId.Should().Be(design.Id);
        pin.Design.VersionNumber.Should().Be(7);
        pin.Design.Digest.Should().Be(digest);

        mockPublish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.DesignSystemPinJson == row.DesignSystemPinJson),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Later_approval_does_not_change_the_written_pin()
    {
        using InferenceApiDbContext db = CreateContext();
        (Guid projectId, Guid workflowId, Guid userId) = await SeedProjectAndWorkflowAsync(db);
        DesignSystemVersion v7 = Version(7, DesignVersionState.Approved, Digest('a'));
        DesignSystem system = AddSystem(db, projectId, DesignSystemKinds.Design, v7);
        await db.SaveChangesAsync();

        Mock<IPublishEndpoint> mockPublish = new();
        WorkflowExecutionService service = new(
            db,
            mockPublish.Object,
            NullLogger<WorkflowExecutionService>.Instance,
            designSystemPinService: new DesignSystemPinService(db));

        WorkflowExecutionResponse response = await service.ExecuteAsync(
            projectId, workflowId, userId, null, ExecutionInitiator.Human, CancellationToken.None);

        // A later approval and a moved current_version_id must not touch the frozen row.
        DesignSystemVersion v8 = Version(8, DesignVersionState.Approved, Digest('c'));
        AddSystemVersion(db, system, v8);
        await db.SaveChangesAsync();

        WorkflowExecution row = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == response.Id);
        DesignSystemPin? pin = DesignSystemPinJson.TryParse(row.DesignSystemPinJson);
        pin.Should().NotBeNull();
        pin!.Design!.VersionId.Should().Be(v7.Id);
        pin.Design.VersionNumber.Should().Be(7);
    }

    private static void AddSystemVersion(InferenceApiDbContext db, DesignSystem system, DesignSystemVersion version)
    {
        version.DesignSystemId = system.Id;
        db.DesignSystemVersions.Add(version);
        system.CurrentVersionId = version.Id;
    }
}
