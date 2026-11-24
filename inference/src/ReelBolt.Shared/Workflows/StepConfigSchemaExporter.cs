using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Reflectively exports JSON schemas for step configuration records.
/// Scans <c>ReelBolt.Shared.Workflows</c> for sealed record types ending in <c>StepConfig</c>,
/// matches them to <see cref="StepType"/> enum members by name, and generates their JSON schemas.
/// </summary>
public class StepConfigSchemaExporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Schema-export options carrying the one hook this exporter needs:
    /// <see cref="JsonSchemaExporterOptions.TransformSchemaNode"/>, the documented mechanism for
    /// teaching <see cref="JsonSchemaExporter"/> about
    /// <see cref="System.ComponentModel.DescriptionAttribute"/>, which it does not consume on its own.
    /// </summary>
    private static readonly JsonSchemaExporterOptions SchemaExporterOptions = new()
    {
        TransformSchemaNode = LiftDescriptionAttribute,
    };

    private readonly Dictionary<string, Type?> _configTypeCache;

    public StepConfigSchemaExporter()
    {
        _configTypeCache = BuildConfigTypeCache();
    }

    /// <summary>
    /// Gets the schema for a specific step type.
    /// Returns a result with <c>HasConfig: false</c> if the step type has no configuration record.
    /// Never throws.
    /// </summary>
    public StepConfigSchemaResult GetSchema(StepType stepType)
    {
        string stepTypeName = stepType.ToString();

        if (!_configTypeCache.TryGetValue(stepTypeName, out Type? configType))
        {
            return new StepConfigSchemaResult(false, null, null);
        }

        if (configType == null)
        {
            return new StepConfigSchemaResult(false, null, null);
        }

        try
        {
            JsonNode? schema = JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, configType, SchemaExporterOptions);
            if (stepType == StepType.VideoGenerate)
            {
                AnnotateVideoGenerateResolution(schema);
            }

            return new StepConfigSchemaResult(true, configType.Name, schema);
        }
        catch
        {
            // If schema generation fails, return no config rather than throwing
            return new StepConfigSchemaResult(false, null, null);
        }
    }

    /// <summary>
    /// Gets schemas for all <see cref="StepType"/> enum values.
    /// Returns a dictionary keyed by the string representation of each <see cref="StepType"/>.
    /// Never throws.
    /// </summary>
    public Dictionary<string, StepConfigSchemaResult> GetAllSchemas()
    {
        Dictionary<string, StepConfigSchemaResult> result = new();

        foreach (StepType stepType in Enum.GetValues<StepType>())
        {
            string stepTypeName = stepType.ToString();
            result[stepTypeName] = GetSchema(stepType);
        }

        return result;
    }

    /// <summary>
    /// Lifts a <see cref="DescriptionAttribute"/> declared on a step-config property onto that
    /// property's OWN schema node as its <c>description</c> keyword, so a property that documents
    /// itself in C# also documents itself in the payload the workflow assistant and every MCP
    /// client read from <c>ListStepSchemas</c>.
    /// <para>
    /// Scoped strictly: the annotation is written only when the node being transformed is the
    /// direct node of the annotated property. Every other node — including the type-level node and
    /// the nested nodes of a complex property, which carry their enclosing property's
    /// <see cref="JsonSchemaExporterContext.PropertyInfo"/> — is returned untouched, so a step
    /// config carrying no <see cref="DescriptionAttribute"/> serializes byte-identically to before
    /// this hook existed.
    /// </para>
    /// <para>
    /// Total by construction: an annotation is best-effort, so any failure inside this delegate is
    /// swallowed here. That keeps the discipline <see cref="GetSchema"/> already had — a working
    /// schema must never become a missing one.
    /// </para>
    /// </summary>
    internal static JsonNode LiftDescriptionAttribute(JsonSchemaExporterContext context, JsonNode schemaNode)
    {
        try
        {
            if (context.PropertyInfo is { AttributeProvider: { } attributeProvider } property
                && IsThePropertysOwnNode(context.Path, property.Name)
                && TryReadDescription(attributeProvider, out string? description)
                && schemaNode is JsonObject propertyNode)
            {
                propertyNode["description"] = description;
            }
        }
        catch
        {
            // Best-effort annotation: never fail the export on its behalf.
        }

        return schemaNode;
    }

    /// <summary>
    /// T-105: <c>resolution</c> is a free string validated against the chosen model's price card, so the schema must
    /// say which values each card accepts. The list is generated from the cards (never hand-written) and appended to
    /// the property's own description. Best-effort like every annotation here.
    /// </summary>
    private static void AnnotateVideoGenerateResolution(JsonNode? schema)
    {
        try
        {
            if (schema?["properties"]?["resolution"] is JsonObject node)
            {
                string existing = node["description"]?.GetValue<string>() is { Length: > 0 } d ? d + " " : string.Empty;
                node["description"] = existing
                    + "Allowed values depend on the model's price card and are exact and case-sensitive. "
                    + "Per model: " + ReelBolt.Shared.Inference.VideoGenerationResolutionCatalog.Describe()
                    + ". Providers that quote live (Higgsfield) or price by template (ComfyUI) are checked when the step runs.";
            }
        }
        catch
        {
            // Best-effort annotation: never fail the export on its behalf.
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> addresses the node generated for the property itself rather
    /// than a node nested inside it. System.Text.Json attaches the enclosing property's
    /// <see cref="JsonSchemaExporterContext.PropertyInfo"/> to every node beneath that property, so
    /// without this check a complex-typed annotated property would stamp its <c>description</c> onto
    /// each of its children too.
    /// </summary>
    private static bool IsThePropertysOwnNode(ReadOnlySpan<string> path, string propertyName)
        => path.Length >= 2 && string.Equals(path[^1], propertyName, StringComparison.Ordinal);

    private static bool TryReadDescription(ICustomAttributeProvider attributeProvider, out string? description)
    {
        foreach (object attribute in attributeProvider.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true))
        {
            if (attribute is DescriptionAttribute { Description: { Length: > 0 } text } && !string.IsNullOrWhiteSpace(text))
            {
                description = text;
                return true;
            }
        }

        description = null;
        return false;
    }

    /// <summary>
    /// Builds a cache of step type names to their corresponding configuration record types.
    /// Scans the <c>ReelBolt.Shared.Workflows</c> namespace for sealed records ending in
    /// <c>StepConfig</c>, excludes interface types, and maps them to <see cref="StepType"/>
    /// enum members by name (case-sensitive).
    /// </summary>
    private static Dictionary<string, Type?> BuildConfigTypeCache()
    {
        Dictionary<string, Type?> cache = new();

        // Initialize all StepType enum values with null (no config)
        foreach (StepType stepType in Enum.GetValues<StepType>())
        {
            cache[stepType.ToString()] = null;
        }

        // Find all sealed records ending in StepConfig in the Workflows namespace
        Assembly sharedAssembly = typeof(StepType).Assembly;
        Type[] workflowTypes = sharedAssembly.GetTypes()
            .Where(t => t.Namespace == typeof(StepConfigSchemaExporter).Namespace
                && t.Name.EndsWith("StepConfig", StringComparison.Ordinal)
                && !t.IsInterface)
            .ToArray();

        foreach (Type configType in workflowTypes)
        {
            // Strip "StepConfig" suffix to get the step type name
            string stepTypeName = configType.Name[..^"StepConfig".Length];

            // Match against StepType enum members (case-sensitive)
            if (cache.ContainsKey(stepTypeName))
            {
                cache[stepTypeName] = configType;
            }
        }

        return cache;
    }
}

/// <summary>
/// Result of exporting a step's configuration schema.
/// </summary>
public sealed record StepConfigSchemaResult(
    bool HasConfig,
    string? ConfigTypeName,
    JsonNode? Schema);
