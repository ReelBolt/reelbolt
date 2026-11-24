using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-010: <see cref="VideoSourceKind.StepResult"/> — a VideoAnalyze source that names a step result of
/// an EARLIER run in the same project. Covers the config shape, the save-time refusal and the cache key.
/// </summary>
public sealed class VideoSourceStepResultTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Guid ResultA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ResultB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    [Fact]
    public void StepResult_source_round_trips_as_kind_and_stepResultId()
    {
        VideoSourceRef source = JsonSerializer.Deserialize<VideoSourceRef>(
            $$"""{"kind":"StepResult","stepResultId":"{{ResultA}}"}""", Json)!;

        source.Kind.Should().Be(VideoSourceKind.StepResult);
        source.StepResultId.Should().Be(ResultA);
        JsonSerializer.Serialize(source, Json).Should().Contain($"\"stepResultId\":\"{ResultA}\"");
    }

    [Fact]
    public void Existing_source_payloads_still_deserialize_with_no_StepResultId()
    {
        VideoSourceRef source = JsonSerializer.Deserialize<VideoSourceRef>(
            """{"kind":"StepOutput","stepOrder":2,"inSec":1.5}""", Json)!;

        source.StepResultId.Should().BeNull();
        source.Validate().Should().BeNull();
    }

    [Fact]
    public void Validate_requires_StepResultId_for_a_StepResult_source()
    {
        new VideoSourceRef(VideoSourceKind.StepResult).Validate().Should().Contain("StepResultId");
        new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: Guid.Empty).Validate().Should().Contain("StepResultId");
    }

    [Fact]
    public void Validate_refuses_a_StepResult_source_that_also_names_another_identity()
    {
        new VideoSourceRef(VideoSourceKind.StepResult, ProjectFileId: Guid.NewGuid(), StepResultId: ResultA)
            .Validate().Should().NotBeNull();
        new VideoSourceRef(VideoSourceKind.StepResult, StepOrder: 2, StepResultId: ResultA)
            .Validate().Should().NotBeNull();
    }

    [Fact]
    public void Validate_accepts_a_StepResult_source_with_a_trim_window()
    {
        new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: ResultA, InSec: 1, OutSec: 4)
            .Validate().Should().BeNull();
    }

    [Fact]
    public void Validate_refuses_a_StepResultId_on_any_other_kind()
    {
        new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Guid.NewGuid(), StepResultId: ResultA)
            .Validate().Should().NotBeNull();
    }

    // ---- save-time validation ----

    private static CreateWorkflowStepRequest AnalyzeStep(string configJson) =>
        JsonSerializer.Deserialize<CreateWorkflowStepRequest>(new JsonObject
        {
            ["agentDefinitionId"] = Guid.NewGuid().ToString(),
            ["stepOrder"] = 1,
            ["stepType"] = "VideoAnalyze",
            ["videoAnalyzeConfigJson"] = configJson
        }.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string SingleSourceConfig(string sourceJson) => $$"""{"version":1,"source":{{sourceJson}}}""";

    private static string ListSourceConfig(params Guid[] ids) =>
        $$"""{"version":1,"source":{"kind":"PreviousStepOutput"},"sources":[{{string.Join(",", ids.Select(i => $$"""{"kind":"StepResult","stepResultId":"{{i}}"}"""))}}]}""";

    [Fact]
    public void Save_validator_refuses_a_StepResult_source_without_an_id()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(
            AnalyzeStep(SingleSourceConfig("""{"kind":"StepResult"}""")));

        errors.Should().ContainSingle().Which.Should().Contain("Step 1 (VideoAnalyze)").And.Contain("StepResultId");
    }

    [Fact]
    public void Save_validator_leaves_existing_source_kinds_untouched()
    {
        StepConfigSaveValidator.ValidateStep(AnalyzeStep(SingleSourceConfig("""{"kind":"PreviousStepOutput"}""")))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Save_validator_refuses_a_step_result_outside_the_workflows_project_with_the_same_wording_as_a_missing_one()
    {
        Guid foreign = ResultA;
        Guid missing = ResultB;
        // Neither id is in the project: a foreign row and an absent row are indistinguishable.
        IReadOnlyList<string> errors = await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
            [AnalyzeStep(ListSourceConfig(foreign, missing))],
            (_, _) => Task.FromResult(false),
            CancellationToken.None);

        errors.Should().HaveCount(2);
        errors[0].Replace(foreign.ToString(), "<id>").Should().Be(errors[1].Replace(missing.ToString(), "<id>"));
        errors[0].Should().Contain("Step 1 (VideoAnalyze)").And.Contain("not found in this project");
    }

    [Fact]
    public async Task Save_validator_accepts_StepResult_sources_in_Source_and_in_Sources_when_the_project_owns_them()
    {
        List<Guid> asked = [];
        IReadOnlyList<string> errors = await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
            [
                AnalyzeStep(SingleSourceConfig($$"""{"kind":"StepResult","stepResultId":"{{ResultA}}"}""")),
                AnalyzeStep(ListSourceConfig(ResultB))
            ],
            (id, _) => { asked.Add(id); return Task.FromResult(true); },
            CancellationToken.None);

        errors.Should().BeEmpty();
        asked.Should().BeEquivalentTo(new[] { ResultA, ResultB });
    }

    [Fact]
    public async Task Save_validator_never_looks_up_when_no_source_is_a_StepResult()
    {
        int lookups = 0;
        IReadOnlyList<string> errors = await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
            [AnalyzeStep(SingleSourceConfig("""{"kind":"PreviousStepOutput"}"""))],
            (_, _) => { lookups++; return Task.FromResult(false); },
            CancellationToken.None);

        errors.Should().BeEmpty();
        lookups.Should().Be(0);
    }

    // ---- step cache ----

    [Fact]
    public void Cache_key_differs_for_two_different_step_results()
    {
        StepCacheKeyBuilder builder = new();
        StepCacheKeyInputs Inputs(Guid id) => new()
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.VideoAnalyze,
            AgentType = AgentType.VideoStoryEditor,
            AgentDefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            MaxIterations = 1,
            VideoAnalyzeConfigJson = SingleSourceConfig($$"""{"kind":"StepResult","stepResultId":"{{id}}"}"""),
            ResolvedInput = "",
            ProjectFileFingerprint = "fp",
            WorkflowDefinitionId = Guid.Parse("44444444-4444-4444-4444-444444444444")
        };

        builder.Build(Inputs(ResultA)).Should().NotBe(builder.Build(Inputs(ResultB)));
        builder.Build(Inputs(ResultA)).Should().Be(builder.Build(Inputs(ResultA)));
    }
}
