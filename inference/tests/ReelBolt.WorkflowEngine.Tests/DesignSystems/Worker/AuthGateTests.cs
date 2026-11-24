using AwesomeAssertions;
using ReelBolt.ImportWorker;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

public class AuthGateTests
{
    private const string Token = "correct-horse-battery-staple";

    [Fact]
    public void Valid_bearer_accepted()
        => AuthGate.IsAuthorized($"Bearer {Token}", Token).Should().BeTrue();

    [Fact]
    public void Scheme_is_case_insensitive()
    {
        AuthGate.IsAuthorized($"bearer {Token}", Token).Should().BeTrue();
        AuthGate.IsAuthorized($"BEARER {Token}", Token).Should().BeTrue();
        AuthGate.IsAuthorized($"BeArEr {Token}", Token).Should().BeTrue();
    }

    [Fact]
    public void Missing_header_rejected()
    {
        AuthGate.IsAuthorized(null, Token).Should().BeFalse();
        AuthGate.IsAuthorized(string.Empty, Token).Should().BeFalse();
    }

    [Fact]
    public void Wrong_token_rejected()
        => AuthGate.IsAuthorized("Bearer not-the-token", Token).Should().BeFalse();

    [Fact]
    public void Basic_scheme_rejected()
        => AuthGate.IsAuthorized($"Basic {Token}", Token).Should().BeFalse();

    [Fact]
    public void Empty_expected_token_rejects_everything()
    {
        AuthGate.IsAuthorized("Bearer ", string.Empty).Should().BeFalse();
        AuthGate.IsAuthorized(null, string.Empty).Should().BeFalse();
        AuthGate.IsAuthorized("Bearer anything", string.Empty).Should().BeFalse();
    }

    [Theory]
    [InlineData("correct-horse-battery-stapl")]
    [InlineData("correct-horse-battery-staplf")]
    [InlineData("Correct-horse-battery-staple")]
    [InlineData("xorrect-horse-battery-staple")]
    [InlineData("correct-horse-battery-staple-")]
    [InlineData("c")]
    public void Tokens_differing_in_length_prefix_or_last_character_are_all_rejected(string presented)
        => AuthGate.IsAuthorized($"Bearer {presented}", Token).Should().BeFalse();

    [Fact]
    public void Non_ascii_tokens_compare_exactly()
    {
        AuthGate.IsAuthorized("Bearer s\u00e9cret-\u2603", "s\u00e9cret-\u2603").Should().BeTrue();
        AuthGate.IsAuthorized("Bearer secret-\u2603", "s\u00e9cret-\u2603").Should().BeFalse();
    }

    [Fact]
    public void Surrounding_whitespace_after_the_scheme_is_trimmed_but_inner_whitespace_is_not()
    {
        AuthGate.IsAuthorized($"Bearer   {Token}  ", Token).Should().BeTrue();
        AuthGate.IsAuthorized("Bearer correct-horse battery-staple", Token).Should().BeFalse();
    }
}
