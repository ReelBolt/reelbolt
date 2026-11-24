using AwesomeAssertions;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

public sealed class ModerationContentHashTests
{
    [Fact]
    public void The_same_input_hashes_the_same_way()
    {
        ModerationInput input = new(
            "a brief",
            [new ModerationImageInput("image/jpeg", [1, 2, 3])],
            ModerationContentKind.Video);

        string first = ModerationContentHash.Compute(input);
        string second = ModerationContentHash.Compute(input);

        first.Should().Be(second);
        first.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void One_changed_image_byte_changes_the_hash()
    {
        string first = ModerationContentHash.Compute(new ModerationInput(
            "a brief", [new ModerationImageInput("image/jpeg", [1, 2, 3])], ModerationContentKind.Image));
        string second = ModerationContentHash.Compute(new ModerationInput(
            "a brief", [new ModerationImageInput("image/jpeg", [1, 2, 4])], ModerationContentKind.Image));

        first.Should().NotBe(second);
    }
}
