using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.OutputSchemas;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Per-schema-type JSON options hook. Returns the tolerant default unless a type registered its own
/// (more lenient) options; T-002 registers <c>ScriptwriterOutput</c> here. Kept tiny on purpose.
/// </summary>
internal static class StructuredOutputOptions
{
    private static readonly Dictionary<Type, JsonSerializerOptions> Overrides = new();

    /// <summary>Tolerant defaults: never stricter than what the real consumers accept.</summary>
    public static JsonSerializerOptions Default { get; } = Create();

    public static void Register(Type schemaType, JsonSerializerOptions options)
    {
        lock (Overrides) Overrides[schemaType] = options;
    }

    public static JsonSerializerOptions For(Type schemaType)
    {
        lock (Overrides) return Overrides.TryGetValue(schemaType, out JsonSerializerOptions? o) ? o : Default;
    }

    private static JsonSerializerOptions Create() => new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
    };
}

/// <summary>
/// Checks that a structured agent's final answer really deserializes into its declared
/// <c>OutputSchemaType</c> (schema enforcement is provider dependent: DeepSeek and other
/// OpenAI-compatible endpoints ignore <c>ForJsonSchema</c>). Provider-agnostic by construction.
/// </summary>
internal static class StructuredOutputValidator
{
    /// <summary>
    /// Schema types with NO typed consumer in the engine: their answer is read ad hoc (JsonElement /
    /// next agent's prompt / render tool output), so a typed deserialize here could fail a run that
    /// works today. They keep only the "a JSON object exists" check. Justification per type:
    /// DirectorOutput, ReviewOutput, VideoReviewOutput, AnimationStrategyOutput: consumed as text by
    /// later agents or loosely parsed; RenderManifestOutput and RemotionProjectBuildOutput: the
    /// Author/translator final answer is a tool-run summary, the artifact is the side effect.
    /// </summary>
    private static readonly HashSet<Type> ExemptFromTypedCheck =
    [
        typeof(DirectorOutput),
        typeof(ReviewOutput),
        typeof(VideoReviewOutput),
        typeof(AnimationStrategyOutput),
        typeof(RenderManifestOutput),
        typeof(RemotionProjectBuildOutput),
    ];

    /// <summary>
    /// The outputs VideoCompile reads through DeserializeWithQuoteFallback. Only these accept
    /// single-quoted (Python-dict style) JSON downstream, so only these get the quote-normalised retry.
    /// </summary>
    private static readonly HashSet<Type> QuoteFallbackTypes =
    [
        typeof(VideoEditDecisionOutput),
        typeof(MusicPlanOutput),
        typeof(MotionGraphicsPlanOutput),
        typeof(ColorGradePlanOutput),
        typeof(SfxPlanOutput),
        typeof(MotionPlanOutput),
        typeof(DynamicsPlanOutput),
        typeof(TakeSelectionOutput),
    ];

    public static bool IsExempt(Type schemaType) => ExemptFromTypedCheck.Contains(schemaType);

    /// <summary>Null when valid; otherwise a precise, model-readable error.</summary>
    public static string? Validate(string? output, Type schemaType)
    {
        string? json = RobustJsonExtractor.ExtractLastJsonObject(output ?? string.Empty);
        if (json == null)
            return "The response contained no parseable JSON object.";
        if (IsExempt(schemaType))
            return null;
        JsonSerializerOptions options = StructuredOutputOptions.For(schemaType);
        try
        {
            object? value = JsonSerializer.Deserialize(json, schemaType, options);
            return value == null ? "The JSON object deserialized to null." : null;
        }
        catch (JsonException ex)
        {
            // VideoCompile / SoundDesigner / TakeSelector consumers retry once on the quote-normalised
            // text (single-quoted, Python-dict style JSON). The validator must accept whatever they accept.
            string? normalized = QuoteFallbackTypes.Contains(schemaType)
                ? RobustJsonExtractor.NormalizeQuotedStrings(json)
                : null;
            if (normalized != null)
            {
                try
                {
                    if (JsonSerializer.Deserialize(normalized, schemaType, options) != null)
                        return null;
                }
                catch (JsonException)
                {
                }
            }

            string path = string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path;
            return $"{ex.Message} Path: {path}";
        }
    }
}
