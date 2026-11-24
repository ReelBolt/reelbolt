using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The frame-driven gsap rule: the ReelBolt guide in the remotion-markup preface and the one-line rule in
/// both prompt copies (the agent's DefaultPrompt and the seeded row) of the five agents that write scenes.
/// </summary>
public class GsapGuideTests
{
    private const string Rule =
        "gsap is installed: drive it only by the frame (build the timeline paused and `tl.seek(frame / fps)` from useCurrentFrame in a layout effect); never real time, requestAnimationFrame, ScrollTrigger or repeat: -1.";

    private static string Flat(string s) => Regex.Replace(s, @"\s+", " ");

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "sandbox", "template", "package-catalog.json")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    public static IEnumerable<object[]> Agents() =>
    [
        [typeof(AuthorAgentImpl), AgentType.AuthorAgent],
        [typeof(CompositionReviserAgent), AgentType.CompositionReviser],
        [typeof(MotionGraphicsPlannerAgent), AgentType.MotionGraphicsPlanner],
        [typeof(MotionGraphicsDirectorAgent), AgentType.MotionGraphicsDirector],
        [typeof(GraphicDesignerAgent), AgentType.GraphicDesigner],
    ];

    [Theory]
    [MemberData(nameof(Agents))]
    public void Both_prompt_copies_carry_the_frame_driven_gsap_rule(Type agent, AgentType type)
    {
        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string fallback = (string)agent.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        foreach (string raw in new[] { fallback, builtInAgents[type].SystemPrompt })
            Flat(raw).Should().Contain(Rule);
    }

    private const string GuidePointer =
        "(UseSkill remotion-markup: gifs.md, lottie.md, transitions.md; the gsap rules are in the skill's ReelBolt preface)";

    [Theory]
    [InlineData("gifs.md")]
    [InlineData("lottie.md")]
    [InlineData("transitions.md")]
    public void The_guides_named_by_the_prompt_pointer_exist(string file) =>
        File.Exists(Path.Combine(RepoRoot(), "inference", "skills", "remotion-markup", file)).Should().BeTrue();

    [Theory]
    [MemberData(nameof(Agents))]
    public void Both_prompt_copies_name_exactly_the_three_guides(Type agent, AgentType type)
    {
        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string fallback = (string)agent.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        foreach (string raw in new[] { fallback, builtInAgents[type].SystemPrompt })
            Flat(raw).Should().Contain(GuidePointer);
    }

    [Fact]
    public void The_remotion_markup_preface_carries_the_gsap_guide()
    {
        string text = Flat(File.ReadAllText(Path.Combine(RepoRoot(), "inference", "skills", "remotion-markup", "REELBOLT-PREFACE.md")));
        text.Should().Contain("## GSAP in Remotion (ReelBolt)");
        text.Should().Contain("GSAP is driven by the frame, never by time.");
        text.Should().Contain("`gsap.timeline({paused: true})`");
        text.Should().Contain("`tl.seek(frame / fps)`");
        text.Should().Contain("`useLayoutEffect`");
        text.Should().Contain("`gsap.context(() => {...}, containerRef)`");
        text.Should().Contain("`ctx.revert()`");
        text.Should().Contain("no `requestAnimationFrame`, no `gsap.ticker`, no `delayedCall`");
        text.Should().Contain("Never use `ScrollTrigger`, `ScrollSmoother`");
        text.Should().Contain("Avoid `repeat: -1` and `yoyo` loops.");
        text.Should().Contain("`tl.seek(frame / fps, false)`");
        text.Should().Contain("`gsap.registerPlugin(...)`");
        text.Should().Contain("`SplitText`");
        text.Should().Contain("background: '#0f172a'");
    }

    [Fact]
    public void The_taste_skill_preface_allows_gsap_in_the_frame_driven_form_only()
    {
        string text = Flat(File.ReadAllText(Path.Combine(RepoRoot(), "inference", "skills", "taste-skill", "REELBOLT-PREFACE.md")));
        text.Should().Contain("GSAP is allowed, but only in the frame-driven form");
        text.Should().NotContain("(Motion, GSAP)");
        text.Should().Contain("ScrollTrigger, ScrollSmoother, parallax on scroll");
    }
}
