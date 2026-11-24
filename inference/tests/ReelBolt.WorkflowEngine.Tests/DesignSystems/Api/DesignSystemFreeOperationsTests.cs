using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
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
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// "static parse and import free": the design-system import, static parse, export and library
/// surfaces make no model call and record no charge. Two pins: none of the types behind those surfaces can
/// even reach a model client, a price or a charge path (constructor dependencies), and an import, approve
/// and export run inside a usage scope leaves that scope at zero and writes no ledger row and no usage
/// event. If a future change gives any of them a model call, this fails and the change needs a charge
/// design first.
/// </summary>
public sealed class DesignSystemFreeOperationsTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000098");

    /// <summary>Every type that could make or price a model call, or charge one.</summary>
    private static readonly Type[] ModelOrChargeTypes =
    [
        typeof(IChatClient),
        typeof(IChatClientFactory),
        typeof(IAgentChatClientProvider),
        typeof(IEmbeddingClientFactory),
        typeof(IVideoGenerationClientFactory),
        typeof(IInferenceProviderResolver),
        typeof(IModelPriceService),
        typeof(IUsageRecorder),
        typeof(INonWorkflowChargeService),
        typeof(ICreditGate)
    ];

    /// <summary>The import, static parse, export, library and drift surfaces of the design system.</summary>
    public static TheoryData<Type> FreeSurfaces() => new(
        typeof(DesignSystemsController),
        typeof(OrgDesignLibrariesController),
        typeof(DesignSystemService),
        typeof(DesignSystemIngestService),
        typeof(DesignSystemPinService),
        typeof(DesignSystemDriftService),
        typeof(FigmaVariablesClient),
        typeof(ImportWorkerClient),
        typeof(DesignSystemStepExecutor));

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();

    public DesignSystemFreeOperationsTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-system-free-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [MemberData(nameof(FreeSurfaces))]
    public void A_design_system_surface_cannot_reach_a_model_or_a_charge(Type surface)
    {
        IEnumerable<Type> dependencies = surface
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        dependencies.Should().NotContain(
            d => ModelOrChargeTypes.Any(m => m.IsAssignableFrom(d)),
            $"{surface.Name} is free here; a model call there needs a charge design first");
    }

    [Fact]
    public async Task Import_approve_and_export_inside_a_usage_scope_meter_nothing_and_charge_nothing()
    {
        Project project = SeedProject();
        byte[] css = Encoding.UTF8.GetBytes(
            ":root { --color-brand-primary: #6d28d9; --color-accent: #f59e0b; --font-sans: Inter, sans-serif; --radius: 0.5rem; }");
        ProjectFile file = SeedFile(project.Id, "app/globals.css", css);

        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(project.Id, file.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(css));
        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        var service = new DesignSystemService(_db, storage.Object, gate.Object, NullLogger<DesignSystemService>.Instance);
        ICurrentUser owner = new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner);
        var controller = new DesignSystemsController(
            _db, owner, new ProjectAccessService(_db, owner), service,
            Mock.Of<IDesignSystemIngestService>(), NullLogger<DesignSystemsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        using IDisposable scope = UsageScope.Begin(HomeOrg, project.Id, null, null, _ownerId, UsageSource.Assistant);
        UsageScopeState state = UsageScope.Current!;

        ActionResult<ImportResponse> imported = await controller.Import(
            project.Id, new ImportRequest([file.Id], ["design"]), CancellationToken.None);
        imported.Result.Should().BeOfType<OkObjectResult>("the import succeeded");
        DesignSystemVersion draft = _db.DesignSystemVersions.Single();
        (await service.ApproveAsync(project.Id, draft.Id, _ownerId, CancellationToken.None))
            .Status.Should().Be(DesignSystemWriteStatus.Ok);
        IActionResult exported = await controller.Export(project.Id, null, "css", CancellationToken.None);
        exported.Should().BeOfType<FileContentResult>();

        state.RunningCreditsMillis.Should().Be(0, "nothing metered a priced unit");
        state.TotalTokens.Should().Be(0, "no model was called");
        state.SawUnpricedPlatformUsage.Should().BeFalse();
        _db.UsageEvents.Should().BeEmpty();
        _db.CreditLedgerEntries.Should().BeEmpty();
    }

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private Project SeedProject()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project",
            OwnerId = _ownerId,
            OrganizationId = HomeOrg,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private ProjectFile SeedFile(Guid projectId, string path, byte[] bytes)
    {
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalPath = path,
            OriginalFileName = path,
            StorageKey = $"projects/{projectId}/{path}",
            SizeBytes = bytes.Length,
            MimeType = "text/css",
            UploadedAt = DateTime.UtcNow,
        };
        _db.ProjectFiles.Add(file);
        _db.SaveChanges();
        return file;
    }
}
