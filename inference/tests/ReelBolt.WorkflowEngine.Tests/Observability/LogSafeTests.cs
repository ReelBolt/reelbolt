using AwesomeAssertions;
using ReelBolt.Shared.Observability;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Observability;

public sealed class LogSafeTests
{
    [Fact]
    public void Value_Null_StaysNull() => LogSafe.Value(null).Should().BeNull();

    [Fact]
    public void Value_PlainText_IsUnchanged() => LogSafe.Value("acme/repo@main").Should().Be("acme/repo@main");

    [Theory]
    [InlineData("a\r\nINFO forged", "aINFO forged")]
    [InlineData("a\nb\rc", "abc")]
    [InlineData("a\u2028b\u2029c\u202Ed", "abcd")]
    [InlineData("tab\there\u001b[31m\u0000", "tabhere[31m")]
    public void Value_RemovesLineBreaksAndControlCharacters(string input, string expected) =>
        LogSafe.Value(input).Should().Be(expected);

    [Fact]
    public void Value_LongInput_IsBounded()
    {
        string result = LogSafe.Value(new string('x', LogSafe.MaxLength * 4))!;

        result.Length.Should().Be(LogSafe.MaxLength + 1);
        result.Should().EndWith("…");
    }

    [Fact]
    public void ForLog_MatchesValue() => "x\ny".ForLog().Should().Be("xy");
}
