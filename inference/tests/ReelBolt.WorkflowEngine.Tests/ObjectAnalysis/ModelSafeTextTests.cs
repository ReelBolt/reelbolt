#nullable enable
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ObjectAnalysis;

public class ModelSafeTextTests
{
    [Theory]
    [InlineData("plain name.mp4", "plain name.mp4")]
    [InlineData("a” b “c “d", "a b c d")]
    [InlineData("say \"hi\" it's `x`", "say hi its x")]
    [InlineData("line\nbreak\tand   spaces", "line break and spaces")]
    [InlineData("rtl‮evil⁦x⁩​﻿", "rtlevilx")]
    [InlineData("nul\0byte\u0007bell", "nulbytebell")]
    [InlineData("  ", "")]
    public void Quotes_backticks_controls_and_bidi_characters_are_removed(string input, string expected) =>
        ModelSafeText.Clean(input).Should().Be(expected);

    [Fact]
    public void A_long_name_is_cut_and_null_is_empty()
    {
        ModelSafeText.Clean(new string('x', 200)).Should().HaveLength(81).And.EndWith("…");
        ModelSafeText.Clean(null).Should().BeEmpty();
        ModelSafeText.Clean(new string('x', 200), int.MaxValue).Should().HaveLength(200);
    }
}
