using ReelBolt.Shared.Workflows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// Tests for <see cref="AssistantToolBridgeController"/> — the HTTP bridge the MCP server proxies
/// (docs/design/mcp-tool-bridge.md §3, docs/design/mcp-oauth.md §6). The properties asserted here are
/// the ones that make the bridge safe to expose: the catalog is the provider's OWN registered set (not
/// a second list), the gated tools are hidden and refused unless the caller opted in, a refusal is a
/// result rather than an error and writes no domain rows, every call is metered exactly once, an
/// MCP-caused execution is labelled as such, and no assistant thread is touched.
/// </summary>
public class AssistantToolBridgeControllerTests
{
    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    /// <summary>
    /// Counts <c>SaveChanges</c>/<c>SaveChangesAsync</c> so a test can assert what a call persisted
    /// rather than only inspecting the text it returned.
    /// </summary>
    private sealed class WriteCountingDbContext(DbContextOptions<InferenceApiDbContext> options)
        : InferenceApiDbContext(options)
    {
        public int SaveCount { get; private set; }

        public void ResetSaveCount() => SaveCount = 0;

        public override int SaveChanges()
        {
            SaveCount++;
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static WriteCountingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WriteCountingDbContext(options);
    }

    /// <summary>
    /// A caller whose platform-authority claim is fixed for the test. It implements no
    /// <c>IsAdmin</c> alias and carries no role the body could supply — authority is this object,
    /// which stands in for the verified JWT.
    /// </summary>
    private sealed class FakeCurrentUser(Guid userId, bool isPlatformAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = isPlatformAdmin;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
    }

    /// <summary>Records the initiator the ExecuteWorkflow tool passed, and creates the run row.</summary>
    private sealed class RecordingExecutionService(InferenceApiDbContext db) : IWorkflowExecutionService
    {

        // Only system-workflow owners (the editor's render endpoint) queue by kind; no execute path
        // under test here does.
        public Task<WorkflowExecutionResponse> QueueAsync(
            Guid projectId,
            Guid workflowId,
            Guid initiatedByUserId,
            string? userRequest,
            ExecutionInitiator initiator,
            CancellationToken ct,
            bool regenerateVideoClips,
            RunChoices? runChoices,
            WorkflowDefinitionKind kind,
            Guid? executionId,
            Func<CancellationToken, Task>? beforePublish = null)
            => throw new NotSupportedException("The execute paths under test never queue by workflow kind.");
        public List<ExecutionInitiator> Initiators { get; } = new();

        public async Task<WorkflowExecutionResponse> ExecuteAsync(
            Guid projectId,
            Guid workflowId,
            Guid initiatedByUserId,
            string? userRequest,
            ExecutionInitiator initiator,
            CancellationToken ct,
            bool regenerateVideoClips = false,
            RunChoices? runChoices = null)
        {
            Initiators.Add(initiator);

            var execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                ProjectId = projectId,
                InitiatedByUserId = initiatedByUserId,
                Initiator = initiator,
                Status = ExecutionStatus.Queued,
                StartedAt = DateTime.UtcNow,
                UserRequest = userRequest
            };
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync(ct);

            return new WorkflowExecutionResponse(
                execution.Id,
                workflowId,
                execution.Status.ToString(),
                execution.StartedAt,
                null,
                0,
                null,
                execution.CorrelationId,
                "no bus in this harness",
                new List<StepResultResponse>(),
                new List<ReviewScoreResponse>(),
                userRequest);
        }
    }

    /// <summary>Runs the insert callback and grants the rate-limit slot.</summary>
    private sealed class GrantingGuard : IAssistantExecutionGuard
    {
        public async Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }
    }

    /// <summary>
    /// Wires the real surfaces the way the chat transport's request scope does, so the controller
    /// builds its provider through the same factory and the same services a real request would.
    /// </summary>
    private static AssistantToolBridgeController CreateController(
        InferenceApiDbContext db,
        ICurrentUser user,
        IWorkflowExecutionService? executionService = null,
        IAssistantExecutionGuard? guard = null,
        AssistantPictureTools pictureTools = null,
        INonWorkflowChargeService? charges = null,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        IInferenceProviderResolver? resolver = null,
        IWorkflowExecutionPreflight? preflight = null,
        IWorkflowEditDiffService? diffService = null,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var serviceCollection = new ServiceCollection();
        configureServices?.Invoke(serviceCollection);
        if (configuration is not null) serviceCollection.AddSingleton(configuration);
        if (resolver is not null) serviceCollection.AddSingleton(resolver);
        serviceCollection.AddScoped(_ => db);
        serviceCollection.AddScoped(_ => user);
        serviceCollection.AddScoped(_ => diffService ?? Mock.Of<IWorkflowEditDiffService>());
        serviceCollection.AddScoped(_ => executionService ?? Mock.Of<IWorkflowExecutionService>());
        serviceCollection.AddScoped(_ => guard ?? Mock.Of<IAssistantExecutionGuard>());
        serviceCollection.AddScoped<IWorkflowExecutionPreflight>(_ => preflight ?? new WorkflowExecutionPreflight(db, null));
        serviceCollection.AddScoped(_ => new AssistantProjectTools(
            db,
            ConfirmationTokenService.CreateEphemeral(),
            AssistantToolTestFactory.TemplateProvisioning(db),
            user,
            TestTenancy.Access(db, user)));
        serviceCollection.AddScoped(_ => new AssistantFileTools(
            db,
            ConfirmationTokenService.CreateEphemeral(),
            Mock.Of<IFileStorageService>(),
            Mock.Of<IPublishEndpoint>(),
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
            TestTenancy.Access(db, user)));
        serviceCollection.AddScoped(_ => new AssistantWorkflowTools(
            db, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(db, user)));
        serviceCollection.AddScoped(_ => new AssistantAdminTools(
            db, ConfirmationTokenService.CreateEphemeral(), user, Mock.Of<ISecretProtector>()));
        serviceCollection.AddScoped(_ => new AssistantKnowledgeTools(
            db, new FakePlatformDocsSearch(), AssistantToolTestFactory.TemplateProvisioning(db)));
        serviceCollection.AddScoped(_ => new AssistantStyleTools(
            db, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(db, user)));
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.DesignSystemTools(
            db, user, ConfirmationTokenService.CreateEphemeral()));
        serviceCollection.AddScoped(_ => pictureTools ?? AssistantToolTestFactory.PictureTools(
            db, user, ConfirmationTokenService.CreateEphemeral()));
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.P0Tools(db, user, ConfirmationTokenService.CreateEphemeral()).Outputs);
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.P0Tools(db, user, ConfirmationTokenService.CreateEphemeral()).Uploads);
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.P0Tools(db, user, ConfirmationTokenService.CreateEphemeral()).Runs);
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.P0Tools(db, user, ConfirmationTokenService.CreateEphemeral(), guard).Revisions);
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.P0Tools(db, user, ConfirmationTokenService.CreateEphemeral(), guard).ChangeRequests);
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.P2Tools(db, user, ConfirmationTokenService.CreateEphemeral()).Patch);
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.P2Tools(db, user, ConfirmationTokenService.CreateEphemeral()).PictureDocuments);
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.P2Tools(db, user, ConfirmationTokenService.CreateEphemeral()).GreenScreen);
        serviceCollection.AddScoped(_ => new AssistantMediaOverlayTools(
            db, TestTenancy.Access(db, user), new WorkflowEditDiffService(db), ConfirmationTokenService.CreateEphemeral(), user));
        serviceCollection.AddScoped(_ => new AssistantAudioCleanupTools(
            db, TestTenancy.Access(db, user),
            new ReelBolt.Inference.Api.Services.Media.AudioCleanupService(
                db, TestTenancy.Access(db, user), user, Mock.Of<IWorkflowExecutionService>(),
                Mock.Of<ReelBolt.Inference.Api.Services.Workflows.IAssistantExecutionGuard>(), Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>()),
            ConfirmationTokenService.CreateEphemeral(), user));
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.VoiceTools(db, user, ConfirmationTokenService.CreateEphemeral()));
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.AvatarTools(db, user));
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.VideoEventsTools(db, user));
        serviceCollection.AddScoped(_ => new AssistantHistoryTools(
            ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(db, user),
            Mock.Of<ReelBolt.Inference.Api.Services.History.IProjectHistoryService>(),
            Mock.Of<ReelBolt.Inference.Api.Services.History.IProjectHistoryRestoreService>()));
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.SocialTools(db, user, ConfirmationTokenService.CreateEphemeral()));
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.RecipeTools(db, user, ConfirmationTokenService.CreateEphemeral()));
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.MarketplaceTools(db, user, ConfirmationTokenService.CreateEphemeral()));
        serviceCollection.AddScoped(_ => Mock.Of<ReelBolt.Inference.Api.Services.ObjectAnalysis.IObjectAnalysisService>());
        serviceCollection.AddScoped(_ => Mock.Of<ReelBolt.Inference.Api.Services.PostTexts.IPostTextWriter>());
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.ObjectEffectTools(db, user, ConfirmationTokenService.CreateEphemeral()));
        serviceCollection.AddScoped(_ => Mock.Of<ReelBolt.Inference.Api.Services.Media.IVideoEnhanceService>());
        serviceCollection.AddScoped(_ => AssistantToolTestFactory.VideoEnhanceTools(db, user, ConfirmationTokenService.CreateEphemeral()));
        serviceCollection.AddScoped<IProjectAccessService>(_ => TestTenancy.Access(db, user));

        var controller = new AssistantToolBridgeController(
            db, user, TestTenancy.Access(db, user), NullLogger<AssistantToolBridgeController>.Instance, charges ?? new Billing.FakeNonWorkflowCharges(), productEvents);
        var httpContext = new DefaultHttpContext { RequestServices = serviceCollection.BuildServiceProvider() };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    /// Seeds the caller's <c>application_users</c> row with the MCP admin opt-in. Without a row the
    /// setting reads false — that is the "no row" fail-closed case, tested separately.
    /// </summary>
    private static void SeedUser(InferenceApiDbContext db, Guid userId, bool mcpAdminToolsEnabled)
    {
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = userId,
            Email = "test@example.com",
            DisplayName = "Test User",
            McpAdminToolsEnabled = mcpAdminToolsEnabled
        });
        db.SaveChanges();
    }

    private static Project SeedProject(InferenceApiDbContext db, Guid ownerId, string name)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static WorkflowDefinition SeedWorkflow(InferenceApiDbContext db, Guid projectId, string name)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProjectId = projectId,
            Kind = WorkflowDefinitionKind.Standard,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkflowDefinitions.Add(workflow);
        db.SaveChanges();
        return workflow;
    }

    /// <summary>The tool result text of a 200 response, asserting the shape on the way.</summary>
    private static AssistantToolCallResultDto PayloadOf(ActionResult<AssistantToolCallResultDto> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<AssistantToolCallResultDto>().Subject;
    }

    private static async Task<AssistantToolCatalogResponse> CatalogOf(AssistantToolBridgeController controller)
    {
        var ok = (await controller.GetToolCatalog(CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<AssistantToolCatalogResponse>().Subject;
    }

    /// <summary>The gated subset as the provider itself reports it — never a hand-written list.</summary>
    private static IReadOnlyList<string> GatedNames(InferenceApiDbContext db, Guid userId)
    {
        var user = new FakeCurrentUser(userId);
        return AssistantToolTestFactory.CreateProvider(db, user).GetAdminToolNames(new AssistantToolContext(userId));
    }

    // ---------------------------------------------------------------------
    // GET /api/v1/assistant/tool-catalog
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ToolCatalog_ReturnsTheCollapsedMcpSurface_WithTitlesAndAnnotations()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var user = new FakeCurrentUser(userId);
        // Admin tools opt-in ON, so the catalog is the FULL MCP surface.
        SeedUser(db, userId, mcpAdminToolsEnabled: true);
        AssistantToolBridgeController controller = CreateController(db, user);

        List<AIFunction> registered = AssistantToolTestFactory
            .CreateProvider(db, user)
            .GetTools(new AssistantToolContext(userId))
            .ToList();

        IReadOnlyList<McpToolDescriptor> expected =
            AssistantMcpToolSurface.Project(registered, new HashSet<string>(StringComparer.Ordinal));

        AssistantToolCatalogResponse catalog = await CatalogOf(controller);

        catalog.Tools.Should().NotBeEmpty();
        catalog.Tools.Select(t => t.Name).Should().BeEquivalentTo(expected.Select(t => t.Name));
        catalog.Tools.Should().HaveCount(expected.Count).And.HaveCount(101);
        catalog.Tools.Select(t => t.Name).Should().OnlyHaveUniqueItems("a tool name is unique in the set");
        catalog.Tools.Should().NotContain(t =>
            t.Name.StartsWith("Propose", StringComparison.Ordinal)
            || t.Name.StartsWith("Apply", StringComparison.Ordinal));

        foreach (McpToolDescriptor descriptor in expected)
        {
            AssistantToolDescriptorDto dto = catalog.Tools.Single(t => t.Name == descriptor.Name);

            dto.Description.Should().Be(descriptor.Description);
            dto.Title.Should().Be(descriptor.Annotation.Title);
            dto.Annotations.ReadOnlyHint.Should().Be(descriptor.Annotation.ReadOnlyHint);
            dto.Annotations.DestructiveHint.Should().Be(descriptor.Annotation.DestructiveHint);
            dto.Annotations.IdempotentHint.Should().Be(descriptor.Annotation.IdempotentHint);
            dto.Annotations.OpenWorldHint.Should().Be(descriptor.Annotation.OpenWorldHint);
            dto.InputSchema.GetRawText().Should().Be(descriptor.InputSchema.GetRawText());
            dto.InputSchema.ValueKind.Should().Be(JsonValueKind.Object, "every AIFunctionFactory schema is an object");

            // §4 of the tool-bridge contract: the MCP server adds ONE `context` wrapper property to
            // every bridged tool's schema. A real tool declaring `context` would collide with it, so
            // the C# side must stay free of one.
            bool declaresContext = dto.InputSchema.TryGetProperty("properties", out JsonElement properties)
                && properties.TryGetProperty("context", out _);
            declaresContext.Should().BeFalse("no bridged tool may declare an argument named 'context'");
        }
    }

    [Fact]
    public async Task ToolCatalog_OmitsTheGatedTools_WhenAdminToolsAreOff()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var user = new FakeCurrentUser(userId);
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        AssistantToolBridgeController controller = CreateController(db, user);

        IReadOnlyList<string> gated = GatedNames(db, userId);
        gated.Should().NotBeEmpty("the admin surface is registered");

        List<AIFunction> registered = AssistantToolTestFactory
            .CreateProvider(db, user)
            .GetTools(new AssistantToolContext(userId))
            .ToList();

        AssistantToolCatalogResponse catalog = await CatalogOf(controller);
        IReadOnlyList<McpToolDescriptor> expected =
            AssistantMcpToolSurface.Project(registered, gated.ToHashSet(StringComparer.Ordinal));

        catalog.Tools.Select(t => t.Name).Should().BeEquivalentTo(expected.Select(t => t.Name));
        catalog.Tools.Select(t => t.Name).Should().NotIntersectWith(gated,
            "a narrow token is not even told the admin tools exist");
        catalog.Tools.Select(t => t.Name).Should().NotIntersectWith(
            AssistantMcpToolSurface.Collapsed
                .Where(p => gated.Contains(p.ApplyName))
                .Select(p => p.Name),
            "the collapsed names of admin tools are hidden too");
    }

    [Fact]
    public async Task ToolCatalog_ListsTheGatedTools_WhenAdminToolsAreOn()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var user = new FakeCurrentUser(userId);
        SeedUser(db, userId, mcpAdminToolsEnabled: true);
        AssistantToolBridgeController controller = CreateController(db, user);

        IReadOnlyList<string> gated = GatedNames(db, userId);

        AssistantToolCatalogResponse catalog = await CatalogOf(controller);
        catalog.Tools.Select(t => t.Name).Should().Contain(
            AssistantMcpToolSurface.Collapsed
                .Where(p => gated.Contains(p.ApplyName))
                .Select(p => p.Name));
    }

    [Fact]
    public async Task ToolCatalog_FailsClosed_WhenTheCallerHasNoUserRow()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        // No application_users row at all.
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        IReadOnlyList<string> gated = GatedNames(db, userId);

        AssistantToolCatalogResponse catalog = await CatalogOf(controller);
        catalog.Tools.Select(t => t.Name).Should().NotIntersectWith(gated);
        catalog.Tools.Select(t => t.Name).Should().NotIntersectWith(
            AssistantMcpToolSurface.Collapsed.Where(p => gated.Contains(p.ApplyName)).Select(p => p.Name));
    }

    private static void SetScope(AssistantToolBridgeController controller, string scope) =>
        controller.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("scope", scope)], "test"));

    [Theory]
    [InlineData("ExecuteWorkflow")]
    [InlineData("ListProjects")]
    [InlineData("SearchPlatformDocs")]
    [InlineData("WaitForExecution")]
    [InlineData("")]
    [InlineData("getexecutionstatus")]
    [InlineData("GetExecutionStatus ")]
    public async Task Runs_watch_token_is_refused_every_tool_but_GetExecutionStatus_and_nothing_runs(string tool)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: true);
        db.ResetSaveCount();
        var charges = new Billing.FakeNonWorkflowCharges();
        var executionService = new Mock<IWorkflowExecutionService>(MockBehavior.Strict);
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), executionService.Object, charges: charges);
        SetScope(controller, "runs.watch");

        ActionResult<AssistantToolCallResultDto> result = await controller.CallTool(
            new AssistantToolCallRequest { Name = tool }, CancellationToken.None);

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(403);
        System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain("\"error\":\"mcp_token_scope\"");
        db.SaveCount.Should().Be(0);
        (await db.McpToolCalls.CountAsync()).Should().Be(0);
        charges.Turns.Should().BeEmpty();
        charges.Claims.Should().BeEmpty();
    }

    [Fact]
    public async Task The_first_tool_call_records_mcp_connected_once_and_a_refused_call_records_nothing()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        var log = new Services.Telemetry.RecordingProductEventLog();
        var emitter = new ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter(
            log, NullLogger<ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter>.Instance);

        AssistantToolBridgeController refused = CreateController(db, new FakeCurrentUser(userId), productEvents: emitter);
        SetScope(refused, "runs.watch");
        await refused.CallTool(new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None);
        log.Rows.Should().BeEmpty("a scope-refused call never reached a tool");

        for (int i = 0; i < 2; i++)
        {
            AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), productEvents: emitter);
            await controller.CallTool(new AssistantToolCallRequest { Name = "GetExecutionStatus" }, CancellationToken.None);
        }

        log.Rows.Should().ContainSingle().Which.Event.Should().Be("product.mcp_connected");
        log.Rows[0].UserId.Should().Be(userId);
    }

    [Fact]
    public async Task Runs_watch_token_reaches_GetExecutionStatus()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));
        SetScope(controller, "runs.watch");

        ActionResult<AssistantToolCallResultDto> result = await controller.CallTool(
            new AssistantToolCallRequest { Name = "GetExecutionStatus" }, CancellationToken.None);

        (result.Result as ObjectResult)?.StatusCode.Should().NotBe(403);
        (await db.McpToolCalls.CountAsync()).Should().Be(1, "an allowed call is recorded like any other");
    }

    [Theory]
    [InlineData("runs.watch assistant.tools")]
    [InlineData("runs.watch assistant.admin")]
    [InlineData("assistant.tools")]
    [InlineData("")]
    public async Task Combined_or_tool_scopes_and_unscoped_callers_keep_existing_behaviour(string scope)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));
        if (scope.Length > 0) SetScope(controller, scope);

        ActionResult<AssistantToolCallResultDto> result = await controller.CallTool(
            new AssistantToolCallRequest { Name = "NoSuchTool" }, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>("the scope check does not apply, so the tool lookup answers");
    }

    private const string OutOfCreditsText =
        "You've used this month's included assistant credits and your workspace has no credits left. "
        + "Add credits on the Plan & billing page to keep going.";

    [Theory]
    [InlineData("LookAtPicture")]
    [InlineData("RunPictureTool")]
    [InlineData("SearchPlatformDocs")]
    public async Task Out_of_credits_refuses_a_Platform_billed_tool_with_a_plain_isError_result_and_never_runs_it(string tool)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        var charges = new Billing.FakeNonWorkflowCharges { RefuseTurns = NonWorkflowChargeCodes.InsufficientCredits };
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), charges: charges);

        ActionResult<AssistantToolCallResultDto> result = await controller.CallTool(
            new AssistantToolCallRequest { Name = tool }, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>("a refusal is an ordinary tool result, not an HTTP error");
        AssistantToolCallResultDto payload = PayloadOf(result);
        payload.IsError.Should().BeTrue();
        payload.Content.Should().ContainSingle().Which.Text.Should().Be(OutOfCreditsText);
        charges.Turns.Should().ContainSingle().Which.Should().Be(((Guid?)TestTenancy.OrgOf(userId), UsageSource.Mcp));
        charges.TurnSettlements.Should().BeEmpty();
        charges.Claims.Should().BeEmpty("the tool never reached its pre-claim");
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        (call.Succeeded, call.ErrorCode).Should().Be((false, "insufficient_credits"));
    }

    /// <summary>
    /// S-75: a tool that cannot be executed answers ONE fixed sentence plus a reference an operator can grep the log
    /// by. The exception's own text — the SDK's argument-binding complaint, and in production a storage key, an
    /// internal host or a provider message — never crosses the bridge to the MCP client.
    /// </summary>
    [Fact]
    public async Task A_tool_that_cannot_run_answers_a_generic_failure_with_a_reference_not_the_exception_text()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        SeedProject(db, userId, "Harbour launch film");
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        // GetOutput requires an outputId; invoking it without one is the "could not be invoked" failure.
        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "GetOutput" }, CancellationToken.None));

        payload.IsError.Should().BeTrue();
        string text = payload.Content.Should().ContainSingle().Subject.Text;
        text.Should().StartWith("tool execution failed: the server could not run it")
            .And.Contain($"(ref {controller.HttpContext.TraceIdentifier})", "the reference names the request the log line names");
        text.Should().NotContain("System.").And.NotContain("Exception").And.NotContain("outputId")
            .And.NotContain("Argument");
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        (call.Succeeded, call.ErrorCode).Should().Be((false, "tool_execution_failed"));
    }

    /// <summary>
    /// QA-11: with the allowance used up and the balance 0 the turn hold is refused, but a tool the cost table
    /// classifies Free spends nothing, so it still runs: no hold, no claim, no settlement, nothing charged.
    /// </summary>
    [Fact]
    public async Task A_Free_tool_still_runs_at_allowance_used_and_balance_zero_without_a_hold()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        SeedProject(db, userId, "Harbour launch film");
        var charges = new Billing.FakeNonWorkflowCharges { RefuseTurns = NonWorkflowChargeCodes.InsufficientCredits };
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), charges: charges);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None));

        payload.IsError.Should().BeFalse(payload.Content[0].Text);
        payload.Content[0].Text.Should().Contain("Harbour launch film", "the free tool ran");
        charges.TurnSettlements.Should().BeEmpty("no hold was taken, so none is settled");
        charges.Claims.Should().BeEmpty("a free tool claims nothing");
        charges.Released.Should().BeEmpty();
        charges.UnheldCharges.Should().BeEmpty();
        charges.UnsettledRecords.Should().BeEmpty();
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        (call.Succeeded, call.ErrorCode).Should().Be((true, null));
    }

    /// <summary>
    /// Fail closed (Security review): the waiver is for InsufficientCredits only. With no organization the turn hold
    /// is refused as NoOrganization, so a Free tool is refused exactly like a paid one and never runs.
    /// </summary>
    [Fact]
    public async Task Free_tool_is_still_refused_when_the_caller_has_no_organization()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        ICurrentUser noWorkspace = Mock.Of<ICurrentUser>(u => u.UserId == userId && u.IsAuthenticated && u.OrganizationId == null);
        var charges = new Billing.FakeNonWorkflowCharges { RefuseTurns = NonWorkflowChargeCodes.NoOrganization };
        AssistantToolBridgeController controller = CreateController(db, noWorkspace, charges: charges);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None));

        payload.IsError.Should().BeTrue();
        payload.Content.Should().ContainSingle().Which.Text.Should().Be(NonWorkflowChargeResults.AssistantNoWorkspaceText);
        charges.Turns.Should().ContainSingle().Which.Should().Be(((Guid?)null, UsageSource.Mcp));
        charges.Claims.Should().BeEmpty("the tool never reached its pre-claim");
        charges.TurnSettlements.Should().BeEmpty();
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        (call.Succeeded, call.ErrorCode).Should().Be((false, "insufficient_credits"));
    }

    /// <summary>
    /// Fail closed (Security review): when the credit gate cannot evaluate the turn (GateUnavailable) a Free tool is
    /// refused with the same gate-unavailable text a paid tool gets, and it never runs.
    /// </summary>
    [Fact]
    public async Task Free_tool_is_still_refused_when_the_credit_gate_is_unavailable()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        SeedProject(db, userId, "Harbour launch film");
        var charges = new Billing.FakeNonWorkflowCharges { RefuseTurns = NonWorkflowChargeCodes.GateUnavailable };
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), charges: charges);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None));

        payload.IsError.Should().BeTrue();
        payload.Content.Should().ContainSingle().Which.Text.Should().Be(NonWorkflowChargeResults.AssistantGateUnavailableText);
        payload.Content[0].Text.Should().NotContain("Harbour launch film", "the tool never ran");
        charges.Turns.Should().ContainSingle().Which.Should().Be(((Guid?)TestTenancy.OrgOf(userId), UsageSource.Mcp));
        charges.Claims.Should().BeEmpty("the tool never reached its pre-claim");
        charges.TurnSettlements.Should().BeEmpty();
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        (call.Succeeded, call.ErrorCode).Should().Be((false, "insufficient_credits"));
    }

    /// <summary>Fail closed: a name the cost table does not classify is never free, so it is never admitted at 0/0.</summary>
    [Fact]
    public void A_name_the_cost_table_does_not_classify_is_not_free()
    {
        AssistantPlatformTools.IsPlatformBilled("ListProjects").Should().BeFalse();
        AssistantPlatformTools.IsPlatformBilled("ToolNobodyClassified").Should().BeTrue();
        AssistantPlatformTools.IsPlatformBilled(null).Should().BeTrue();
    }

    /// <summary>
    /// ExecuteWorkflow is classified Free because the run is charged by the workflow credit gate, not the turn. At 0/0 the
    /// tool is admitted, and its own preflight then refuses the run: nothing is queued.
    /// </summary>
    /// <summary>
    /// QA finding 2 (contract section 9): the automatic version is best effort end to end. A history service
    /// that cannot be built, or one whose capture throws, must not turn the MCP call into "tool execution
    /// failed"; the tool still runs (here the workflow's own preflight refuses, proving it was reached).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_history_service_that_cannot_resolve_or_capture_never_fails_the_tool_call(bool resolutionThrows)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "MCP Project");
        WorkflowDefinition workflow = SeedWorkflow(db, project.Id, "Main");
        Mock<IWorkflowExecutionPreflight> preflight = new();
        preflight.Setup(p => p.CheckAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowExecutionPreflightResult(false, false, 12, 0, 10, 14, true));
        Mock<ReelBolt.Inference.Api.Services.History.IProjectHistoryService> history = new();
        history.Setup(h => h.TryCaptureAutoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("capture blew up"));
        AssistantToolBridgeController controller = CreateController(
            db, new FakeCurrentUser(userId), new RecordingExecutionService(db), new GrantingGuard(), preflight: preflight.Object,
            configureServices: services =>
            {
                if (resolutionThrows)
                    services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectHistoryService>(
                        _ => throw new InvalidOperationException("cannot build the history service"));
                else
                    services.AddScoped(_ => history.Object);
            });

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "ExecuteWorkflow",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["workflowId"] = JsonSerializer.SerializeToElement(workflow.Id),
                ["userRequest"] = JsonSerializer.SerializeToElement("make the video"),
                ["regenerateVideoClips"] = JsonSerializer.SerializeToElement(false)
            },
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, WorkflowId = workflow.Id }
        }, CancellationToken.None));

        payload.Content[0].Text.Should().NotContain("tool execution failed");
        payload.Content[0].Text.Should().Contain("INSUFFICIENT_CREDITS", "the tool itself ran after the capture failed");
    }

    [Fact]
    public async Task ExecuteWorkflow_at_zero_credits_is_refused_by_its_own_preflight_and_queues_nothing()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "MCP Project");
        WorkflowDefinition workflow = SeedWorkflow(db, project.Id, "Main");
        var charges = new Billing.FakeNonWorkflowCharges { RefuseTurns = NonWorkflowChargeCodes.InsufficientCredits };
        Mock<IWorkflowExecutionPreflight> preflight = new();
        preflight.Setup(p => p.CheckAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowExecutionPreflightResult(false, false, 12, 0, 10, 14, true));
        var executionService = new RecordingExecutionService(db);
        AssistantToolBridgeController controller = CreateController(
            db, new FakeCurrentUser(userId), executionService, new GrantingGuard(), charges: charges, preflight: preflight.Object);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "ExecuteWorkflow",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["workflowId"] = JsonSerializer.SerializeToElement(workflow.Id),
                ["userRequest"] = JsonSerializer.SerializeToElement("make the video"),
                ["regenerateVideoClips"] = JsonSerializer.SerializeToElement(false)
            },
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, WorkflowId = workflow.Id }
        }, CancellationToken.None));

        payload.Content[0].Text.Should().Contain("INSUFFICIENT_CREDITS", "the workflow's own preflight refused the run");
        preflight.Verify(p => p.CheckAsync(It.IsAny<Guid?>(), project.Id, workflow.Id, It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        executionService.Initiators.Should().BeEmpty("the run was not queued");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        charges.TurnSettlements.Should().BeEmpty();
    }

    /// <summary>
    /// QA-P0-15: an MCP client calling ExecuteWorkflow through <c>POST /api/v1/assistant/tool-call</c> with a full paid
    /// bucket reads the same RUN_RATE_LIMITED body (sentence, bucket, used, limit, window, wait) chat and REST give, from
    /// the real guard; nothing is queued.
    /// </summary>
    [Fact]
    public async Task ExecuteWorkflow_over_the_hourly_limit_reaches_the_MCP_client_as_a_RUN_RATE_LIMITED_body()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "MCP Project");
        WorkflowDefinition workflow = SeedWorkflow(db, project.Id, "Main");
        for (int i = 0; i < 10; i++)
        {
            db.RunRateEvents.Add(new RunRateEvent
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ProjectId = project.Id,
                Bucket = RunRateEvent.PaidBucket,
                Source = RunRateEvent.ExecuteSource,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10 - i),
            });
        }

        db.SaveChanges();
        var executionService = new RecordingExecutionService(db);
        var guard = new AssistantExecutionGuard(db, new ConfigurationBuilder().Build());
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), executionService, guard);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "ExecuteWorkflow",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["workflowId"] = JsonSerializer.SerializeToElement(workflow.Id),
                ["userRequest"] = JsonSerializer.SerializeToElement("make the video"),
                ["regenerateVideoClips"] = JsonSerializer.SerializeToElement(false)
            },
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, WorkflowId = workflow.Id }
        }, CancellationToken.None));

        using JsonDocument body = JsonDocument.Parse(payload.Content.Should().ContainSingle().Subject.Text);
        JsonElement root = body.RootElement;
        root.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
        root.GetProperty("error").GetString().Should().StartWith("run limit reached — 10 of 10 paid runs in the last hour on this project; try again in ");
        root.GetProperty("bucket").GetString().Should().Be("paid");
        root.GetProperty("used").GetInt32().Should().Be(10);
        root.GetProperty("limit").GetInt32().Should().Be(10);
        root.GetProperty("windowMinutes").GetInt32().Should().Be(60);
        root.GetProperty("retryAfterSeconds").GetInt32().Should().BeInRange(1, 50 * 60 + 1, "the oldest run frees at +41 min");
        executionService.Initiators.Should().BeEmpty("a refused run is never queued");
        (await db.RunRateEvents.CountAsync()).Should().Be(10, "a refusal records nothing");
    }

    [Fact]
    public async Task An_allowed_tool_call_is_held_as_Mcp_then_settled_at_what_it_metered()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        SeedProject(db, userId, "Harbour launch film");
        var charges = new Billing.FakeNonWorkflowCharges();
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), charges: charges);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None));

        payload.IsError.Should().BeFalse();
        payload.Content[0].Text.Should().Contain("Harbour launch film");
        charges.Turns.Should().ContainSingle().Which.Should().Be(((Guid?)TestTenancy.OrgOf(userId), UsageSource.Mcp));
        charges.TurnSettlements.Should().ContainSingle().Which.MeteredMillis.Should().Be(0);
    }

    /// <summary>F-2.3: a call over the workspace's in-flight cap is refused with 429 TURN_CONCURRENCY_LIMIT and a readable message.</summary>
    [Fact]
    public async Task A_call_over_the_concurrency_cap_is_a_429_with_a_readable_message_and_never_runs_the_tool()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        var charges = new Billing.FakeNonWorkflowCharges { RefuseTurns = NonWorkflowChargeCodes.TurnConcurrencyLimit };
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), charges: charges);

        ActionResult<AssistantToolCallResultDto> result = await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None);

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        JsonElement body = JsonSerializer.SerializeToElement(refused.Value);
        body.GetProperty("code").GetString().Should().Be("TURN_CONCURRENCY_LIMIT");
        body.GetProperty("message").GetString().Should().Be(NonWorkflowChargeResults.AssistantTurnConcurrencyText);
        charges.TurnSettlements.Should().BeEmpty();
        (await db.McpToolCalls.SingleAsync()).ErrorCode.Should().Be("turn_concurrency_limit");
    }

    /// <summary>
    /// an MCP call is pre-claimed before it runs: a Platform-billed tool (the docs search's
    /// embeddings) at the unpriced default, a tool that costs no credits at 0.
    /// </summary>
    [Fact]
    public async Task A_platform_billed_call_is_pre_claimed_at_its_quote_and_a_free_one_at_nothing()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        var charges = new Billing.FakeNonWorkflowCharges();
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), charges: charges);

        await controller.CallTool(new AssistantToolCallRequest { Name = "SearchPlatformDocs", Arguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("credits") } }, CancellationToken.None);
        await controller.CallTool(new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None);

        charges.Claims.Select(c => c.StepMillis).Should().Equal(10_000L, 0L);
        charges.Claims.Should().OnlyContain(c => !c.AdmitUnderConcurrencyCap, "an MCP call is admitted under the cap when it begins");
    }

    /// <summary>
    /// F-2.2/F-3.3: the cut-off is checked before the call's paid work; a stop is a readable tool error (never a
    /// protocol error) and the tool never runs.
    /// </summary>
    [Fact]
    public async Task A_call_cut_off_before_its_paid_work_is_a_readable_tool_error_and_the_tool_never_runs()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        SeedProject(db, userId, "Harbour launch film");
        var charges = new Billing.FakeNonWorkflowCharges { StopAtCheck = 1, StopCode = NonWorkflowChargeCodes.InsufficientCredits };
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), charges: charges);

        ActionResult<AssistantToolCallResultDto> result = await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        AssistantToolCallResultDto payload = PayloadOf(result);
        payload.IsError.Should().BeTrue();
        payload.Content.Should().ContainSingle().Which.Text.Should().Be(NonWorkflowChargeResults.AssistantTurnOutOfCreditsText);
        charges.TurnChecks.Should().ContainSingle();
        charges.TurnSettlements.Should().ContainSingle("the hold is still closed at what was metered");
        (await db.McpToolCalls.SingleAsync()).ErrorCode.Should().Be("turn_stopped");
    }

    /// <summary>Waits until cancelled (a tool call that never finishes on its own).</summary>
    private sealed class HangingGuard : IAssistantExecutionGuard
    {
        public async Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new RunRateDecision(false, bucket, 10, 10, 3600, 60);
        }
    }

    /// <summary>F-4.2: a call past Assistant:MaxTurnSeconds is cancelled and answered as a readable tool error.</summary>
    [Fact]
    public async Task A_call_past_the_wall_clock_is_cancelled_and_answered_as_a_readable_tool_error()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "MCP Project");
        WorkflowDefinition workflow = SeedWorkflow(db, project.Id, "Main");
        var charges = new Billing.FakeNonWorkflowCharges();
        Microsoft.Extensions.Configuration.IConfiguration configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AssistantChatLimits.MaxTurnSecondsKey] = "1" })
            .Build();
        AssistantToolBridgeController controller = CreateController(
            db, new FakeCurrentUser(userId), new RecordingExecutionService(db), new HangingGuard(), charges: charges, configuration: configuration);

        Task<ActionResult<AssistantToolCallResultDto>> call = controller.CallTool(new AssistantToolCallRequest
        {
            Name = "ExecuteWorkflow",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["workflowId"] = JsonSerializer.SerializeToElement(workflow.Id),
                ["userRequest"] = JsonSerializer.SerializeToElement("make the video"),
                ["regenerateVideoClips"] = JsonSerializer.SerializeToElement(false)
            },
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, WorkflowId = workflow.Id }
        }, CancellationToken.None);
        (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(call, "the wall clock cancels the call");

        ActionResult<AssistantToolCallResultDto> result = await call;
        result.Result.Should().BeOfType<OkObjectResult>("a timeout is a tool result, never a protocol error");
        AssistantToolCallResultDto payload = PayloadOf(result);
        payload.IsError.Should().BeTrue();
        payload.Content.Should().ContainSingle().Which.Text.Should().Be(NonWorkflowChargeResults.AssistantTurnTooLongText);
        charges.TurnSettlements.Should().ContainSingle();
        (await db.McpToolCalls.SingleAsync()).ErrorCode.Should().Be("turn_timeout");
    }

    [Fact]
    public async Task A_tool_call_with_no_workspace_is_refused_rather_than_run_uncharged()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        ICurrentUser noWorkspace = Mock.Of<ICurrentUser>(u => u.UserId == userId && u.IsAuthenticated && u.OrganizationId == null);
        var charges = new Billing.FakeNonWorkflowCharges { RefuseTurns = NonWorkflowChargeCodes.NoOrganization };
        AssistantToolBridgeController controller = CreateController(db, noWorkspace, charges: charges);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListProjects" }, CancellationToken.None));

        payload.IsError.Should().BeTrue();
        payload.Content.Should().ContainSingle().Which.Text.Should().Be(NonWorkflowChargeResults.AssistantNoWorkspaceText);
        charges.Turns.Should().ContainSingle().Which.Should().Be(((Guid?)null, UsageSource.Mcp), "the charge service decides, the bridge never skips it");
        charges.TurnSettlements.Should().BeEmpty();
    }

    [Fact]
    public void The_bridge_routes_are_exactly_the_frozen_contracts()
    {
        // Both routes are authenticated with the existing JWT scheme, exactly as every sibling
        // authenticated controller is.
        typeof(AssistantToolBridgeController).GetCustomAttributes<AuthorizeAttribute>()
            .Should().NotBeEmpty();

        typeof(AssistantToolBridgeController).GetCustomAttribute<RouteAttribute>()!.Template
            .Should().Be("api/v1/assistant");

        typeof(AssistantToolBridgeController)
            .GetMethod(nameof(AssistantToolBridgeController.GetToolCatalog))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("tool-catalog");

        typeof(AssistantToolBridgeController)
            .GetMethod(nameof(AssistantToolBridgeController.CallTool))!
            .GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("tool-call");
    }

    [Fact]
    public void The_bridge_payloads_use_the_frozen_wire_keys()
    {
        // MVC serializes responses with the web defaults; the frozen contract fixes the key names,
        // so they are pinned here rather than left to the shape of a record property.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        string catalogJson = JsonSerializer.Serialize(new AssistantToolCatalogResponse(
        [
            new AssistantToolDescriptorDto(
                "SomeTool", "a description", JsonSerializer.SerializeToElement(new { type = "object" }),
                "Some tool",
                new AssistantToolAnnotationsDto("Some tool", ReadOnlyHint: true, DestructiveHint: false,
                    IdempotentHint: true, OpenWorldHint: false))
        ]), web);

        using (JsonDocument catalog = JsonDocument.Parse(catalogJson))
        {
            JsonElement tools = catalog.RootElement.GetProperty("tools");
            tools.ValueKind.Should().Be(JsonValueKind.Array);
            tools[0].GetProperty("name").GetString().Should().Be("SomeTool");
            tools[0].GetProperty("description").GetString().Should().Be("a description");
            tools[0].GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
            tools[0].GetProperty("title").GetString().Should().Be("Some tool");
            JsonElement annotations = tools[0].GetProperty("annotations");
            annotations.GetProperty("title").GetString().Should().Be("Some tool");
            annotations.GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
            annotations.GetProperty("destructiveHint").GetBoolean().Should().BeFalse();
            annotations.GetProperty("idempotentHint").GetBoolean().Should().BeTrue();
            annotations.GetProperty("openWorldHint").GetBoolean().Should().BeFalse();
        }

        string resultJson = JsonSerializer.Serialize(AssistantToolCallResultDto.TextResult("hello"), web);
        using (JsonDocument result = JsonDocument.Parse(resultJson))
        {
            result.RootElement.GetProperty("isError").GetBoolean().Should().BeFalse();
            JsonElement content = result.RootElement.GetProperty("content");
            content[0].GetProperty("type").GetString().Should().Be("text");
            content[0].GetProperty("text").GetString().Should().Be("hello");
        }
    }

    // ---------------------------------------------------------------------
    // POST /api/v1/assistant/tool-call — gating (mcp_admin_tools_enabled)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_gated_tool_is_refused_when_admin_tools_are_off_and_writes_no_domain_rows()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId, isPlatformAdmin: false));

        int agentsBefore = await db.AgentDefinitions.CountAsync();

        // The body TRIES to carry authority AND to turn the setting on. Neither works: the bound
        // request type has no place to put them, the setting is read from the caller's own row, and
        // the acting caller is the ICurrentUser the transport verified.
        const string body = """
            {
              "name": "ListAgentsAdmin",
              "arguments": {},
              "userId": "00000000-0000-0000-0000-000000000001",
              "isPlatformAdmin": true,
              "isAdmin": true,
              "role": "Admin",
              "mcpAdminToolsEnabled": true
            }
            """;
        AssistantToolCallRequest request = JsonSerializer.Deserialize<AssistantToolCallRequest>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        db.ResetSaveCount();
        var result = await controller.CallTool(request, CancellationToken.None);
        AssistantToolCallResultDto payload = PayloadOf(result);

        // 1. The refusal is an ordinary tool-level result the model reads — 200, isError false, JSON
        //    text — never a 403 that would confirm the tool exists and never a 500.
        result.Result.Should().BeOfType<OkObjectResult>();
        payload.IsError.Should().BeFalse("a gate refusal is a result the model reads, never a transport error");
        payload.Content.Should().ContainSingle();
        payload.Content[0].Type.Should().Be("text");
        payload.Content[0].Text.Should().Contain("admin tools are not enabled");
        using (JsonDocument refusal = JsonDocument.Parse(payload.Content[0].Text))
            refusal.RootElement.GetProperty("code").GetString().Should().Be("MCP_ADMIN_TOOLS_DISABLED");

        // 2. NO DOMAIN WRITES. The only row is the metering row the contract requires, and it records
        //    this very refusal.
        db.SaveCount.Should().Be(1, "the only write is the mcp_tool_calls row");
        (await db.AgentDefinitions.CountAsync()).Should().Be(agentsBefore, "the tool never ran");
        (await db.UsageEvents.CountAsync()).Should().Be(0, "usage_events records provider cost, not tool usage");
        db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty("nothing is left unpersisted or half-written");

        McpToolCall call = await db.McpToolCalls.SingleAsync();
        call.ToolName.Should().Be("ListAgentsAdmin");
        call.Succeeded.Should().BeFalse();
        call.ErrorCode.Should().Be("mcp_admin_tools_disabled");
        call.UserId.Should().Be(userId);
        call.OrganizationId.Should().Be(TestTenancy.OrgOf(userId));
    }

    [Fact]
    public async Task A_gated_tool_with_the_setting_on_still_runs_its_own_authority_check()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();

        // Setting ON, caller NOT a platform admin: the tool must still refuse in its own body. The
        // setting is an ADDITIONAL condition, never a substitute for per-call authority.
        SeedUser(db, userId, mcpAdminToolsEnabled: true);
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId, isPlatformAdmin: false));

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListAgentsAdmin" }, CancellationToken.None));

        payload.IsError.Should().BeFalse("the tool's own refusal is a readable result");
        payload.Content[0].Text.Should().Contain("admin privileges required");

        // ...and it is metered as a REFUSAL, not a success: a body-level decline and the bridge's own
        // refusals are recorded the same way, so "somebody keeps calling a tool they have no authority
        // for" is visible to an operator (the model's view stays isError:false; only the meter differs).
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        call.Succeeded.Should().BeFalse();
        call.ErrorCode.Should().Be("tool_refused");
        (await db.UsageEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Every_gated_tool_a_non_admin_calls_is_metered_as_a_refusal()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: true);

        // The three gated tools an operator saw recorded as successes by a non-admin with the opt-in
        // on. One is platform-scoped and two are organization-scoped, so this covers both authorities.
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId, isPlatformAdmin: false));
        string[] tools = ["ListAgentsAdmin", "GetDecisionCalibration", "ListSkills"];

        foreach (string name in tools)
        {
            AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
                new AssistantToolCallRequest { Name = name }, CancellationToken.None));
            payload.IsError.Should().BeFalse($"{name} declines as a readable result");
        }

        List<McpToolCall> rows = await db.McpToolCalls.OrderBy(c => c.ToolName).ToListAsync();
        rows.Select(r => r.ToolName).Should().BeEquivalentTo(tools);
        rows.Should().AllSatisfy(r =>
        {
            r.Succeeded.Should().BeFalse($"{r.ToolName} was refused, not obeyed");
            r.ErrorCode.Should().Be("tool_refused");
        });
    }

    [Fact]
    public async Task The_same_gated_tool_succeeds_for_a_platform_admin_with_the_setting_on()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        db.AgentDefinitions.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "AuthorAgent",
            Description = "a built-in agent",
            SystemPrompt = "You are a test agent",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        SeedUser(db, userId, mcpAdminToolsEnabled: true);

        AssistantToolBridgeController controller = CreateController(
            db, new FakeCurrentUser(userId, isPlatformAdmin: true));

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListAgentsAdmin" }, CancellationToken.None));

        // The gate must not degrade into "always refuse".
        payload.IsError.Should().BeFalse();
        payload.Content[0].Text.Should().Contain("AuthorAgent");

        // The positive control for the refusal classifier: a real payload is metered as a SUCCESS with
        // no error code, so "everything is a refusal now" would fail here.
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        call.Succeeded.Should().BeTrue();
        call.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_tool_name_is_a_404_carrying_the_name_never_a_403()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: true);
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        var result = await controller.CallTool(
            new AssistantToolCallRequest { Name = "NoSuchTool" }, CancellationToken.None);

        var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(notFound.Value));
        body.RootElement.GetProperty("name").GetString().Should().Be("NoSuchTool");

        McpToolCall call = await db.McpToolCalls.SingleAsync();
        call.ToolName.Should().Be("NoSuchTool");
        call.Succeeded.Should().BeFalse();
        call.ErrorCode.Should().Be("unknown_tool");
    }

    [Fact]
    public async Task The_deprecated_WaitForExecution_alias_answers_as_GetExecutionStatus_and_is_not_in_the_catalog()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        Project project = SeedProject(db, userId, "Alias film");
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "WaitForExecution",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["executionId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
                ["projectId"] = JsonSerializer.SerializeToElement(project.Id),
                ["timeoutSeconds"] = JsonSerializer.SerializeToElement(60)
            }
        }, CancellationToken.None));

        using JsonDocument body = JsonDocument.Parse(payload.Content[0].Text);
        body.RootElement.GetProperty("deprecated").GetString().Should().Be("WaitForExecution is replaced by GetExecutionStatus; it no longer waits");
        body.RootElement.GetProperty("error").GetString().Should().Be(AssistantWorkflowTools.ExecutionNotFoundMessage);
        (await db.McpToolCalls.SingleAsync()).ToolName.Should().Be("GetExecutionStatus");

        IEnumerable<string> names = (await CatalogOf(controller)).Tools.Select(t => t.Name);
        names.Should().Contain("GetExecutionStatus").And.NotContain("WaitForExecution").And.NotContain("WatchExecution");
    }

    [Fact]
    public async Task A_missing_tool_name_is_a_400_and_is_still_metered()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        var result = await controller.CallTool(new AssistantToolCallRequest(), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        call.ToolName.Should().BeEmpty();
        call.ErrorCode.Should().Be("name_required");
    }

    [Fact]
    public async Task A_genuine_execution_failure_is_reported_as_isError_true()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        // ValidateWorkflow's `steps` is a required parameter: invoking it with none refuses at the
        // binder, which is an execution failure rather than a tool-level refusal.
        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ValidateWorkflow" }, CancellationToken.None));

        payload.IsError.Should().BeTrue();
        payload.Content.Should().ContainSingle();
        payload.Content[0].Text.Should().NotBeNullOrWhiteSpace("the model still gets something actionable");

        McpToolCall call = await db.McpToolCalls.SingleAsync();
        call.Succeeded.Should().BeFalse();
        call.ErrorCode.Should().Be("tool_execution_failed");
    }

    // ---------------------------------------------------------------------
    // T-030: an unpriced Platform model is a readable isError result
    // ---------------------------------------------------------------------

    private static (Project Project, EditImageDocument Document) SeedPicture(InferenceApiDbContext db, Guid userId, FakeFileStorage storage)
    {
        Project project = SeedProject(db, userId, "Poster project");
        EditImageDocument document = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "Poster",
            CanvasWidth = 640,
            CanvasHeight = 480,
            Revision = 1
        };
        EditImageLayer layer = new()
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            ZOrder = 1,
            Kind = EditImageLayerKind.Source,
            Role = "background",
            Label = "photo.png",
            StorageKey = $"projects/{project.Id}/userFiles/photo.png",
            Width = 640,
            Height = 480,
            TransformJson = "{}"
        };
        storage.Objects[layer.StorageKey] = [1, 2, 3];
        db.EditImageDocuments.Add(document);
        db.EditImageLayers.Add(layer);
        db.SaveChanges();
        return (project, document);
    }

    private static Mock<IInferenceProviderResolver> VisionResolver()
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(x => x.ResolveVisionAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "https://v", "gpt-4o", "k", 30));
        return resolver;
    }

    /// <summary>
    /// an MCP LookAtPicture is sized exactly like the chat turn's (the same guard
    /// hooks): its claim carries the picture's image tokens, so a 24 MP stored picture claims more than a small one.
    /// </summary>
    [Fact]
    public async Task An_mcp_look_at_a_large_stored_picture_claims_more_than_at_a_small_one()
    {
        static async Task<long> LookClaimAsync(int width, int height)
        {
            using WriteCountingDbContext db = CreateContext();
            Guid userId = Guid.NewGuid();
            var user = new FakeCurrentUser(userId);
            FakeFileStorage storage = new();
            (Project project, EditImageDocument document) = SeedPicture(db, userId, storage);
            storage.Objects[$"projects/{project.Id}/userFiles/photo.png"] = PngHeader(width, height);
            IInferenceProviderResolver resolver = VisionResolver().Object;
            AssistantPictureTools pictureTools = AssistantToolTestFactory.PictureTools(
                db, user, ConfirmationTokenService.CreateEphemeral(), storage, resolver: resolver, chatClients: Mock.Of<IChatClientFactory>());
            var charges = new Billing.FakeNonWorkflowCharges { EstimateBy = r => (long)r.Quantity };
            AssistantToolBridgeController controller = CreateController(db, user, pictureTools: pictureTools, charges: charges, resolver: resolver);

            await controller.CallTool(new AssistantToolCallRequest
            {
                Name = "LookAtPicture",
                Context = new AssistantToolCallPageContext { ProjectId = project.Id, ImageDocumentId = document.Id }
            }, CancellationToken.None);

            return charges.Claims.Single().StepMillis;
        }

        long small = await LookClaimAsync(512, 512);
        long large = await LookClaimAsync(6000, 4000);

        large.Should().BeGreaterThan(small);
        (large - small).Should().Be(
            AssistantTurnGuard.ImageTokensFor(6000L * 4000) - AssistantTurnGuard.ImageTokensFor(640L * 480),
            "the small picture's row records 640 x 480, larger than its 512 x 512 header");
    }

    /// <summary>
    /// T-095: the MCP bridge binds the call's arguments once and hands the same dictionary to the guard and
    /// to the tool, so the layer the claim sized is the layer the look sends: a new backmost layer that appears while
    /// the call is being claimed is not looked at instead.
    /// </summary>
    [Fact]
    public async Task An_mcp_look_sends_the_layer_its_claim_was_sized_at()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var user = new FakeCurrentUser(userId);
        FakeFileStorage storage = new();
        (Project project, EditImageDocument document) = SeedPicture(db, userId, storage);
        Guid sized = await db.EditImageLayers.Where(l => l.DocumentId == document.Id).Select(l => l.Id).SingleAsync();
        storage.Objects[$"projects/{project.Id}/userFiles/photo.png"] = PngHeader(512, 512);
        IInferenceProviderResolver resolver = VisionResolver().Object;
        Mock<IChatClient> client = new();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A poster.")));
        Mock<IChatClientFactory> chatClients = new();
        chatClients.Setup(c => c.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(client.Object);
        AssistantPictureTools pictureTools = AssistantToolTestFactory.PictureTools(
            db, user, ConfirmationTokenService.CreateEphemeral(), storage, resolver: resolver, chatClients: chatClients.Object);
        var charges = new Billing.FakeNonWorkflowCharges { EstimateBy = r => (long)r.Quantity };
        Guid behind = Guid.NewGuid();
        charges.OnClaim = _ =>
        {
            // Meanwhile, another editor request puts a huge new layer at the back of the picture.
            string key = $"projects/{project.Id}/userFiles/huge.png";
            storage.Objects[key] = PngHeader(6000, 6000);
            db.EditImageLayers.Add(new EditImageLayer
            {
                Id = behind,
                DocumentId = document.Id,
                ZOrder = 0,
                Kind = EditImageLayerKind.Image,
                Role = "background",
                Label = "huge.png",
                StorageKey = key,
                Width = 6000,
                Height = 6000,
                TransformJson = "{}"
            });
            db.SaveChanges();
        };
        AssistantToolBridgeController controller = CreateController(db, user, pictureTools: pictureTools, charges: charges, resolver: resolver);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "LookAtPicture",
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, ImageDocumentId = document.Id }
        }, CancellationToken.None));

        payload.IsError.Should().BeFalse(payload.Content.FirstOrDefault()?.Text);
        using JsonDocument result = JsonDocument.Parse(payload.Content.Single().Text!);
        result.RootElement.GetProperty("layerId").GetGuid().Should().Be(sized, "the invoked arguments carry the sized layerId");
        charges.Claims.Single().StepMillis.Should().Be(
            AssistantTurnGuard.EstimateInputTokens([new ChatMessage(ChatRole.User, AssistantPictureTools.DefaultLookQuestion)], null)
            + AssistantTurnGuard.ImageTokensFor(640L * 480) + AssistantPictureTools.LookOutputTokens);
    }

    /// <summary>A PNG signature and IHDR chunk declaring <paramref name="width"/> x <paramref name="height"/>.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        byte[] png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        png[24] = 8;
        png[25] = 6;
        return png;
    }

    [Fact]
    public async Task A_tool_refused_for_an_unpriced_model_is_a_readable_isError_result()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var user = new FakeCurrentUser(userId);
        FakeFileStorage storage = new();
        (Project project, EditImageDocument document) = SeedPicture(db, userId, storage);
        var refusal = new ModelUnpricedException(InferenceProviderCapability.Vision, "Pricey vision");
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        AssistantPictureTools pictureTools = AssistantToolTestFactory.PictureTools(
            db, user, ConfirmationTokenService.CreateEphemeral(), storage, resolver: resolver.Object, chatClients: Mock.Of<IChatClientFactory>());
        AssistantToolBridgeController controller = CreateController(db, user, pictureTools: pictureTools);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "LookAtPicture",
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, ImageDocumentId = document.Id }
        }, CancellationToken.None));

        payload.IsError.Should().BeTrue("an unpriced model is a failure the model must not mistake for a result");
        payload.Content.Should().ContainSingle().Which.Text.Should().Be(refusal.Message, "the plain sentence, not JSON");
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        (call.Succeeded, call.ErrorCode).Should().Be((false, "model_unpriced"));
    }

    [Fact]
    public async Task A_tool_that_throws_for_an_unpriced_model_is_answered_with_the_plain_refusal_as_isError()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var user = new FakeCurrentUser(userId);
        FakeFileStorage storage = new();
        (Project project, EditImageDocument document) = SeedPicture(db, userId, storage);
        var refusal = new ModelUnpricedException(InferenceProviderCapability.Vision, "Pricey vision");
        Mock<IChatClientFactory> chatClients = new();
        chatClients.Setup(c => c.Get(It.IsAny<ResolvedInferenceProvider>())).Throws(refusal);
        AssistantPictureTools pictureTools = AssistantToolTestFactory.PictureTools(
            db, user, ConfirmationTokenService.CreateEphemeral(), storage, resolver: VisionResolver().Object, chatClients: chatClients.Object);
        AssistantToolBridgeController controller = CreateController(db, user, pictureTools: pictureTools);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "LookAtPicture",
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, ImageDocumentId = document.Id }
        }, CancellationToken.None));

        payload.IsError.Should().BeTrue();
        payload.Content.Should().ContainSingle().Which.Text.Should().Be(refusal.Message);
        McpToolCall call = await db.McpToolCalls.SingleAsync();
        (call.Succeeded, call.ErrorCode).Should().Be((false, "model_unpriced"));
    }

    // ---------------------------------------------------------------------
    // POST /api/v1/assistant/tool-call — metering
    // ---------------------------------------------------------------------

    [Fact]
    public async Task One_mcp_tool_calls_row_is_written_per_call_including_refusals()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        SeedUser(db, userId, mcpAdminToolsEnabled: false);
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        // A plain success, a gated refusal, and an unknown tool: three calls, three rows.
        await controller.CallTool(new AssistantToolCallRequest { Name = "ListStepSchemas" }, CancellationToken.None);
        await controller.CallTool(new AssistantToolCallRequest { Name = "ListAgentsAdmin" }, CancellationToken.None);
        await controller.CallTool(new AssistantToolCallRequest { Name = "NoSuchTool" }, CancellationToken.None);

        List<McpToolCall> rows = await db.McpToolCalls.OrderBy(c => c.CalledAt).ToListAsync();
        rows.Should().HaveCount(3, "one row per bridged call, refusal or not");
        rows.Select(r => r.ToolName).Should().BeEquivalentTo(["ListStepSchemas", "ListAgentsAdmin", "NoSuchTool"]);
        rows.Single(r => r.ToolName == "ListStepSchemas").Succeeded.Should().BeTrue();
        rows.Single(r => r.ToolName == "ListStepSchemas").ErrorCode.Should().BeNull();
        rows.Single(r => r.ToolName == "ListAgentsAdmin").Succeeded.Should().BeFalse();
        rows.Single(r => r.ToolName == "ListAgentsAdmin").ErrorCode.Should().Be("mcp_admin_tools_disabled");
        rows.Single(r => r.ToolName == "NoSuchTool").ErrorCode.Should().Be("unknown_tool");

        // A read tool's row is a record, not a charge: nothing lands in the provider-cost ledger.
        (await db.UsageEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_MCP_caused_execution_is_labelled_ExecutionInitiator_Mcp()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "MCP Project");
        WorkflowDefinition workflow = SeedWorkflow(db, project.Id, "Main");

        var executionService = new RecordingExecutionService(db);
        AssistantToolBridgeController controller = CreateController(
            db, new FakeCurrentUser(userId), executionService, new GrantingGuard());

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "ExecuteWorkflow",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["workflowId"] = JsonSerializer.SerializeToElement(workflow.Id),
                ["userRequest"] = JsonSerializer.SerializeToElement("make the video"),
                ["regenerateVideoClips"] = JsonSerializer.SerializeToElement(false)
            },
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, WorkflowId = workflow.Id }
        }, CancellationToken.None));

        payload.IsError.Should().BeFalse(payload.Content[0].Text);
        executionService.Initiators.Should().ContainSingle().Which.Should().Be(ExecutionInitiator.Mcp,
            "work done because of an MCP client is attributed to MCP, not to the assistant chat");

        WorkflowExecution run = await db.WorkflowExecutions.SingleAsync();
        run.Initiator.Should().Be(ExecutionInitiator.Mcp);
    }

    /// <summary>
    /// A run's snapshot (kind ExecutionSnapshot) is a system object: no MCP tool call can read-modify, delete or run
    /// it. Execution goes through the REAL execution service with a granting guard, so only its kind filter stands
    /// between the snapshot id and a queued run.
    /// </summary>
    [Fact]
    public async Task No_MCP_tool_call_can_edit_delete_or_execute_a_run_snapshot()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "MCP Project");
        WorkflowDefinition snapshot = SeedWorkflow(db, project.Id, "Launch reel");
        snapshot.Kind = WorkflowDefinitionKind.ExecutionSnapshot;
        db.SaveChanges();
        AssistantToolBridgeController controller = CreateController(
            db, new FakeCurrentUser(userId),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            new GrantingGuard());

        async Task<string> CallAsync(string tool, params (string Key, object? Value)[] args)
        {
            try
            {
                AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
                {
                    Name = tool,
                    Arguments = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value)),
                    Context = new AssistantToolCallPageContext { ProjectId = project.Id }
                }, CancellationToken.None));
                return payload.Content[0].Text;
            }
            catch (WorkflowNotFoundException)
            {
                return "workflow not found";
            }
        }

        object[] steps = [];
        // MCP exposes the collapsed name only: it proposes and applies inside one call, and a
        // client-supplied confirmationToken is never honoured.
        (await CallAsync("SaveWorkflow", ("workflowId", snapshot.Id), ("name", null), ("steps", steps)))
            .Should().Contain("workflow not found").And.NotContain("confirmationToken");
        (await CallAsync("SaveWorkflow", ("workflowId", snapshot.Id), ("name", null), ("steps", steps), ("confirmationToken", "forged")))
            .Should().Contain("workflow not found");
        (await CallAsync("DeleteWorkflow", ("workflowId", snapshot.Id)))
            .Should().Contain("not found").And.NotContain("confirmationToken");
        (await CallAsync("DeleteWorkflow", ("workflowId", snapshot.Id), ("confirmationToken", "forged")))
            .Should().Contain("not found");
        // ExecuteWorkflow is the one of the five that THROWS (WorkflowNotFoundException) rather than answering a
        // refusal result, so since S-75 the bridge no longer echoes its message: the call reads as the generic
        // execution failure with a reference, and the "not found" wording itself stays in the log. What this test is
        // about — the snapshot cannot be run — is asserted by the two checks below.
        (await CallAsync("ExecuteWorkflow", ("workflowId", snapshot.Id), ("userRequest", "go"), ("regenerateVideoClips", false)))
            .Should().StartWith("tool execution failed").And.NotContain("workflowName").And.NotContain("not found");

        db.ChangeTracker.Clear();
        (await db.WorkflowDefinitions.SingleAsync(w => w.Id == snapshot.Id)).Kind.Should().Be(WorkflowDefinitionKind.ExecutionSnapshot);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0, "no run was queued against the snapshot");
    }

    // ---------------------------------------------------------------------
    // context + arguments travel verbatim; the body carries no authority
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Page_context_reaches_the_tool_as_its_AssistantToolContext()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "Context Project");
        db.AgentDefinitions.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "ContextAgent",
            Description = "a built-in agent",
            SystemPrompt = "You are a test agent",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        // ListAgents is called here WITHOUT its optional projectId, so the project it acts on can ONLY
        // come from the per-call context: this is the context contract in its purest form.
        AssistantToolCallResultDto withContext = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "ListAgents",
            Context = new AssistantToolCallPageContext { ProjectId = project.Id }
        }, CancellationToken.None));

        withContext.IsError.Should().BeFalse(withContext.Content[0].Text);
        withContext.Content[0].Text.Should().Contain("ContextAgent");

        AssistantToolCallResultDto withoutContext = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListAgents" }, CancellationToken.None));

        withoutContext.IsError.Should().BeFalse("a missing project is a tool result, not a failure");
        withoutContext.Content[0].Text.Should().Contain("no project in context");
    }

    [Fact]
    public async Task Arguments_reach_the_tool_verbatim()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "Argument Project");
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "GetProject",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["projectId"] = JsonSerializer.SerializeToElement(project.Id)
            }
        }, CancellationToken.None));

        payload.IsError.Should().BeFalse();
        payload.Content[0].Text.Should().Contain("Argument Project");
    }

    [Fact]
    public void The_tool_call_body_carries_no_identity_or_authority_field()
    {
        // The structural half of "userId comes from the JWT, never the body": there is nowhere in
        // the contract to put an identity, an admin bit, a role, a permission or the admin-tools
        // opt-in. Re-introducing one is a privilege-escalation hole, and this fails on the property
        // list. (The MCP admin opt-in is read from application_users, never from the body.)
        typeof(AssistantToolCallRequest).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["Name", "Arguments", "Context"]);

        typeof(AssistantToolCallPageContext).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["ProjectId", "WorkflowId", "ImageDocumentId"]);
    }

    // ---------------------------------------------------------------------
    // These are not chat turns
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_tool_call_neither_creates_nor_appends_to_an_assistant_thread()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var user = new FakeCurrentUser(userId);

        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = TestTenancy.OrgOf(userId),
            Title = "Existing chat",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.AssistantThreads.Add(thread);
        db.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Role = "user",
            Content = "hello",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        int threadsBefore = await db.AssistantThreads.CountAsync();
        int messagesBefore = await db.AssistantMessages.CountAsync();
        threadsBefore.Should().Be(1);
        messagesBefore.Should().Be(1);

        AssistantToolBridgeController controller = CreateController(db, user);
        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(
            new AssistantToolCallRequest { Name = "ListStepSchemas" }, CancellationToken.None));
        payload.IsError.Should().BeFalse();

        (await db.AssistantThreads.CountAsync()).Should().Be(threadsBefore, "the bridge must not allocate a thread");
        (await db.AssistantMessages.CountAsync()).Should().Be(messagesBefore, "the bridge must not append a turn");
        (await db.AssistantThreads.SingleAsync()).Id.Should().Be(thread.Id);
        (await db.AssistantMessages.SingleAsync()).Sequence.Should().Be(1);
        (await db.McpToolCalls.CountAsync()).Should().Be(1, "the call is still metered");
    }

    [Fact]
    public async Task A_tool_call_creates_no_thread_at_all_when_none_exists()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId));

        await controller.CallTool(new AssistantToolCallRequest { Name = "ListStepSchemas" }, CancellationToken.None);

        (await db.AssistantThreads.CountAsync()).Should().Be(0);
        (await db.AssistantMessages.CountAsync()).Should().Be(0);
    }

    /// <summary>Hands out a metering wrapper over a fixed-usage chat client, whatever provider is resolved.</summary>
    private sealed class MeteredChatClients(IUsageRecorder recorder) : IChatClientFactory
    {
        public IChatClient Get(ResolvedInferenceProvider provider) =>
            new MeteringChatClient(new UsageReportingChatClient(200, 30), provider, recorder);
    }

    [Fact]
    public async Task A_tool_calls_model_usage_is_metered_as_Mcp_for_the_callers_org_authorized_project_and_user()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        FakeCurrentUser user = new(userId);
        Project project = SeedProject(db, userId, "Picture Project");
        ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "cover.png", MimeType = "image/png", StorageKey = $"projects/{project.Id}/userFiles/cover.png", Category = "userFiles" };
        EditImageDocument document = new() { Id = Guid.NewGuid(), ProjectId = project.Id, SourceProjectFileId = file.Id, CanvasWidth = 1280, CanvasHeight = 720, Revision = 1 };
        EditImageLayer source = new() { Id = Guid.NewGuid(), DocumentId = document.Id, ZOrder = 1, Kind = EditImageLayerKind.Source, Role = "background", Label = "cover.png", StorageKey = file.StorageKey, Width = 1280, Height = 720, TransformJson = "{}" };
        db.ProjectFiles.Add(file);
        db.EditImageDocuments.Add(document);
        db.EditImageLayers.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        FakeFileStorage storage = new();
        storage.Objects[source.StorageKey!] = [1, 2, 3, 4];
        Mock<IInferenceProviderResolver> resolver = new();
        resolver
            .Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(
                Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "https://vision.example/v1", "gpt-4o", "key", 30));
        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new StaticPriceService(), sink, NullLogger<UsageRecorder>.Instance,
            metering: Microsoft.Extensions.Options.Options.Create(new MeteringOptions { FailOnUnattributed = true }));
        AssistantPictureTools pictureTools = AssistantToolTestFactory.PictureTools(
            db, user, ConfirmationTokenService.CreateEphemeral(), storage,
            resolver: resolver.Object, chatClients: new MeteredChatClients(recorder));
        AssistantToolBridgeController controller = CreateController(db, user, pictureTools: pictureTools);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "LookAtPicture",
            Context = new AssistantToolCallPageContext { ProjectId = project.Id, ImageDocumentId = document.Id }
        }, CancellationToken.None));

        payload.IsError.Should().BeFalse(payload.Content[0].Text);
        payload.Content[0].Text.Should().NotContain("\"error\"");
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        sink.Events.Should().NotBeEmpty();
        sink.Events.Should().OnlyContain(e =>
            e.Source == UsageSource.Mcp
            && e.OrganizationId == TestTenancy.OrgOf(userId)
            && e.ProjectId == project.Id
            && e.UserId == userId);
        UsageScope.Current.Should().BeNull("the bridge closes the scope it opened");
    }
    // ---------------------------------------------------------------------
    // T-014: ApplyWorkflow over the bridge is project-scoped like every other save path
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("continue")]
    [InlineData("stepResult")]
    public async Task ApplyWorkflow_over_the_bridge_refuses_a_sibling_projects_source_and_writes_no_workflow(string kind)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId, "Film");
        Project sibling = SeedProject(db, userId, "Sibling film");
        var author = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Author",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        var siblingRun = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            ProjectId = sibling.Id,
            OrganizationId = sibling.OrganizationId,
            Status = ExecutionStatus.Passed
        };
        var siblingResult = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = siblingRun.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputStorageKey = $"projects/{sibling.Id}/outputFiles/{siblingRun.Id:D}/film.mp4",
            Status = StepStatus.Completed
        };
        db.AgentDefinitions.Add(author);
        db.WorkflowExecutions.Add(siblingRun);
        db.WorkflowStepResults.Add(siblingResult);
        await db.SaveChangesAsync();

        CreateWorkflowStepRequest step = kind == "continue"
            ? ReelBolt.WorkflowEngine.Tests.StepOptions.ProjectScopedSaveValidationTests.AuthorStep(author.Id, siblingRun.Id)
            : ReelBolt.WorkflowEngine.Tests.StepOptions.ProjectScopedSaveValidationTests.AnalyzeStep(author.Id, siblingResult.Id);
        List<CreateWorkflowStepRequest> steps = [step];
        var diffService = new WorkflowEditDiffService(db);
        AssistantToolBridgeController controller = CreateController(db, new FakeCurrentUser(userId), diffService: diffService);

        AssistantToolCallResultDto payload = PayloadOf(await controller.CallTool(new AssistantToolCallRequest
        {
            Name = "SaveWorkflow",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["workflowId"] = JsonSerializer.SerializeToElement<Guid?>(null),
                ["name"] = JsonSerializer.SerializeToElement("wf"),
                ["steps"] = JsonSerializer.SerializeToElement(steps)
            },
            Context = new AssistantToolCallPageContext { ProjectId = project.Id }
        }, CancellationToken.None));

        string text = payload.Content[0].Text;
        text.Should().Contain(kind == "continue" ? StepOptionsSaveValidator.NotFoundCode : "not found in this project");
        (await db.WorkflowDefinitions.CountAsync()).Should().Be(0, "a refused apply writes no workflow");
        (await db.WorkflowSteps.CountAsync()).Should().Be(0);
    }
}
