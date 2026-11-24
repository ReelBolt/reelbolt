using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="GeneratedPromptSanitizer"/> is the single shared hygiene pass for the ONLY value a
/// planning agent authors that leaves the building — the free-prose generation prompt. These tests
/// pin its two load-bearing properties: control characters other than <c>\n</c>/<c>\r</c>/<c>\t</c>
/// never reach a provider, and an over-length prompt is REPORTED rather than silently truncated
/// (the deterministic step fails the run closed with INVALID_PROMPT instead of paying to generate a
/// half-sentence the model never wrote).
/// </summary>
public class GeneratedPromptSanitizerTests
{
    // ---------------------------------------------------------------------
    // StripControlCharacters
    // ---------------------------------------------------------------------

    [Fact]
    public void StripControlCharacters_keeps_newline_carriage_return_and_tab()
    {
        // These three are ordinary multi-line prompt formatting, not structure smuggling — dropping
        // them would mangle legitimate prose for no security gain.
        string result = GeneratedPromptSanitizer.StripControlCharacters("line one\nline two\r\n\tindented");

        result.Should().Be("line one\nline two\r\n\tindented",
            "newline, carriage return and tab are preserved verbatim because they are ordinary prompt formatting, not smuggled structure");
    }

    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u0001")]
    [InlineData("\u0007")]
    [InlineData("\u000B")]
    [InlineData("\u000C")]
    [InlineData("\u001B")]
    [InlineData("\u007F")]
    [InlineData("\u0085")]
    public void StripControlCharacters_removes_every_other_control_character(string control)
    {
        // NUL can truncate a downstream parser's view of the string, ESC can forge terminal output
        // in a log, and a bare vertical tab / form feed can fake a section break in a prompt.
        string result = GeneratedPromptSanitizer.StripControlCharacters($"a{control}b");

        result.Should().Be("ab",
            $"U+{(int)control[0]:X4} is a control character other than \\n/\\r/\\t and must never reach a provider's prompt");
    }

    [Fact]
    public void StripControlCharacters_null_or_empty_yields_empty_string_never_null()
    {
        // The return type is non-nullable by contract; a null would surface as a NullReferenceException
        // somewhere far from the prompt that caused it.
        GeneratedPromptSanitizer.StripControlCharacters(null).Should().Be(string.Empty,
            "a missing prompt must degrade to an empty string, never to null");
        GeneratedPromptSanitizer.StripControlCharacters(string.Empty).Should().Be(string.Empty,
            "an empty prompt is already empty and must not be turned into null");
    }

    [Fact]
    public void StripControlCharacters_leaves_ordinary_text_byte_identical()
    {
        const string plain = "A chef plating pasta, warm window light, 35mm.";

        GeneratedPromptSanitizer.StripControlCharacters(plain).Should().Be(plain,
            "sanitizing must be a no-op for ordinary prose so it cannot quietly rewrite a prompt the model actually authored");
    }

    // ---------------------------------------------------------------------
    // Sanitize: the empty / blank cases
    // ---------------------------------------------------------------------

    [Fact]
    public void Sanitize_prompt_of_only_control_characters_is_empty()
    {
        // A prompt that sanitizes to nothing is the case that must fail the step closed rather than
        // submit a blank prompt to a paid provider.
        var sanitized = GeneratedPromptSanitizer.Sanitize("\u0000\u0001\u001B\u007F", InferenceProviderKind.MiniMax);

        sanitized.Text.Should().Be(string.Empty,
            "every character of this prompt is stripped, so nothing survives to send");
        sanitized.IsEmpty.Should().BeTrue(
            "the caller must treat a prompt that sanitized to nothing exactly like a missing prompt and fail closed instead of submitting a blank one");
    }

    [Fact]
    public void Sanitize_whitespace_only_prompt_is_empty()
    {
        var sanitized = GeneratedPromptSanitizer.Sanitize("   \t  ", InferenceProviderKind.MiniMax);

        sanitized.IsEmpty.Should().BeTrue(
            "spaces and tabs carry no instruction, so a whitespace-only prompt is as unusable as an absent one");
    }

    [Fact]
    public void Sanitize_prompt_of_only_newlines_is_empty_even_though_the_text_survives_stripping()
    {
        // \n\r\t survive control-character stripping, so this prompt is non-empty as a STRING while
        // still being unusable as an INSTRUCTION. IsEmpty must therefore be whitespace-based, which
        // is exactly the bug a naive `Text.Length == 0` check would ship.
        var sanitized = GeneratedPromptSanitizer.Sanitize("\n\r\t", InferenceProviderKind.MiniMax);

        sanitized.Text.Should().NotBeEmpty(
            "newline, carriage return and tab are deliberately preserved by the strip pass");
        sanitized.IsEmpty.Should().BeTrue(
            "IsEmpty must mean whitespace-only, not length-zero, or a prompt of pure newlines would be submitted as if it were instructions");
    }

    [Fact]
    public void Sanitize_null_prompt_is_empty_and_not_over_length()
    {
        var sanitized = GeneratedPromptSanitizer.Sanitize(null, InferenceProviderKind.MiniMax);

        sanitized.Text.Should().Be(string.Empty, "a null prompt sanitizes to the empty string, never to null");
        sanitized.IsEmpty.Should().BeTrue("a null prompt is unusable and must be reported as empty");
        sanitized.ExceedsMaxLength.Should().BeFalse("an empty prompt cannot be over a positive cap");
    }

    // ---------------------------------------------------------------------
    // Sanitize: the length cap is reported, never applied
    // ---------------------------------------------------------------------

    [Fact]
    public void Sanitize_text_exactly_at_the_cap_is_not_over_it()
    {
        const string atCap = "aaaaaaaaaa"; // 10 characters

        var sanitized = GeneratedPromptSanitizer.Sanitize(atCap, 10);

        sanitized.ExceedsMaxLength.Should().BeFalse(
            "a prompt exactly at the cap is legal; using >= instead of > here would reject the longest prompt an operator is allowed to write");
    }

    [Fact]
    public void Sanitize_text_one_character_over_the_cap_is_over_it_and_not_truncated()
    {
        const string oneOver = "aaaaaaaaaaa"; // 11 characters

        var sanitized = GeneratedPromptSanitizer.Sanitize(oneOver, 10);

        sanitized.ExceedsMaxLength.Should().BeTrue(
            "one character over the cap must be reported so the step can fail closed with INVALID_PROMPT");
        sanitized.Text.Should().Be(oneOver,
            "the sanitizer deliberately does NOT truncate: sending a silently cut-off prompt would cost money and produce a clip the model never asked for, with no signal that anything was lost");
        sanitized.Text.Length.Should().Be(11, "the over-length text is carried through whole for the caller to judge");
    }

    [Fact]
    public void Sanitize_length_is_measured_after_control_characters_are_stripped()
    {
        // 12 characters raw, 1 character once the 11 control characters are gone: measuring the RAW
        // length would reject a prompt that is one character long in every sense that matters to a
        // provider.
        var sanitized = GeneratedPromptSanitizer.Sanitize("\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u000B\u000Cx", 10);

        sanitized.Text.Should().Be("x", "only the non-control character survives the strip pass");
        sanitized.ExceedsMaxLength.Should().BeFalse(
            "the cap applies to the text that would actually be sent, not to the raw input the model emitted");
    }

    [Fact]
    public void Sanitize_carries_the_cap_it_measured_against()
    {
        var sanitized = GeneratedPromptSanitizer.Sanitize("hello", 123);

        sanitized.MaxLength.Should().Be(123,
            "the cap travels with the text so the caller never has to re-derive which provider's limit applied");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Sanitize_non_positive_explicit_cap_throws(int maxLength)
    {
        // A non-positive cap is a caller bug, not a provider fact. Accepted silently it would mark
        // EVERY prompt over-length and — because the caller fails closed — quietly disable generation
        // rather than surface the mistake.
        Action act = () => GeneratedPromptSanitizer.Sanitize("hello", maxLength);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "a cap of {0} can never be a real provider limit, so the caller's mistake must surface here rather than as a pipeline that silently refuses all input",
            maxLength);
    }

    // ---------------------------------------------------------------------
    // MaxPromptLengthFor / Sanitize(text, kind)
    // ---------------------------------------------------------------------

    [Fact]
    public void MaxPromptLengthFor_is_total_over_every_provider_kind_and_always_positive()
    {
        // An enum-matched method that could fall through to 0 would mark every prompt over-length
        // for a newly added provider kind and turn it into a pipeline that refuses all input. A
        // wrong-but-positive default degrades; a zero cap cannot.
        foreach (InferenceProviderKind kind in Enum.GetValues<InferenceProviderKind>())
        {
            int cap = GeneratedPromptSanitizer.MaxPromptLengthFor(kind);

            cap.Should().BeGreaterThan(0,
                $"{kind} must have a positive prompt cap, because a zero cap fails every prompt closed and silently disables generation for that provider kind");
        }
    }

    [Fact]
    public void MaxPromptLengthFor_every_kind_lets_an_ordinary_prompt_through()
    {
        // Same invariant as above, asserted through observable behaviour rather than the raw number:
        // no provider kind may mark a plainly reasonable prompt as over-length.
        string ordinary = new('a', 1000);

        foreach (InferenceProviderKind kind in Enum.GetValues<InferenceProviderKind>())
        {
            GeneratedPromptSanitizer.MaxPromptLengthFor(kind).Should().BeGreaterThan(ordinary.Length,
                $"{kind}'s cap must admit an ordinary 1000-character prompt, or that provider kind could never generate anything");
        }
    }

    [Fact]
    public void Sanitize_with_a_kind_uses_that_kinds_cap()
    {
        var sanitized = GeneratedPromptSanitizer.Sanitize("hello", InferenceProviderKind.MiniMax);

        sanitized.MaxLength.Should().Be(GeneratedPromptSanitizer.DefaultMaxPromptLength,
            "video generation is the only capability that sends model-authored prose outbound, and its "
            + "kinds (MiniMax and Higgsfield) both take the documented shared default");
        GeneratedPromptSanitizer.DefaultMaxPromptLength.Should().Be(7000,
            "this constant matches phase 1's private MaxPromptLength exactly; changing it silently changes what every existing video-generation step accepts");
    }

    [Fact]
    public void MaxPromptLengthFor_gives_both_video_generation_kinds_the_shared_default()
    {
        // Higgsfield is the second video-generation kind and has its own explicit arm rather than
        // riding the fall-through, so that a future tighter cap for it has an obvious place to live
        // and cannot be added by accident to the default and thereby to every kind at once.
        GeneratedPromptSanitizer.MaxPromptLengthFor(InferenceProviderKind.Higgsfield)
            .Should().Be(GeneratedPromptSanitizer.DefaultMaxPromptLength);
        GeneratedPromptSanitizer.MaxPromptLengthFor(InferenceProviderKind.Higgsfield)
            .Should().Be(GeneratedPromptSanitizer.MaxPromptLengthFor(InferenceProviderKind.MiniMax));
    }

    [Fact]
    public void Sanitize_with_a_kind_still_strips_and_measures_in_one_pass()
    {
        var sanitized = GeneratedPromptSanitizer.Sanitize("a\u0000b", InferenceProviderKind.MiniMax);

        sanitized.Text.Should().Be("ab",
            "the provider-kind overload must apply the same strip rule as the explicit-cap overload, or one call site would leak control characters the other removes");
    }

    // ---------------------------------------------------------------------
    // SanitizedPrompt
    // ---------------------------------------------------------------------

    [Fact]
    public void SanitizedPrompt_is_empty_is_whitespace_based_not_length_based()
    {
        new SanitizedPrompt("   ", 10).IsEmpty.Should().BeTrue(
            "whitespace-only means unusable, which is what the caller's fail-closed branch keys on");
        new SanitizedPrompt("x", 10).IsEmpty.Should().BeFalse(
            "any real content at all means the prompt is usable");
    }

    [Fact]
    public void SanitizedPrompt_exceeds_max_length_is_strictly_greater()
    {
        new SanitizedPrompt(new string('a', 10), 10).ExceedsMaxLength.Should().BeFalse(
            "exactly at the cap is allowed");
        new SanitizedPrompt(new string('a', 11), 10).ExceedsMaxLength.Should().BeTrue(
            "one past the cap is refused");
    }
}
