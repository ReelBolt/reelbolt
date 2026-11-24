using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The chunker is load-bearing: the renderer/TTS uses the whole payload, so the chunks must cover
/// every character and never exceed the client's per-call cap.
/// </summary>
public sealed class ModerationTextChunkerTests
{
    [Fact]
    public void The_chunks_reproduce_the_input_exactly()
    {
        string text = string.Join(" ", Enumerable.Repeat("caption line", 500));

        ModerationTextChunker.TryChunk(text, 1000, out IReadOnlyList<string> chunks).Should().BeTrue();

        chunks.Should().HaveCountGreaterThan(1);
        string.Concat(chunks).Should().Be(text);
        chunks.Should().OnlyContain(c => c.Length <= 1000);
    }

    [Fact]
    public void A_long_token_is_hard_split_rather_than_dropped()
    {
        string text = new('x', 2500);

        ModerationTextChunker.TryChunk(text, 1000, out IReadOnlyList<string> chunks).Should().BeTrue();

        string.Concat(chunks).Should().Be(text);
        chunks.Should().OnlyContain(c => c.Length <= 1000);
    }

    [Fact]
    public void More_chunks_than_the_bound_is_refused_rather_than_partly_classified()
    {
        string text = new('x', 100_000);

        ModerationTextChunker.TryChunk(text, 1000, out IReadOnlyList<string> chunks, maxChunks: 10)
            .Should().BeFalse();
        chunks.Should().BeEmpty();
    }

    [Fact]
    public void A_configured_cap_below_256_is_still_respected()
    {
        string text = new('x', 100);

        ModerationTextChunker.TryChunk(text, 10, out IReadOnlyList<string> chunks, maxChunks: 20)
            .Should().BeTrue();

        chunks.Should().OnlyContain(c => c.Length <= 10,
            "a chunk larger than the client cap would be truncated and partly unchecked");
        string.Concat(chunks).Should().Be(text);
    }

    [Fact]
    public void Blank_text_needs_no_chunks()
    {
        ModerationTextChunker.TryChunk("   ", 1000, out IReadOnlyList<string> chunks).Should().BeTrue();
        chunks.Should().BeEmpty();
    }
}
