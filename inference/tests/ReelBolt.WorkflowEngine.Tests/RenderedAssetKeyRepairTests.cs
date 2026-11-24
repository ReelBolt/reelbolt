using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression test: found live producing the alpha launch film.
///
/// The graphics room renders its own overlay and insert assets, is handed the real storage key by
/// <c>RenderVideoAndUploadToStorage</c>, and is asked only to echo that key back. On this run it
/// retyped the key instead, and got one hex digit of the execution GUID wrong — <c>…-84f1-…</c>
/// for an execution that was <c>…-80f1-…</c> — identically across all five assets. The prefix
/// check rejected every one, and the finished film lost both tracked inserts and all three
/// overlays to a silent "no graphics applied" degrade. The run before it had transcribed
/// correctly and passed, so this was luck, not correctness.
///
/// Repairing onto the execution's own prefix NARROWS the boundary the check exists to hold: the
/// result is inside that prefix by construction, so another project's key cannot be followed even
/// in principle. Only the file name stays model-controlled, and it already was.
/// </summary>
public class RenderedAssetKeyRepairTests
{
    private const string Prefix =
        "projects/83d13f94-13ae-444e-a64c-cbbd47742413/outputFiles/3a311b9e-848b-4050-80f1-843b744e12f9/";

    [Fact]
    public void The_exact_failure_that_cost_the_film_is_repaired()
    {
        // One wrong hex digit in the execution GUID: 84f1 where the execution is 80f1.
        const string mistyped =
            "projects/83d13f94-13ae-444e-a64c-cbbd47742413/outputFiles/3a311b9e-848b-4050-84f1-843b744e12f9/insert-desktop-projects.mp4";

        VideoCompileStepExecutor.NormalizeRenderedAssetKey(mistyped, Prefix)
            .Should().Be(Prefix + "insert-desktop-projects.mp4");
    }

    [Fact]
    public void A_correct_key_is_returned_untouched()
    {
        string correct = Prefix + "overlay-analyze.webm";

        VideoCompileStepExecutor.NormalizeRenderedAssetKey(correct, Prefix)
            .Should().BeSameAs(correct, "the common path must stay byte-identical to the pre-repair behaviour");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("some/dir/")]
    [InlineData(".")]
    [InlineData("..")]
    public void Nothing_recoverable_yields_null_and_the_caller_drops_it(string? key)
    {
        VideoCompileStepExecutor.NormalizeRenderedAssetKey(key, Prefix).Should().BeNull();
    }

    [Theory]
    [InlineData("projects/SOMEONE-ELSE/outputFiles/deadbeef/secret.mp4")]
    [InlineData("../../../etc/passwd")]
    [InlineData("projects/other/outputFiles/x/../../../../etc/shadow")]
    public void A_key_naming_anything_outside_this_execution_can_only_land_inside_it(string hostile)
    {
        string? resolved = VideoCompileStepExecutor.NormalizeRenderedAssetKey(hostile, Prefix);

        // The point of the repair: whatever was named, the result is confined to our own prefix.
        resolved.Should().NotBeNull();
        resolved!.Should().StartWith(Prefix);
        resolved.Should().NotContain("..");
        resolved[Prefix.Length..].Should().NotContain("/");
    }

    [Fact]
    public void The_extension_is_taken_from_the_repaired_key_so_a_webm_stays_a_webm()
    {
        // The caller derives the scratch file's extension from the returned key; losing it would
        // hand ffmpeg a .webm alpha overlay named .mp4.
        const string mistypedWebm =
            "projects/83d13f94-13ae-444e-a64c-cbbd47742413/outputFiles/WRONG-GUID/overlay-render.webm";

        VideoCompileStepExecutor.NormalizeRenderedAssetKey(mistypedWebm, Prefix)
            .Should().EndWith(".webm");
    }
}
