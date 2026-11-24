using System;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression coverage for <see cref="ReactRemotionSandboxTools.ResolveSandboxHttpTimeout"/>.
///
/// The bug this pins: the sandbox HTTP client was left on <c>HttpClient</c>'s DEFAULT 100-second
/// timeout while every exec request it carried declared a LONGER server-side deadline (600s for a
/// Remotion render, up to the sandbox's 900s ceiling for an agent-driven command, 300s for the
/// sandbox's own default). The client consequently aborted the POST at exactly 100s while the
/// sandbox was still working, and the agent saw a bare <c>TaskCanceledException</c> transport
/// error instead of a structured timeout — which took out every Remotion render slower than 100
/// seconds, i.e. essentially all of them.
///
/// The invariant, and the only thing that actually matters: the client's deadline must always be
/// STRICTLY GREATER than the server-side deadline the sandbox is enforcing for that same command,
/// so the sandbox wins the race and reports the timeout itself.
/// </summary>
public class SandboxHttpTimeoutTests
{
    [Theory]
    [InlineData(600)]   // Remotion render
    [InlineData(120)]   // npm run <script>
    [InlineData(540)]   // what the failing launch-film render actually asked for
    [InlineData(1)]
    [InlineData(300)]
    public void Client_timeout_exceeds_the_command_deadline_it_carries(int commandTimeoutSeconds)
    {
        TimeSpan resolved = ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(commandTimeoutSeconds);

        resolved.TotalSeconds.Should().BeGreaterThan(commandTimeoutSeconds);
    }

    [Fact]
    public void Long_running_deadlines_beat_the_dotnet_default_that_caused_the_bug()
    {
        // 100s is HttpClient's default and the exact point every real render used to die. Only the
        // deadlines that actually exceed it are asserted here: a command legitimately asking for,
        // say, 1 second SHOULD resolve below 100s — the invariant is ordering against its own
        // deadline (covered above), not a blanket floor.
        foreach (int requested in new[] { 0, 120, 300, 540, 600, 900, 5000 })
        {
            ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(requested)
                .TotalSeconds.Should().BeGreaterThan(100, "requested={0}", requested);
        }
    }

    [Fact]
    public void A_short_deadline_is_not_padded_up_to_the_old_default()
    {
        // Deliberate: the fix sizes off the command's own deadline, so a genuinely quick command
        // fails fast instead of hanging for the legacy 100 seconds.
        ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(1)
            .Should()
            .Be(TimeSpan.FromSeconds(1 + ReactRemotionSandboxTools.SandboxCommandTimeoutHeadroomSeconds));
    }

    [Fact]
    public void Zero_means_sandbox_default_and_is_sized_to_the_servers_ceiling()
    {
        // The engine cannot read the sandbox's SANDBOX_EXEC_TIMEOUT (5 min out of the box), so a
        // caller-supplied 0 must size to the ceiling that bounds it rather than collapsing to the
        // headroom alone — which would have been SHORTER than the 100s default it replaced.
        ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(0)
            .Should()
            .Be(TimeSpan.FromSeconds(
                ReactRemotionSandboxTools.SandboxMaxCommandSeconds
                + ReactRemotionSandboxTools.SandboxCommandTimeoutHeadroomSeconds));
    }

    [Fact]
    public void Negative_is_treated_like_zero_rather_than_shrinking_the_timeout()
    {
        ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(-30)
            .Should()
            .Be(ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(0));
    }

    [Fact]
    public void A_long_render_deadline_is_honoured_and_only_the_sandbox_ceiling_clamps_it()
    {
        // A long film's render is sized in hours (RenderTimeoutPolicy); the client must wait for it.
        ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(5000)
            .Should()
            .Be(TimeSpan.FromSeconds(5000 + ReactRemotionSandboxTools.SandboxCommandTimeoutHeadroomSeconds));

        // sandbox SANDBOX_EXEC_MAX_TIMEOUT defaults to 6 h; beyond it the client would outwait the sandbox.
        ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(100_000)
            .Should()
            .Be(TimeSpan.FromSeconds(
                LocalSandboxSession.MaxRequestedCommandSeconds
                + ReactRemotionSandboxTools.SandboxCommandTimeoutHeadroomSeconds));
    }

    [Fact]
    public void Render_and_npm_script_deadlines_are_the_ones_actually_sent()
    {
        // These constants are shared by the exec request and the client sizing; if they ever drift
        // apart the ordering invariant above silently stops holding for the real call sites.
        ReactRemotionSandboxTools.RenderCommandTimeoutSeconds.Should().Be(900);
        ReactRemotionSandboxTools.NpmScriptCommandTimeoutSeconds.Should().Be(120);

        ReactRemotionSandboxTools.ResolveSandboxHttpTimeout(
                ReactRemotionSandboxTools.RenderCommandTimeoutSeconds)
            .TotalSeconds.Should().BeGreaterThan(ReactRemotionSandboxTools.RenderCommandTimeoutSeconds);
    }
}
