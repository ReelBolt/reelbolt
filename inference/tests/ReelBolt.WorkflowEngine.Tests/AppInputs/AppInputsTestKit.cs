using System;
using System.Collections.Generic;
using System.Text.Json;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>Small builders shared by the app-input contract tests (docs/design/app-inputs.md, lane L1).</summary>
internal static class AppInputsTestKit
{
    public static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public static Dictionary<string, JsonElement> Values(params (string Id, string Raw)[] values)
    {
        Dictionary<string, JsonElement> map = new(StringComparer.Ordinal);
        foreach ((string id, string raw) in values)
            map[id] = J(raw);
        return map;
    }

    public static AppManifestInput Input(string id, string type, string label = "L", bool? required = null, string? def = null,
        int? maxLength = null, double? min = null, double? max = null, AppInputOption[]? options = null,
        AppInputShowWhen? showWhen = null, string? capability = null, Guid? agent = null, IReadOnlyList<Guid>? allowed = null,
        bool? more = null, string? choices = null) =>
        new(AppManifestVocabulary.KindInput, Label: label, Default: def is null ? null : J(def), Min: min, Max: max,
            Id: id, Type: type, Required: required, MaxLength: maxLength, Options: options, ShowWhen: showWhen,
            Capability: capability, Agent: agent, Allowed: allowed, More: more,
            Choices: choices is null ? null : JsonSerializer.Deserialize<List<JsonElement>>(choices));

    public static AppInputOption Option(string raw, string label) => new(J(raw), label);

    public static AppManifest Manifest(params AppManifestInput[] inputs) => new(Version: 2, Inputs: inputs);

    public static AppBindingStep Step(int order, StepType type, string? config = null, string? condition = null,
        string? trueBranch = null, string? falseBranch = null, Guid? agent = null, string? name = null) =>
        new(order, type, config, condition, trueBranch, falseBranch, null, agent, name);

    /// <summary>A save request carrying <paramref name="config"/> in the column of <paramref name="type"/>.</summary>
    public static CreateWorkflowStepRequest Request(int order, StepType type, string? config, string? condition = null) => type switch
    {
        StepType.VideoGenerate => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition, VideoGenerateConfigJson: config),
        StepType.VideoCompile => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition, VideoCompileConfigJson: config),
        StepType.VideoAnalyze => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition, VideoAnalyzeConfigJson: config),
        StepType.Voiceover => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition, VoiceoverConfigJson: config),
        StepType.ThumbnailGenerate => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition, ThumbnailGenerateConfigJson: config),
        StepType.ExtractFrame => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition) { ExtractFrameConfigJson = config },
        StepType.AudioGenerate => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition) { AudioGenerateConfigJson = config },
        StepType.AudioCleanup => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition) { AudioCleanupConfigJson = config },
        _ => new(Guid.NewGuid(), order, null, "s", type.ToString(), condition)
    };
}
