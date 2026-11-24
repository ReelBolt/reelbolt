using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-023 (S-17, QA-P0-7 cache part): a VideoAnalyze step whose source is a StepResult keys its cache entry on what
/// that source RESOLVED to (the storage key, or a missing marker), not only on the configured id — so a source
/// that was pruned, deleted or re-pointed since the entry was stored is a miss, never a stale hit.
/// </summary>
public sealed class StepResultSourceCacheKeyTests : IDisposable
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _definitionId = Guid.NewGuid();
    private readonly WorkflowEngineDbContext _db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_unchanged_resolved_source_hits()
    {
        WorkflowStepResult source = SeedSource(GeneratedKey());
        StepResultCache cache = Cache();

        await cache.StoreAsync(Inputs(source.Id), Completed(), CancellationToken.None);

        (await cache.TryGetAsync(Inputs(source.Id), CancellationToken.None)).Hit.Should().BeTrue();
    }

    [Fact]
    public async Task A_source_pruned_after_the_entry_was_stored_misses()
    {
        WorkflowStepResult source = SeedSource(GeneratedKey());
        StepResultCache cache = Cache();
        await cache.StoreAsync(Inputs(source.Id), Completed(), CancellationToken.None);

        source.OutputStorageKey = null; // retention pruned the take
        await _db.SaveChangesAsync();

        (await cache.TryGetAsync(Inputs(source.Id), CancellationToken.None)).Hit.Should().BeFalse();
    }

    [Fact]
    public async Task A_source_row_deleted_after_the_entry_was_stored_misses()
    {
        WorkflowStepResult source = SeedSource(GeneratedKey());
        StepResultCache cache = Cache();
        await cache.StoreAsync(Inputs(source.Id), Completed(), CancellationToken.None);

        _db.WorkflowStepResults.Remove(source);
        await _db.SaveChangesAsync();

        (await cache.TryGetAsync(Inputs(source.Id), CancellationToken.None)).Hit.Should().BeFalse();
    }

    [Fact]
    public async Task A_source_whose_output_key_changed_misses()
    {
        WorkflowStepResult source = SeedSource(GeneratedKey(take: 1));
        StepResultCache cache = Cache();
        await cache.StoreAsync(Inputs(source.Id), Completed(), CancellationToken.None);

        source.OutputStorageKey = GeneratedKey(take: 2);
        await _db.SaveChangesAsync();

        (await cache.TryGetAsync(Inputs(source.Id), CancellationToken.None)).Hit.Should().BeFalse();
    }

    /// <summary>S-17 (round 2): every spelling of the StepResult kind the engine binds (numeric, numeric string, padded,
    /// any case) fingerprints its source, so pruning the source after the store is a miss, never a stale hit.</summary>
    [Theory]
    [InlineData("4")]
    [InlineData("\"4\"")]
    [InlineData("\" StepResult \"")]
    [InlineData("\"stepresult\"")]
    [InlineData("\"STEPRESULT\"")]
    public async Task Every_kind_spelling_the_engine_binds_fingerprints_the_source(string kindJson)
    {
        WorkflowStepResult source = SeedSource(GeneratedKey());
        string config = $$$"""{"version":1,"Source":{"Kind":{{{kindJson}}},"StepResultId":"{{{source.Id}}}"}}""";
        // The case is real: the engine's own config options read this source as a StepResult.
        var engineOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        JsonDocument.Parse(config).RootElement.GetProperty("Source").Deserialize<VideoSourceRef>(engineOptions)!
            .Kind.Should().Be(VideoSourceKind.StepResult);
        StepResultCache cache = Cache();
        StepCacheKeyInputs inputs = Inputs(source.Id) with { VideoAnalyzeConfigJson = config };
        await cache.StoreAsync(inputs, Completed(), CancellationToken.None);

        source.OutputStorageKey = null; // retention pruned the take
        await _db.SaveChangesAsync();

        (await cache.TryGetAsync(inputs, CancellationToken.None)).Hit.Should().BeFalse();
    }

    /// <summary>S-64 / QA-P1-4: a compile's insert content named by a StepResult reference (the fallback and each
    /// per-plate entry, in any spelling the engine binds) keys on what it resolves to, a still included.</summary>
    [Theory]
    [InlineData("""{"version":1,"decision":{"source":"Previous"},"insertContentRef":{"from":"StepResult","stepResultId":"ID"}}""", "mp4")]
    [InlineData("""{"version":1,"decision":{"source":"Previous"},"insertContentRef":{"From":" stepresult ","StepResultId":"ID"}}""", "png")]
    [InlineData("""{"version":1,"decision":{"source":"Previous"},"insertContents":[{"regionId":"r0","insertContentRef":{"from":"StepResult","stepResultId":"ID"}}]}""", "png")]
    public async Task A_compile_insert_reference_to_a_step_result_pruned_after_the_store_misses(string configTemplate, string extension)
    {
        WorkflowStepResult source = SeedSource($"projects/{_projectId}/agentFiles/generated/{Guid.NewGuid():D}/step-2-v1.{extension}");
        StepCacheKeyInputs inputs = Inputs(source.Id) with
        {
            StepType = StepType.VideoCompile,
            VideoAnalyzeConfigJson = null,
            VideoCompileConfigJson = configTemplate.Replace("ID", source.Id.ToString("D"), StringComparison.Ordinal)
        };
        StepResultCache cache = Cache();
        await cache.StoreAsync(inputs, Completed(), CancellationToken.None);
        (await cache.TryGetAsync(inputs, CancellationToken.None)).Hit.Should().BeTrue();

        source.OutputStorageKey = null; // retention pruned the take
        await _db.SaveChangesAsync();

        (await cache.TryGetAsync(inputs, CancellationToken.None)).Hit.Should().BeFalse();
    }

    /// <summary>S-64 / QA-P1-4: an ExtractFrame step whose source is a StepResult keys on what it resolves to.</summary>
    [Fact]
    public async Task An_extract_frame_step_result_source_re_pointed_after_the_store_misses()
    {
        WorkflowStepResult source = SeedSource(GeneratedKey(take: 1));
        StepCacheKeyInputs inputs = Inputs(source.Id) with
        {
            StepType = StepType.ExtractFrame,
            VideoAnalyzeConfigJson = null,
            ExtractFrameConfigJson = $$$"""{"source":{"kind":"StepResult","stepResultId":"{{{source.Id}}}"},"at":"Last"}"""
        };
        StepResultCache cache = Cache();
        await cache.StoreAsync(inputs, Completed(), CancellationToken.None);
        (await cache.TryGetAsync(inputs, CancellationToken.None)).Hit.Should().BeTrue();

        source.OutputStorageKey = GeneratedKey(take: 2);
        await _db.SaveChangesAsync();

        (await cache.TryGetAsync(inputs, CancellationToken.None)).Hit.Should().BeFalse();
    }

    [Fact]
    public void The_resolved_sources_component_moves_the_key_and_absent_keeps_the_old_framing()
    {
        StepCacheKeyBuilder builder = new();
        StepCacheKeyInputs inputs = Inputs(Guid.NewGuid());

        builder.Build(inputs with { StepResultSources = "a" }).Should().NotBe(builder.Build(inputs with { StepResultSources = "b" }));
        builder.Build(inputs with { StepResultSources = "a" }).Should().NotBe(builder.Build(inputs));
    }

    // ---- harness ----

    private string GeneratedKey(int take = 1) =>
        $"projects/{_projectId}/agentFiles/generated/{Guid.NewGuid():D}/step-2-v{take}.mp4";

    private WorkflowStepResult SeedSource(string? key)
    {
        WorkflowExecution run = new() { Id = Guid.NewGuid(), ProjectId = _projectId };
        WorkflowStepResult row = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowExecution = run,
            OutputStorageKey = key,
            Status = StepStatus.Completed
        };
        _db.WorkflowExecutions.Add(run);
        _db.WorkflowStepResults.Add(row);
        _db.SaveChanges();
        return row;
    }

    private StepResultCache Cache() => new(
        _db, new StepCacheKeyBuilder(), new StepCachePolicy(),
        Options.Create(new StepCacheOptions()), NullLogger<StepResultCache>.Instance);

    private StepCacheKeyInputs Inputs(Guid stepResultId) => new()
    {
        ProjectId = _projectId,
        StepType = StepType.VideoAnalyze,
        AgentType = AgentType.VideoStoryEditor,
        AgentDefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        MaxIterations = 1,
        VideoAnalyzeConfigJson = $$$"""{"version":1,"source":{"kind":"StepResult","stepResultId":"{{{stepResultId}}}"}}""",
        ResolvedInput = "",
        ProjectFileFingerprint = "fp",
        WorkflowDefinitionId = _definitionId
    };

    private static StepExecutionResult Completed() => new()
    {
        Output = "{\"analysis\":\"ok\"}",
        NextStepIndex = 1,
        Status = StepStatus.Completed
    };
}
