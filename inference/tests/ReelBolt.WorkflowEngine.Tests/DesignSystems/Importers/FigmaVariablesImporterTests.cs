using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// P3-12 coverage for the pure <see cref="FigmaVariablesImporter"/>: the REST body's
/// <c>meta.variables</c>/<c>meta.variableCollections</c> shape, the default-mode base tokens, the
/// dark/light mode overrides, VARIABLE_ALIAS references, FLOAT numbers and the dropped
/// STRING/BOOLEAN/unknown-mode cases.
/// </summary>
public sealed class FigmaVariablesImporterTests
{
    private static readonly FigmaVariablesImporter Importer = new();

    [Fact]
    public void Colour_default_mode_becomes_a_base_token_and_dark_becomes_an_override()
    {
        DesignImportResult result = Import(VariablesJson);

        JsonObject design = result.Design!;
        JsonObject token = DesignDocument.FindToken(design, "color.primary.base")!;
        Assert.Equal("color", token["$type"]!.GetValue<string>());
        Assert.Equal("#ff0000", token["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("srgb", token["$value"]!["colorSpace"]!.GetValue<string>());

        JsonObject dark = design["modes"]!["dark"]!.AsObject();
        Assert.Equal("#000000", dark["color.primary.base"]!["hex"]!.GetValue<string>());
        Assert.Equal(0.4, token["$extensions"]!["com.reelbolt"]!["conf"]!.GetValue<double>());
    }

    [Fact]
    public void Float_becomes_a_number_token()
    {
        DesignImportResult result = Import(VariablesJson);

        JsonObject token = DesignDocument.FindToken(result.Design!, "number.spacing.gap")!;
        Assert.Equal("number", token["$type"]!.GetValue<string>());
        Assert.Equal(8, Number(token["$value"]!));
    }

    [Fact]
    public void Variable_alias_becomes_a_path_alias()
    {
        DesignImportResult result = Import(VariablesJson);

        JsonObject token = DesignDocument.FindToken(result.Design!, "color.semantic.brand")!;
        Assert.Equal("{color.primary.base}", token["$value"]!.GetValue<string>());
    }

    [Fact]
    public void String_is_skipped_with_info_and_boolean_is_ignored()
    {
        DesignImportResult result = Import(VariablesJson);

        Assert.Null(DesignDocument.FindToken(result.Design!, "color.text.label"));
        Assert.Null(DesignDocument.FindToken(result.Design!, "number.flag.on"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("BOOLEAN", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == "VARIABLE_SKIPPED" && d.Message.Contains("STRING", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_mode_name_is_dropped_with_info()
    {
        DesignImportResult result = Import(VariablesJson);

        JsonObject modes = result.Design!["modes"]!.AsObject();
        Assert.False(modes.ContainsKey("contrast"));
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == ImportDiagnosticSeverity.Info
            && d.Code == "MODE_SKIPPED"
            && d.Message.Contains("Contrast", StringComparison.Ordinal));
    }

    [Fact]
    public void A_colour_only_response_is_a_valid_design_document()
    {
        DesignImportResult result = Import(ColourOnlyJson);

        Assert.NotNull(result.Design);
        AssertValid(result.Design!);
    }

    [Fact]
    public void CanRead_claims_figma_variables_json_only()
    {
        byte[] variables = Encoding.UTF8.GetBytes(VariablesJson);
        byte[] unrelated = Encoding.UTF8.GetBytes("{\"name\":\"not figma\"}");

        Assert.True(Importer.CanRead("figma-variables.json", variables));
        Assert.False(Importer.CanRead("tokens.css", variables));
        Assert.False(Importer.CanRead("tokens.json", unrelated));
    }

    [Fact]
    public void A_response_without_variables_answers_no_tokens_recognized()
    {
        DesignImportResult result = Import("{\"meta\":{\"variableCollections\":{}}}");

        Assert.Null(result.Design);
        Assert.Contains(result.Diagnostics, d => d.Code == "NO_TOKENS_RECOGNIZED");
    }

    private static DesignImportResult Import(string json)
    {
        var file = new ImportFile("figma-variables.json", Encoding.UTF8.GetBytes(json), string.Empty, null);
        return Importer.Import([file]);
    }

    private static DesignValidationResult Validate(JsonObject design)
    {
        var context = new DesignValidationContext(
            _ => false,
            new HashSet<string>(StringComparer.Ordinal));
        return DesignDocumentValidator.Validate(design, context);
    }

    private static void AssertValid(JsonObject design)
    {
        DesignValidationResult validation = Validate(design);
        Assert.True(
            validation.IsValid,
            string.Join("; ", validation.Violations.Select(v => $"{v.Code} {v.Path} {v.Message}")));
    }

    private static double Number(JsonNode? node)
    {
        Assert.True(CanonicalJson.TryGetNumber(node, out double value), "expected a JSON number");
        return value;
    }

    private const string VariablesJson = """
    {
      "meta": {
        "variables": {
          "VariableID:1": {
            "id": "VariableID:1",
            "name": "primary/base",
            "resolvedType": "COLOR",
            "valuesByMode": {
              "mode-light": { "r": 1, "g": 0, "b": 0, "a": 1 },
              "mode-dark": { "r": 0, "g": 0, "b": 0, "a": 1 },
              "mode-contrast": { "r": 1, "g": 1, "b": 0, "a": 1 }
            },
            "variableCollectionId": "CollectionID:1"
          },
          "VariableID:2": {
            "id": "VariableID:2",
            "name": "spacing/gap",
            "resolvedType": "FLOAT",
            "valuesByMode": { "mode-light": 8, "mode-dark": 4 },
            "variableCollectionId": "CollectionID:1"
          },
          "VariableID:3": {
            "id": "VariableID:3",
            "name": "text/label",
            "resolvedType": "STRING",
            "valuesByMode": { "mode-light": "Hello" },
            "variableCollectionId": "CollectionID:1"
          },
          "VariableID:4": {
            "id": "VariableID:4",
            "name": "flag/on",
            "resolvedType": "BOOLEAN",
            "valuesByMode": { "mode-light": true },
            "variableCollectionId": "CollectionID:1"
          },
          "VariableID:5": {
            "id": "VariableID:5",
            "name": "semantic/brand",
            "resolvedType": "COLOR",
            "valuesByMode": { "mode-light": { "type": "VARIABLE_ALIAS", "id": "VariableID:1" } },
            "variableCollectionId": "CollectionID:1"
          }
        },
        "variableCollections": {
          "CollectionID:1": {
            "id": "CollectionID:1",
            "name": "Theme",
            "defaultModeId": "mode-light",
            "modes": [
              { "modeId": "mode-light", "name": "Light" },
              { "modeId": "mode-dark", "name": "Dark" },
              { "modeId": "mode-contrast", "name": "Contrast" }
            ]
          }
        }
      }
    }
    """;

    private const string ColourOnlyJson = """
    {
      "meta": {
        "variables": {
          "VariableID:1": {
            "id": "VariableID:1",
            "name": "primary/base",
            "resolvedType": "COLOR",
            "valuesByMode": {
              "mode-light": { "r": 1, "g": 0, "b": 0, "a": 1 },
              "mode-dark": { "r": 0, "g": 0, "b": 0, "a": 1 }
            },
            "variableCollectionId": "CollectionID:1"
          },
          "VariableID:2": {
            "id": "VariableID:2",
            "name": "semantic/brand",
            "resolvedType": "COLOR",
            "valuesByMode": { "mode-light": { "type": "VARIABLE_ALIAS", "id": "VariableID:1" } },
            "variableCollectionId": "CollectionID:1"
          }
        },
        "variableCollections": {
          "CollectionID:1": {
            "id": "CollectionID:1",
            "name": "Theme",
            "defaultModeId": "mode-light",
            "modes": [
              { "modeId": "mode-light", "name": "Light" },
              { "modeId": "mode-dark", "name": "Dark" }
            ]
          }
        }
      }
    }
    """;
}
