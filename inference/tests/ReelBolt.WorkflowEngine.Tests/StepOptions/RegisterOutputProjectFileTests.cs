using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.StepOptions;

/// <summary>
/// Step option <c>registerOutputAsProjectFile</c>, run by the REAL <see cref="WorkflowExecutorService"/>:
/// a passed step's delivered output is registered in place as an <c>outputFiles</c> project file — one
/// row per storage key however often the step runs, every delivered clip for VideoGenerate, and a
/// registration failure never fails the step.
/// </summary>
public sealed class RegisterOutputProjectFileTests : IDisposable
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly WorkflowEngineDbContext _db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IProjectFileWorkspace> _workspace = new();
    private readonly List<(string Key, string Name, string Mime, long Size, string Category)> _registered = [];
    private readonly Dictionary<string, string> _objects = new(StringComparer.Ordinal);
    private Exception? _registerFailure;
    private readonly Guid _organizationId = Guid.NewGuid();
    private OrganizationEntitlementSet? _entitlements;
    private readonly CapturingLogger<WorkflowExecutorService> _log = new();

    /// <summary>Records every formatted log line, so a "registers nothing" path can be shown not to be silent.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoopScope();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    public RegisterOutputProjectFileTests()
    {
        _workspace
            .Setup(w => w.RegisterStoredObjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns<Guid, string, string, string, long, SummaryStatus, FileIndexingStatus, CancellationToken, string>(
                async (projectId, key, name, mime, size, _, _, ct, category) =>
                {
                    if (_registerFailure is not null)
                        throw _registerFailure;
                    _registered.Add((key, name, mime, size, category));
                    ProjectFile row = new() { Id = Guid.NewGuid(), ProjectId = projectId, StorageKey = key, OriginalFileName = name, Category = category, MimeType = mime, SizeBytes = size };
                    _db.ProjectFiles.Add(row);
                    await _db.SaveChangesAsync(ct);
                    return new ProjectWorkspaceFile(row.Id, projectId, name, name, category, key, mime, size, DateTime.UtcNow, null);
                });
        _workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, path, _) =>
                _objects.TryGetValue(key, out string? body) ? File.WriteAllTextAsync(path, body) : throw new FileNotFoundException(key));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_passed_step_with_the_flag_registers_its_output_once_with_a_run_named_file()
    {
        string key = OutputKey("render.mp4");

        WorkflowStepResult row = await RunAsync(StepType.Agent, register: true, key);

        row.Status.Should().Be(StepStatus.Completed);
        _registered.Should().ContainSingle();
        (string Key, string Name, string Mime, long Size, string Category) file = _registered[0];
        file.Key.Should().Be(key);
        file.Name.Should().Be("Launch film - Author.mp4");
        file.Mime.Should().Be("video/mp4");
        file.Size.Should().Be(1234, "the size is read from the stored object");
        file.Category.Should().Be("outputFiles");
    }

    [Fact]
    public async Task Without_the_flag_nothing_is_registered()
    {
        await RunAsync(StepType.Agent, register: false, OutputKey("render.mp4"));

        _registered.Should().BeEmpty();
    }

    [Fact]
    public async Task Running_again_with_the_same_output_key_does_not_add_a_second_row()
    {
        string key = OutputKey("render.mp4");

        await RunAsync(StepType.Agent, register: true, key);
        await RunAsync(StepType.Agent, register: true, key); // a retried run / a cache hit serves the same key

        _registered.Should().ContainSingle();
        (await _db.ProjectFiles.CountAsync(f => f.StorageKey == key)).Should().Be(1);
    }

    [Fact]
    public async Task An_output_the_step_already_registered_itself_is_not_registered_twice()
    {
        string key = OutputKey("compiled.mp4");
        _db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = _projectId, StorageKey = key, OriginalFileName = "x.mp4", Category = "outputFiles" });
        await _db.SaveChangesAsync();

        await RunAsync(StepType.VideoCompile, register: true, key); // VideoCompile's own RegisterProjectFile did it

        _registered.Should().BeEmpty();
    }

    [Fact]
    public async Task A_registration_failure_never_fails_the_step()
    {
        _registerFailure = new InvalidOperationException("database is down");

        WorkflowStepResult row = await RunAsync(StepType.Agent, register: true, OutputKey("render.mp4"));

        row.Status.Should().Be(StepStatus.Completed);
        (await _db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Passed);
    }

    [Fact]
    public async Task A_key_outside_this_projects_outputs_is_never_registered()
    {
        await RunAsync(StepType.Agent, register: true, $"projects/{Guid.NewGuid()}/outputFiles/theirs.mp4");

        _registered.Should().BeEmpty();
    }

    [Fact]
    public async Task VideoGenerate_registers_every_delivered_clip_from_its_manifest_with_the_real_generated_keys()
    {
        (string clip1, string clip2, string manifestKey) = SeedGeneratedManifest();

        WorkflowStepResult row = await RunAsync(StepType.VideoGenerate, register: true, clip1, manifestKey, label: "Shots");

        row.Status.Should().Be(StepStatus.Completed);
        _registered.Select(r => r.Key).Should().BeEquivalentTo([clip1, clip2]);
        _registered.Select(r => r.Name).Should().OnlyHaveUniqueItems().And.AllSatisfy(n => n.Should().StartWith("Launch film - Shots"));
        // Register-in-place keeps the category the object's key already says: agentFiles/generated/ → agentFiles.
        _registered.Should().AllSatisfy(r => r.Category.Should().Be("agentFiles"));
    }

    [Fact]
    public async Task A_generated_clip_key_of_another_project_in_the_manifest_is_never_registered()
    {
        (string clip1, _, string manifestKey) = SeedGeneratedManifest(
            secondClip: $"projects/{Guid.NewGuid()}/agentFiles/generated/{Guid.NewGuid():D}/step-1-v2.mp4");

        await RunAsync(StepType.VideoGenerate, register: true, clip1, manifestKey, label: "Shots");

        _registered.Select(r => r.Key).Should().BeEquivalentTo([clip1]);
    }

    [Fact]
    public async Task Over_quota_the_outputs_are_still_registered_the_step_passes_and_the_output_says_overQuota()
    {
        // CLAUDE.md: workflow outputs are never blocked mid-run. The ruling (QA-P0-6): register anyway, flag it.
        _entitlements = new OrganizationEntitlementSet { Tier = "creator", StorageGb = 1 };
        _db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            StorageKey = $"projects/{_projectId}/inputFiles/big.mov",
            OriginalFileName = "big.mov",
            Category = "inputFiles",
            SizeBytes = 1024L * 1024L * 1024L
        });
        await _db.SaveChangesAsync();
        (string clip1, string clip2, string manifestKey) = SeedGeneratedManifest();

        WorkflowStepResult row = await RunAsync(StepType.VideoGenerate, register: true, clip1, manifestKey, label: "Shots");

        row.Status.Should().Be(StepStatus.Completed);
        (await _db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Passed);
        _registered.Select(r => r.Key).Should().BeEquivalentTo([clip1, clip2]);
        Registration(row).Should().NotBeNull();
        Registration(row)!["registered"]!.GetValue<int>().Should().Be(2);
        Registration(row)!["overQuota"]!.GetValue<bool>().Should().BeTrue();
        row.OutputJson.Should().NotContain("projects/", "the registration note carries no storage key");
    }

    [Fact]
    public async Task Under_quota_the_output_records_the_registration_without_overQuota()
    {
        _entitlements = new OrganizationEntitlementSet { Tier = "creator", StorageGb = 15 };

        WorkflowStepResult row = await RunAsync(StepType.Agent, register: true, OutputKey("render.mp4"));

        Registration(row)!["registered"]!.GetValue<int>().Should().Be(1);
        Registration(row)!["overQuota"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_registered_leaves_the_step_output_untouched()
    {
        WorkflowStepResult row = await RunAsync(StepType.Agent, register: true, $"projects/{Guid.NewGuid()}/outputFiles/theirs.mp4");

        Registration(row).Should().BeNull();
    }

    [Fact]
    public async Task Nothing_addressable_registers_nothing_and_is_logged_rather_than_silent()
    {
        // QA-P0-20: the early return for "no addressable output" was silent — no log, no flag — so a run that
        // registered nothing was indistinguishable from one that registered fine.
        WorkflowStepResult row = await RunAsync(StepType.Agent, register: true, $"projects/{Guid.NewGuid()}/outputFiles/theirs.mp4");

        _registered.Should().BeEmpty();
        Registration(row).Should().BeNull();
        _log.Messages.Should().Contain(m => m.Contains("no addressable output"),
            "a run that registers nothing must say so; logged: " + string.Join(" | ", _log.Messages));
    }

    // ------------------------------------------------------------------------------------------------

    private string OutputKey(string name) => $"projects/{_projectId}/outputFiles/{Guid.NewGuid():D}/{name}";

    /// <summary>
    /// The REAL VideoGenerate shapes (VideoGenerateStepExecutor): takes at
    /// <c>projects/{p}/agentFiles/generated/{exec}/step-{n}-v{k}.mp4</c>, the manifest at
    /// <c>projects/{p}/agentFiles/video-analysis/{exec}/step-{n}-generated-clips.json</c>.
    /// </summary>
    private (string Clip1, string Clip2, string ManifestKey) SeedGeneratedManifest(string? secondClip = null)
    {
        Guid exec = Guid.NewGuid();
        string clip1 = $"projects/{_projectId}/agentFiles/generated/{exec:D}/step-1-v1.mp4";
        string clip2 = secondClip ?? $"projects/{_projectId}/agentFiles/generated/{exec:D}/step-1-v2.mp4";
        string manifestKey = $"projects/{_projectId}/agentFiles/video-analysis/{exec:D}/step-1-generated-clips.json";
        _objects[manifestKey] = $$"""
            {"clips":[
              {"clipId":"v1","status":"succeeded","storageKey":"{{clip1}}"},
              {"clipId":"v2","status":"succeeded","storageKey":"{{clip2}}"},
              {"clipId":"v3","status":"failed","storageKey":null}
            ]}
            """;
        return (clip1, clip2, manifestKey);
    }

    private static JsonObject? Registration(WorkflowStepResult row) =>
        row.OutputJson is { } json && JsonNode.Parse(json) is JsonObject output ? output["registration"] as JsonObject : null;

    private sealed class FixedResultExecutor(StepType stepType, string outputKey, string? artifactKey) : IStepExecutor
    {
        public StepType StepType { get; } = stepType;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = "{\"ok\":true}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed,
                OutputStorageKey = outputKey,
                ArtifactStorageKey = artifactKey
            });
    }

    private sealed class Scopes(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(provider);

        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }
    }

    private async Task<WorkflowStepResult> RunAsync(
        StepType stepType, bool register, string outputKey, string? artifactKey = null, string label = "Author")
    {
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = stepType,
            Label = label,
            AgentDefinitionId = agent.Id,
            AgentDefinition = agent,
            RegisterOutputAsProjectFile = register
        };
        WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "Launch film", ProjectId = _projectId, Steps = [step] };
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ProjectId = _projectId,
            OrganizationId = _organizationId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };
        // Only one execution is ever asserted on as "the" execution; earlier ones are cleared.
        _db.WorkflowExecutions.RemoveRange(_db.WorkflowExecutions);
        if (!await _db.Projects.AnyAsync(p => p.Id == _projectId))
            _db.Projects.Add(new Project { Id = _projectId, OrganizationId = _organizationId, Name = "Launch" });
        _db.AgentDefinitions.Add(agent);
        _db.WorkflowDefinitions.Add(definition);
        _db.WorkflowExecutions.Add(execution);
        await _db.SaveChangesAsync();

        Mock<IAmazonS3> s3 = new();
        s3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 1234, HttpStatusCode = HttpStatusCode.OK });

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(_db);
        services.AddSingleton(_workspace.Object);
        services.AddSingleton(s3.Object);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        if (_entitlements is { } set)
            services.AddSingleton(Mock.Of<IOrganizationEntitlements>(e =>
                e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == ValueTask.FromResult(set)));
        ServiceProvider provider = services.BuildServiceProvider();

        WorkflowExecutorService service = new(
            scopeFactory: new Scopes(provider),
            eventPublisher: Mock.Of<IWorkflowEventPublisher>(),
            logger: _log,
            executors: [new FixedResultExecutor(stepType, outputKey, artifactKey)],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(execution.Id, "corr", CancellationToken.None);

        return await _db.WorkflowStepResults.SingleAsync(r => r.WorkflowExecutionId == execution.Id);
    }
}
