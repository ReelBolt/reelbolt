using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

/// <summary>
/// Alias resolution and its rule: a value may be <c>"{a.b}"</c> for any type, the target must exist
/// with the same type, and a chain may not cycle or run deeper than eight hops.
/// </summary>
public class DesignDocumentAliasTests
{
    private static readonly DesignValidationContext Context =
        new(_ => false, new HashSet<string>(StringComparer.Ordinal));

    private static JsonNode ColorValue(string hex)
    {
        var value = new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" };
        return value;
    }

    private static JsonObject Token(string type, JsonNode? value) => new()
    {
        ["$type"] = type,
        ["$value"] = value?.DeepClone(),
    };

    private static JsonObject Root() => new() { ["schema"] = DesignDocument.Schema };

    private static JsonObject Chain(int aliasCount)
    {
        JsonObject root = Root();
        var color = new JsonObject();
        root["color"] = color;

        for (int i = 0; i < aliasCount; i++)
            color[$"t{i}"] = Token("color", JsonValue.Create($"{{color.t{i + 1}}}"));

        color[$"t{aliasCount}"] = Token("color", ColorValue("#2563eb"));
        return root;
    }

    [Fact]
    public void ValidAlias_Resolves()
    {
        JsonObject root = Root();
        var color = new JsonObject
        {
            ["brand"] = new JsonObject { ["primary"] = Token("color", ColorValue("#2563eb")) },
            ["alias"] = Token("color", JsonValue.Create("{color.brand.primary}")),
        };
        root["color"] = color;

        DesignDocument.ResolveAlias(root, "color.alias", out JsonNode? value).Should().BeTrue();
        value.Should().NotBeNull();
        value!["hex"]!.GetValue<string>().Should().Be("#2563eb");
    }

    [Fact]
    public void MissingTarget_ReturnsFalse()
    {
        JsonObject root = Root();
        root["color"] = new JsonObject { ["alias"] = Token("color", JsonValue.Create("{color.nope}")) };

        DesignDocument.ResolveAlias(root, "color.alias", out JsonNode? value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void TypeMismatch_IsFlaggedAsV12()
    {
        JsonObject root = Root();
        root["color"] = new JsonObject { ["alias"] = Token("color", JsonValue.Create("{spacing.md}")) };
        root["spacing"] = new JsonObject
        {
            ["md"] = Token("dimension", new JsonObject { ["value"] = 16, ["unit"] = "px" }),
        };

        DesignValidationResult result = DesignDocumentValidator.Validate(root, Context);
        result.Violations.Should().Contain(violation => violation.Code == "DS_V12");
    }

    [Fact]
    public void Cycle_ReturnsFalse()
    {
        JsonObject root = Root();
        var color = new JsonObject
        {
            ["a"] = Token("color", JsonValue.Create("{color.b}")),
            ["b"] = Token("color", JsonValue.Create("{color.a}")),
        };
        root["color"] = color;

        DesignDocument.ResolveAlias(root, "color.a", out JsonNode? value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void DepthEightResolves_AndDepthNineReturnsFalse()
    {
        JsonObject depthEight = Chain(8);
        DesignDocument.ResolveAlias(depthEight, "color.t0", out JsonNode? value).Should().BeTrue();
        value.Should().NotBeNull();

        JsonObject depthNine = Chain(9);
        DesignDocument.ResolveAlias(depthNine, "color.t0", out JsonNode? tooDeep).Should().BeFalse();
        tooDeep.Should().BeNull();
    }
}
