using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public class CanonicalJsonTests
{
    [Fact]
    public void Serialize_SortsObjectKeysRecursively()
    {
        JsonNode? node = JsonNode.Parse("{\"b\":1,\"a\":{\"d\":2,\"c\":[3,1]}}");

        CanonicalJson.Serialize(node).Should().Be("{\"a\":{\"c\":[3,1],\"d\":2},\"b\":1}");
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1.0", "1")]
    [InlineData("1.5", "1.5")]
    [InlineData("-0", "0")]
    [InlineData("-0.0", "0")]
    [InlineData("1e3", "1000")]
    [InlineData("0.1", "0.1")]
    public void Serialize_FormatsNumbersCanonically(string json, string expected)
    {
        CanonicalJson.Serialize(JsonNode.Parse(json)).Should().Be(expected);
    }

    [Fact]
    public void Serialize_IntBackedNodeIsANumber()
    {
        CanonicalJson.Serialize(JsonValue.Create(5)).Should().Be("5");
    }

    [Fact]
    public void Serialize_DoesNotEscapeHtmlOrNonAscii()
    {
        CanonicalJson.Serialize(JsonValue.Create("a<b>")).Should().Be("\"a<b>\"");
        CanonicalJson.Serialize(JsonValue.Create("é")).Should().Be("\"é\"");
    }

    [Fact]
    public void Serialize_EscapesObjectKeysToo()
    {
        var node = new JsonObject
        {
            ["a<b"] = 1,
        };

        CanonicalJson.Serialize(node).Should().Be("{\"a<b\":1}");
    }

    [Fact]
    public void Serialize_NullNodeIsJsonNull()
    {
        CanonicalJson.Serialize(null).Should().Be("null");
    }

    [Theory]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void Sha256Hex_MatchesKnownVectors(string input, string expected)
    {
        CanonicalJson.Sha256Hex(input).Should().Be(expected);
    }

    [Fact]
    public void Digest_MatchesKnownVectors()
    {
        CanonicalJson.Digest(JsonNode.Parse("{\"a\":1}")!)
            .Should().Be("015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862");

        CanonicalJson.Digest(JsonNode.Parse("{\"b\":\"x\",\"a\":[1,2]}")!)
            .Should().Be("d0f56dda38d34376527524ddd98f7ec117d5cbfb240f86e11bfb9d2d01a717d9");
    }

    [Fact]
    public void Digest_IgnoresKeyOrderButKeepsArrayOrder()
    {
        CanonicalJson.Digest(JsonNode.Parse("{\"b\":\"x\",\"a\":[1,2]}")!)
            .Should().Be(CanonicalJson.Digest(JsonNode.Parse("{\"a\":[1,2],\"b\":\"x\"}")!));

        CanonicalJson.Digest(JsonNode.Parse("{\"a\":[1,2]}")!)
            .Should().NotBe(CanonicalJson.Digest(JsonNode.Parse("{\"a\":[2,1]}")!));
    }

    [Fact]
    public void TryGetNumber_AcceptsNumbersAndRejectsEverythingElse()
    {
        CanonicalJson.TryGetNumber(JsonNode.Parse("5"), out double five).Should().BeTrue();
        five.Should().Be(5.0);

        CanonicalJson.TryGetNumber(JsonNode.Parse("1.5"), out double oneAndAHalf).Should().BeTrue();
        oneAndAHalf.Should().Be(1.5);

        CanonicalJson.TryGetNumber(JsonNode.Parse("\"5\""), out _).Should().BeFalse();
        CanonicalJson.TryGetNumber(JsonNode.Parse("true"), out _).Should().BeFalse();
        CanonicalJson.TryGetNumber(null, out _).Should().BeFalse();
    }
}
