using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class CssThemeImporterTests
{
    private static readonly CssThemeImporter Importer = new();

    [Fact]
    public void Shadcn_Globals_Maps_Colour_Tokens_And_Radius()
    {
        DesignImportResult result = Import(Fixture("shadcn-globals.css"));

        JsonObject? background = DesignDocument.FindToken(result.Design!, "color.background");
        Assert.NotNull(background);
        Assert.Equal("color", background!["$type"]!.GetValue<string>());
        Assert.Equal("#ffffff", background["$value"]!["hex"]!.GetValue<string>());

        JsonObject? foreground = DesignDocument.FindToken(result.Design!, "color.foreground");
        JsonObject? text = DesignDocument.FindToken(result.Design!, "color.text");
        Assert.NotNull(foreground);
        Assert.NotNull(text);
        Assert.Equal(
            foreground!["$value"]!["hex"]!.GetValue<string>(),
            text!["$value"]!["hex"]!.GetValue<string>());

        Assert.NotNull(DesignDocument.FindToken(result.Design!, "color.primary"));

        JsonObject? radius = DesignDocument.FindToken(result.Design!, "radius.base");
        Assert.NotNull(radius);
        Assert.Equal(0.625, Number(radius!["$value"]!["value"]));
        Assert.Equal("rem", radius["$value"]!["unit"]!.GetValue<string>());

        AssertValid(result.Design!);
    }

    [Fact]
    public void Plain_Root_Hex_Maps_To_Colour_Brand()
    {
        DesignImportResult result = Import("tokens.css", ":root { --brand: #6d28d9; }");

        JsonObject? brand = DesignDocument.FindToken(result.Design!, "color.brand");
        Assert.NotNull(brand);
        Assert.Equal("#6d28d9", brand!["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal(0.95, Confidence(brand));
        AssertValid(result.Design!);
    }

    [Fact]
    public void Tailwind_Theme_Namespaces_Map_To_Token_Groups()
    {
        DesignImportResult result = Import(Fixture("tailwind-theme.css"));

        JsonObject? brand = DesignDocument.FindToken(result.Design!, "color.brand.500");
        Assert.NotNull(brand);
        Assert.Equal("#6d28d9", brand!["$value"]!["hex"]!.GetValue<string>());

        JsonObject? fontSans = DesignDocument.FindToken(result.Design!, "typography.family.sans");
        Assert.NotNull(fontSans);
        Assert.Equal("fontFamily", fontSans!["$type"]!.GetValue<string>());
        JsonArray families = fontSans["$value"]!.AsArray();
        Assert.Equal(2, families.Count);
        Assert.Equal("Inter", families[0]!.GetValue<string>());
        Assert.Equal("sans-serif", families[1]!.GetValue<string>());

        JsonObject? textXl = DesignDocument.FindToken(result.Design!, "typography.size.xl");
        Assert.NotNull(textXl);
        Assert.Equal(1.25, Number(textXl!["$value"]!["value"]));
        Assert.Equal("rem", textXl["$value"]!["unit"]!.GetValue<string>());

        JsonObject? spacing = DesignDocument.FindToken(result.Design!, "spacing");
        Assert.NotNull(spacing);
        Assert.Equal(0.25, Number(spacing!["$value"]!["value"]));
        Assert.Equal("rem", spacing["$value"]!["unit"]!.GetValue<string>());

        JsonObject? ease = DesignDocument.FindToken(result.Design!, "motion.easing.snappy");
        Assert.NotNull(ease);
        Assert.Equal("cubicBezier", ease!["$type"]!.GetValue<string>());
        Assert.Equal(
            new[] { 0.2, 0.0, 0.0, 1.0 },
            ease["$value"]!.AsArray().Select(point => point!.GetValue<double>()).ToArray());

        Assert.Null(DesignDocument.FindToken(result.Design!, "motion.easing.spin"));
        AssertValid(result.Design!);
    }

    [Fact]
    public void Oklch_Converts_To_Srgb_And_Keeps_The_Original()
    {
        DesignImportResult result = Import("tokens.css", ":root { --background: oklch(1 0 0); }");

        JsonObject? background = DesignDocument.FindToken(result.Design!, "color.background");
        Assert.NotNull(background);
        Assert.Equal("#ffffff", background!["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("oklch(1 0 0)", Origin(background));
        AssertValid(result.Design!);
    }

    [Fact]
    public void Out_Of_Gamut_Oklch_Is_Clipped_To_A_Valid_Hex()
    {
        DesignImportResult result = Import("tokens.css", ":root { --background: oklch(0.7 0.5 30); }");

        JsonObject? background = DesignDocument.FindToken(result.Design!, "color.background");
        Assert.NotNull(background);
        string hex = background!["$value"]!["hex"]!.GetValue<string>();
        Assert.StartsWith("#", hex, StringComparison.Ordinal);
        Assert.Equal(7, hex.Length);
        Assert.All(hex[1..], character => Assert.Contains(character, "0123456789abcdef"));
        AssertValid(result.Design!);
    }

    [Fact]
    public void Dark_Block_Writes_Mode_Overrides_For_Existing_Tokens()
    {
        DesignImportResult result = Import(Fixture("shadcn-globals.css"));

        JsonObject? modes = result.Design!["modes"] as JsonObject;
        Assert.NotNull(modes);
        JsonObject? dark = modes!["dark"] as JsonObject;
        Assert.NotNull(dark);
        Assert.Equal("#0b0b0f", dark!["color.background"]!["hex"]!.GetValue<string>());
        AssertValid(result.Design!);
    }

    [Fact]
    public void Data_Theme_Dark_Selector_Is_Recognized()
    {
        DesignImportResult result = Import(
            "tokens.css",
            ":root { --brand: #6d28d9; } [data-theme=dark] { --brand: #ffffff; }");

        JsonObject? dark = (result.Design!["modes"] as JsonObject)?["dark"] as JsonObject;
        Assert.NotNull(dark);
        Assert.Equal("#ffffff", dark!["color.brand"]!["hex"]!.GetValue<string>());
        AssertValid(result.Design!);
    }

    [Fact]
    public void A_theme_dark_block_becomes_modes_dark()
    {
        var importer = new CssThemeImporter();
        ImportFile css = Css("tokens.css", ":root{--color-background:#fff;--color-text:#111}" +
            ".theme-dark{--color-background:#111;--color-text:#eee}");
        JsonObject doc = importer.Import([css]).Design!;

        Assert.NotNull(DesignDocument.FindToken(doc, "color.background"));
        Assert.Equal("#111111", doc["modes"]!["dark"]!["color.background"]!["hex"]!.GetValue<string>());
        Assert.Equal("#eeeeee", doc["modes"]!["dark"]!["color.text"]!["hex"]!.GetValue<string>());
        AssertValid(doc);
    }

    [Fact]
    public void A_manifest_theme_selector_names_its_mode()
    {
        var options = new CssImportOptions(
            new Dictionary<string, string> { ["[data-theme=midnight]"] = "midnight" });
        ImportFile css = Css("tokens.css",
            ":root{--color-background:#fff}[data-theme=midnight]{--color-background:#000}");
        JsonObject doc = new CssThemeImporter().Import([css], options).Design!;

        Assert.Equal("#000000", doc["modes"]!["midnight"]!["color.background"]!["hex"]!.GetValue<string>());
        AssertValid(doc);
    }

    [Fact]
    public void A_path_mapper_emits_only_its_own_paths_and_may_emit_two()
    {
        var options = new CssImportOptions(
            new Dictionary<string, string>(),
            (bare, type) => bare == "nws-background" && type == "color"
                ? ["color.background", "color.nws.background"]
                : []);
        ImportFile css = Css("tokens.css", ":root{--nws-background:#fff;--unmapped:#123456}");
        JsonObject doc = new CssThemeImporter().Import([css], options).Design!;

        Assert.NotNull(DesignDocument.FindToken(doc, "color.background"));
        Assert.NotNull(DesignDocument.FindToken(doc, "color.nws.background"));
        Assert.Null(DesignDocument.FindToken(doc, "color.unmapped"));
        AssertValid(doc);
    }

    [Fact]
    public void A_forced_type_maps_a_weight_to_a_font_weight_token()
    {
        var options = new CssImportOptions(
            new Dictionary<string, string>(),
            (bare, type) => bare == "type-h3-weight" ? ["typography.h3.weight"] : [],
            bare => bare.EndsWith("-weight", StringComparison.Ordinal) ? "fontWeight" : null);
        ImportFile css = Css("tokens.css", ":root{--type-h3-weight:400}");
        JsonObject doc = new CssThemeImporter().Import([css], options).Design!;

        JsonObject? token = DesignDocument.FindToken(doc, "typography.h3.weight");
        Assert.NotNull(token);
        Assert.Equal("fontWeight", token!["$type"]!.GetValue<string>());
        Assert.Equal(400d, Number(token["$value"]!));
        AssertValid(doc);
    }

    [Fact]
    public void A_forced_type_maps_a_line_height_to_a_number_token()
    {
        var options = new CssImportOptions(
            new Dictionary<string, string>(),
            (bare, type) => bare == "type-h3-lh" ? ["typography.h3.line-height"] : [],
            bare => bare.EndsWith("-lh", StringComparison.Ordinal) ? "number" : null);
        ImportFile css = Css("tokens.css", ":root{--type-h3-lh:1.167}");
        JsonObject doc = new CssThemeImporter().Import([css], options).Design!;

        JsonObject? token = DesignDocument.FindToken(doc, "typography.h3.line-height");
        Assert.NotNull(token);
        Assert.Equal("number", token!["$type"]!.GetValue<string>());
        Assert.Equal(1.167, Number(token["$value"]!), 3);
        AssertValid(doc);
    }

    [Fact]
    public void Dark_Override_Without_A_Base_Token_Is_Ignored()
    {
        DesignImportResult result = Import("tokens.css", ".dark { --missing: #ffffff; }");

        Assert.Null(result.Design!["modes"]);
    }

    [Fact]
    public void Layer_Base_Root_Is_Recognized()
    {
        DesignImportResult result = Import("tokens.css", "@layer base { :root { --brand: #6d28d9; } }");

        Assert.NotNull(DesignDocument.FindToken(result.Design!, "color.brand"));
        AssertValid(result.Design!);
    }

    [Fact]
    public void Non_Design_Variables_Are_Skipped()
    {
        DesignImportResult result = Import(
            "tokens.css",
            ":root { --z-index-foo: 10; --scrollbar-x: 4px; --viewport-w: 100vw; --safe-area-top: 2rem; }");

        Assert.Empty(DesignDocument.Tokens(result.Design!));
        AssertValid(result.Design!);
    }

    [Fact]
    public void Calc_And_Clamp_Are_Skipped_With_A_Warning()
    {
        DesignImportResult result = Import(
            "tokens.css",
            ":root { --x: calc(100% - 1rem); --y: clamp(1rem, 2vw, 3rem); }");

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNSUPPORTED_VALUE" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
        Assert.Empty(DesignDocument.Tokens(result.Design!));
    }

    [Fact]
    public void Var_Reference_Becomes_An_Alias_To_The_Produced_Token()
    {
        DesignImportResult result = Import("tokens.css", ":root { --brand: #6d28d9; --y: var(--brand); }");

        JsonObject? alias = DesignDocument.FindToken(result.Design!, "color.y");
        Assert.NotNull(alias);
        Assert.Equal("color", alias!["$type"]!.GetValue<string>());
        Assert.Equal("{color.brand}", alias["$value"]!.GetValue<string>());
        AssertValid(result.Design!);
    }

    [Fact]
    public void Tailwind_V4_Inline_Theme_Emits_Concrete_Values_And_No_Self_Alias()
    {
        // The production shape (showcase/src/index.css): `@theme inline` aliases every semantic
        // colour to the raw variable, and both names map to the same token path. Writing the alias
        // at its own target replaced the concrete value with `{color.background}`, which the
        // validator rejects as a cycle (DS_V12) and Compose fails closed on.
        DesignImportResult result = Import(Fixture("tailwind-v4-inline-theme.css"));

        JsonObject? background = DesignDocument.FindToken(result.Design!, "color.background");
        Assert.NotNull(background);
        Assert.Equal("#ffffff", background!["$value"]!["hex"]!.GetValue<string>());

        foreach (DesignTokenRef token in DesignDocument.Tokens(result.Design!))
        {
            if (token.Value is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out string? alias))
            {
                Assert.NotEqual("{" + token.Path + "}", alias);
            }
        }

        AssertValid(result.Design!);
    }

    [Fact]
    public void Genuine_Alias_To_A_Different_Token_Is_Still_Emitted()
    {
        DesignImportResult result = Import(
            "tokens.css",
            ":root { --background: #ffffff; --surface: var(--background); }");

        JsonObject? surface = DesignDocument.FindToken(result.Design!, "color.surface");
        Assert.NotNull(surface);
        Assert.Equal("{color.background}", surface!["$value"]!.GetValue<string>());
        AssertValid(result.Design!);
    }

    [Fact]
    public void Alias_Chain_Through_A_Dropped_Self_Alias_Still_Resolves()
    {
        DesignImportResult result = Import(
            "tokens.css",
            ":root { --background: #ffffff; --color-background: var(--background); --surface: var(--color-background); }");

        JsonObject? background = DesignDocument.FindToken(result.Design!, "color.background");
        Assert.NotNull(background);
        Assert.Equal("#ffffff", background!["$value"]!["hex"]!.GetValue<string>());

        JsonObject? surface = DesignDocument.FindToken(result.Design!, "color.surface");
        Assert.NotNull(surface);
        Assert.Equal("{color.background}", surface!["$value"]!.GetValue<string>());
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "UNRESOLVED_ALIAS");
        AssertValid(result.Design!);
    }

    [Fact]
    public void Multi_Path_Mapping_Drops_Only_The_Path_Equal_To_Its_Target()
    {
        // --foreground maps to both color.foreground and color.text; only the path equal to the
        // resolved target (color.foreground) is dropped, the other is still emitted as an alias.
        DesignImportResult result = Import(
            "tokens.css",
            ":root { --color-foreground: #111111; --foreground: var(--color-foreground); }");

        JsonObject? foreground = DesignDocument.FindToken(result.Design!, "color.foreground");
        Assert.NotNull(foreground);
        Assert.Equal("#111111", foreground!["$value"]!["hex"]!.GetValue<string>());

        JsonObject? text = DesignDocument.FindToken(result.Design!, "color.text");
        Assert.NotNull(text);
        Assert.Equal("{color.foreground}", text!["$value"]!.GetValue<string>());
        AssertValid(result.Design!);
    }

    [Fact]
    public void Dark_Self_Alias_Does_Not_Overwrite_The_Dark_Value()
    {
        DesignImportResult result = Import(
            "tokens.css",
            ":root { --background: oklch(1 0 0); } .dark { --background: #0b0b0f; --color-background: var(--background); }");

        JsonObject? dark = (result.Design!["modes"] as JsonObject)?["dark"] as JsonObject;
        Assert.NotNull(dark);
        Assert.Equal("#0b0b0f", dark!["color.background"]!["hex"]!.GetValue<string>());
        AssertValid(result.Design!);
    }

    [Fact]
    public void Unresolved_Var_Reference_Warns_And_Emits_Nothing()
    {
        DesignImportResult result = Import("tokens.css", ":root { --y: var(--missing); }");

        Assert.Null(DesignDocument.FindToken(result.Design!, "color.y"));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNRESOLVED_ALIAS" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
    }

    [Fact]
    public void Hostile_Css_Is_Ignored_Bounded_And_Fast()
    {
        string css = Fixture("hostile.css");

        DesignImportResult result = HostileInput.RunWithin(
            TimeSpan.FromSeconds(20),
            () => Importer.Import([new ImportFile("hostile.css", Encoding.UTF8.GetBytes(css), string.Empty, null)]));
        Assert.NotNull(DesignDocument.FindToken(result.Design!, "color.brand"));
        Assert.Null(DesignDocument.FindToken(result.Design!, "color.evil"));
        Assert.Null(DesignDocument.FindToken(result.Design!, "color.calc"));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNSUPPORTED_VALUE" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == ImportDiagnosticSeverity.Error);
        AssertValid(result.Design!);
    }

    [Fact]
    public void Forward_declared_alias_chain_of_60k_is_resolved_in_linear_time_and_capped()
    {
        var sb = new StringBuilder(":root{--a0:#ffffff;");
        for (int i = 1; i < 60_000; i++)
            sb.Append("--a").Append(i).Append(":var(--a").Append(i - 1).Append(");");

        sb.Append('}');

        DesignImportResult result = HostileInput.RunWithin(TimeSpan.FromSeconds(10), () => Import(sb.ToString()));

        int tokens = System.Linq.Enumerable.Count(DesignDocument.Tokens(result.Design!));
        Assert.Equal(1500, tokens);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOKEN_CAP");
    }

    [Fact]
    public void Short_alias_chain_declared_in_both_orders_still_resolves_every_link()
    {
        DesignImportResult result = Import(":root{--c3:var(--c2);--c2:var(--c1);--c1:var(--c0);--c0:#123456;}");

        Assert.Equal("{color.c0}", DesignDocument.FindToken(result.Design!, "color.c1")?["$value"]?.GetValue<string>());
        Assert.Equal("{color.c1}", DesignDocument.FindToken(result.Design!, "color.c2")?["$value"]?.GetValue<string>());
        Assert.Equal("{color.c2}", DesignDocument.FindToken(result.Design!, "color.c3")?["$value"]?.GetValue<string>());
    }

    [Fact]
    public void Hostile_css_of_200_kb_with_many_tokens_and_garbage_is_bounded_and_fast()
    {
        var sb = new StringBuilder(":root{");
        for (int i = 0; sb.Length < 120_000; i++)
            sb.Append("--color-t").Append(i).Append(":#6d28d9;--x").Append(i).Append(":calc(1px + ").Append(i).Append("px);");

        sb.Append('}');
        sb.Append(new string('{', 40_000));
        sb.Append(new string('}', 40_000));
        sb.Append("@import url(http://evil.example/x.css);");
        string css = sb.ToString();
        Assert.True(css.Length >= 200_000, "fixture is " + css.Length + " chars");

        DesignImportResult result = HostileInput.RunWithin(TimeSpan.FromSeconds(10), () => Import(css));

        Assert.True(System.Linq.Enumerable.Count(DesignDocument.Tokens(result.Design!)) <= 1500);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOKEN_CAP");
    }

    [Fact]
    public void CanRead_Accepts_Css_With_Custom_Properties_Or_Theme()
    {
        Assert.True(Importer.CanRead("globals.css", Encoding.UTF8.GetBytes(":root { --x: red; }")));
        Assert.True(Importer.CanRead("theme.css", Encoding.UTF8.GetBytes("@theme { --color-x: #000000; }")));
        Assert.False(Importer.CanRead("plain.css", Encoding.UTF8.GetBytes("body { color: red; }")));
        Assert.False(Importer.CanRead("tokens.json", Encoding.UTF8.GetBytes("--x")));
    }

    [Fact]
    public void Source_Digest_Is_Recomputed_And_Product_Map_Is_Null()
    {
        var file = new ImportFile("tokens.css", Encoding.UTF8.GetBytes(":root { --brand: #6d28d9; }"), "deadbeef", null);

        DesignImportResult result = Importer.Import([file]);

        ImportSourceRef source = Assert.Single(result.Sources);
        Assert.Equal("css-theme", source.Importer);
        Assert.Equal(64, source.Sha256.Length);
        Assert.NotEqual("deadbeef", source.Sha256);
        Assert.Null(result.ProductMap);
    }

    private static DesignImportResult Import(string css) => Import("tokens.css", css);

    private static DesignImportResult Import(string path, string css)
    {
        var file = new ImportFile(path, Encoding.UTF8.GetBytes(css), string.Empty, null);
        return Importer.Import([file]);
    }

    private static ImportFile Css(string path, string css) =>
        new(path, Encoding.UTF8.GetBytes(css), string.Empty, null);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDirectory(), name));

    private static string FixtureDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures");

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
            string.Join("; ", validation.Violations.Select(violation => $"{violation.Code} {violation.Path} {violation.Message}")));
    }

    private static double Number(JsonNode? node)
    {
        Assert.True(CanonicalJson.TryGetNumber(node, out double value), "expected a JSON number");
        return value;
    }

    private static double Confidence(JsonObject token)
    {
        JsonObject? extensions = token["$extensions"] as JsonObject;
        JsonObject? reelbolt = extensions?["com.reelbolt"] as JsonObject;
        Assert.NotNull(reelbolt);
        Assert.True(CanonicalJson.TryGetNumber(reelbolt!["conf"], out double confidence), "expected conf");
        return confidence;
    }

    private static string? Origin(JsonObject token)
    {
        if (token["$extensions"] is not JsonObject extensions)
            return null;
        if (extensions["com.reelbolt"] is not JsonObject reelbolt)
            return null;

        return reelbolt["orig"] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : null;
    }
}
