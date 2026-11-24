using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>The outcome of a snapshot attempt: a recipe (with its canonical JSON) or the reasons it cannot be published.</summary>
public sealed record PortableRecipeResult(PortableRecipe? Recipe, IReadOnlyList<PortableRefusal> Refusals, string? CanonicalJson = null);

/// <summary>
/// Turns a workflow into a <see cref="PortableRecipe"/>: refuses what a public recipe may not contain, strips or slots every author-owned id,
/// sanitises every text, and runs <see cref="SnapshotLeakGuard"/> over the result. Fails closed throughout.
/// </summary>
public static partial class PortableRecipeBuilder
{
    public const int MaxSteps = 40;
    public const int MaxSnapshotBytes = 256 * 1024;
    public const int MaxLoopIterations = 10;
    public const int MinPassMark = 1;
    public const int MaxPassMark = 10;
    public const int MaxExpressionChars = 2000;

    [GeneratedRegex(@"^[A-Za-z0-9_ $.()<>=!&|+\-*/%,'"":?\[\]{}@#]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ExpressionChars();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidToken();

    [GeneratedRegex(@"[0-9a-fA-F]{32,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexRun();

    [GeneratedRegex(@"^[0-9]{1,4}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StepOrderText();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafeKey();

    [GeneratedRegex(@"^[A-Za-z0-9 _.:@/#%+\-$*\[\](),={}!]{0,200}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeepString();

    public static PortableRecipeResult Build(
        WorkflowDefinition workflow,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyDictionary<Guid, AgentDefinition> agentsById,
        AppManifest? manifest)
    {
        if (workflow.Kind != WorkflowDefinitionKind.Standard)
        {
            return Refused(new PortableRefusal(
                "HIDDEN_WORKFLOW_KIND", null, "This workflow is a system workflow of a project and cannot be shared."));
        }

        if (steps.Count == 0)
            return Refused(new PortableRefusal("STEP_TYPE_NOT_SHAREABLE", null, "The workflow has no steps to share."));
        if (steps.Count > MaxSteps)
            return Refused(new PortableRefusal("TOO_LARGE", null, $"A shared recipe can have at most {MaxSteps} steps; this one has {steps.Count}."));

        WalkResult walk = new();
        List<PortableStep> portable = [];
        foreach (WorkflowStep step in steps.OrderBy(s => s.StepOrder))
        {
            PortableStep? built;
            try
            {
                built = BuildStep(step, agentsById, walk);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException or NotSupportedException)
            {
                walk.Refusals.Add(new PortableRefusal(
                    MarketplaceStepPublishability.StepTypeNotShareable, step.StepOrder, $"Step {step.StepOrder}: its settings could not be read."));
                built = null;
            }

            if (built is not null)
                portable.Add(built);
        }

        AppManifest? cleanManifest = manifest is null ? null : SanitizeManifest(manifest, walk);
        if (walk.Refusals.Count > 0)
            return Refused([.. walk.Refusals]);

        PortableRecipe recipe = new(PortableRecipe.CurrentSchemaVersion, portable, cleanManifest, walk.Slots, walk.Removed);
        string json = recipe.ToCanonicalJson();
        if (Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes)
        {
            return Refused(new PortableRefusal(
                "TOO_LARGE", null, $"The recipe is larger than {MaxSnapshotBytes / 1024} KB once its settings are written out."));
        }

        IReadOnlyList<PortableRefusal> leaks = SnapshotLeakGuard.Scan(json);
        return leaks.Count > 0 ? Refused([.. leaks]) : new PortableRecipeResult(recipe, [], json);
    }

    private static PortableRecipeResult Refused(params PortableRefusal[] refusals) =>
        new(null, [.. refusals.DistinctBy(r => (r.Code, r.StepOrder, r.Message))]);

    private static PortableStep? BuildStep(WorkflowStep step, IReadOnlyDictionary<Guid, AgentDefinition> agentsById, WalkResult walk)
    {
        int order = step.StepOrder;
        if (MarketplaceStepPublishability.For(step.StepType) is StepPublishability.NotShareable refusal)
        {
            walk.Refusals.Add(new PortableRefusal(refusal.ReasonCode, order, $"Step {order}: {refusal.Message}"));
            return null;
        }

        bool ok = true;
        if (!agentsById.TryGetValue(step.AgentDefinitionId, out AgentDefinition? agent) || !agent.IsBuiltIn)
        {
            walk.Refusals.Add(CustomAgent(order));
            ok = false;
        }

        foreach (StepAgentReference reference in StepAgentReferences.Extract(step))
        {
            if (!agentsById.TryGetValue(reference.AgentDefinitionId, out AgentDefinition? other) || !other.IsBuiltIn)
            {
                walk.Refusals.Add(CustomAgent(order));
                ok = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(step.ContinueCompositionJson))
        {
            walk.Refusals.Add(new PortableRefusal("RUN_BOUND_SOURCE", order, ConfigWalker.RunBoundMessage));
            ok = false;
        }

        if (step.MaxIterations is < 1 or > MaxLoopIterations || step.MinScore is < MinPassMark or > MaxPassMark)
        {
            walk.Refusals.Add(new PortableRefusal(
                "LOOP_BOUNDS", order,
                $"Step {order}: a review loop can run at most {MaxLoopIterations} rounds with a pass mark from {MinPassMark} to {MaxPassMark}."));
            ok = false;
        }

        List<AgentType>? parallelTypes = null;
        if (step.StepType == StepType.Parallel)
        {
            if (!string.IsNullOrWhiteSpace(step.ParallelAgentIdsJson) && !IsGuidArray(step.ParallelAgentIdsJson))
            {
                walk.Refusals.Add(new PortableRefusal(
                    MarketplaceStepPublishability.StepTypeNotShareable, order, $"Step {order}: its list of agents could not be read."));
                ok = false;
            }

            parallelTypes = [];
            foreach (StepAgentReference reference in StepAgentReferences.Extract(step))
            {
                if (agentsById.TryGetValue(reference.AgentDefinitionId, out AgentDefinition? member) && member.IsBuiltIn)
                    parallelTypes.Add(member.AgentType);
            }
        }

        JsonNode? config = null;
        if (MarketplaceStepPublishability.ConfigTypeOf(step.StepType) is { } configType)
        {
            string? configJson = StepConfigColumns.Get(step);
            if (!string.IsNullOrWhiteSpace(configJson))
            {
                JsonNode? parsed = TryParse(configJson);
                if (parsed is null)
                {
                    walk.Refusals.Add(new PortableRefusal(
                        MarketplaceStepPublishability.StepTypeNotShareable, order, $"Step {order}: its settings could not be read."));
                    ok = false;
                }
                else
                {
                    config = ConfigWalker.Walk(parsed, configType, order, walk);
                }
            }
        }

        if (!ok)
            return null;

        if (!string.IsNullOrWhiteSpace(step.EdgeConditionJson))
            walk.Removed.Add(new PortableRemoval(order, "edgeCondition", "A legacy edge condition (not shared)"));

        JsonNode? mapping = null;
        if (!string.IsNullOrWhiteSpace(step.InputMappingJson))
        {
            JsonNode? parsedMapping = TryParse(step.InputMappingJson);
            mapping = parsedMapping is null ? null : CleanMapping(parsedMapping, "inputMapping", order, walk);
            if (parsedMapping is null)
                walk.Removed.Add(new PortableRemoval(order, "inputMapping", "An input mapping that could not be read (not shared)"));
        }

        return new PortableStep(
            order,
            step.StepType,
            agent?.AgentType,
            TextField(step.Label, "label", order, walk),
            config,
            ExpressionField(step.ConditionExpression, "conditionExpression", order, walk),
            ExpressionField(step.LoopSourceExpression, "loopSourceExpression", order, walk),
            step.LoopTargetStepOrder,
            step.MaxIterations,
            step.MinScore,
            mapping,
            BranchOrder(step.TrueBranchStepOrder, "trueBranchStepOrder", order, walk),
            BranchOrder(step.FalseBranchStepOrder, "falseBranchStepOrder", order, walk),
            parallelTypes,
            step.AgentInputContextMode,
            SelectedOrders(step.SelectedPriorStepOrdersJson),
            step.CacheMode,
            step.RegisterOutputAsProjectFile);
    }

    private static PortableRefusal CustomAgent(int order) => new(
        "CUSTOM_AGENT", order, $"Step {order} uses an agent you made yourself. Only the built-in agents can be shared.");

    private static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TextField(string? text, string field, int order, WalkResult walk)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string clean = MarketplaceText.Sanitize(text);
        if (clean.Length > MarketplaceText.MaxFieldChars)
        {
            walk.Refusals.Add(new PortableRefusal(
                "TOO_LARGE", order, $"Step {order}: a text is longer than {MarketplaceText.MaxFieldChars} characters."));
            clean = clean[..MarketplaceText.MaxFieldChars];
        }

        return clean.Length == 0 ? null : clean;
    }

    private static bool IsGuidArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A condition or loop expression is code-like, not prose: the prose sanitiser would strip <c>&lt;</c> and <c>&gt;</c> and what lies
    /// between. It is validated instead (length, no control characters, no id-shaped token, only the characters the evaluator's grammar
    /// uses) and stored exactly as written; anything else refuses the publish.
    /// </summary>
    private static string? ExpressionField(string? text, string field, int order, WalkResult walk)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (IsValidExpression(text))
            return text;
        walk.Refusals.Add(new PortableRefusal(
            "EXPRESSION_INVALID", order, $"Step {order}: its {field} cannot be shared because it is not a plain condition."));
        return null;
    }

    internal static bool IsValidExpression(string text)
    {
        if (text.Length > MaxExpressionChars || text.Any(char.IsControl))
            return false;
        try
        {
            return ExpressionChars().IsMatch(text) && !GuidToken().IsMatch(text) && !HexRun().Matches(text).Any(m => m.Value.Any(char.IsDigit));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string? BranchOrder(string? value, string field, int order, WalkResult walk)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string trimmed = value.Trim();
        // A step number, or the token "end" that finishes the run there (docs/design/app-inputs.md 9.4).
        if (StepOrderText().IsMatch(trimmed) || string.Equals(trimmed, StaticBranchResolver.EndToken, StringComparison.Ordinal))
            return trimmed;
        walk.Removed.Add(new PortableRemoval(order, field, "A branch target in an unusual form (not shared)"));
        return null;
    }

    private static IReadOnlyList<int>? SelectedOrders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            int[]? orders = JsonSerializer.Deserialize<int[]>(json);
            return orders is { Length: > 0 } ? orders : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The input mapping is a small JSON document of step references and labels: strings are sanitised, odd keys dropped.</summary>
    private static JsonNode? CleanMapping(JsonNode node, string path, int order, WalkResult walk)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject cleaned = [];
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    if (!SafeKey().IsMatch(kv.Key) || kv.Value is null)
                    {
                        walk.Removed.Add(new PortableRemoval(order, path, "An input mapping entry with an unusual name (not shared)"));
                        continue;
                    }

                    JsonNode? child = CleanMapping(kv.Value, $"{path}.{kv.Key}", order, walk);
                    if (child is not null)
                        cleaned[kv.Key] = child;
                }

                return cleaned;
            case JsonArray arr:
                JsonArray list = [];
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is { } item && CleanMapping(item, $"{path}[{i}]", order, walk) is { } child)
                        list.Add(child);
                }

                return list;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                string raw = value.GetValue<string>();
                if (IsValidExpression(raw))
                    return JsonValue.Create(raw);
                walk.Refusals.Add(new PortableRefusal(
                    "EXPRESSION_INVALID", order, $"Step {order}: its input mapping cannot be shared because a value is not a plain reference."));
                return null;
            case JsonValue value when value.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return value.DeepClone();
            default:
                return null;
        }
    }

    private static bool IsAgentModelPick(AppManifestInput input) =>
        input.Kind == AppManifestVocabulary.KindInput
        && string.Equals(input.Type, "model", StringComparison.Ordinal)
        && string.Equals(input.Capability, "Chat", StringComparison.Ordinal);

    private static AppManifest SanitizeManifest(AppManifest manifest, WalkResult walk)
    {
        // Built field by field (never a `with` copy of the whole record): a new manifest member is dropped until it is handled here.
        AppManifest pre = new(
            manifest.Version,
            manifest.Title is null ? null : MarketplaceText.Sanitize(manifest.Title),
            manifest.Tagline is null ? null : MarketplaceText.Sanitize(manifest.Tagline),
            manifest.Category,
            manifest.SuggestedStylePreset,
            // A "pick the model of an agent" input (a Chat model choice) is bound to an agent of the author's own workspace by
            // id, which is never published; a copy's agents are the built-in ones, so the input would pick nothing. It stays out.
            // So does the permission to send media: the app derives it from the copy's own steps on every read (never stored).
            manifest.Inputs?.Where(i => !IsAgentModelPick(i) && !MediaEgressPermission.IsReserved(i)).Select(i => new AppManifestInput(
                i.Kind, i.Field, i.Role, i.Label is null ? null : MarketplaceText.Sanitize(i.Label), i.Default, i.Choices, i.Min, i.Max,
                i.Id, i.Type, i.Help, i.Example, i.Required, i.MaxLength, i.Step, i.Unit, i.Options, i.More, i.ShowWhen, i.Capability, i.Job,
                null, null, null, i.OnLabel, i.OffLabel, i.LabelKey, i.HelpKey, i.ExampleKey, null)).ToList(),
            manifest.Result,
            manifest.Advanced,
            manifest.RequestTemplate is null ? null : MarketplaceText.Sanitize(manifest.RequestTemplate));
        AppManifest clean = AppManifestValidator.Sanitize(pre);
        return new AppManifest(
            clean.Version,
            Capped(clean.Title, walk),
            Capped(clean.Tagline, walk),
            clean.Category,
            clean.SuggestedStylePreset is { } style && KeepString().IsMatch(style) ? style : null,
            clean.Inputs?.Select(i => new AppManifestInput(
                i.Kind,
                i.Field is { } f && KeepString().IsMatch(f) ? f : null,
                i.Role is { } r && KeepString().IsMatch(r) ? r : null,
                Capped(i.Label, walk),
                SanitizeElement(i.Default),
                i.Choices?.Select(c => SanitizeElement(c)).Where(c => c is not null).Select(c => c!.Value).ToList(),
                i.Min,
                i.Max,
                KeepOrNull(i.Id),
                KeepOrNull(i.Type),
                Capped(i.Help, walk),
                Capped(i.Example, walk),
                i.Required,
                i.MaxLength,
                i.Step,
                KeepOrNull(i.Unit),
                i.Options?.Select(o => new AppInputOption(
                    SanitizeElement(o.Value) ?? JsonSerializer.SerializeToElement(string.Empty), Capped(o.Label, walk), Capped(o.Hint, walk), KeepOrNull(o.LabelKey))).ToList(),
                i.More,
                i.ShowWhen is { } w ? new AppInputShowWhen(KeepOrNull(w.Input) ?? string.Empty, SanitizeElement(w.EqualsValue) ?? JsonSerializer.SerializeToElement(string.Empty)) : null,
                KeepOrNull(i.Capability),
                KeepOrNull(i.Job),
                null, // Allowed: model ids of the author's organisation, never published
                null, // Recommended: same
                null, // Agent: an agent id, never published
                Capped(i.OnLabel, walk),
                Capped(i.OffLabel, walk),
                KeepOrNull(i.LabelKey),
                KeepOrNull(i.HelpKey),
                KeepOrNull(i.ExampleKey),
                null)).ToList(), // Permission: the media permission field is derived from the copy's steps, never published
            clean.Result,
            clean.Advanced,
            Capped(clean.RequestTemplate, walk));
    }

    private static string? KeepOrNull(string? value) => value is not null && KeepString().IsMatch(value) ? value : null;

    private static string? Capped(string? text, WalkResult walk)
    {
        if (text is null)
            return null;
        string clean = MarketplaceText.Sanitize(text);
        if (clean.Length <= MarketplaceText.MaxFieldChars)
            return clean;
        walk.Refusals.Add(new PortableRefusal(
            "TOO_LARGE", null, $"A text in the app description is longer than {MarketplaceText.MaxFieldChars} characters."));
        return clean[..MarketplaceText.MaxFieldChars];
    }

    private static JsonElement? SanitizeElement(JsonElement? element)
    {
        if (element is not { } e)
            return null;
        if (e.ValueKind == JsonValueKind.String)
            return JsonSerializer.SerializeToElement(MarketplaceText.Sanitize(e.GetString()));
        return e.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? e.Clone() : null;
    }
}

/// <summary>Which <see cref="WorkflowStep"/> column carries the config JSON of each step type.</summary>
internal static class StepConfigColumns
{
    public static string? Get(WorkflowStep step) => step.StepType switch
    {
        StepType.Conditional => step.ConditionalConfigJson,
        StepType.ReviewLoop => step.ReviewLoopConfigJson,
        StepType.Extract => step.ExtractConfigJson,
        StepType.VideoAnalyze => step.VideoAnalyzeConfigJson,
        StepType.VideoCompile => step.VideoCompileConfigJson,
        StepType.EditRoom => step.EditRoomConfigJson,
        StepType.GraphicsRoom => step.GraphicsRoomConfigJson,
        StepType.ColorGradeRoom => step.ColorGradeRoomConfigJson,
        StepType.VideoGenerate => step.VideoGenerateConfigJson,
        StepType.Voiceover => step.VoiceoverConfigJson,
        StepType.ThumbnailGenerate => step.ThumbnailGenerateConfigJson,
        StepType.DailiesRoom => step.DailiesRoomConfigJson,
        StepType.DesignSystem => step.DesignSystemConfigJson,
        StepType.ExtractFrame => step.ExtractFrameConfigJson,
        StepType.AudioGenerate => step.AudioGenerateConfigJson,
        StepType.PinInsert => step.PinInsertConfigJson,
        StepType.AudioCleanup => step.AudioCleanupConfigJson,
        StepType.VideoEnhance => step.VideoEnhanceConfigJson,
        StepType.ImageGenerate => step.ImageGenerateConfigJson,
        _ => null
    };

    public static void Set(WorkflowStep step, string? json)
    {
        switch (step.StepType)
        {
            case StepType.Conditional: step.ConditionalConfigJson = json; break;
            case StepType.ReviewLoop: step.ReviewLoopConfigJson = json; break;
            case StepType.Extract: step.ExtractConfigJson = json; break;
            case StepType.VideoAnalyze: step.VideoAnalyzeConfigJson = json; break;
            case StepType.VideoCompile: step.VideoCompileConfigJson = json; break;
            case StepType.EditRoom: step.EditRoomConfigJson = json; break;
            case StepType.GraphicsRoom: step.GraphicsRoomConfigJson = json; break;
            case StepType.ColorGradeRoom: step.ColorGradeRoomConfigJson = json; break;
            case StepType.VideoGenerate: step.VideoGenerateConfigJson = json; break;
            case StepType.Voiceover: step.VoiceoverConfigJson = json; break;
            case StepType.ThumbnailGenerate: step.ThumbnailGenerateConfigJson = json; break;
            case StepType.DailiesRoom: step.DailiesRoomConfigJson = json; break;
            case StepType.DesignSystem: step.DesignSystemConfigJson = json; break;
            case StepType.ExtractFrame: step.ExtractFrameConfigJson = json; break;
            case StepType.AudioGenerate: step.AudioGenerateConfigJson = json; break;
            case StepType.PinInsert: step.PinInsertConfigJson = json; break;
            case StepType.AudioCleanup: step.AudioCleanupConfigJson = json; break;
            case StepType.VideoEnhance: step.VideoEnhanceConfigJson = json; break;
            case StepType.ImageGenerate: step.ImageGenerateConfigJson = json; break;
        }
    }
}
