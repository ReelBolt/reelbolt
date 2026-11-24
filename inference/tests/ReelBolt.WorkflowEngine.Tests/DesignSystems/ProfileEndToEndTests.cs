using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems;

/// <summary>
/// P2-25: the whole Phase-2 pipeline in one test over a shared store — seed a project with a CSS
/// token file, a <c>package.json</c> and route files; run the real Parse and Compose executors (the
/// Compose ingest HTTP call is served by a fake handler that invokes the REAL
/// <see cref="IDesignSystemIngestService"/>); approve the resulting Drafts; build the pin with the
/// real <see cref="IDesignSystemPinService"/>; load it with the real
/// <see cref="DesignSystemRunState"/> and assert the ambient blocks. A <c>-ds</c> template's
/// <c>Require</c> succeeds with the pin and fails <c>PROFILE_MISSING</c> without it, and the legacy
/// <c>quick-win-promo</c> template is unaffected.
/// </summary>
public sealed class ProfileEndToEndTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid OrgId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000025");

    private const string Css = ":root { --color-brand-primary: #6d28d9; --radius: 0.625rem; }";

    private const string PackageJson =
        """{"name":"acme","dependencies":{"next":"^15.0.0","react":"^19.0.0","tailwindcss":"^4.0.0"}}""";

    private const string Page = "export default function Home() { return <main>hi</main>; }";

    private const string Route =
        "export async function GET() { return new Response(\"ok\"); }\nexport async function POST() { return new Response(\"ok\"); }";

    // =================================================================
    // 1. The whole scenario
    // =================================================================

    [Fact]
    public async Task Full_build_pipeline_produces_approved_pin_and_ambient_blocks()
    {
        Guid userId = Guid.NewGuid();
        var (connection, api) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using SqliteConnection _ = connection;
        using InferenceApiDbContext apiDb = api;

        var store = new FakeStore();
        (Guid projectId, Guid executionId) = SeedProject(apiDb, store, userId);

        using var engineDb = new WorkflowEngineDbContext(EngineOptions(connection));

        var designSystemService = new DesignSystemService(
            apiDb, store, Gate().Object, NullLogger<DesignSystemService>.Instance);
        var ingestService = new DesignSystemIngestService(
            apiDb, designSystemService, store, NullLogger<DesignSystemIngestService>.Instance);
        var pinService = new DesignSystemPinService(apiDb);

        using ServiceProvider executorProvider = new ServiceCollection()
            .AddSingleton(engineDb)
            .BuildServiceProvider();
        IServiceScopeFactory scopeFactory = executorProvider.GetRequiredService<IServiceScopeFactory>();

        var handler = new FakeIngestHandler(ingestService, projectId, userId);
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("InferenceApi"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://inference-api/") });

        var executor = new DesignSystemStepExecutor(
            store, scopeFactory, httpFactory.Object, NullLogger<DesignSystemStepExecutor>.Instance);

        // --- Parse ---------------------------------------------------------------
        WorkflowStep parseStep = Step(1, """{"op":"Parse","version":1,"parse":{}}""");
        StepExecutionResult parseResult = await executor.ExecuteAsync(Context(parseStep, executionId, projectId));

        parseResult.Status.Should().Be(StepStatus.Completed);
        parseResult.ArtifactStorageKey.Should().NotBeNullOrEmpty();
        store.Objects.Should().ContainKey(parseResult.ArtifactStorageKey!);

        // --- Compose (real ingest through the fake HTTP boundary) ---------------
        WorkflowStep composeStep = Step(
            2,
            """{"op":"Compose","version":1,"compose":{"parseStepOrder":1,"designFallbackStepOrders":null,"productFallbackStepOrders":null,"ingest":true}}""");
        var history = new List<StepOutputHistoryEntry>
        {
            new(1, "parse", parseResult.Output, null, parseResult.ArtifactStorageKey)
        };

        StepExecutionResult composeResult =
            await executor.ExecuteAsync(Context(composeStep, executionId, projectId, history));

        composeResult.Status.Should().Be(StepStatus.Completed);
        JsonNode.Parse(composeResult.Output)!["meta"]!["ingest"]!.GetValue<string>().Should().Be("done");
        handler.Hits.Should().Be(1, "the Compose step reaches the ingest boundary exactly once");

        // --- Approve both Drafts ------------------------------------------------
        List<DesignSystemVersion> drafts = await apiDb.DesignSystemVersions
            .Where(version => version.WorkflowExecutionId == executionId && version.State == DesignVersionState.Draft)
            .ToListAsync();
        drafts.Should().HaveCount(2, "the artifact carries a design document and a product map");

        foreach (DesignSystemVersion draft in drafts)
        {
            DesignSystemWriteResult approved =
                await designSystemService.ApproveAsync(projectId, draft.Id, userId, CancellationToken.None);
            approved.Status.Should().Be(DesignSystemWriteStatus.Ok);
        }

        // --- Pin ----------------------------------------------------------------
        string? pinJson = await pinService.BuildPinJsonAsync(projectId, CancellationToken.None);
        pinJson.Should().NotBeNull();

        DesignSystemPin pin = DesignSystemPinJson.TryParse(pinJson)!;
        pin.Should().NotBeNull();
        pin.Design!.VersionNumber.Should().Be(1);
        pin.ProductMap!.VersionNumber.Should().Be(1);

        // --- Ambient blocks -----------------------------------------------------
        using ServiceProvider sp = AmbientProvider(engineDb);
        var execution = new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            DesignSystemPinJson = pinJson
        };

        DesignSystemRunState? state = await DesignSystemRunState.LoadAsync(execution, sp, CancellationToken.None);
        state.Should().NotBeNull();

        (string? design, string? product) = state!.BlocksFor(AgentType.RemotionComponentTranslator);
        design.Should().Contain("[DESIGN SYSTEM v1]").And.Contain("#6D28D9");
        product.Should().Contain("[PRODUCT PROFILE v1]");
    }

    // =================================================================
    // 2-3. Require
    // =================================================================

    [Fact]
    public async Task Require_succeeds_when_the_pin_is_present()
    {
        // Present AND loadable: the run's ambient state carries the digest-verified documents.
        ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine.LoadedPinFixture.Loaded loaded =
            await ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine.LoadedPinFixture.LoadAsync();

        using IDisposable ambient = DesignSystemAmbient.Enter(loaded.State);
        StepExecutionResult result = await RequireAsync(loaded.PinJson);

        result.Status.Should().Be(StepStatus.Completed);
        JsonObject view = JsonNode.Parse(result.Output)!.AsObject()["view"]!.AsObject();
        view["design"]!["versionNumber"]!.GetValue<int>().Should().Be(1);
        view["productMap"]!["versionNumber"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Require_fails_PROFILE_MISSING_without_a_pin()
    {
        StepExecutionResult result = await RequireAsync(pin: null);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("PROFILE_MISSING");
        result.FailureClass.Should().Be(FailureClass.UserInput);
    }

    // =================================================================
    // 4-5. Legacy / unpinned
    // =================================================================

    [Fact]
    public void Legacy_quick_win_promo_is_unaffected()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey("quick-win-promo")!;

        template.Steps.Should().HaveCount(11);
        template.AutoCreateOnProject.Should().BeTrue();
        template.Steps.Should().NotContain(step => step.StepType == ReelBolt.Shared.Data.Models.StepType.DesignSystem);
    }

    [Fact]
    public void Decorate_without_state_is_byte_identical()
    {
        DesignSystemAmbient.Decorate("base", AgentType.ScriptwriterAgent).Should().BeSameAs("base");
    }

    // =================================================================
    // Harness
    // =================================================================

    private static DbContextOptions<WorkflowEngineDbContext> EngineOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(connection).Options;

    private static ServiceProvider AmbientProvider(WorkflowEngineDbContext engineDb)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => engineDb);
        services.AddScoped<PinnedDocumentLoader>();
        services.Configure<DesignSystemAmbientOptions>(options =>
        {
            options.Enabled = true;
            options.DesignMaxChars = 2400;
            options.ProductMaxChars = 3200;
        });
        return services.BuildServiceProvider();
    }

    private static Mock<IPlanGateService> Gate()
    {
        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        return gate;
    }

    private static async Task<StepExecutionResult> RequireAsync(string? pin)
    {
        WorkflowStep requireStep = Step(
            3, """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":true}}""");
        var executor = new DesignSystemStepExecutor(
            new FakeStore(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<DesignSystemStepExecutor>.Instance);

        return await executor.ExecuteAsync(Context(requireStep, Guid.NewGuid(), Guid.NewGuid(), pin: pin));
    }

    private static WorkflowStep Step(int order, string configJson) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Guid.NewGuid(),
        AgentDefinitionId = Guid.NewGuid(),
        StepOrder = order,
        StepType = ReelBolt.Shared.Data.Models.StepType.DesignSystem,
        DesignSystemConfigJson = configJson
    };

    private static StepExecutionContext Context(
        WorkflowStep step,
        Guid executionId,
        Guid projectId,
        IReadOnlyList<StepOutputHistoryEntry>? history = null,
        string? pin = null) => new()
        {
            Execution = new WorkflowExecution
            {
                Id = executionId,
                ProjectId = projectId,
                DesignSystemPinJson = pin
            },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history ?? [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "profile-e2e",
            CancellationToken = CancellationToken.None
        };

    private static (Guid ProjectId, Guid ExecutionId) SeedProject(
        InferenceApiDbContext db, FakeStore store, Guid userId)
    {
        db.Organizations.Add(new Organization
        {
            Id = OrgId,
            Name = "Acme",
            Slug = "acme",
            Kind = OrganizationKind.Team,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            OwnerId = userId,
            OrganizationId = OrgId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);

        (string Name, string? OriginalPath, string Content)[] fixtures =
        [
            ("globals.css", "app", Css),
            ("package.json", null, PackageJson),
            ("page.tsx", "app", Page),
            ("route.ts", "app/api/hello", Route)
        ];

        foreach ((string name, string? originalPath, string content) in fixtures)
        {
            ProjectWorkspaceFile file = store.AddFile(project.Id, name, originalPath, content);
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = file.Id,
                ProjectId = project.Id,
                OriginalFileName = file.OriginalFileName,
                OriginalPath = file.OriginalPath,
                Category = file.Category,
                StorageKey = file.StorageKey,
                StorageBucket = store.BucketName,
                MimeType = file.MimeType,
                SizeBytes = file.SizeBytes,
                UploadedAt = file.UploadedAt
            });
        }

        Guid executionId = Guid.NewGuid();
        Guid definitionId = Guid.NewGuid();
        db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definitionId,
            StepOrder = 2,
            StepType = ReelBolt.Shared.Data.Models.StepType.DesignSystem,
            DesignSystemConfigJson = """{"op":"Compose","version":1,"compose":{"parseStepOrder":1}}""",
        });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            ProjectId = project.Id,
            OrganizationId = OrgId,
            InitiatedByUserId = userId,
            Status = ExecutionStatus.Queued
        });

        db.SaveChanges();
        return (project.Id, executionId);
    }

    /// <summary>
    /// One in-memory object store shared by the executor's <see cref="IProjectFileWorkspace"/> and
    /// the ingest service's <see cref="IFileStorageService"/>, so the Parse artifact, the Compose
    /// artifact and the ingest download all read the same bytes.
    /// </summary>
    private sealed class FakeStore : IProjectFileWorkspace, IFileStorageService
    {
        private readonly Dictionary<Guid, ProjectWorkspaceFile> _files = new();

        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public string BucketName => "test-bucket";

        public ProjectWorkspaceFile AddFile(Guid projectId, string fileName, string? originalPath, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            var file = new ProjectWorkspaceFile(
                Guid.NewGuid(),
                projectId,
                fileName,
                originalPath,
                "sourceFiles",
                $"projects/{projectId:D}/sourceFiles/{fileName}",
                "text/plain",
                bytes.Length,
                DateTime.UtcNow,
                null);

            _files[file.Id] = file;
            Objects[file.StorageKey] = bytes;
            return file;
        }

        // --- IProjectFileWorkspace ---------------------------------------------

        public Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProjectWorkspaceFile>>(
                _files.Values.Where(file => file.ProjectId == projectId).ToList());

        public Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct) =>
            Task.FromResult(Encoding.UTF8.GetString(Objects[_files[Guid.Parse(fileReference)].StorageKey]));

        public Task DownloadStorageKeyToFileAsync(
            Guid projectId, string storageKey, string destinationPath, CancellationToken ct)
        {
            File.WriteAllBytes(destinationPath, Objects[storageKey]);
            return Task.CompletedTask;
        }

        /// <summary>The bounded twin (S-67): this store holds the bytes, so its own length answers the cap.</summary>
        public Task DownloadStorageKeyToFileAsync(
            Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct)
        {
            if (Objects.TryGetValue(storageKey, out byte[]? bytes) && bytes.Length > maxBytes)
                throw new StorageObjectTooLargeException(maxBytes, bytes.Length);
            return DownloadStorageKeyToFileAsync(projectId, storageKey, destinationPath, ct);
        }

        public Task<string> UploadArtifactAsync(
            Guid projectId, string localFilePath, string fileName, string contentType,
            CancellationToken ct, string category = "agentFiles")
        {
            string key = $"projects/{projectId:D}/{category}/{fileName}";
            Objects[key] = File.ReadAllBytes(localFilePath);
            return Task.FromResult(key);
        }

        public Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(Objects.ContainsKey(storageKey));

        public Task<ProjectWorkspaceFile> WriteTextFileAsync(
            Guid projectId, string fileName, string content, string contentType, CancellationToken ct,
            string category = "agentFiles", string? originalPath = null) =>
            throw new NotSupportedException();

        public Task<ProjectWorkspaceFile> UploadBinaryFileAsync(
            Guid projectId, string localFilePath, string fileName, string contentType,
            SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
            string category = "outputFiles", string? originalPath = null) =>
            throw new NotSupportedException();

        public Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(
            Guid projectId, string storageKey, string fileName, string contentType, long sizeBytes,
            SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
            string category = "outputFiles") =>
            throw new NotSupportedException();

        // --- IFileStorageService -----------------------------------------------

        public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(Objects.TryGetValue(storageKey, out byte[]? bytes)
                ? new StoredObjectMetadata(bytes.Length, "application/json")
                : null);

        public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(Objects[storageKey]));

        public Task<StoredFileObject> UploadAsync(
            Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
            IReadOnlyDictionary<string, string>? metadata, CancellationToken ct,
            string category = "userFiles", string? originalPath = null) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> DeleteManyAsync(
            Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task MoveAsync(
            Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CopyAsync(
            Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>
    /// The I/O boundary: parses the request path/body, invokes the REAL ingest service directly, and
    /// answers the shape the executor reads (<c>versionIds</c>).
    /// </summary>
    private sealed class FakeIngestHandler(
        IDesignSystemIngestService ingest, Guid projectId, Guid userId) : HttpMessageHandler
    {
        public int Hits { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hits++;

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            JsonObject json = JsonNode.Parse(body)!.AsObject();
            Guid executionId = Guid.Parse(json["executionId"]!.GetValue<string>());
            int stepOrder = json["stepOrder"]!.GetValue<int>();

            DesignSystemIngestResult result = await ingest.IngestAsync(
                projectId, userId, executionId, stepOrder, cancellationToken);

            var versionIds = new JsonArray();
            foreach (Guid id in result.VersionIds ?? [])
                versionIds.Add(JsonValue.Create(id.ToString("D")));

            var responseBody = new JsonObject { ["versionIds"] = versionIds };
            return new HttpResponseMessage(
                result.Status == DesignSystemIngestStatus.Ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = new StringContent(responseBody.ToJsonString(WebOptions), Encoding.UTF8, "application/json")
            };
        }
    }
}
