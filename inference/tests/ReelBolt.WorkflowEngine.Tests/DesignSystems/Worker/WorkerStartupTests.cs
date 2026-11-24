using System;
using AwesomeAssertions;
using ReelBolt.ImportWorker;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

public class WorkerStartupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("  \n  ")]
    public void RequireToken_throws_for_null_empty_whitespace(string? value)
    {
        Action act = () => WorkerStartup.RequireToken(value);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RequireToken_returns_value()
        => WorkerStartup.RequireToken("a-real-token").Should().Be("a-real-token");

    [Fact]
    public void Options_default_to_two_slots_and_fifty_pages()
    {
        WorkerOptions options = WorkerStartup.OptionsFromEnvironment("tok", _ => null);

        options.MaxConcurrency.Should().Be(2);
        options.PdfMaxPages.Should().Be(50);
        options.Token.Should().Be("tok");
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData("16", 16)]
    [InlineData("0", 2)]
    [InlineData("17", 2)]
    [InlineData("-1", 2)]
    [InlineData("many", 2)]
    [InlineData("", 2)]
    public void Concurrency_is_configurable_within_bounds_and_never_disabled(string raw, int expected)
        => WorkerStartup.OptionsFromEnvironment("tok", name => name == "IMPORT_WORKER_MAX_CONCURRENCY" ? raw : null)
            .MaxConcurrency.Should().Be(expected);

    [Fact]
    public void Options_ToString_never_prints_the_token()
    {
        WorkerOptions options = new("super-secret-bearer-value");

        string text = options.ToString();

        text.Should().NotContain("super-secret-bearer-value");
        text.Should().Contain("MaxConcurrency = 2");
        $"{options}".Should().NotContain("super-secret-bearer-value");
    }
}
