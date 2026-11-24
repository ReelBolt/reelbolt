using System;
using System.IO;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The ReelBolt prefaces of the vendored web-design skills tell the model which fonts it can use in the
/// sandbox: the installed families (<see cref="SandboxFonts.FamilyList"/>), never Google Fonts or a font
/// URL. The vendored skill text itself is never edited, so the preface is where that rule lives.
/// </summary>
public class SkillPrefaceFontRuleTests
{
    [Theory]
    [InlineData("taste-skill")]
    [InlineData("brutalist-skill")]
    [InlineData("minimalist-skill")]
    public void The_preface_points_at_the_installed_fonts_not_google_fonts(string skill)
    {
        string raw = File.ReadAllText(Path.Combine(RepositoryRoot(), "inference", "skills", skill, "REELBOLT-PREFACE.md"));
        string preface = Regex.Replace(raw, @"\s+", " ");

        preface.Should().Contain("Fonts are local: the sandbox has no internet, so set CSS `fontFamily` to an installed family (" + SandboxFonts.FamilyList + ")");
        preface.Should().NotContain("google-fonts.md");
        preface.Should().NotContain("local-fonts.md");
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(SkillPrefaceFontRuleTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
                return dir;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
