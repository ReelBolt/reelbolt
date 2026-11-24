using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>QA probes (voiceover-doubled) on the source-speech guard.</summary>
public partial class VideoCompileStepExecutorTests
{
    /// <summary>The Case A argv test is only meaningful if muting changes the argv of this fixture at all.</summary>
    [Fact]
    public async Task QA_guard_case_a_argv_differs_from_the_unmuted_compile()
    {
        var unmuted = await RunGuard(GuardArtifact(0, camera: false, readable: false));
        var guarded = await RunGuard(GuardArtifact(1.0, camera: false));
        guarded.Filter.Should().NotBe(unmuted.Filter, "Case A must actually drop the source sound in the argv");
    }

    /// <summary>
    /// Case A behaves "as MuteSourceAudio": the review facts on the voiceover node (headroom against the dialogue
    /// under each line) should then match an explicit MuteSourceAudio compile, since no dialogue plays under the lines.
    /// </summary>
    [Fact]
    public async Task QA_guard_case_a_headroom_matches_explicit_mute()
    {
        var muted = await RunGuard(GuardArtifact(1.0, camera: false), c => c with { MuteSourceAudio = true });
        var guarded = await RunGuard(GuardArtifact(1.0, camera: false));
        string mutedHeadroom = muted.Output.GetProperty("voiceover").GetProperty("headroom").GetRawText();
        string guardedHeadroom = guarded.Output.GetProperty("voiceover").GetProperty("headroom").GetRawText();
        guardedHeadroom.Should().Be(mutedHeadroom, $"guarded={guardedHeadroom} muted={mutedHeadroom}");
    }
}
