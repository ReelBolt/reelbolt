using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Schema fixes for tool parameters whose CLR type is a raw <see cref="JsonElement"/> and whose
/// generated schema is therefore untyped.
/// </summary>
/// <remarks>
/// <para>
/// The run brief is the known case: <c>ExecuteWorkflow</c>, <c>ValidateWorkflow</c> and the two
/// recipe-create tools accept it as <see cref="JsonElement"/> because
/// <c>RunBriefValidator.ParseJson</c> owns the one set of parse/validation rules. The generated
/// schema for a <see cref="JsonElement"/> declares no type, which is what the connector review
/// flags. <see cref="RunBriefSchema"/> is the typed companion of that validator: the same fields,
/// the same enum names, no required members, unknown fields refused.
/// </para>
/// <para>
/// The wrapper changes only the declaration; binding still hands the raw JSON to the tool, so the
/// validator keeps producing its plain-language errors and no behaviour changes.
/// </para>
/// </remarks>
public static class AssistantToolSchemas
{
    /// <summary>The typed JSON Schema of <c>ReelBolt.Shared.Briefs.RunBrief</c>.</summary>
    public static readonly JsonElement RunBriefSchema = JsonDocument.Parse(RunBriefSchemaJson).RootElement.Clone();

    /// <summary>Replaces the <c>brief</c> property's schema on a tool that accepts a run brief.</summary>
    /// <remarks>The schema also declares <c>inputs</c>, the values of the app's own fields (docs/design/app-inputs.md 6.1).</remarks>
    public static AIFunction WithRunBriefSchema(AIFunction tool, string parameterName = "brief")
    {
        ArgumentNullException.ThrowIfNull(tool);
        JsonElement current = tool.JsonSchema;
        if (current.ValueKind is JsonValueKind.Undefined)
            return tool;

        JsonNode? parsed = JsonNode.Parse(current.GetRawText());
        if (parsed is not JsonObject root)
            return tool;

        if (root["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            root["properties"] = properties;
        }

        JsonNode brief = JsonNode.Parse(RunBriefSchemaJson)!;
        if (brief["properties"] is JsonObject briefProperties)
            briefProperties["inputs"] = JsonNode.Parse(BriefInputsSchemaJson);
        properties[parameterName] = brief;
        return new SchemaOverrideAIFunction(tool, JsonSerializer.SerializeToElement(root));
    }

    /// <summary>
    /// Types the <c>appManifest</c> argument of the workflow save tools (docs/design/app-inputs.md 13.2): the app page as
    /// REST takes it on create and update. The manifest validator owns the rules; this only declares the shape.
    /// </summary>
    public static AIFunction WithAppManifestSchema(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        JsonElement current = tool.JsonSchema;
        if (current.ValueKind is JsonValueKind.Undefined || JsonNode.Parse(current.GetRawText()) is not JsonObject root
            || root["properties"] is not JsonObject properties || !properties.ContainsKey("appManifest"))
            return tool;

        properties["appManifest"] = JsonNode.Parse(AppManifestSchemaJson);
        return new SchemaOverrideAIFunction(tool, JsonSerializer.SerializeToElement(root));
    }

    /// <summary>The <c>brief.inputs</c> member: the app's own fields by id (GetWorkflow lists them).</summary>
    private const string BriefInputsSchemaJson = """
    {
      "type": "object",
      "description": "The values of the app's own fields (its app inputs), by input id as GetWorkflow lists them: text, a number, true or false; a model field takes the id of one of its options.",
      "additionalProperties": { "type": ["string", "number", "boolean"] }
    }
    """;

    private const string AppManifestSchemaJson = """
    {
      "type": ["object", "null"],
      "description": "The app page saved with the workflow: version 2, title, tagline, inputs (brief fields, file slots, notes and app inputs) and requestTemplate. Omit it to keep the stored page.",
      "additionalProperties": true,
      "properties": {
        "version": { "type": "integer", "enum": [1, 2] },
        "title": { "type": "string" },
        "tagline": { "type": "string" },
        "requestTemplate": { "type": "string", "description": "Text added to each run's request, with {{input.id}} placeholders." },
        "inputs": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": true,
            "required": ["kind"],
            "properties": {
              "kind": { "type": "string", "enum": ["brief", "files", "notes", "input"] },
              "id": { "type": "string", "description": "An app input's id: a lower-case letter then letters and digits." },
              "type": { "type": "string", "enum": ["shortText", "longText", "number", "duration", "choice", "toggle", "shape", "model"] },
              "label": { "type": "string" },
              "help": { "type": "string" },
              "example": { "type": "string" },
              "required": { "type": "boolean" },
              "more": { "type": "boolean", "description": "true puts the field under More options." }
            }
          }
        }
      }
    }
    """;

    /// <summary>
    /// Types the two free-form JSON members of a workflow patch operation:
    /// <c>patch</c> (an RFC 7396 merge-patch object) and <c>value</c> (any JSON scalar, object or
    /// array the named field accepts). Both are <see cref="JsonElement"/> in CLR terms, which
    /// otherwise declares no type at all.
    /// </summary>
    public static AIFunction WithWorkflowPatchSchema(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        JsonElement current = tool.JsonSchema;
        if (current.ValueKind is JsonValueKind.Undefined)
            return tool;

        JsonNode? parsed = JsonNode.Parse(current.GetRawText());
        if (parsed is not JsonObject root
            || root["properties"] is not JsonObject properties
            || properties["operations"] is not JsonObject operations
            || operations["items"] is not JsonObject items
            || items["properties"] is not JsonObject itemProperties)
            return tool;

        itemProperties["patch"] = JsonNode.Parse("""
            {
              "type": ["object", "null"],
              "description": "RFC 7396 merge-patch for the step's per-type config (field name to new value).",
              "additionalProperties": true
            }
            """);
        itemProperties["value"] = JsonNode.Parse("""
            {
              "description": "New value for the named field (a JSON string, number, boolean, object or array), or null to clear it.",
              "anyOf": [
                { "type": "string" },
                { "type": "number" },
                { "type": "boolean" },
                { "type": "object", "additionalProperties": true },
                { "type": "array", "items": {
                    "anyOf": [
                      { "type": "string" }, { "type": "number" }, { "type": "boolean" },
                      { "type": "object", "additionalProperties": true }
                    ]
                } },
                { "type": "null" }
              ]
            }
            """);
        // The app-page operations' field (docs/design/app-inputs.md 13.2): one app input, a JSON object.
        if (itemProperties.ContainsKey("input"))
        {
            itemProperties["input"] = JsonNode.Parse("""
                {
                  "type": ["object", "null"],
                  "description": "setAppInput: the field (app input) to add or replace by id: id, type, label, help, example, required, default, min, max, unit, options, more, showWhen.",
                  "additionalProperties": true
                }
                """);
        }

        return new SchemaOverrideAIFunction(tool, JsonSerializer.SerializeToElement(root));
    }

    private const string RunBriefSchemaJson = """
    {
      "type": "object",
      "description": "Optional run brief. Every field is optional; unknown fields are refused.",
      "additionalProperties": false,
      "properties": {
        "language": {
          "type": "string",
          "description": "BCP-47 language tag for narration, on-screen text and captions (for example it or en-US)."
        },
        "sourceLanguage": {
          "type": "string",
          "description": "BCP-47 language tag of the spoken language in the footage."
        },
        "lengthSec": {
          "type": "integer",
          "minimum": 5,
          "maximum": 600,
          "description": "Target runtime in seconds."
        },
        "format": {
          "type": "string",
          "description": "Output shape: Original, Vertical, Square, Landscape or Portrait."
        },
        "resolution": {
          "type": "string",
          "enum": ["Auto", "720p", "1080p", "2160p"],
          "description": "Output resolution, measured on the short side."
        },
        "stylePreset": {
          "type": "string",
          "description": "Key of a built-in editing-style preset."
        },
        "styleId": {
          "type": "string",
          "format": "uuid",
          "description": "Id of a saved editing style."
        },
        "style": {
          "type": "object",
          "description": "Inline editing style; every axis is optional.",
          "properties": {
            "pacing": { "type": "string", "enum": ["Slow", "Medium", "Fast", "BeatSynced"] },
            "storytelling": { "type": "string", "enum": ["Chronological", "HookFirst", "ProblemSolution", "Montage"] },
            "editing": { "type": "string", "enum": ["JumpCuts", "SmoothTransitions", "Mixed"] },
            "tone": { "type": "string", "enum": ["Calm", "Upbeat", "Energetic", "Serious", "Playful", "Inspirational"] },
            "captions": { "type": "string", "enum": ["Clean", "Karaoke", "Punch", "Bold"] },
            "color": { "type": "string", "enum": ["Natural", "Warm", "Cool", "Filmic", "Vibrant", "Muted", "Mono"] },
            "music": { "type": "string", "enum": ["Low", "Medium", "High"] },
            "graphics": { "type": "string", "enum": ["None", "Minimal", "Moderate", "Rich"] },
            "notes": { "type": "string" },
            "language": { "type": "string" }
          }
        },
        "tone": {
          "type": "string",
          "enum": ["Calm", "Upbeat", "Energetic", "Serious", "Playful", "Inspirational"]
        },
        "designSystem": {
          "type": "string",
          "enum": ["project", "none"],
          "description": "project uses the project's approved brand tokens; none runs without them."
        },
        "voice": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "mode": { "type": "string", "enum": ["none", "narrated"] },
            "projectVoiceId": { "type": "string", "format": "uuid" },
            "libraryVoiceId": { "type": "string", "format": "uuid" }
          }
        },
        "sourceFiles": {
          "type": "array",
          "description": "Project files grouped by what they are for. One entry per role.",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["role", "fileIds"],
            "properties": {
              "role": {
                "type": "string",
                "enum": ["video", "code", "music", "sfx", "image", "document", "insertContent"]
              },
              "fileIds": {
                "type": "array",
                "minItems": 1,
                "items": { "type": "string", "format": "uuid" }
              }
            }
          }
        },
        "notes": { "type": "string" },
        "avatar": {
          "type": "object",
          "additionalProperties": false,
          "description": "The project avatar (a consented person) a VideoGenerate step that names none shows in this run. It never turns on the step's permission to send media, which is given only by the customer in the app, or by their own approval of a workflow change that says so; without it the step is refused.",
          "properties": {
            "avatarId": { "type": "string", "format": "uuid" }
          }
        }
      }
    }
    """;

    /// <summary>A declaration with a replaced input schema; invocation is delegated unchanged.</summary>
    private sealed class SchemaOverrideAIFunction : DelegatingAIFunction
    {
        private readonly JsonElement _jsonSchema;

        public SchemaOverrideAIFunction(AIFunction innerFunction, JsonElement jsonSchema)
            : base(innerFunction)
        {
            _jsonSchema = jsonSchema;
        }

        public override JsonElement JsonSchema => _jsonSchema;
    }
}
