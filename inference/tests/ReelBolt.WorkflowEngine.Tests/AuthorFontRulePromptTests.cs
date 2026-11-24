using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The Author's font rule, in both copies of its prompt (<see cref="AuthorAgentImpl"/>'s fallback and
/// the seeded row): the sandbox has no internet, so the Author must draw text with an installed family
/// by CSS name, never <c>@remotion/google-fonts</c> or a font URL, which the render tools refuse. The
/// family list is pinned to <see cref="SandboxFonts.FamilyList"/>, so it cannot drift from the image.
/// </summary>
public class AuthorFontRulePromptTests
{
    [Fact]
    public void Both_copies_state_the_local_font_rule_with_the_installed_families()
    {
        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string fallback = (string)typeof(AuthorAgentImpl)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        foreach (string raw in new[] { fallback, builtInAgents[AgentType.AuthorAgent].SystemPrompt })
        {
            string prompt = Regex.Replace(raw, @"\s+", " ");
            prompt.Should().Contain("- Fonts are local only: the sandbox has no internet.");
            prompt.Should().Contain("installed families: " + SandboxFonts.FamilyList + ".");
            prompt.Should().Contain("Never import `@remotion/google-fonts`");
            // A staged font is loaded with the loadFont + staticFile snippet; only remote loads are refused.
            prompt.Should().Contain("paste the `snippet` it returns (`loadFont` from `@remotion/fonts` with a local `staticFile(...)`)");
            prompt.Should().Contain("use the plain family name in `fontFamily`");
            prompt.Should().NotContain("@font-face it returns");
            prompt.Should().NotContain("local `@font-face`");
            prompt.Should().Contain("never load a font from a remote URL");
            prompt.Should().NotContain("never load a font from a URL");
            prompt.Should().NotContain("existing font mechanism");
        }
    }

    [Fact]
    public void Both_copies_tell_the_author_to_check_the_block_list_before_writing_any_scene()
    {
        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string fallback = (string)typeof(AuthorAgentImpl)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        foreach (string raw in new[] { fallback, builtInAgents[AgentType.AuthorAgent].SystemPrompt })
        {
            string prompt = Regex.Replace(raw, @"\s+", " ");
            prompt.Should().Contain("Before you write any scene file, check the block list below.");
            prompt.Should().Contain("call AddBlock(id) for it and build on the block");
            prompt.Should().Contain("Write custom code only for what no block covers.");
            prompt.Should().Contain("GetBlock(id) shows its props and an example");
        }
    }
}
