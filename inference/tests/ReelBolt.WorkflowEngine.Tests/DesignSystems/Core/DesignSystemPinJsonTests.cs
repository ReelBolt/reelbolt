using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using System;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

/// <summary>
/// The pin's JSON codec (SPEC 3.5): camelCase, compact, null fields omitted, tolerant parsing.
/// </summary>
public class DesignSystemPinJsonTests
{
    [Fact]
    public void Serialize_round_trips_through_TryParse()
    {
        DesignSystemPin pin = new(
            1,
            new DesignSystemPinMember(Guid.NewGuid(), 7, new string('a', 64)),
            new DesignSystemPinMember(Guid.NewGuid(), 3, new string('b', 64)));

        DesignSystemPin? parsed = DesignSystemPinJson.TryParse(DesignSystemPinJson.Serialize(pin));

        parsed.Should().NotBeNull();
        parsed!.Should().Be(pin);
        parsed.V.Should().Be(1);
        parsed.Design!.VersionId.Should().Be(pin.Design!.VersionId);
        parsed.Design.VersionNumber.Should().Be(7);
        parsed.Design.Digest.Should().Be(pin.Design.Digest);
        parsed.ProductMap!.VersionId.Should().Be(pin.ProductMap!.VersionId);
        parsed.ProductMap.VersionNumber.Should().Be(3);
        parsed.ProductMap.Digest.Should().Be(pin.ProductMap.Digest);
    }

    [Fact]
    public void Serialize_uses_camelCase_and_omits_null_members()
    {
        DesignSystemPin pin = new(
            1,
            new DesignSystemPinMember(Guid.NewGuid(), 7, new string('a', 64)),
            new DesignSystemPinMember(Guid.NewGuid(), 3, new string('b', 64)));

        string json = DesignSystemPinJson.Serialize(pin);

        json.Should().Contain("\"v\":1");
        json.Should().Contain("\"versionId\"");
        json.Should().Contain("\"versionNumber\"");
        json.Should().Contain("\"digest\"");
        json.Should().Contain("\"design\"");
        json.Should().Contain("\"productMap\"");
        json.Should().NotContain("LibraryVersionId");
        json.Should().NotContain("libraryVersionId");
        json.Should().NotContain("source");
        json.Should().NotContain(" ");
        json.Should().NotContain("\n");
    }

    [Fact]
    public void Serialize_single_member_omits_the_other()
    {
        DesignSystemPin pin = new(
            1,
            new DesignSystemPinMember(Guid.NewGuid(), 7, new string('a', 64)),
            null);

        string json = DesignSystemPinJson.Serialize(pin);

        json.Should().Contain("\"design\"");
        json.Should().NotContain("productMap");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    public void TryParse_null_or_garbage_returns_null(string? json)
    {
        DesignSystemPinJson.TryParse(json).Should().BeNull();
    }
}
