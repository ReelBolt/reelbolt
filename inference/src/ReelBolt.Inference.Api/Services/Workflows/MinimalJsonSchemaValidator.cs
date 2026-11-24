using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Minimal JSON Schema validator supporting only:
/// - Required properties validation
/// - Primitive type mismatches (string, integer, number, boolean)
///
/// Does not validate: nested objects, arrays, or complex constraints.
/// </summary>
public class MinimalJsonSchemaValidator
{
    /// <summary>
    /// Validates a JSON configuration string against a JSON Schema.
    /// </summary>
    /// <param name="schema">The JSON Schema as a JsonNode</param>
    /// <param name="configJson">The raw JSON configuration string to validate</param>
    /// <returns>A validation result with Valid flag and list of error messages</returns>
    public ValidationResult Validate(JsonNode schema, string configJson)
    {
        var errors = new List<string>();

        // Try to parse the config JSON
        JsonNode? configNode;
        try
        {
            configNode = JsonNode.Parse(configJson);
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
            return new ValidationResult(false, errors);
        }

        if (configNode == null)
        {
            errors.Add("Config JSON is null");
            return new ValidationResult(false, errors);
        }

        // Extract properties and required array from schema
        var propertiesNode = schema["properties"];
        var requiredNode = schema["required"];

        // Get the list of required properties
        var requiredProperties = new HashSet<string>();
        if (requiredNode is JsonArray requiredArray)
        {
            foreach (var item in requiredArray)
            {
                if (item?.GetValue<string>() is string propName)
                {
                    requiredProperties.Add(propName);
                }
            }
        }

        // Validate required properties are present and have correct types
        if (propertiesNode is JsonObject propertiesObj)
        {
            foreach (var requiredProp in requiredProperties)
            {
                // Check if property exists in config
                var configObj = configNode.AsObject();
                if (!configObj.ContainsKey(requiredProp))
                {
                    errors.Add($"Missing required property: {requiredProp}");
                    continue;
                }

                // Get the schema for this property
                var propSchema = propertiesNode[requiredProp];
                if (propSchema == null)
                    continue;

                // Get the expected type from schema
                var typeNode = propSchema["type"];
                if (typeNode == null)
                    continue;

                var expectedType = typeNode.GetValue<string>();
                var actualValue = configObj[requiredProp];

                // Validate primitive types only (skip arrays and objects)
                if (expectedType != "array" && expectedType != "object")
                {
                    if (!IsPrimitiveTypeMatch(actualValue, expectedType))
                    {
                        errors.Add($"Property '{requiredProp}' has incorrect type. Expected {expectedType}, got {GetJsonType(actualValue)}");
                    }
                }
            }
        }

        return new ValidationResult(errors.Count == 0, errors);
    }

    private static bool IsPrimitiveTypeMatch(JsonNode? value, string expectedType)
    {
        if (value is not JsonValue jsonValue)
            return false;

        JsonValueKind kind = jsonValue.GetValueKind();
        return expectedType switch
        {
            "string" => kind == JsonValueKind.String,
            "integer" => kind == JsonValueKind.Number && IsIntegerNumber(jsonValue),
            "number" => kind == JsonValueKind.Number,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
    }

    private static bool IsIntegerNumber(JsonValue value)
    {
        try
        {
            // Try to parse as long first (most common case)
            _ = value.GetValue<long>();
            return true;
        }
        catch
        {
            // If it fails, try decimal to check for fractional part
            try
            {
                var decimalValue = value.GetValue<decimal>();
                return decimalValue == Math.Truncate(decimalValue);
            }
            catch
            {
                return false;
            }
        }
    }

    private static string GetJsonType(JsonNode? node)
    {
        return node switch
        {
            JsonObject => "object",
            JsonArray => "array",
            JsonValue jv => GetJsonValueType(jv),
            null => "null",
            _ => "unknown"
        };
    }

    private static string GetJsonValueType(JsonValue value)
    {
        JsonValueKind kind = value.GetValueKind();
        if (kind is JsonValueKind.True or JsonValueKind.False)
            return "boolean";
        if (kind == JsonValueKind.Number)
        {
            if (IsIntegerNumber(value))
                return "integer";
            return "number";
        }
        if (kind == JsonValueKind.String)
            return "string";

        return "unknown";
    }
}

public record ValidationResult(bool Valid, IReadOnlyList<string> Errors);
