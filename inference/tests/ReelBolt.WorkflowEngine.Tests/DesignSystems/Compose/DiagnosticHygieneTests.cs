using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose;

/// <summary>Model-chosen text (token keys, dropped values) must never reach a diagnostic unredacted.</summary>
public class DiagnosticHygieneTests
{
    private static readonly DesignValidationContext Context = new(_ => true, new HashSet<string>());

    internal static readonly string[] Secrets =
    [
        "sk-ant-api03-" + new string('a', 40),
        "-----BEGIN " + "PRIVATE KEY-----",
        "eyJ" + new string('b', 12) + "." + new string('c', 12) + "." + new string('d', 12),
        "sk-proj-" + new string('e', 40),
    ];

    private static List<ImportDiagnostic> Run(StyleAndThemeOutput style)
    {
        var diagnostics = new List<ImportDiagnostic>();
        JsonObject design = LegacyAnalyzerMapper.DesignFrom(style, diagnostics)!;
        DesignTokenSanitizer.Sanitize(design, Context, diagnostics);
        DesignDocumentValidator.Validate(design, Context).IsValid.Should().BeTrue();
        design.ToJsonString().Should().NotContainAny(Secrets);
        return diagnostics;
    }

    private static void AssertClean(IEnumerable<ImportDiagnostic> diagnostics)
    {
        foreach (ImportDiagnostic d in diagnostics)
        {
            d.Path.Should().NotContainAny(Secrets);
            d.Message.Should().NotContainAny(Secrets);
            d.Path.Length.Should().BeLessThanOrEqualTo(200);
        }
    }

    [Fact]
    public void Secret_shaped_keys_with_unparseable_values_never_reach_a_diagnostic_or_the_document()
    {
        var style = new StyleAndThemeOutput { Colors = new ColorPalette(), Typography = new Typography(), Spacing = new Spacing() };
        foreach (string secret in Secrets)
        {
            style.Colors.Additional[secret] = "linear-gradient(red, blue)";
            style.Typography.FontSizes[secret] = "clamp(1rem, 2vw, 2rem)";
            style.Spacing.Scale[secret] = "auto";
        }

        List<ImportDiagnostic> diagnostics = Run(style);

        diagnostics.Should().NotBeEmpty();
        AssertClean(diagnostics);
    }

    [Fact]
    public void Secret_shaped_keys_with_valid_values_are_dropped_with_clean_diagnostics()
    {
        var style = new StyleAndThemeOutput { Colors = new ColorPalette() };
        foreach (string secret in Secrets)
            style.Colors.Additional[secret] = "#112233";

        List<ImportDiagnostic> diagnostics = Run(style);

        AssertClean(diagnostics);
    }

    [Fact]
    public void A_lowercase_secret_key_that_passes_the_key_charset_is_redacted_in_the_sanitiser_path()
    {
        string key = Secrets[3];
        var doc = new JsonObject
        {
            ["schema"] = DesignDocument.Schema,
            ["color"] = new JsonObject
            {
                [key] = new JsonObject { ["$type"] = "color", ["$value"] = "nonsense" },
            },
        };
        var diagnostics = new List<ImportDiagnostic>();

        DesignTokenSanitizer.Sanitize(doc, Context, diagnostics);

        diagnostics.Should().ContainSingle();
        AssertClean(diagnostics);
    }

    [Fact]
    public void A_dropped_value_is_never_echoed()
    {
        var doc = new JsonObject
        {
            ["schema"] = DesignDocument.Schema,
            ["color"] = new JsonObject
            {
                ["bad"] = new JsonObject { ["$type"] = "color", ["$value"] = "marker-qzx-visible-text" },
            },
        };
        var diagnostics = new List<ImportDiagnostic>();
        DesignTokenSanitizer.Sanitize(doc, Context, diagnostics);

        var style = new StyleAndThemeOutput { Colors = new ColorPalette { Primary = "marker-qzx-visible-text" } };
        LegacyAnalyzerMapper.DesignFrom(style, diagnostics);

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => !d.Message.Contains("marker-qzx") && !d.Path.Contains("marker-qzx"));
    }

    [Fact]
    public void A_hostile_output_yields_a_bounded_number_of_diagnostics()
    {
        var style = new StyleAndThemeOutput { Colors = new ColorPalette() };
        for (int i = 0; i < 5000; i++)
            style.Colors.Additional["k" + i] = "not-a-colour";

        var diagnostics = new List<ImportDiagnostic>();
        LegacyAnalyzerMapper.DesignFrom(style, diagnostics);
        List<ImportDiagnostic> capped = DesignTokenSanitizer.CapDiagnostics(diagnostics);

        capped.Count.Should().BeLessThanOrEqualTo(DesignTokenSanitizer.MaxDiagnostics + 1);
        capped[^1].Code.Should().Be("DIAGNOSTICS_TRUNCATED");
    }
}
