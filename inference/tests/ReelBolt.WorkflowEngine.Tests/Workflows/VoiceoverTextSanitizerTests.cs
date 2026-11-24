using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// Tests for <see cref="VoiceoverTextSanitizer"/>, which strips Fish Audio emotion tags,
/// control characters, and URLs from LLM-authored voiceover text before synthesis.
/// </summary>
public class VoiceoverTextSanitizerTests
{
    /// <summary>
    /// The exact value the raw-input cap is expected to have. Deliberately a literal in the test
    /// rather than a compile-time reference to <c>VoiceoverTextSanitizer.HardMaxRawChars</c>: this
    /// test file must still COMPILE against the pre-ticket sanitizer, so the RED proof for this
    /// ticket produces real failing assertions instead of a CS0117 compile error. The public const
    /// itself (its existence, that it is a <c>const</c>, and this exact value) is asserted by
    /// reflection in <see cref="Every_static_Regex_field_has_a_finite_match_timeout"/>.
    /// </summary>
    private const int ExpectedHardMaxRawChars = 4096;

    private readonly ITestOutputHelper _output;

    public VoiceoverTextSanitizerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Sanitize_Strips_square_bracket_emotion_tags()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("[whispering] This is a secret.", 200);

        // Assert
        result.Should().NotContain("[");
        result.Should().NotContain("]");
        result.Should().Contain("This is a secret");
    }

    [Fact]
    public void Sanitize_Strips_parenthesis_emotion_tags()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("(excited) This is great!", 200);

        // Assert
        result.Should().NotContain("(");
        result.Should().NotContain(")");
        result.Should().Contain("This is great");
    }

    [Fact]
    public void Sanitize_Strips_both_bracket_types()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize(
            "[whispering] Hello (excited) world",
            200);

        // Assert
        result.Should().NotContain("[");
        result.Should().NotContain("]");
        result.Should().NotContain("(");
        result.Should().NotContain(")");
        result.Should().Contain("Hello");
        result.Should().Contain("world");
    }

    [Fact]
    public void Sanitize_Strips_URLs()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize(
            "Visit https://example.com for more information.",
            200);

        // Assert
        result.Should().NotContain("https://");
        result.Should().NotContain("example.com");
        result.Should().Contain("Visit");
        result.Should().Contain("for more information");
    }

    [Fact]
    public void Sanitize_Strips_http_URLs()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize(
            "Check out http://test.org now!",
            200);

        // Assert
        result.Should().NotContain("http://");
        result.Should().Contain("Check out");
        result.Should().Contain("now");
    }

    [Fact]
    public void Sanitize_Strips_control_characters()
    {
        // Arrange: Create a string with a control character (e.g., null, tab in certain contexts)
        string input = "Hello\u0000World";  // null character

        // Act
        string result = VoiceoverTextSanitizer.Sanitize(input, 200);

        // Assert
        result.Should().NotContain("\u0000");
    }

    [Fact]
    public void Sanitize_Collapses_multiple_spaces()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("Hello     world", 200);

        // Assert
        result.Should().Be("Hello world");
    }

    [Fact]
    public void Sanitize_Truncates_to_maxLength()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize(
            "This is a very long sentence that should be truncated",
            15);

        // Assert
        result.Length.Should().BeLessThanOrEqualTo(15);
    }

    [Fact]
    public void Sanitize_Handles_mixed_content()
    {
        // Arrange: Mix of emotion tags, URLs, and regular text
        string input = "[whispering] Check https://example.com (excited) for details!";

        // Act
        string result = VoiceoverTextSanitizer.Sanitize(input, 200);

        // Assert
        result.Should().NotContain("[");
        result.Should().NotContain("]");
        result.Should().NotContain("(");
        result.Should().NotContain(")");
        result.Should().NotContain("https://");
        result.Should().Contain("Check");
        result.Should().Contain("for details");
    }

    [Fact]
    public void Sanitize_Trims_leading_and_trailing_whitespace()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("   Hello world   ", 200);

        // Assert
        result.Should().Be("Hello world");
    }

    [Fact]
    public void Sanitize_Returns_empty_string_when_input_is_null()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize(null, 200);

        // Assert
        result.Should().Be(string.Empty);
    }

    [Fact]
    public void Sanitize_Returns_empty_string_when_input_is_empty()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize(string.Empty, 200);

        // Assert
        result.Should().Be(string.Empty);
    }

    [Fact]
    public void Sanitize_Returns_empty_string_when_input_is_whitespace_only()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("   \n\t  ", 200);

        // Assert
        result.Should().Be(string.Empty);
    }

    // ---------------------------------------------------------------------------------------------
    // Adversarial cases from the security verdict for this branch (commit ee16a576).
    //
    // Bypass class 1: `ControlCharPattern` used to run AFTER both bracket patterns. `.` does not
    // match `\n`, so `[whis\npering]` survived steps 1-2 intact and step 3 then DELETED the newline,
    // reconstituting the literal tag `[whispering]` in the string handed to Fish Audio. The tests
    // below assert the exact sanitized output, so a reordering regression cannot pass them.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Sanitize_Strips_square_bracket_tag_whose_name_contains_a_newline()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("[whis\npering] Hello.", 200);

        // Assert
        result.Should().Be("Hello.");
        result.Should().NotContain("whispering");
    }

    [Fact]
    public void Sanitize_Strips_square_bracket_tag_delimited_by_newlines()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("[\nwhispering\n] Hello.", 200);

        // Assert
        result.Should().Be("Hello.");
        result.Should().NotContain("whispering");
    }

    [Fact]
    public void Sanitize_Strips_parenthesis_tag_whose_name_contains_a_newline()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("(whis\npering) Hello.", 200);

        // Assert
        result.Should().Be("Hello.");
        result.Should().NotContain("whispering");
    }

    [Fact]
    public void Sanitize_Strips_parenthesis_tag_delimited_by_newlines()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("(\nwhispering\n) Hello.", 200);

        // Assert
        result.Should().Be("Hello.");
        result.Should().NotContain("whispering");
    }

    // ---------------------------------------------------------------------------------------------
    // Bypass class 2: only `\p{Cc}` was stripped, so every `\p{Cf}` format character passed through
    // verbatim into the TTS request. Each code point gets its own case on purpose — the point is
    // per-character coverage, not one representative sample.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData('\u200B')] // ZERO WIDTH SPACE
    [InlineData('\u202E')] // RIGHT-TO-LEFT OVERRIDE
    [InlineData('\uFEFF')] // ZERO WIDTH NO-BREAK SPACE / BOM
    [InlineData('\u00AD')] // SOFT HYPHEN
    [InlineData('\u2066')] // LEFT-TO-RIGHT ISOLATE
    [InlineData('\u2069')] // POP DIRECTIONAL ISOLATE
    public void Sanitize_Strips_format_characters_Cf(char formatChar)
    {
        // Arrange
        string input = $"Hello{formatChar}World";

        // Act
        string result = VoiceoverTextSanitizer.Sanitize(input, 200);

        // Assert
        result.Should().Be("HelloWorld", $"U+{(int)formatChar:X4} is Cf and must be stripped");
        result.Should().NotContain(formatChar.ToString());
    }

    [Fact]
    public void Sanitize_Still_strips_every_Cc_control_character()
    {
        // U+0000..U+001F and U+007F — the coverage the pre-ticket pattern already had and which the
        // widened Cc+Cf class must not lose.
        for (int codePoint = 0x00; codePoint <= 0x1F; codePoint++)
        {
            char control = (char)codePoint;
            string result = VoiceoverTextSanitizer.Sanitize($"Hello{control}World", 200);

            result.Should().Be("HelloWorld", $"U+{codePoint:X4} is Cc and must be stripped");
        }

        string withDel = VoiceoverTextSanitizer.Sanitize("Hello\u007FWorld", 200);
        withDel.Should().Be("HelloWorld", "U+007F is Cc and must be stripped");
    }

    // ---------------------------------------------------------------------------------------------
    // Bypass class 3: neither regex had a match timeout and `Sanitize` was handed
    // `maxLength: int.MaxValue` by `VoiceoverStepExecutor`, truncating only AFTER every regex had
    // run. `Sanitize` on a 1.28M-character line of `[` measured ~26 s of CPU, on the engine shared
    // by every tenant. These two tests pin the cap AND the wall-clock ceiling.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Sanitize_Caps_raw_input_before_any_regex_runs()
    {
        // Arrange: 100k ordinary characters — no tags, no control chars, nothing to strip. Whatever
        // comes out can only be shorter because of the raw-input cap.
        string input = new('a', 100_000);

        // Act
        string result = VoiceoverTextSanitizer.Sanitize(input, int.MaxValue);

        // Assert
        result.Length.Should().Be(ExpectedHardMaxRawChars);
    }

    [Fact]
    public void Sanitize_Completes_within_two_seconds_on_1_28M_square_brackets()
    {
        // Arrange: the pathological input the verdict measured at ~26 s. There is no `]` anywhere,
        // so the non-greedy `\[.*?\]` fails from every start position — quadratic if it is ever
        // allowed to see the whole string.
        string input = new('[', 1_280_000);

        // Act
        Stopwatch stopwatch = Stopwatch.StartNew();
        string result = VoiceoverTextSanitizer.Sanitize(input, int.MaxValue);
        stopwatch.Stop();

        // Assert
        _output.WriteLine(
            $"Sanitize({input.Length:N0} '[') = {stopwatch.Elapsed.TotalMilliseconds:F1} ms "
            + $"(HardMaxRawChars={ExpectedHardMaxRawChars:N0})");

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2),
            "the raw-input cap must keep the super-linear bracket patterns off an unbounded string");
        result.Length.Should().BeLessThanOrEqualTo(ExpectedHardMaxRawChars);
    }

    // ---------------------------------------------------------------------------------------------
    // Timeout guard: a Regex constructed without a match timeout silently gets
    // Regex.InfiniteMatchTimeout back. This reflection test fails the suite if any static Regex on
    // the sanitizer loses its finite timeout (or a new one is added without one). It also asserts
    // the raw-input cap exists as a public const with the pinned value — asserted by reflection
    // (rather than by compiling against it) so this file still compiles against the pre-ticket
    // sanitizer and the RED proof shows real failures instead of a compile error.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_static_Regex_field_has_a_finite_match_timeout()
    {
        // Arrange
        List<FieldInfo> regexFields = [.. typeof(VoiceoverTextSanitizer)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => typeof(Regex).IsAssignableFrom(field.FieldType))];

        // Assert: if this ever hits zero the loop below would vacuously pass, so the field set is
        // itself asserted non-empty.
        regexFields.Should().NotBeEmpty("VoiceoverTextSanitizer is regex-based");

        foreach (FieldInfo field in regexFields)
        {
            Regex regex = (Regex)field.GetValue(null)!;

            regex.MatchTimeout.Should().NotBe(
                Regex.InfiniteMatchTimeout,
                $"{field.Name} must be constructed with a finite match timeout");
            regex.MatchTimeout.Should().BeGreaterThan(
                TimeSpan.Zero,
                $"{field.Name} must have a positive match timeout");
        }

        FieldInfo? capField = typeof(VoiceoverTextSanitizer)
            .GetField("HardMaxRawChars", BindingFlags.Public | BindingFlags.Static);

        capField.Should().NotBeNull("the raw-input cap must be a public constant");
        capField!.IsLiteral.Should().BeTrue("HardMaxRawChars must be a const, usable at call sites");
        capField.GetRawConstantValue().Should().Be(ExpectedHardMaxRawChars);
    }

    // ---------------------------------------------------------------------------------------------
    // Bypass class 4 (info-level in the verdict): nested/unbalanced brackets. `\[.*?\]` is
    // non-greedy, so `[a[b]c]` matches `[a[b]` and leaves the trailing `c]`. That is the documented
    // behaviour of the pattern, it is unchanged by this ticket, and it is NOT a security issue: the
    // leftover text contains no complete tag and no control/format character, so nothing is
    // reconstituted in the string sent to Fish Audio. Pinned here so a future change to the
    // bracket semantics has to be deliberate rather than accidental.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Sanitize_Keeps_documented_non_greedy_semantics_for_nested_brackets()
    {
        // Arrange & Act
        string result = VoiceoverTextSanitizer.Sanitize("[a[b]c] Hello.", 200);

        // Assert
        result.Should().Be("c] Hello.");
    }
}
