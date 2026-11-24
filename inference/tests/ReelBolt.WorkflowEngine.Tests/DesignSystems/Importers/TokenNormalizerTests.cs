using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class TokenNormalizerTests
{
    [Fact]
    public void Dimension_16px_Becomes_Px_Object()
    {
        JsonNode? value = TokenNormalizer.NormalizeDimension(JsonValue.Create("16px"), out ImportDiagnostic? diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(value);
        Assert.Equal(16, Number(value!["value"]));
        Assert.Equal("px", value!["unit"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("px", 16)]
    [InlineData("rem", 1.5)]
    [InlineData("em", 2)]
    public void Dimension_Value_Unit_Object_Round_Trips(string unit, double number)
    {
        var raw = new JsonObject { ["value"] = number, ["unit"] = unit };

        JsonNode? value = TokenNormalizer.NormalizeDimension(raw, out ImportDiagnostic? diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(value);
        Assert.Equal(number, Number(value!["value"]));
        Assert.Equal(unit, value!["unit"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"value\":16,\"unit\":\"pt\"}")]
    [InlineData("{\"value\":\"x\",\"unit\":\"px\"}")]
    [InlineData("{\"value\":16}")]
    [InlineData("{\"value\":20000,\"unit\":\"px\"}")]
    public void Dimension_Malformed_Object_Is_Rejected_With_A_Diagnostic(string json)
    {
        JsonNode? value = TokenNormalizer.NormalizeDimension(JsonNode.Parse(json), out ImportDiagnostic? diagnostic);

        Assert.Null(value);
        Assert.Equal("DIMENSION_INVALID", diagnostic!.Code);
    }

    [Theory]
    [InlineData("ms", 200)]
    [InlineData("s", 0.2)]
    public void Duration_Value_Unit_Object_Round_Trips(string unit, double number)
    {
        var raw = new JsonObject { ["value"] = number, ["unit"] = unit };

        JsonNode? value = TokenNormalizer.NormalizeDuration(raw, out ImportDiagnostic? diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(value);
        Assert.Equal(number, Number(value!["value"]));
        Assert.Equal(unit, value!["unit"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"value\":2,\"unit\":\"min\"}")]
    [InlineData("{\"value\":61,\"unit\":\"s\"}")]
    [InlineData("{\"value\":-1,\"unit\":\"ms\"}")]
    public void Duration_Malformed_Object_Is_Rejected_With_A_Diagnostic(string json)
    {
        JsonNode? value = TokenNormalizer.NormalizeDuration(JsonNode.Parse(json), out ImportDiagnostic? diagnostic);

        Assert.Null(value);
        Assert.Equal("DURATION_INVALID", diagnostic!.Code);
    }

    [Fact]
    public void Dimension_1rem_Keeps_Rem_Unit()
    {
        JsonNode? value = TokenNormalizer.NormalizeDimension(JsonValue.Create("1rem"), out _);

        Assert.NotNull(value);
        Assert.Equal(1, Number(value!["value"]));
        Assert.Equal("rem", value!["unit"]!.GetValue<string>());
    }

    [Fact]
    public void Dimension_Bare_Number_Defaults_To_Px()
    {
        JsonNode? value = TokenNormalizer.NormalizeDimension(JsonValue.Create(5), out _);

        Assert.NotNull(value);
        Assert.Equal(5, Number(value!["value"]));
        Assert.Equal("px", value!["unit"]!.GetValue<string>());
    }

    [Fact]
    public void Dimension_Non_Numeric_String_Is_Null()
    {
        JsonNode? value = TokenNormalizer.NormalizeDimension(JsonValue.Create("abc"), out ImportDiagnostic? diagnostic);

        Assert.Null(value);
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void Dimension_Above_Limit_Is_Null()
    {
        Assert.Null(TokenNormalizer.NormalizeDimension(JsonValue.Create("10001px"), out _));
    }

    [Fact]
    public void Dimension_Non_Finite_Is_Null()
    {
        JsonNode? raw = JsonNode.Parse("1e999");
        Assert.Null(TokenNormalizer.NormalizeDimension(raw, out _));
    }

    [Fact]
    public void FontFamily_Single_String_Becomes_Array()
    {
        JsonNode? value = TokenNormalizer.NormalizeFontFamily(JsonValue.Create("Inter"), out _);

        var array = Assert.IsType<JsonArray>(value);
        Assert.Single(array);
        Assert.Equal("Inter", array[0]!.GetValue<string>());
    }

    [Fact]
    public void FontFamily_Comma_Separated_String_Is_Split()
    {
        JsonNode? value = TokenNormalizer.NormalizeFontFamily(JsonValue.Create("A, B"), out _);

        var array = Assert.IsType<JsonArray>(value);
        Assert.Equal(2, array.Count);
        Assert.Equal("A", array[0]!.GetValue<string>());
        Assert.Equal("B", array[1]!.GetValue<string>());
    }

    [Fact]
    public void FontFamily_Six_Entries_Is_Null()
    {
        var array = new JsonArray("a", "b", "c", "d", "e", "f");
        Assert.Null(TokenNormalizer.NormalizeFontFamily(array, out _));
    }

    [Theory]
    [InlineData("normal", 400)]
    [InlineData("bold", 700)]
    [InlineData("lighter", 300)]
    [InlineData("bolder", 800)]
    public void FontWeight_Keywords_Map(string keyword, int expected)
    {
        JsonNode? value = TokenNormalizer.NormalizeFontWeight(JsonValue.Create(keyword), out _);

        Assert.NotNull(value);
        Assert.Equal(expected, value!.GetValue<int>());
    }

    [Fact]
    public void FontWeight_Numeric_Is_Kept()
    {
        JsonNode? value = TokenNormalizer.NormalizeFontWeight(JsonValue.Create(700), out _);

        Assert.NotNull(value);
        Assert.Equal(700, value!.GetValue<int>());
    }

    [Fact]
    public void Duration_200ms_Becomes_Milliseconds()
    {
        JsonNode? value = TokenNormalizer.NormalizeDuration(JsonValue.Create("200ms"), out _);

        Assert.NotNull(value);
        Assert.Equal(200, Number(value!["value"]));
        Assert.Equal("ms", value!["unit"]!.GetValue<string>());
    }

    [Fact]
    public void Duration_0_2s_Becomes_Seconds()
    {
        JsonNode? value = TokenNormalizer.NormalizeDuration(JsonValue.Create("0.2s"), out _);

        Assert.NotNull(value);
        Assert.Equal(0.2, Number(value!["value"]));
        Assert.Equal("s", value!["unit"]!.GetValue<string>());
    }

    [Fact]
    public void Duration_Above_Limit_Is_Null()
    {
        Assert.Null(TokenNormalizer.NormalizeDuration(JsonValue.Create("61s"), out _));
    }

    [Fact]
    public void CubicBezier_Four_Numbers_Are_Kept()
    {
        JsonNode? value = TokenNormalizer.NormalizeCubicBezier(new JsonArray(0, 0, 1, 1), out _);

        var array = Assert.IsType<JsonArray>(value);
        Assert.Equal(4, array.Count);
    }

    [Fact]
    public void CubicBezier_X1_Out_Of_Range_Is_Null()
    {
        Assert.Null(TokenNormalizer.NormalizeCubicBezier(new JsonArray(2, 0, 1, 1), out _));
    }

    [Fact]
    public void Shadow_Object_Is_Normalized()
    {
        var shadow = new JsonObject
        {
            ["color"] = "#000000",
            ["offsetX"] = "0px",
            ["offsetY"] = "1px",
            ["blur"] = "2px",
            ["spread"] = "0px",
        };

        JsonNode? value = TokenNormalizer.NormalizeShadow(shadow, out ImportDiagnostic? diagnostic);

        Assert.Null(diagnostic);
        var entry = Assert.IsType<JsonObject>(value);
        Assert.Equal("#000000", entry["color"]!["hex"]!.GetValue<string>());
        Assert.Equal("srgb", entry["color"]!["colorSpace"]!.GetValue<string>());
        Assert.Equal(1, Number(entry["offsetY"]!["value"]));
        Assert.Equal("px", entry["offsetY"]!["unit"]!.GetValue<string>());
    }

    [Fact]
    public void Shadow_Array_Of_Five_Is_Null()
    {
        var entry = new JsonObject { ["color"] = "#000000" };
        var array = new JsonArray(entry, entry.DeepClone(), entry.DeepClone(), entry.DeepClone(), entry.DeepClone());

        Assert.Null(TokenNormalizer.NormalizeShadow(array, out _));
    }

    [Fact]
    public void Shadow_Css_String_Is_Parsed_Best_Effort()
    {
        JsonNode? value = TokenNormalizer.NormalizeShadow(JsonValue.Create("0 1px 2px rgba(0,0,0,0.5)"), out _);

        var entry = Assert.IsType<JsonObject>(value);
        Assert.Equal("#00000080", entry["color"]!["hex"]!.GetValue<string>());
        Assert.Equal(0, Number(entry["offsetX"]!["value"]));
        Assert.Equal(1, Number(entry["offsetY"]!["value"]));
        Assert.Equal(2, Number(entry["blur"]!["value"]));
    }

    [Fact]
    public void Shadow_Unparseable_Css_Is_Null()
    {
        Assert.Null(TokenNormalizer.NormalizeShadow(JsonValue.Create("not a shadow"), out ImportDiagnostic? diagnostic));
        Assert.NotNull(diagnostic);
    }

    private static double Number(JsonNode? node)
    {
        Assert.True(CanonicalJson.TryGetNumber(node, out double value), "expected a JSON number");
        return value;
    }
}
